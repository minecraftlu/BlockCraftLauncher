package main

import (
    "context"
    "errors"
    "io"
    "net"
    "os"
    "sync"
    "time"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BedrockProtocolBridge/internal/gamebridge"
    "github.com/sandertv/gophertunnel/minecraft"
)

// Offline game process modes deliberately have NO live networking or
// NetEase authentication. The host disables authentication only because
// both sides are synthetic tests owned by the same local CI runner.
func runOfflineGame(ctx context.Context, r io.ReadCloser, w io.WriteCloser, host bool) error {
    hello,err:=wire.ReadFrame(r)
    if err!=nil{return err}
    if hello.Kind!=wire.Hello{return wire.ErrMalformed}
    if err:=wire.WriteFrame(w,wire.Frame{Kind:wire.Ready,Payload:hello.Payload});err!=nil{return err}
    conn,err:=gamebridge.NewIPCPacketConn(ctx,r,w)
    if err!=nil{return err}
    defer conn.Close()
    if host{return runLocalGameHost(ctx,conn)}
    return runLocalGameClient(ctx,conn)
}

type offlineGameListener struct {
    channel chan net.Conn
    done chan struct{}
    once sync.Once
}

func (l *offlineGameListener) Accept() (net.Conn,error) {
    select {
    case c:=<-l.channel:return c,nil
    case <-l.done:return nil,net.ErrClosed
    }
}
func (l *offlineGameListener) Close() error {
    l.once.Do(func(){close(l.done)})
    return nil
}
func (*offlineGameListener) Addr() net.Addr {
    return &net.UDPAddr{IP:net.IPv4(127,0,0,1),Port:19132}
}
func (*offlineGameListener) ID() int64 {return 1}
func (*offlineGameListener) PongData([]byte) {}

type offlineGameNetwork struct{ listener minecraft.NetworkListener }
func (*offlineGameNetwork) DialContext(context.Context,string)(net.Conn,error){
    return nil,gamebridge.ErrInvalidTarget
}
func (*offlineGameNetwork) PingContext(context.Context,string)([]byte,error){
    return nil,gamebridge.ErrInvalidTarget
}
func (n *offlineGameNetwork) Listen(string)(minecraft.NetworkListener,error){
    return n.listener,nil
}

func runLocalGameHost(ctx context.Context, conn *gamebridge.IPCPacketConn) error {
    source:=&offlineGameListener{
        channel:make(chan net.Conn,1),
        done:make(chan struct{}),
    }
    source.channel<-&gamebridge.PacketConn{PacketTransport:conn}
    listener,err:=(minecraft.ListenConfig{
        AuthenticationDisabled:true, // Offline CI fixture, NEVER a live mode.
    }).ListenNetwork(&offlineGameNetwork{listener:source},"127.0.0.1:19132")
    if err!=nil{return err}
    defer listener.Close()
    accepted,err:=listener.Accept()
    if err!=nil{return err}
    if accepted==nil{return errors.New("synthetic local host refused a nil connection")}
    defer accepted.Close()
    player,ok:=accepted.(*minecraft.Conn)
    if !ok{return errors.New("invalid local test host player")}
    return player.StartGameContext(ctx,minecraft.GameData{
        WorldName:"BlockCraft two-process offline world",
        EntityUniqueID:17,
        EntityRuntimeID:17,
    })
}

func runLocalGameClient(ctx context.Context, conn *gamebridge.IPCPacketConn) error {
    network,err:=gamebridge.NewAttachedNetwork(conn)
    if err!=nil{return err}
    // Offline JWT is permitted ONLY here, against the synthetic mock host.
    // gamebridge.DialAuthenticated (for real remote rooms) rejects it.
    game,err:=(minecraft.Dialer{}).DialContextNetwork(
        ctx,network,gamebridge.ExistingSessionAddress)
    if err!=nil{return err}
    defer game.Close()
    if err:=game.DoSpawnContext(ctx);err!=nil{return err}
    if game.GameData().WorldName!="BlockCraft two-process offline world" {
        return errors.New("local test StartGame metadata mismatch")
    }
    return nil
}

// Keep the synthetic process bounded even when the orchestrator dies or
// a Bedrock reader/writer is blocked. Never output game packets to stderr.
func runOfflineGameProcess(host bool) error {
    ctx,cancel:=context.WithTimeout(context.Background(),20*time.Second)
    defer cancel()
    return runOfflineGame(ctx,os.Stdin,os.Stdout,host)
}
