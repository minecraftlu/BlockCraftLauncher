// bridgefixture is an OFFLINE-ONLY cross-process integration test.
// It never uses NetEase credentials, room IDs, tokens or external sockets.
package main

import (
    "bytes"
    "context"
    "crypto/rand"
    "errors"
    "flag"
    "fmt"
    "io"
    "os"
    "os/exec"
    "path/filepath"
    "time"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

var errFixture = errors.New("offline bridge fixture failed")

func runOffline(ctx context.Context, worker string) error {
    if ctx==nil||ctx.Err()!=nil||!filepath.IsAbs(worker){return errFixture}
    info,err:=os.Stat(worker)
    if err!=nil||!info.Mode().IsRegular(){return errFixture}
    cmd:=exec.CommandContext(ctx,worker,"-offline-ipc-selftest")
    cmd.Stderr=io.Discard
    stdin,err:=cmd.StdinPipe()
    if err!=nil{return errFixture}
    stdout,err:=cmd.StdoutPipe()
    if err!=nil{return errFixture}
    if err:=cmd.Start();err!=nil{return errFixture}
    defer func(){_ = stdin.Close();_ = stdout.Close();if cmd.ProcessState==nil {_ = cmd.Process.Kill();_ = cmd.Wait()}}()

    nonce:=make([]byte,wire.NonceBytes)
    if _,err:=rand.Read(nonce);err!=nil{return errFixture}
    if err:=wire.WriteFrame(stdin,wire.Frame{Kind:wire.Hello,Payload:nonce});err!=nil{return errFixture}
    ready,err:=wire.ReadFrame(stdout)
    if err!=nil || ready.Kind!=wire.Ready || !bytes.Equal(ready.Payload,nonce){return errFixture}

    // Synthetic packet only; no login, no player identity, no world.
    batch:=[]byte{0x02,0x2a,0x2b}
    if err:=wire.WriteFrame(stdin,wire.Frame{Kind:wire.Data,Payload:batch});err!=nil{return errFixture}
    echoed,err:=wire.ReadFrame(stdout)
    if err!=nil||echoed.Kind!=wire.Data||!bytes.Equal(echoed.Payload,batch){return errFixture}
    if err:=wire.WriteFrame(stdin,wire.Frame{Kind:wire.Stop});err!=nil{return errFixture}
    stopped,err:=wire.ReadFrame(stdout)
    if err!=nil||stopped.Kind!=wire.Stopped{return errFixture}
    _=stdin.Close()
    if err:=cmd.Wait();err!=nil{return errFixture}
    return nil
}

func main() {
    worker:=flag.String("worker","","absolute path to the local offline worker EXE")
    offline:=flag.Bool("offline-ipc-selftest",false,"offline binary pipe test only")
    flag.Parse()
    if !*offline||*worker=="" {
        fmt.Fprintln(os.Stderr,"LOCAL_BRIDGE_OFFLINE_ONLY")
        os.Exit(2)
    }
    ctx,cancel:=context.WithTimeout(context.Background(),12*time.Second)
    defer cancel()
    if err:=runOffline(ctx,*worker);err!=nil {
        fmt.Fprintln(os.Stderr,"LOCAL_BRIDGE_IPC_FAILED")
        os.Exit(1)
    }
    fmt.Println("LOCAL_BRIDGE_IPC_OK_GAME_NOT_JOINED")
}
