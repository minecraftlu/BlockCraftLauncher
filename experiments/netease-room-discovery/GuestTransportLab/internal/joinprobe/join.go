// Package joinprobe implements a bounded, user-initiated genuine RakNet
// lobby admission probe. It does not create a Minecraft game client.
// The user's own second account is authenticated locally by a separately
// selected helper. Tokens, cookies and network IDs are NEVER printed.
package joinprobe

import (
    "bytes"
    "context"
    "crypto/aes"
    "crypto/md5"
    cryptorand "crypto/rand"
    "encoding/json"
    "errors"
    "fmt"
    "io"
    "log/slog"
    "net"
    "net/http"
    "os"
    "os/exec"
    "path/filepath"
    "strconv"
    "strings"
    "time"

    "github.com/Happy2018new/nemc-tan-lobby-solver/core/nethernet"
    rtcwebrtc "github.com/Happy2018new/nemc-tan-lobby-solver/core/webrtc"
    "github.com/Happy2018new/nemc-tan-lobby-solver/core/raknet"
    protoencoding "github.com/Happy2018new/nemc-tan-lobby-solver/protocol/encoding"
    "github.com/Happy2018new/nemc-tan-lobby-solver/protocol/packet"
    "github.com/Happy2018new/nemc-tan-lobby-solver/protocol/service/signaling"
)

const (
    roomEndpoint = "https://g79mcltransfer.minecraft.cn/room-with-name"
    nodeEndpoint = "https://g79.update.netease.com/transferserver_obt_new.list"
    maxHTTPBytes = 1024 * 1024
    maxHelperOutput = 16384
)

type Request struct {
    HelperPath string
    CookiePath string
    RoomID string
    // The room is owned or the host has explicitly invited the tester.
    AuthorizedByHost bool
    // AttemptWebRTC requires real lobby admission and a host-ready event.
    AttemptWebRTC bool
}

type Report struct {
    Status string `json:"status"`
    Scope string `json:"scope"`
    LoginAccepted bool `json:"login_accepted"`
    RoomAdmissionAccepted bool `json:"room_admission_accepted"`
    HostReadySeen bool `json:"host_ready_seen"`
    SignalingAuthenticated bool `json:"signaling_authenticated"`
    WebRTCTransportConnected bool `json:"webrtc_transport_connected"`
    TransportStable bool `json:"transport_stable"`
    MinecraftWorldJoined bool `json:"minecraft_world_joined"`
    ModeConfirmed bool `json:"g79_mode_confirmed"`
    Detail string `json:"detail"`
}

func makeReport(status, detail string) Report {
    return Report{
        Status: status,
        Detail: detail,
        Scope: "user_authorized_single_real_room_admission_probe",
        MinecraftWorldJoined: false,
    }
}

type login struct {
    uid uint32
    token []byte
    nickname string
    g79Confirmed bool
}

func parseLogin(raw []byte) (login, error) {
    var out login
    var mode string
    var isPE bool
    for _, line := range strings.Split(string(raw), "\n") {
        name, value, found := strings.Cut(strings.TrimSpace(strings.TrimPrefix(line, "\ufeff")), "=")
        if !found { continue }
        name, value = strings.ToLower(strings.TrimSpace(name)), strings.TrimSpace(value)
        switch name {
        case "uid":
            n, err := strconv.ParseUint(value, 10, 32)
            if err == nil && n > 0 { out.uid = uint32(n) }
        case "login_token":
            // The helper and Stage8 preflight accept AES-compatible
            // ASCII session keys of 16, 24 or 32 bytes. The RakNet lobby
            // nonce uses MD5(token) -> a 16-byte protocol key regardless
            // of session token key length.
            if (len(value) == 16 || len(value) == 24 || len(value) == 32) && validASCII(value) {
                out.token = []byte(value)
            }
        case "nickname":
            if len(value) > 0 && len(value) < 256 { out.nickname = value }
        case "platform":
            isPE = value == "2"
        case "auth_mode":
            mode = strings.ToLower(value)
        }
    }
    if out.uid == 0 || !isPE || len(out.token) == 0 || mode == "x19" ||
        (mode != "" && mode != "g79") {
        clear(out.token)
        return login{}, errors.New("no usable PE game session")
    }
    if out.nickname == "" {
        clear(out.token)
        return login{}, errors.New("missing player nickname for lobby login")
    }
    out.g79Confirmed = mode == "g79"
    return out, nil
}

func validASCII(s string) bool {
    for i := 0; i < len(s); i++ {
        if s[i] < 0x21 || s[i] > 0x7e { return false }
    }
    return true
}

type cappedBuffer struct {
    Buffer bytes.Buffer
    overflow bool
    max int
}

func (w *cappedBuffer) Write(p []byte) (int, error) {
    n := len(p)
    remain := w.max - w.Buffer.Len()
    if remain < n {
        w.overflow = true
        if remain > 0 { _, _ = w.Buffer.Write(p[:remain]) }
        return n, nil
    }
    _, _ = w.Buffer.Write(p)
    return n, nil
}

// runHelper passes only filesystem paths to a child process. Never run the
// upstream -dump-auth command directly in a terminal: it prints a live token.
func runHelper(ctx context.Context, req Request) (login, error) {
    if filepath.Base(req.HelperPath) != "connect.exe" { return login{}, errors.New("helper must be connect.exe") }
    if !filepath.IsAbs(req.HelperPath) || !filepath.IsAbs(req.CookiePath) {
        return login{}, errors.New("helper and cookie paths must be absolute")
    }
    if _, err := os.Stat(req.CookiePath); err != nil { return login{}, errors.New("local cookie unavailable") }

    workdir, err := os.MkdirTemp("", "blockcraft-probe-")
    if err != nil { return login{}, errors.New("cannot create temporary private folder") }
    defer os.RemoveAll(workdir)

    config := map[string]any{
        "auth_mode": "g79",
        "cookie_file": req.CookiePath,
        "transfer": true,
        "transfer_server": "127.0.0.1",
        "log_level": "error",
    }
    data, _ := json.Marshal(config)
    if err := os.WriteFile(filepath.Join(workdir, "auth.json"), data, 0600); err != nil {
        return login{}, errors.New("cannot write temporary config")
    }

    cmd := exec.CommandContext(ctx, req.HelperPath, "-dump-auth", "-c", filepath.Join(workdir, "auth.json"))
    cmd.Dir = filepath.Dir(req.HelperPath)
    var captured cappedBuffer
    captured.max = maxHelperOutput
    cmd.Stdout = &captured
    cmd.Stderr = io.Discard
    if err := cmd.Run(); err != nil {
        clear(captured.Buffer.Bytes())
        return login{}, errors.New("third-party helper failed or account verification is required")
    }
    if captured.overflow {
        clear(captured.Buffer.Bytes())
        return login{}, errors.New("helper output too large")
    }
    result, err := parseLogin(captured.Buffer.Bytes())
    clear(captured.Buffer.Bytes())
    return result, err
}

func clear(data []byte) {
    for i := range data { data[i] = 0 }
}

func asUint(x any) (uint64, bool) {
    var s string
    switch v := x.(type) {
    case json.Number: s = v.String()
    case string: s = v
    default: return 0, false
    }
    n, err := strconv.ParseUint(s, 10, 64)
    return n, err == nil
}

func readHTTP(ctx context.Context, method, url string, requestBody []byte, uid uint32) ([]byte, error) {
    client := &http.Client{Timeout: 12 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error {
        return http.ErrUseLastResponse
    }}
    var body io.Reader
    if requestBody != nil { body = bytes.NewReader(requestBody) }
    request, err := http.NewRequestWithContext(ctx, method, url, body)
    if err != nil { return nil, err }
    if requestBody != nil {
        request.Header.Set("Content-Type", "application/json")
        request.Header.Set("user-id", strconv.FormatUint(uint64(uid), 10))
    }
    reply, err := client.Do(request)
    if err != nil { return nil, err }
    defer reply.Body.Close()
    if reply.StatusCode != http.StatusOK || reply.ContentLength > maxHTTPBytes { return nil, errors.New("non-success response") }
    data, err := io.ReadAll(io.LimitReader(reply.Body, maxHTTPBytes+1))
    if err != nil || len(data) > maxHTTPBytes { return nil, errors.New("oversized or unreadable response") }
    return data, nil
}

type room struct {
    owner, roomID uint32
    srv uint64
}

type server struct {
    host string
    port uint16
    signalingPort uint16
}

func parseRoomJSON(data []byte, target string) (room, error) {
    var result struct {
        Code json.Number `json:"code"`
        List []map[string]json.RawMessage `json:"list"`
    }
    decoder := json.NewDecoder(bytes.NewReader(data))
    decoder.UseNumber()
    if err := decoder.Decode(&result); err != nil || result.Code.String() != "0" {
        return room{}, errors.New("bad room response")
    }
    for _, entry := range result.List {
        match := false
        for _, k := range []string{"name", "rid", "room_unique_id"} {
            var val any
            d := json.NewDecoder(bytes.NewReader(entry[k]));d.UseNumber()
            if d.Decode(&val) == nil && fmt.Sprint(val) == target { match = true;break }
        }
        if !match { continue }
        var hid, rid, srv, count, capacity, protected any
        _ = json.Unmarshal(entry["hid"], &hid)
        _ = json.Unmarshal(entry["rid"], &rid)
        _ = json.Unmarshal(entry["srv"], &srv)
        _ = json.Unmarshal(entry["cnt"], &count)
        _ = json.Unmarshal(entry["cap"], &capacity)
        _ = json.Unmarshal(entry["need_password"], &protected)
        // json.Unmarshal into interface{} uses float64; instead reparse with UseNumber
        for _, item := range []struct{name string;ptr *any}{
            {"hid",&hid},{"rid",&rid},{"srv",&srv},
            {"cnt",&count},{"cap",&capacity},{"need_password",&protected},
        } {
            d := json.NewDecoder(bytes.NewReader(entry[item.name]))
            d.UseNumber();_ = d.Decode(item.ptr)
        }
        ownerID, okA := asUint(hid); roomID, okB := asUint(rid); serverID, okC := asUint(srv)
        if !okA || !okB || !okC || ownerID == 0 || roomID == 0 || serverID == 0 ||
            ownerID > 0xffffffff || roomID > 0xffffffff {
            return room{}, errors.New("invalid room and server identifiers")
        }
        cnt, cOK := asUint(count); cap, capOK := asUint(capacity)
        if cOK && capOK && cap > 0 && cnt >= cap { return room{}, errors.New("room full") }
        pass, pOK := asUint(protected)
        if pOK && pass == 1 { return room{}, errors.New("password-protected room is not supported") }
        return room{owner:uint32(ownerID),roomID:uint32(roomID),srv:serverID},nil
    }
    return room{}, errors.New("room not found")
}

func parseServerJSON(data []byte, srv uint64) (server, error) {
    decoder := json.NewDecoder(bytes.NewReader(data))
    decoder.UseNumber()
    var rows []map[string]any
    if err := decoder.Decode(&rows); err != nil { return server{}, errors.New("invalid server list") }
    for _, row := range rows {
        id, ok := asUint(row["id"]);if !ok || id != srv { continue }
        host, ok := row["ip"].(string)
        if !ok || net.ParseIP(host) == nil { return server{}, errors.New("unexpected server address") }
        ports, ok := row["ports"].([]any)
        if !ok || len(ports) == 0 { return server{}, errors.New("missing RakNet port") }
        port, ok := asUint(ports[0])
        if !ok || port < 1 || port > 65535 { return server{}, errors.New("invalid RakNet port") }
        var signalPort uint64
        if signal, exists := row["SignalWebPort"]; exists {
            value,valid := asUint(signal)
            if valid && value >= 1 && value <= 65535 { signalPort = value }
        }
        return server{host:host,port:uint16(port),signalingPort:uint16(signalPort)},nil
    }
    return server{}, errors.New("no matching lobby server")
}

type material struct {
    rand []byte
    aesRand []byte
    encKey []byte
    decKey []byte
}

func (m *material) destroy() {
    clear(m.rand);clear(m.aesRand);clear(m.encKey);clear(m.decKey)
}

func derive(token []byte) (*material, error) {
    if len(token) != 16 && len(token) != 24 && len(token) != 32 {
        return nil, errors.New("unexpected token size")
    }
    nonce := make([]byte,16)
    if _,err := cryptorand.Read(nonce); err != nil { return nil,err }
    digest := md5.Sum(token) // Legacy wire protocol requirement, NOT a password hash.
    block,err := aes.NewCipher(digest[:])
    if err != nil { clear(nonce); return nil,err }
    padded := make([]byte,32)
    copy(padded,nonce)
    for i:=16;i<32;i++ {padded[i]=16}
    aesRand := make([]byte,32)
    block.Encrypt(aesRand[:16],padded[:16])
    block.Encrypt(aesRand[16:],padded[16:])
    enc := make([]byte,0,32);enc=append(enc,digest[:]...);enc=append(enc,nonce...)
    dec := make([]byte,0,32);dec=append(dec,nonce...);dec=append(dec,digest[:]...)
    clear(padded)
    return &material{rand:nonce,aesRand:aesRand,encKey:enc,decKey:dec},nil
}

func sendPacket(enc *packet.Encoder, pk packet.Packet) error {
    buf:=bytes.NewBuffer(nil)
    header := packet.Header{PacketID:pk.ID()}
    if err:=header.Write(buf);err!=nil{return err}
    writer:=protoencoding.NewWriter(buf,0)
    pk.Marshal(writer)
    return enc.Encode(buf.Bytes())
}

func receivePacket(ctx context.Context, conn *raknet.Conn, dec *packet.Decoder) (pk packet.Packet, err error) {
    type result struct {packet packet.Packet;err error}
    ch:=make(chan result,1)
    go func(){
        defer func(){
            if recover()!=nil {ch<-result{err:errors.New("invalid lobby packet")}}
        }()
        b,e:=dec.Decode();if e!=nil{ch<-result{err:errors.New("cannot decode lobby packet")};return}
        if len(b)<2 || len(b)>64*1024{ch<-result{err:errors.New("invalid lobby packet length")};return}
        buf:=bytes.NewBuffer(b)
        header:=packet.Header{}
        if e=header.Read(buf);e!=nil{ch<-result{err:errors.New("invalid lobby header")};return}
        pk:=packet.NewServerPool()[header.PacketID]
        if pk==nil{ch<-result{err:fmt.Errorf("unsupported lobby packet id %d",header.PacketID)};return}
        pk.Marshal(protoencoding.NewReader(buf))
        ch<-result{packet:pk}
    }()
    select{
    case r:=<-ch:return r.packet,r.err
    case <-ctx.Done():
        _=conn.Close()
        return nil,errors.New("timed out waiting for verified lobby response")
    }
}

func probeLobby(ctx context.Context, loginSession login, roomInfo room, node server, attemptWebRTC bool) Report {
    address:=net.JoinHostPort(node.host,strconv.Itoa(int(node.port)))
    dialCtx,cancel:=context.WithTimeout(ctx,12*time.Second)
    defer cancel()
    connection,err:=raknet.DialContext(dialCtx,address)
    if err!=nil{return makeReport("RAKNET_TRANSPORT_FAILED","RakNet 客户端未建立连接；可能是版本、UDP网络或大厅节点状态问题。")}
    defer connection.Close()
    material,err:=derive(loginSession.token)
    if err!=nil{return makeReport("LOCAL_KEY_DERIVATION_FAILED","无法准备临时登录密钥。")}
    defer material.destroy()

    enc:=packet.NewEncoder(connection)
    dec,err:=packet.NewDecoder(connection)
    if err!=nil{return makeReport("RAKNET_CODEC_FAILED","无法初始化大厅数据编解码器。")}
    if err=sendPacket(enc,&packet.TanLoginRequest{
        PlayerID:loginSession.uid,Rand:material.rand,
        AESRand:material.aesRand,PlayerName:loginSession.nickname,
    });err!=nil{return makeReport("TAN_LOGIN_SEND_FAILED","无法发送经过账户身份格式准备的大厅登录请求。")}
    resp,err:=receivePacket(ctx,connection,dec)
    if err!=nil{return makeReport("TAN_LOGIN_RESPONSE_MISSING","大厅登录请求已发送，但没有收到可识别的服务器响应。")}
    loginResponse,ok:=resp.(*packet.TanLoginResponse)
    if !ok{return makeReport("TAN_LOGIN_RESPONSE_UNEXPECTED","收到的服务器数据包不是大厅登录回执。")}
    if loginResponse.ErrorCode!=packet.TanLoginSuccess{
        return makeReport("TAN_LOGIN_REJECTED","大厅实际返回登录拒绝码："+strconv.Itoa(int(loginResponse.ErrorCode))+"。没有发送入房请求。")
    }
    report:=makeReport("TAN_LOGIN_ACCEPTED","服务器已确认大厅登录；正在申请进入指定房间。")
    report.LoginAccepted=true
    report.ModeConfirmed=loginSession.g79Confirmed
    if err=enc.EnableEncryption(material.encKey,material.decKey);err!=nil{
        report.Status="TAN_ENCRYPTION_SETUP_FAILED";report.Detail="无法启用大厅会话加密。";return report
    }
    if err=dec.EnableEncryption(material.encKey,material.decKey);err!=nil{
        report.Status="TAN_ENCRYPTION_SETUP_FAILED";report.Detail="无法启用大厅会话解密。";return report
    }

    var netherID [8]byte
    if _,err=cryptorand.Read(netherID[:]);err!=nil{
        report.Status="CLIENT_NETWORK_ID_FAILED";report.Detail="无法创建游客连接标识。";return report
    }
    clientID:=uint64(0)
    for _,v:=range netherID{clientID=(clientID<<8)|uint64(v)}
    if clientID==0{clientID=1}
    if err=sendPacket(enc,&packet.TanEnterRoomRequest{
        OwnerID:roomInfo.owner,RoomID:roomInfo.roomID,
        EnterPassword:"",EnterTeamID:0,EnterToken:0,FollowTeamID:0,
        NetherNetID:strconv.FormatUint(clientID,10),SupportWebRTCCompress:true,
    });err!=nil{
        report.Status="ENTER_ROOM_SEND_FAILED";report.Detail="大厅登录成功，但发送入房请求失败。";return report
    }
    resp,err=receivePacket(ctx,connection,dec)
    if err!=nil{report.Status="ENTER_ROOM_RESPONSE_MISSING";report.Detail="已提交入房请求，但未能得到可识别服务器回执。";return report}
    admission,ok:=resp.(*packet.TanEnterRoomResponse)
    if !ok{report.Status="ENTER_ROOM_RESPONSE_UNEXPECTED";report.Detail="服务器回执不是预期的入房响应。";return report}
    if admission.ErrorCode!=packet.TanEnterRoomSuccess{
        report.Status="ENTER_ROOM_REJECTED"
        switch admission.ErrorCode{
        case packet.TanEnterRoomNotFound:report.Detail="网易大厅报告房间已关闭或不存在。"
        case packet.TanEnterRoomFullOfPeople:report.Detail="网易大厅报告房间已满。"
        case packet.TanEnterRoomWrongPasscode:report.Detail="网易大厅报告房间需要正确密码。"
        default:report.Detail="网易大厅报告入房拒绝码："+strconv.Itoa(int(admission.ErrorCode))
        }
        return report
    }
    report.RoomAdmissionAccepted=true
    report.Status="SERVER_ROOM_ADMISSION_ACCEPTED"
    report.Detail="真实网易大厅 TanEnterRoomResponse 报告允许入房；探针还不是 Minecraft 游戏客户端。"
    // Room admission is not game readiness. Wait for one host-ready packet.
    resp,err=receivePacket(ctx,connection,dec)
    if err!=nil {
        report.Detail+=" 尚未收到房主就绪事件。"
        return report
    }
    if ready,ok:=resp.(*packet.TanNotifyServerReady);ok && ready.NetherNetID!="" {
        report.HostReadySeen=true
        report.Status="HOST_NETHERNET_READY_GAME_NOT_CONNECTED"
        report.Detail="网易大厅允许入房，且收到了房主 NetherNet 就绪通知。尚未连接游戏。"
        if !attemptWebRTC { return report }
        hostNetherID, parseErr := strconv.ParseUint(ready.NetherNetID,10,64)
        if parseErr!=nil || hostNetherID==0 {
            report.Status="HOST_NETHERNET_ID_INVALID"
            report.Detail="房主连接标识格式无法用于 NetherNet 协商。"
            return report
        }
        return probeWebRTC(ctx, loginSession, node, clientID, hostNetherID, report)
    }
    return report
}

// deriveSignalingCredential prepares the legacy NetherNet signaling ticket.
// The ticket and seed remain only in memory until the WebSocket closes.
// The cipher format is required by the existing peer protocol, not a
// recommendation for new encryption designs.
func deriveSignalingCredential(token []byte) (seed []byte, ticket []byte, err error) {
    if len(token)!=16 && len(token)!=24 && len(token)!=32 {
        return nil,nil,errors.New("unsupported signaling session key")
    }
    seed=make([]byte,16)
    if _,err=cryptorand.Read(seed);err!=nil {
        clear(seed);return nil,nil,err
    }
    cipher,err:=aes.NewCipher(token)
    if err!=nil {clear(seed);return nil,nil,err}
    padded:=make([]byte,32)
    copy(padded,seed)
    for i:=16;i<32;i++ {padded[i]=16}
    ticket=make([]byte,32)
    cipher.Encrypt(ticket[:16],padded[:16])
    cipher.Encrypt(ticket[16:],padded[16:])
    clear(padded)
    return seed,ticket,nil
}

// probeWebRTC only runs after a real server TanEnterRoomResponse success and
// TanNotifyServerReady. It uses the original local client NetherNetID, never
// a synthetic room grant. No Minecraft gameplay packets are sent.
func probeWebRTC(ctx context.Context, session login, node server,
    clientNetherID uint64, hostNetherID uint64, result Report) Report {
    if node.signalingPort==0 {
        result.Status="SIGNALING_NODE_UNAVAILABLE"
        result.Detail="当前公开大厅节点列表没有可靠的 WebSocket 信令端口。"
        return result
    }
    seed,ticket,err:=deriveSignalingCredential(session.token)
    if err!=nil {
        result.Status="SIGNALING_TICKET_DERIVATION_FAILED"
        result.Detail="当前登录会话无法生成有效信令票据。"
        return result
    }
    defer clear(seed)
    defer clear(ticket)

    // Use a bounded session; the lobby RakNet connection stays active until
    // this function returns. Do not print ticket, seed, ICE credentials,
    // signaling URL, peer network ID or user/room identifiers.
    // Previous 45s combined signaling + ICE + DTLS + SCTP timeout could
    // terminate a legitimate SCTP retransmission series before its own
    // typed INIT ACK / COOKIE ACK failure became observable. Keep a finite
    // 70s budget for all phases under the explicit 105s process limit.
    signalCtx,cancel:=context.WithTimeout(ctx,70*time.Second)
    defer cancel()
    serverAddress:=net.JoinHostPort(node.host,strconv.Itoa(int(node.signalingPort)))
    signalConn,err:= (signaling.Dialer{
        RefreshTime:signaling.RefreshTimeDisbale,
        NetherNetID:clientNetherID,
    }).DialContext(signalCtx,serverAddress,session.uid,seed,ticket)
    if err!=nil {
        result.Status="SIGNALING_CONNECTION_FAILED"
        result.Detail="真实信令 WebSocket 握手或 ICE 服务器凭据阶段未完成。"
        return result
    }
    defer signalConn.Close(errors.New("BlockCraft stage10 probe complete"))
    result.SignalingAuthenticated=true
    result.Status="SIGNALING_AUTHENTICATED_WEBRTC_PENDING"
    result.Detail="信令服务器提供了 ICE 凭据，正在尝试与房主协商数据连接。"

    // Track only fixed, whitelisted upstream phase markers and signal-type
    // bits. Never log raw ICE candidate addresses, SDP, credentials or IDs.
    observation := &webRTCObservation{}
    observedSignal := trackedSignaling{
        Signaling: signalConn, observation: observation,
    }
    // The fork used by NetherNet supports a bounded SCTP retransmission
    // cap through its ORTC SettingEngine. With default exponential backoff
    // (up to 60s) the 8 INIT/COOKIE retransmissions can outlive the entire
    // diagnostic session. A 3s RTO cap is a single-attempt LAN-oriented
    // compatibility experiment, not an assertion that the host is at fault.
    // Do not disable DTLS verification, ICE checks, or room authorization.
    settings := rtcwebrtc.SettingEngine{}
    settings.SetSCTPRTOMax(3*time.Second)
    api := rtcwebrtc.NewAPI(rtcwebrtc.WithSettingEngine(settings))
    rtc,err:=(nethernet.Dialer{
        API:api,
        Log:slog.New(stageLogger{observation:observation}),
    }).DialContext(signalCtx,hostNetherID,observedSignal)
    if err!=nil {
        result.Status=classifyWebRTCFailure(err,observation)
        result.Detail="WebRTC 已结束在受控诊断阶段；仅报告无敏感信息的分类状态。"
        return result
    }
    defer rtc.Close()
    // A successful DialContext alone only proves that the transport was
    // constructed. Do not call it stable until both data channels have
    // survived a bounded passive observation window. The observer sends
    // zero Minecraft packets and never inspects payload bytes.
    switch holdTransport(signalCtx, rtc, defaultStabilityWindow) {
    case stabilityRemoteClosed:
        result.Status="WEBRTC_EARLY_DISCONNECT_GAME_NOT_JOINED"
        result.Detail="完成 WebRTC 协商后，手机房主连接在短暂观察期内断开。"
        return result
    case stabilityCancelled:
        if errors.Is(signalCtx.Err(), context.DeadlineExceeded) {
            result.Status="WEBRTC_STABILITY_WINDOW_INTERRUPTED"
            result.Detail="在连接稳定性检查结束前，整体有界协商时限已到。"
        } else {
            result.Status="WEBRTC_PROBE_CANCELLED"
            result.Detail="已请求取消并释放 WebRTC 连接。"
        }
        return result
    default:
        result.WebRTCTransportConnected=true
        result.TransportStable=true
        result.Status="WEBRTC_TRANSPORT_STABLE_8S_GAME_NOT_JOINED"
        result.Detail="WebRTC 数据通道已建立且在 8 秒被动检查期间未断开；未发送游戏包或进入世界。"
        return result
    }
}

func Run(ctx context.Context, req Request) Report {
    if !req.AuthorizedByHost{return makeReport("HOST_CONSENT_REQUIRED","只允许测试自己开设或获得房主明确允许的联机房间。")}
    if len(req.RoomID)<4 || len(req.RoomID)>10 {
        return makeReport("ROOM_ID_INVALID","请输入有效的数字房间号。")
    }
    for _,ch:=range req.RoomID {if ch<'0'||ch>'9'{return makeReport("ROOM_ID_INVALID","房间号必须是数字。")}}
    if ctx==nil {return makeReport("INVALID_CONTEXT","缺少可取消的连接会话。")}
    authCtx,cancel:=context.WithTimeout(ctx,35*time.Second)
    session,err:=runHelper(authCtx,req)
    cancel()
    if err!=nil{return makeReport("PE_AUTH_UNAVAILABLE","第二账号暂时无法取得可用 PE 游戏会话。请仅在本地处理验证要求。")}
    defer clear(session.token)

    body,_:=json.Marshal(map[string]any{"name":req.RoomID,"uid":session.uid})
    data,err:=readHTTP(ctx,http.MethodPost,roomEndpoint,body,session.uid)
    if err!=nil{return makeReport("ROOM_LOOKUP_FAILED","无法用第二账号查询当前房间。")}
    roomInfo,err:=parseRoomJSON(data,req.RoomID)
    if err!=nil{return makeReport("ROOM_NOT_JOINABLE","目标房间可能不存在、已满、受密码保护或返回格式变化。")}
    data,err=readHTTP(ctx,http.MethodGet,nodeEndpoint,nil,0)
    if err!=nil{return makeReport("LOBBY_NODE_LOOKUP_FAILED","无法读取网易公开大厅节点列表。")}
    node,err:=parseServerJSON(data,roomInfo.srv)
    if err!=nil{return makeReport("LOBBY_NODE_MAPPING_FAILED","无法可靠匹配当前房间的 RakNet 大厅节点。")}
    return probeLobby(ctx,session,roomInfo,node,req.AttemptWebRTC)
}
