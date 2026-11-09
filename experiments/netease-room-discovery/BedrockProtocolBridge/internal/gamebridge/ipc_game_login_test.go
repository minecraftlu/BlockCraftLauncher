package gamebridge

import (
    "context"
    "errors"
    "log/slog"
    "net"
    "testing"
    "time"

    "github.com/sandertv/gophertunnel/minecraft"
)

// This is a complete real gophertunnel Bedrock client login across the
// EXACT framed IPC net.Conn used by the two-process bridge. Both endpoints
// are still in-memory fixtures; authentication is disabled ONLY on this
// synthetic host. No NetEase service is contacted.
func TestBedrockStartGameAndSpawnOverFramedIPCTransport(t *testing.T) {
    ctx,cancel:=context.WithTimeout(context.Background(),12*time.Second)
    defer cancel()
    clientPipe,hostPipe,err:=pairLocalIPC(ctx)
    if err!=nil{t.Fatal(err)}
    defer clientPipe.Close()
    defer hostPipe.Close()
    var logs safeFixtureLog
    logger:=slog.New(slog.NewTextHandler(&logs,nil))

    host:=&gameHostListener{conn:make(chan net.Conn,1),done:make(chan struct{})}
    host.conn<-&PacketConn{PacketTransport:hostPipe}
    listener,err:=(minecraft.ListenConfig{
        AuthenticationDisabled:true,
        ErrorLog:logger,
    }).ListenNetwork(&localHostNetwork{listener:host},"127.0.0.1:19132")
    if err!=nil{t.Fatalf("local fixture host init: %v",err)}
    defer listener.Close()

    accepted:=make(chan error,1)
    spawned:=make(chan error,1)
    releaseHost:=make(chan struct{})
    defer close(releaseHost)
    go func() {
        conn,e:=listener.Accept()
        if e==nil && conn==nil{e=errors.New("nil local host connection")}
        accepted<-e
        if e==nil && conn!=nil {
            player,ok:=conn.(*minecraft.Conn)
            if !ok{
                spawned<-errors.New("local test host returned wrong connection type")
            } else {
                spawned<-player.StartGameContext(ctx,minecraft.GameData{
                    WorldName:"BlockCraft IPC offline world",
                    EntityUniqueID:100,
                    EntityRuntimeID:100,
                })
            }
        }
        <-releaseHost
        if conn!=nil{_ =conn.Close()}
    }()

    attached,err:=NewAttachedNetwork(clientPipe)
    if err!=nil{t.Fatal(err)}
    client,err:=(minecraft.Dialer{ErrorLog:logger}).DialContextNetwork(
        ctx,attached,ExistingSessionAddress)
    if err!=nil{
        t.Fatalf("local Bedrock IPC login failed: %v; synthetic trace: %s",err,logs.String())
    }
    defer client.Close()
    select {
    case e:=<-accepted:
        if e!=nil{t.Fatalf("synthetic host rejected game login: %v",e)}
    case <-ctx.Done():t.Fatal("mock host never accepted framed IPC Bedrock login")
    }
    if err:=client.DoSpawnContext(ctx);err!=nil{
        t.Fatalf("local world spawn over IPC failed: %v",err)
    }
    select {
    case e:=<-spawned:
        if e!=nil{t.Fatalf("local host world spawn failed: %v",e)}
    case <-ctx.Done():t.Fatal("local host spawn timed out")
    }
    if got:=client.GameData().WorldName;got!="BlockCraft IPC offline world"{
        t.Fatalf("local IPC StartGame metadata mismatch: %q",got)
    }
}

func TestFramedIPCGameLoginCannotFallbackToOfflineIdentity(t *testing.T) {
    ctx,cancel:=context.WithTimeout(context.Background(),time.Second)
    defer cancel()
    client,host,err:=pairLocalIPC(ctx)
    if err!=nil{t.Fatal(err)}
    defer client.Close()
    defer host.Close()
    _,err=DialAuthenticated(ctx,client,minecraft.Dialer{})
    if !errors.Is(err,ErrNoGameIdentity){
        t.Fatalf("unverified official game identity unexpectedly accepted: %v",err)
    }
}
