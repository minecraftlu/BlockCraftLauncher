package joinprobe

import (
    "bytes"
    "crypto/aes"
    "encoding/json"
    "strings"
    "testing"
)

func TestParseSecondAccountSession(t *testing.T) {
    privateToken := "0123456789abcdef"
    raw := "uid=10001\nnickname=LocalGuest\nplatform=2\nlogin_token=" + privateToken + "\n"
    info,err:=parseLogin([]byte(raw))
    if err!=nil {t.Fatal(err)}
    if info.uid!=10001 || info.nickname!="LocalGuest" || info.g79Confirmed || !bytes.Equal(info.token,[]byte(privateToken)){
        t.Fatalf("wrong second-account parsing (DO NOT dump credentials)")
    }
    clear(info.token)
    // Real second-account tokens can have any of the AES-compatible key
    // lengths supported by the Stage8 Windows preflight.
    for _, keyLen := range []int{16, 24, 32} {
        sample := strings.Repeat("A", keyLen)
        attempt, err := parseLogin([]byte(
            "uid=10001\nnickname=LocalGuest\nplatform=2\nlogin_token=" + sample + "\n"))
        if err != nil || len(attempt.token) != keyLen {
            t.Fatal("AES-compatible PE session token was rejected")
        }
        derived, err := derive(attempt.token)
        if err != nil || len(derived.encKey) != 32 {
            t.Fatal("Valid PE token failed legacy RakNet lobby key derivation")
        }
        derived.destroy()
        clear(attempt.token)
    }
    for _,test:=range []string{
        strings.Replace(raw,"platform=2","platform=1",1),
        strings.Replace(raw,"nickname=LocalGuest","nickname=",1),
        strings.Replace(raw,privateToken,"short",1),
        raw+"auth_mode=x19\n",
    }{
        if _,err:=parseLogin([]byte(test));err==nil {t.Fatal("unsafe session accepted")}
    }
}

func TestReadRoomAndServerWithoutCredentials(t *testing.T) {
    roomJSON:=[]byte(`{"code":0,"list":[{"rid":"280474","name":"280474","hid":100100,"srv":9366,"cnt":1,"cap":30,"need_password":0}]}`)
    roomInfo,err:=parseRoomJSON(roomJSON,"280474")
    if err!=nil {t.Fatal(err)}
    if roomInfo.owner!=100100 || roomInfo.roomID!=280474 || roomInfo.srv!=9366 {t.Fatal("bad room correlation")}
    if _,err:=parseRoomJSON(roomJSON,"280475");err==nil {t.Fatal("wrong room accepted")}
    full:=[]byte(`{"code":0,"list":[{"rid":280474,"hid":100100,"srv":9366,"cnt":30,"cap":30}]}`)
    if _,err:=parseRoomJSON(full,"280474");err==nil {t.Fatal("full room accepted")}
    protected:=[]byte(`{"code":0,"list":[{"rid":280474,"hid":100100,"srv":9366,"need_password":1}]}`)
    if _,err:=parseRoomJSON(protected,"280474");err==nil {t.Fatal("password room accepted")}
    nodeJSON:=[]byte(`[{"id":9366,"ip":"42.186.165.240","ports":[19132,19133],"SignalWebPort":8900}]`)
    node,err:=parseServerJSON(nodeJSON,9366)
    if err!=nil {t.Fatal(err)}
    if node.host!="42.186.165.240" || node.port!=19132 || node.signalingPort!=8900 {t.Fatal("bad public node or signaling port")}
    if _,err:=parseServerJSON(nodeJSON,99);err==nil {t.Fatal("wrong server accepted")}
}

func TestLegacyWireMaterialAndDisposal(t *testing.T) {
    token:=[]byte("0123456789abcdef")
    material,err:=derive(token)
    if err!=nil {t.Fatal(err)}
    if len(material.rand)!=16 || len(material.aesRand)!=32 ||
        len(material.encKey)!=32 || len(material.decKey)!=32 {t.Fatal("wrong lengths")}
    encrypted:=material.aesRand
    key:=material.encKey
    material.destroy()
    for _,field:=range [][]byte{encrypted,key}{
        for _,value:=range field {if value!=0 {t.Fatal("key material not cleared")}}
    }
    if _,err:=derive([]byte("not16"));err==nil {t.Fatal("bad token length accepted")}
}

func TestSignalingTicketLegacyCipherAndZeroization(t *testing.T) {
    for _,size := range []int{16,24,32} {
        token:=[]byte(strings.Repeat("K",size))
        seed,ticket,err:=deriveSignalingCredential(token)
        if err!=nil || len(seed)!=16 || len(ticket)!=32 {
            t.Fatal("cannot derive signaling ticket from valid AES session key")
        }
        expected:=make([]byte,32)
        copy(expected,seed)
        for i:=16;i<32;i++ { expected[i]=16 }
        block,err:=aes.NewCipher(token)
        if err!=nil {t.Fatal("invalid AES key")}
        for offset:=0;offset<32;offset+=16 {
            block.Decrypt(expected[offset:offset+16],ticket[offset:offset+16])
        }
        for i:=0;i<16;i++ {if expected[i]!=seed[i] {t.Fatal("wrong signaling ticket nonce")}}
        for i:=16;i<32;i++ {if expected[i]!=16 {t.Fatal("wrong PKCS7 padding")}}
        clear(seed);clear(ticket)
        for _,v:=range ticket {if v!=0 {t.Fatal("signaling secret not cleared")}}
    }
    if _,_,err:=deriveSignalingCredential([]byte("short"));err==nil {
        t.Fatal("invalid ticket AES key length accepted")
    }
}

func TestRoomNodeWithoutSignalingPortDoesNotInventOne(t *testing.T) {
    data:=[]byte(`[{"id":9370,"ip":"42.186.165.240","ports":[19132]}]`)
    node,err:=parseServerJSON(data,9370)
    if err!=nil || node.signalingPort!=0 {t.Fatal("missing signaling port invented")}
    data=[]byte(`[{"id":9370,"ip":"42.186.165.240","ports":[19132],"SignalWebPort":8900}]`)
    node,err=parseServerJSON(data,9370)
    if err!=nil || node.signalingPort!=8900 {t.Fatal("signal node mapping failed")}
}

func TestStatusNeverClaimsMinecraftWorld(t *testing.T) {
    report:=makeReport("SERVER_ROOM_ADMISSION_ACCEPTED","only a fixture")
    report.RoomAdmissionAccepted=true
    out,err:=json.Marshal(report)
    if err!=nil {t.Fatal(err)}
    if bytes.Contains(out,[]byte(`"minecraft_world_joined":true`)) {
        t.Fatal("lobby admission incorrectly marked game connected")
    }
    if Run(nil,Request{AuthorizedByHost:false}).Status!="HOST_CONSENT_REQUIRED" {
        t.Fatal("host permission gate missing")
    }
}
