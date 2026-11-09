package main

import (
    "bytes"
    "context"
    "crypto/rand"
    "errors"
    "io"
    "os"
    "os/exec"
    "path/filepath"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab/internal/ipcrelay"
)

// The source intentionally has NO Write method. This test cannot send
// Bedrock game packets to a remote host, even if a worker misbehaves.
type syntheticInbound struct {
    packets [][]byte
    index int
}
func (s *syntheticInbound) ReadPacket()([]byte,error){
    if s.index>=len(s.packets){return nil,io.EOF}
    p:=s.packets[s.index]
    s.index++
    return p,nil
}
func (*syntheticInbound) Close()error{return nil}

// Run the real pinned worker in a separate Windows process and require that
// it acknowledges only the local nonce and shutdown. No payload data ever
// travels from worker to host, and no credentials/network are involved.
func runOfflineInbound(ctx context.Context,workerExecutable string) error {
    if ctx==nil || ctx.Err()!=nil || !filepath.IsAbs(workerExecutable){return errFixture}
    info,err:=os.Stat(workerExecutable)
    if err!=nil || !info.Mode().IsRegular(){return errFixture}
    cmd:=exec.CommandContext(ctx,workerExecutable,"-readonly-inbound-ipc")
    cmd.Stderr=io.Discard
    stdin,err:=cmd.StdinPipe()
    if err!=nil{return errFixture}
    stdout,err:=cmd.StdoutPipe()
    if err!=nil{_ =stdin.Close();return errFixture}
    if err=cmd.Start();err!=nil{return errFixture}
    waited:=false
    defer func(){
        _=stdin.Close()
        _=stdout.Close()
        if !waited {
            if cmd.Process!=nil && cmd.ProcessState==nil {_=cmd.Process.Kill()}
            _=cmd.Wait()
        }
    }()
    nonce:=make([]byte,wire.NonceBytes)
    if _,err=rand.Read(nonce);err!=nil{return errFixture}
    if err=wire.WriteFrame(stdin,wire.Frame{Kind:wire.Hello,Payload:nonce});err!=nil{return errFixture}
    ack,err:=wire.ReadFrame(stdout)
    if err!=nil || ack.Kind!=wire.Ready || !bytes.Equal(ack.Payload,nonce) {return errFixture}

    source:=&syntheticInbound{packets:[][]byte{
        {0x02,0x2a,0x2b},
        bytes.Repeat([]byte{0x7e},64*1024),
        bytes.Repeat([]byte{0x5a},wire.MaxPayload),
    }}
    stats,err:=ipcrelay.ForwardInbound(ctx,source,stdin)
    if !errors.Is(err,io.EOF) || stats.Messages!=3 ||
        stats.Bytes!=uint64(3+64*1024+wire.MaxPayload) {return errFixture}
    stopped,err:=wire.ReadFrame(stdout)
    if err!=nil || stopped.Kind!=wire.Stopped {return errFixture}
    if err=cmd.Wait();err!=nil{return errFixture}
    waited=true
    return nil
}
