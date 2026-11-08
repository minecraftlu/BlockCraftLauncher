package ipcrelay

import (
    "bytes"
    "context"
    "errors"
    "io"
    "sync"
    "testing"
    "time"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

type fakeTransport struct {
    incoming chan []byte
    outgoing chan []byte
    done chan struct{}
    once sync.Once
}

func newFakeTransport() *fakeTransport {
    return &fakeTransport{
        incoming:make(chan []byte,4),
        outgoing:make(chan []byte,4),
        done:make(chan struct{}),
    }
}
func (t *fakeTransport) ReadPacket() ([]byte,error) {
    select {
    case b:=<-t.incoming:return append([]byte(nil),b...),nil
    case <-t.done:return nil,io.EOF
    }
}
func (t *fakeTransport) Write(b []byte)(int,error) {
    select{
    case t.outgoing<-append([]byte(nil),b...):return len(b),nil
    case <-t.done:return 0,io.ErrClosedPipe
    }
}
func (t *fakeTransport) Close() error {t.once.Do(func(){close(t.done)});return nil}

type fixture struct {
    worker *wire.PacketStream
    source *fakeTransport
    cancel context.CancelFunc
    result chan error
    workerIn *io.PipeReader
    workerOut *io.PipeWriter
}
func startFixture(t *testing.T) *fixture {
    t.Helper()
    workIn, guestOut:=io.Pipe()
    guestIn, workOut:=io.Pipe()
    ctx,cancel:=context.WithTimeout(context.Background(),3*time.Second)
    game:=newFakeTransport()
    result:=make(chan error,1)
    go func(){result<-Run(ctx,game,guestIn,guestOut)}()
    f:=&fixture{
        worker:wire.NewPacketStream(workIn,workOut),
        source:game,cancel:cancel,result:result,workerIn:workIn,workerOut:workOut,
    }
    t.Cleanup(func(){
        cancel()
        _=workIn.Close()
        _=workOut.Close()
        select {
        case <-result:
        case <-time.After(time.Second):t.Error("relay failed to stop on cancellation")
        }
    })
    return f
}

func TestBidirectionalRelayPreservesDatagramBoundaries(t *testing.T) {
    f:=startFixture(t)
    packets:=[][]byte{{0x02,0x2a,0x2b},bytes.Repeat([]byte{0x73},wire.MaxPayload)}
    for _,p:=range packets {
        f.source.incoming<-p
        upstream,err:=f.worker.ReadPacket()
        if err!=nil||!bytes.Equal(upstream,p){t.Fatalf("upstream lost frame: %v",err)}
        if err:=f.worker.SendPacket(upstream);err!=nil{t.Fatal(err)}
        select{
        case got:=<-f.source.outgoing:
            if !bytes.Equal(got,p){t.Fatal("downstream lost frame bytes")}
        case <-time.After(time.Second):t.Fatal("downstream relay stalled")
        }
    }
}

func TestCancellationClosesAllEndpoints(t *testing.T) {
    f:=startFixture(t)
    f.cancel()
    select {
    case err:=<-f.result:
        if !errors.Is(err,context.Canceled){t.Fatalf("expected cancellation, got %v",err)}
        // Preserve test-cleanup's expectation that exactly one result is read.
        f.result<-err
    case <-time.After(time.Second):t.Fatal("relay didn't terminate after cancel")
    }
    select{
    case <-f.source.done:
    default:t.Fatal("transport was not closed")
    }
}

func TestOversizeTransportPacketFailsClosed(t *testing.T) {
    f:=startFixture(t)
    f.source.incoming<-make([]byte,wire.MaxPayload+1)
    select {
    case err:=<-f.result:
        if !errors.Is(err,wire.ErrTooLarge){t.Fatalf("oversize not rejected: %v",err)}
        f.result<-err
    case <-time.After(time.Second):t.Fatal("oversize packet not rejected promptly")
    }
}

func TestWorkerUnexpectedEOFIsNotGracefulStop(t *testing.T) {
    f:=startFixture(t)
    _=f.workerOut.Close()
    select{
    case err:=<-f.result:
        if !errors.Is(err,io.EOF){t.Fatalf("unexpected EOF silently treated as graceful: %v",err)}
        f.result<-err
    case <-time.After(time.Second):t.Fatal("relay didn't detect worker death")
    }
}

func TestMissingOrCancelledEndpointsRejected(t *testing.T) {
    game:=newFakeTransport()
    if err:=Run(context.Background(),nil,nil,nil);!errors.Is(err,ErrInvalidEndpoints){t.Fatal(err)}
    ctx,cancel:=context.WithCancel(context.Background())
    cancel()
    r,w:=io.Pipe()
    defer r.Close()
    defer w.Close()
    if err:=Run(ctx,game,r,w);!errors.Is(err,context.Canceled){t.Fatal(err)}
}
