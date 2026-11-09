package main

import (
    "bytes"
    "errors"
    "testing"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

func TestOfflineIPCRoundTripPacketAndShutdown(t *testing.T) {
    var input,output bytes.Buffer
    nonce:=bytes.Repeat([]byte{0x51},wire.NonceBytes)
    batch:=[]byte{0x02,0x2a,0x2b} // synthetic headerless Bedrock batch
    for _,f:=range []wire.Frame{
        {Kind:wire.Hello,Payload:nonce},
        {Kind:wire.Data,Payload:batch},
        {Kind:wire.Stop},
    }{
        if err:=wire.WriteFrame(&input,f);err!=nil{t.Fatal(err)}
    }
    if err:=run(&input,&output);err!=nil{t.Fatal(err)}
    for _,want:=range []wire.Frame{
        {Kind:wire.Ready,Payload:nonce},
        {Kind:wire.Data,Payload:batch},
        {Kind:wire.Stopped},
    }{
        got,err:=wire.ReadFrame(&output)
        if err!=nil||got.Kind!=want.Kind||!bytes.Equal(got.Payload,want.Payload){
            t.Fatalf("bad local worker response: %v",err)
        }
    }
    if output.Len()!=0{t.Fatal("unexpected output after shutdown")}
}

func TestOfflineIPCRejectsMalformedGameBatch(t *testing.T) {
    var input,output bytes.Buffer
    nonce:=make([]byte,wire.NonceBytes)
    _=wire.WriteFrame(&input,wire.Frame{Kind:wire.Hello,Payload:nonce})
    _=wire.WriteFrame(&input,wire.Frame{Kind:wire.Data,Payload:[]byte{0x05,0x2a}})
    if err:=run(&input,&output);err==nil{t.Fatal("truncated Bedrock batch accepted")}
    response,err:=wire.ReadFrame(&output)
    if err!=nil || response.Kind!=wire.Ready{t.Fatal("no initial ready frame")}
    if output.Len()!=0{t.Fatal("worker echoed invalid game bytes")}
}

func TestOfflineIPCRejectsGameDataBeforeHello(t *testing.T) {
    var input,output bytes.Buffer
    _=wire.WriteFrame(&input,wire.Frame{Kind:wire.Data,Payload:[]byte{0x02,0x2a,0x2b}})
    if err:=run(&input,&output);!errors.Is(err,wire.ErrMalformed){t.Fatalf("want handshake failure, got %v",err)}
    if output.Len()>0{t.Fatal("sent game data before authorizing local protocol handshake")}
}
