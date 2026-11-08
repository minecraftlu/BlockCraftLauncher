package joinprobe

import (
    "context"
    "net"
    "testing"
    "time"
)

func TestPassiveWebRTCStabilityWindow(t *testing.T) {
    t.Run("survives_window_without_payload",func(t *testing.T){
        client, host:=net.Pipe()
        defer client.Close()
        defer host.Close()
        start:=time.Now()
        status:=holdTransport(context.Background(),client,30*time.Millisecond)
        if status!=stabilityWindowComplete {
            t.Fatal("a live, idle data connection must survive the observation window")
        }
        if time.Since(start)<25*time.Millisecond {t.Fatal("observation ended prematurely")}
    })
    t.Run("early_remote_close",func(t *testing.T){
        client, host:=net.Pipe()
        defer client.Close()
        defer host.Close()
        go func(){time.Sleep(20*time.Millisecond);_ = host.Close()}()
        start:=time.Now()
        status:=holdTransport(context.Background(),client,300*time.Millisecond)
        if status!=stabilityRemoteClosed {t.Fatal("early remote close was reported stable")}
        if time.Since(start)>240*time.Millisecond {t.Fatal("remote close was not detected promptly")}
    })
    t.Run("explicit_cancellation",func(t *testing.T){
        client, host:=net.Pipe()
        defer client.Close()
        defer host.Close()
        ctx,cancel:=context.WithCancel(context.Background())
        go func(){time.Sleep(20*time.Millisecond);cancel()}()
        if result:=holdTransport(ctx,client,300*time.Millisecond);result!=stabilityCancelled {
            t.Fatal("cancelled transport must never be reported stable")
        }
    })
    t.Run("already_cancelled",func(t *testing.T){
        client,host:=net.Pipe()
        defer client.Close()
        defer host.Close()
        ctx,cancel:=context.WithCancel(context.Background())
        cancel()
        if holdTransport(ctx,client,time.Second)!=stabilityCancelled {
            t.Fatal("already cancelled transport was not rejected")
        }
    })
}
