package gamebridge

import (
    "bytes"
    "context"
    "errors"
    "io"
    "net"
    "testing"
    "time"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

func pairLocalIPC(ctx context.Context) (*IPCPacketConn,*IPCPacketConn,error) {
    c2sRead,c2sWrite:=io.Pipe()
    s2cRead,s2cWrite:=io.Pipe()
    client,err:=NewIPCPacketConn(ctx,s2cRead,c2sWrite)
    if err!=nil{return nil,nil,err}
    server,err:=NewIPCPacketConn(ctx,c2sRead,s2cWrite)
    if err!=nil{_ =client.Close();return nil,nil,err}
    return client,server,nil
}

func TestBoundedIPCNetConnCarriesCompleteBedrockBatches(t *testing.T) {
    ctx,cancel:=context.WithTimeout(context.Background(),5*time.Second)
    defer cancel()
    client,host,err:=pairLocalIPC(ctx)
    if err!=nil{t.Fatal(err)}
    defer client.Close()
    defer host.Close()

    packets:=[][]byte{{0x02,0x2a,0x2b},bytes.Repeat([]byte{0x73},65535)}
    for _,p:=range packets {
        sent:=make(chan error,1)
        go func(data []byte){
            n,e:=client.Write(data)
            if e==nil && n!=len(data){e=io.ErrShortWrite}
            sent<-e
        }(p)
        received,err:=host.ReadPacket()
        if err!=nil||!bytes.Equal(received,p){
            t.Fatalf("IPC altered game batch boundaries: %v",err)
        }
        if err:=<-sent;err!=nil{t.Fatal(err)}
    }
    if n,err:=client.Write(make([]byte,wire.MaxPayload+1));n!=0||!errors.Is(err,wire.ErrTooLarge){
        t.Fatalf("oversize IPC packet was not rejected: n=%d err=%v",n,err)
    }
    if n,err:=client.Write(nil);n!=0||!errors.Is(err,wire.ErrMalformed){
        t.Fatalf("empty packet accepted: n=%d err=%v",n,err)
    }
}

func TestCancelledIPCConnectionInterruptsBlockedPacketRead(t *testing.T) {
    ctx,cancel:=context.WithCancel(context.Background())
    client,host,err:=pairLocalIPC(ctx)
    if err!=nil{t.Fatal(err)}
    defer client.Close()
    defer host.Close()
    done:=make(chan error,1)
    go func(){_,e:=client.ReadPacket();done<-e}()
    cancel()
    select {
    case err:=<-done:
        if err==nil{t.Fatal("cancelled IPC ReadPacket returned success")}
    case <-time.After(time.Second):
        t.Fatal("cancel did not unblock local Bedrock decoder")
    }
    if _,err:=client.Write([]byte{1});err==nil{
        t.Fatal("closed local IPC transport still wrote game bytes")
    }
}

func TestIPCPacketConnRejectsUnenforcedDeadlinesAndUnownedEndpoints(t *testing.T) {
    if _,err:=NewIPCPacketConn(context.Background(),nil,nil);!errors.Is(err,ErrMissingIPCEndpoint){t.Fatal(err)}
    ctx,cancel:=context.WithCancel(context.Background())
    cancel()
    a,b:=io.Pipe()
    if _,err:=NewIPCPacketConn(ctx,a,b);!errors.Is(err,context.Canceled){t.Fatal(err)}
    ctx2,cancel2:=context.WithCancel(context.Background())
    defer cancel2()
    client,host,err:=pairLocalIPC(ctx2)
    if err!=nil{t.Fatal(err)}
    defer client.Close()
    defer host.Close()
    if !errors.Is(client.SetReadDeadline(time.Now()),ErrIPCDeadlineUnsupported){
        t.Fatal("unsupported local pipe deadline falsely claimed to be enforced")
    }
    if _,ok:=client.LocalAddr().(*net.UDPAddr);!ok{t.Fatal("missing local UDP metadata")}
    if err:=client.Close();err!=nil{t.Fatal(err)}
    if err:=client.Close();err!=nil{t.Fatal("close not idempotent")}
}
