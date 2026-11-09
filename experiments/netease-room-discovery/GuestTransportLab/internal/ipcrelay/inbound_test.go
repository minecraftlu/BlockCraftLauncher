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

// The fake source intentionally exposes NO Write method. If ForwardInbound
// attempts to send any game bytes to the host, these tests cannot compile.
type inboundFixture struct {
    incoming chan []byte
    done chan struct{}
    once sync.Once
}

func (s *inboundFixture) ReadPacket() ([]byte,error) {
    select {
    case p:=<-s.incoming:
        return p,nil
    case <-s.done:
        return nil,io.EOF
    }
}
func (s *inboundFixture) Close() error {
    s.once.Do(func(){close(s.done)})
    return nil
}
func makeInboundFixture() *inboundFixture {
    return &inboundFixture{incoming:make(chan []byte,4),done:make(chan struct{})}
}

func TestInboundBridgeOnlySendsFramedDataToLocalWorker(t *testing.T) {
    source:=makeInboundFixture()
    raw:=[][]byte{[]byte{0x02,0x2a,0x2b},bytes.Repeat([]byte{0x66},64*1024)}
    expected:=make([][]byte,len(raw))
    for i,p:=range raw {
        expected[i]=append([]byte(nil),p...)
        source.incoming<-p
    }
    reader,writer:=io.Pipe()
    ctx,cancel:=context.WithTimeout(context.Background(),3*time.Second)
    defer cancel()
    result:=make(chan struct{
        stats InboundStats
        err error
    },1)
    go func(){
        stats,err:=ForwardInbound(ctx,source,writer)
        result<-struct{stats InboundStats;err error}{stats,err}
    }()
    for _,want:=range expected {
        frame,err:=wire.ReadFrame(reader)
        if err!=nil{t.Fatal(err)}
        if frame.Kind!=wire.Data || !bytes.Equal(frame.Payload,want){
            t.Fatal("inbound game bytes altered or wrong message boundary")
        }
    }
    _=source.Close()
    select{
    case res:=<-result:
        if !errors.Is(res.err,io.EOF) || res.stats.Messages!=2 ||
            res.stats.Bytes!=uint64(3+64*1024) {
            t.Fatalf("invalid read-only stats or EOF: %v %+v",res.err,res.stats)
        }
    case <-time.After(time.Second):t.Fatal("inbound did not terminate on source EOF")
    }
    _=reader.Close()
}

func TestInboundBridgeLimitsAndClearsPayloads(t *testing.T) {
    source:=makeInboundFixture()
    bad:=bytes.Repeat([]byte{0x42},wire.MaxPayload+1)
    source.incoming<-bad
    ctx,cancel:=context.WithTimeout(context.Background(),3*time.Second)
    defer cancel()
    stats,err:=ForwardInbound(ctx,source,&bytesWriteCloser{})
    if !errors.Is(err,ErrInboundLimit) || stats.Messages!=0 {
        t.Fatalf("oversized host message accepted: %v",err)
    }
    for _,v:=range bad {if v!=0{t.Fatal("buffer not cleared after rejection")}}
    select{
    case <-source.done:
    default:t.Fatal("source not released after over-limit message")
    }
}

type bytesWriteCloser struct{bytes.Buffer}
func (*bytesWriteCloser) Close()error{return nil}

func TestInboundCancellationUnblocksIdleReader(t *testing.T) {
    source:=makeInboundFixture()
    ctx,cancel:=context.WithCancel(context.Background())
    done:=make(chan error,1)
    go func(){
        _,err:=ForwardInbound(ctx,source,&bytesWriteCloser{})
        done<-err
    }()
    cancel()
    select{
    case err:=<-done:
        if !errors.Is(err,context.Canceled){t.Fatal(err)}
    case <-time.After(time.Second):t.Fatal("idle inbound relay did not exit")
    }
    select {
    case <-source.done:
    default:t.Fatal("inbound source remained open after cancellation")
    }
}

func TestInboundCancellationInterruptsBlockedWorkerWrite(t *testing.T) {
    source:=makeInboundFixture()
    source.incoming<-[]byte{1,2,3}
    r,w:=io.Pipe() // no peer reads, so WriteFrame cannot complete
    defer r.Close()
    ctx,cancel:=context.WithCancel(context.Background())
    done:=make(chan error,1)
    go func(){
        _,err:=ForwardInbound(ctx,source,w)
        done<-err
    }()
    cancel()
    select{
    case err:=<-done:
        if !errors.Is(err,context.Canceled){t.Fatal(err)}
    case <-time.After(time.Second):t.Fatal("blocked IPC worker write was not cancelled")
    }
}

func TestInboundRejectsMissingEndpoints(t *testing.T) {
    if _,err:=ForwardInbound(nil,nil,nil);!errors.Is(err,ErrInboundStreamMissing){t.Fatal(err)}
}
