package main

import (
    "bytes"
    "errors"
    "io"
    "testing"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

func TestReceiveOnlyWorkerNeverEchoesGamePayload(t *testing.T) {
    var in,out bytes.Buffer
    nonce:=bytes.Repeat([]byte{0x45},wire.NonceBytes)
    data:=[]byte{0xde,0xad,0xbe,0xef,0xaa,0x55,0x7b,0x7d}
    for _,f:=range []wire.Frame{
        {Kind:wire.Hello,Payload:nonce},
        {Kind:wire.Data,Payload:data},
        {Kind:wire.Data,Payload:[]byte{0x13,0x37}},
    }{
        if err:=wire.WriteFrame(&in,f);err!=nil{t.Fatal(err)}
    }
    if err:=runReceiveOnlyPipe(&in,&out);err!=nil{t.Fatal(err)}
    ready,err:=wire.ReadFrame(&out)
    if err!=nil || ready.Kind!=wire.Ready || !bytes.Equal(ready.Payload,nonce) {
        t.Fatalf("local nonce handshake failed: %v",err)
    }
    ended,err:=wire.ReadFrame(&out)
    if err!=nil || ended.Kind!=wire.Stopped{t.Fatalf("missing bounded worker shutdown: %v",err)}
    if out.Len()!=0 {t.Fatal("worker returned unapproved bytes after shutdown")}
    if bytes.Contains(out.Bytes(),data) {t.Fatal("game bytes leaked back to parent")}
}

func TestReceiveOnlyWorkerRequiresLocalHello(t *testing.T) {
    var in,out bytes.Buffer
    if err:=wire.WriteFrame(&in,wire.Frame{Kind:wire.Data,Payload:[]byte{1}});err!=nil{t.Fatal(err)}
    if err:=runReceiveOnlyPipe(&in,&out);!errors.Is(err,wire.ErrMalformed) {
        t.Fatalf("worker accepted game data before handshake: %v",err)
    }
    if out.Len()!=0 {t.Fatal("worker emitted data for unverified local handshake")}
}

func TestReceiveOnlyWorkerRejectsControlInjection(t *testing.T) {
    var in,out bytes.Buffer
    _=wire.WriteFrame(&in,wire.Frame{Kind:wire.Hello,Payload:make([]byte,wire.NonceBytes)})
    _=wire.WriteFrame(&in,wire.Frame{Kind:wire.Stop})
    if err:=runReceiveOnlyPipe(&in,&out);!errors.Is(err,wire.ErrMalformed) {
        t.Fatalf("worker accepted unsanctioned control frame: %v",err)
    }
}

func TestReceiveOnlyWorkerRejectsTruncatedPayload(t *testing.T) {
    var in,out bytes.Buffer
    _=wire.WriteFrame(&in,wire.Frame{Kind:wire.Hello,Payload:make([]byte,wire.NonceBytes)})
    _=wire.WriteFrame(&in,wire.Frame{Kind:wire.Data,Payload:[]byte{1,2,3}})
    source:=in.Bytes()
    // Remove a byte from the final payload after a complete DATA header.
    if err:=runReceiveOnlyPipe(bytes.NewReader(source[:len(source)-1]),&out);!errors.Is(err,io.ErrUnexpectedEOF){
        t.Fatalf("worker failed to detect truncated frame: %v",err)
    }
}
