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
)

type localGameWorker struct {
    cmd *exec.Cmd
    in io.WriteCloser
    out io.ReadCloser
    packets *wire.PacketStream
    waited bool
}

func startLocalGameWorker(ctx context.Context, executable, mode string)(*localGameWorker,error){
    cmd:=exec.CommandContext(ctx,executable,mode)
    cmd.Stderr=io.Discard
    stdin,err:=cmd.StdinPipe()
    if err!=nil{return nil,err}
    stdout,err:=cmd.StdoutPipe()
    if err!=nil{_ =stdin.Close();return nil,err}
    if err:=cmd.Start();err!=nil{_ =stdin.Close();_ =stdout.Close();return nil,err}
    worker:=&localGameWorker{cmd:cmd,in:stdin,out:stdout}
    nonce:=make([]byte,wire.NonceBytes)
    if _,err=rand.Read(nonce);err!=nil{worker.stop();return nil,err}
    if err=wire.WriteFrame(stdin,wire.Frame{Kind:wire.Hello,Payload:nonce});err!=nil {
        worker.stop();return nil,err
    }
    ready,err:=wire.ReadFrame(stdout)
    if err!=nil || ready.Kind!=wire.Ready || !bytes.Equal(ready.Payload,nonce) {
        worker.stop();return nil,errFixture
    }
    worker.packets=wire.NewPacketStream(stdout,stdin)
    return worker,nil
}

func (p *localGameWorker) stop(){
    if p==nil{return}
    _=p.in.Close()
    _=p.out.Close()
    if p.cmd!=nil {
        if p.cmd.Process!=nil && p.cmd.ProcessState==nil {_ =p.cmd.Process.Kill()}
        if !p.waited { _ =p.cmd.Wait();p.waited=true }
    }
}

// Relay only complete framed game packets. The parent does not import the
// Bedrock protocol library, inspect credentials or modify packet content.
func forwardSyntheticGame(dst,src *wire.PacketStream) error {
    for {
        packet,err:=src.ReadPacket()
        if err!=nil {return err}
        if err:=dst.SendPacket(packet);err!=nil{return err}
    }
}

func runOfflineFullGame(ctx context.Context, workerExecutable string) error {
    if ctx==nil || ctx.Err()!=nil || !filepath.IsAbs(workerExecutable) {return errFixture}
    info,err:=os.Stat(workerExecutable)
    if err!=nil || !info.Mode().IsRegular() {return errFixture}

    // Both workers are the SAME pinned, audited Bedrock executable.
    // Only their OFFLINE modes differ. They communicate solely through this
    // parent process, not through socket/network transport.
    host,err:=startLocalGameWorker(ctx,workerExecutable,"-offline-game-host-selftest")
    if err!=nil{return errFixture}
    defer host.stop()
    client,err:=startLocalGameWorker(ctx,workerExecutable,"-offline-game-client-selftest")
    if err!=nil{return errFixture}
    defer client.stop()

    results:=make(chan error,2)
    go func(){results<-forwardSyntheticGame(host.packets,client.packets)}()
    go func(){results<-forwardSyntheticGame(client.packets,host.packets)}()

    // The Bedrock protocol workers exit normally only after the real
    // gophertunnel NetworkSettings/Login/StartGame/PlayerSpawn flow finishes.
    // A raw EOF alone never means that Minecraft login succeeded: require
    // zero exit codes from BOTH isolated game processes.
    for i:=0;i<2;i++ {
        select {
        case e:=<-results:
            if !errors.Is(e,io.EOF) {return errFixture}
        case <-ctx.Done():
            return errFixture
        }
    }
    _=host.in.Close()
    _=client.in.Close()
    if err=host.cmd.Wait();err!=nil{return errFixture}
    host.waited=true
    if err=client.cmd.Wait();err!=nil{return errFixture}
    client.waited=true
    return nil
}
