package bridgewire

import (
    "bytes"
    "errors"
    "io"
    "sync"
    "testing"
)

func TestPacketStreamPreservesBoundariesAndStop(t *testing.T) {
    var outbound bytes.Buffer
    stream:=NewPacketStream(nil,&outbound)
    for _,packet:=range [][]byte{{0x02,0x2a,0x2b},bytes.Repeat([]byte{0x72},65535)}{
        if err:=stream.SendPacket(packet);err!=nil{t.Fatal(err)}
    }
    if err:=stream.RequestStop();err!=nil{t.Fatal(err)}
    if err:=stream.SendPacket([]byte{1});!errors.Is(err,ErrSendClosed){t.Fatal(err)}
    if err:=stream.RequestStop();!errors.Is(err,ErrSendClosed){t.Fatal(err)}
    receive:=NewPacketStream(&outbound,nil)
    first,err:=receive.ReadPacket()
    if err!=nil||!bytes.Equal(first,[]byte{0x02,0x2a,0x2b}){t.Fatal(err)}
    second,err:=receive.ReadPacket()
    if err!=nil||len(second)!=65535{t.Fatal(err)}
    if _,err=receive.ReadPacket();!errors.Is(err,ErrRemoteStop){t.Fatal(err)}
    if _,err=receive.ReadPacket();!errors.Is(err,io.EOF){t.Fatal(err)}
}

func TestPacketStreamRejectsWrongControlAndUnexpectedEOF(t *testing.T) {
    var b bytes.Buffer
    if err:=WriteFrame(&b,Frame{Kind:Ready,Payload:make([]byte,NonceBytes)});err!=nil{t.Fatal(err)}
    if _,err:=NewPacketStream(&b,nil).ReadPacket();!errors.Is(err,ErrMalformed){t.Fatal(err)}
    if _,err:=NewPacketStream(&b,nil).ReadPacket();!errors.Is(err,io.EOF){t.Fatal(err)}
}

func TestPacketStreamConcurrentWritersCannotInterleave(t *testing.T) {
    var b bytes.Buffer
    s:=NewPacketStream(nil,&b)
    const count=80
    var wg sync.WaitGroup
    errs:=make(chan error,count)
    for i:=0;i<count;i++{
        wg.Add(1)
        go func(index int){
            defer wg.Done()
            errs<-s.SendPacket(bytes.Repeat([]byte{byte(index)},100))
        }(i)
    }
    wg.Wait()
    close(errs)
    for err:=range errs{if err!=nil{t.Fatal(err)}}
    receiver:=NewPacketStream(&b,nil)
    seen:=make(map[byte]bool)
    for i:=0;i<count;i++{
        p,err:=receiver.ReadPacket()
        if err!=nil||len(p)!=100{t.Fatalf("packet %d: %v",i,err)}
        for _,c:=range p{if c!=p[0]{t.Fatal("concurrent frame bytes interleaved")}}
        if seen[p[0]]{t.Fatal("duplicate packet")}
        seen[p[0]]=true
    }
    if len(seen)!=count{t.Fatal("missing packets")}
}

func TestPacketStreamMalformedSendDoesNotPoisonConnection(t *testing.T) {
    var b bytes.Buffer
    s:=NewPacketStream(nil,&b)
    if err:=s.SendPacket(nil);!errors.Is(err,ErrMalformed){t.Fatal(err)}
    if err:=s.SendPacket(make([]byte,MaxPayload+1));!errors.Is(err,ErrTooLarge){t.Fatal(err)}
    if err:=s.SendPacket([]byte{0x55});err!=nil{t.Fatal(err)}
    p,err:=NewPacketStream(&b,nil).ReadPacket()
    if err!=nil||!bytes.Equal(p,[]byte{0x55}){t.Fatal(err)}
}

func TestPacketStreamGracefulAck(t *testing.T) {
    var b bytes.Buffer
    p:=NewPacketStream(nil,&b)
    if err:=p.AcknowledgeStop();err!=nil{t.Fatal(err)}
    if err:=NewPacketStream(&b,nil).ReadStopped();err!=nil{t.Fatal(err)}
    if err:=p.AcknowledgeStop();!errors.Is(err,ErrSendClosed){t.Fatal(err)}
}
