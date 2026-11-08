// Package ipcrelay connects an already-owned, packet-oriented transport to
// the local bounded BridgeWire stream. It never performs room discovery,
// authentication, network dialing or game protocol login.
package ipcrelay

import (
    "context"
    "errors"
    "io"
    "sync"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

// Transport must provide full packet reads and network writes. A real
// NetherNet Conn implements this interface; tests use ONLY memory fakes.
type Transport interface {
    ReadPacket() ([]byte,error)
    Write([]byte)(int,error)
    Close() error
}

var ErrInvalidEndpoints = errors.New("ipcrelay: missing transport or local pipe")

// Run relays complete packets in both directions with a single in-flight
// packet per direction. Call only after completing the local Hello/Ready
// handshake. The caller is responsible for providing an authorised session;
// this package does not obtain or manufacture permissions.
//
// All three endpoints must be independently closable to unblock reads on
// cancellation. No payload or account metadata is logged.
func Run(ctx context.Context, transport Transport, stdin io.ReadCloser, stdout io.WriteCloser) error {
    if ctx==nil || transport==nil || stdin==nil || stdout==nil{return ErrInvalidEndpoints}
    if err:=ctx.Err();err!=nil{return err}
    stream:=wire.NewPacketStream(stdin,stdout)
    var closeOnce sync.Once
    closeAll:=func(){
        closeOnce.Do(func(){
            _=transport.Close()
            _=stdin.Close()
            _=stdout.Close()
        })
    }
    done:=make(chan struct{})
    var wg sync.WaitGroup
    wg.Add(2)
    results:=make(chan error,2)

    go func(){
        defer wg.Done()
        for {
            p,err:=transport.ReadPacket()
            if err!=nil{results<-err;return}
            if err=stream.SendPacket(p);err!=nil{results<-err;return}
        }
    }()
    go func(){
        defer wg.Done()
        for {
            p,err:=stream.ReadPacket()
            if errors.Is(err,wire.ErrRemoteStop) {
                // Reply to an explicit worker shutdown, not to raw EOF.
                results<-stream.AcknowledgeStop()
                return
            }
            if err!=nil{results<-err;return}
            n,err:=transport.Write(p)
            if err!=nil{results<-err;return}
            if n!=len(p){results<-io.ErrShortWrite;return}
        }
    }()

    go func(){
        select {
        case <-ctx.Done():closeAll()
        case <-done:
        }
    }()
    var result error
    select {
    case result=<-results:
    case <-ctx.Done():result=ctx.Err()
    }
    closeAll()
    close(done)
    wg.Wait()
    if ctx.Err()!=nil{return ctx.Err()}
    return result
}
