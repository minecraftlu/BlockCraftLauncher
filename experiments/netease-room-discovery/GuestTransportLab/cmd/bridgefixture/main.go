// bridgefixture is an OFFLINE-ONLY cross-process integration test.
// It never uses NetEase credentials, room IDs, tokens or external sockets.
package main

import (
    "bytes"
    "context"
    "crypto/rand"
    "encoding/binary"
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

    stream:=wire.NewPacketStream(stdout,stdin)
    // Exercise sustained message boundaries and backpressure with local
    // synthetic batches, including a frame close to the 1 MiB maximum.
    // This never sends game/login data to a real server.
    for _,size:=range []int{2,3,128,1024,10_000,64*1024,wire.MaxPayload-4} {
        data:=make([]byte,size)
        for i:=range data { data[i]=byte(i*31+size) }
        data[0]=0x2a // synthetic game packet ID
        var length [binary.MaxVarintLen64]byte
        n:=binary.PutUvarint(length[:],uint64(len(data)))
        batch:=append(append([]byte(nil),length[:n]...),data...)
        if err:=stream.SendPacket(batch);err!=nil{return errFixture}
        echoed,err:=stream.ReadPacket()
        if err!=nil||!bytes.Equal(echoed,batch){return errFixture}
    }
    if err:=stream.RequestStop();err!=nil{return errFixture}
    if err:=stream.ReadStopped();err!=nil{return errFixture}
    _=stdin.Close()
    if err:=cmd.Wait();err!=nil{return errFixture}
    return nil
}

func main() {
    worker:=flag.String("worker","","absolute path to the local offline worker EXE")
    offline:=flag.Bool("offline-ipc-selftest",false,"offline binary pipe test only")
    fullGame:=flag.Bool("offline-game-ipc-selftest",false,"two isolated local Bedrock game processes")
    inbound:=flag.Bool("offline-inbound-ipc-selftest",false,"synthetic receive-only worker with no host write capability")
    flag.Parse()
    selected:=0
    if *offline {selected++}
    if *fullGame {selected++}
    if *inbound {selected++}
    if *worker=="" || selected!=1 {
        fmt.Fprintln(os.Stderr,"LOCAL_BRIDGE_OFFLINE_ONLY")
        os.Exit(2)
    }
    limit:=12*time.Second
    if *fullGame {limit=35*time.Second}
    ctx,cancel:=context.WithTimeout(context.Background(),limit)
    defer cancel()
    if *fullGame {
        if err:=runOfflineFullGame(ctx,*worker);err!=nil {
            fmt.Fprintln(os.Stderr,"LOCAL_STAGE12_GAME_IPC_FAILED")
            os.Exit(1)
        }
        fmt.Println("LOCAL_STAGE12_GAME_IPC_OK_OFFLINE_GAME_NOT_JOINED")
        return
    }
    if *inbound {
        if err:=runOfflineInbound(ctx,*worker);err!=nil {
            fmt.Fprintln(os.Stderr,"LOCAL_STAGE12_INBOUND_ONLY_IPC_FAILED")
            os.Exit(1)
        }
        fmt.Println("LOCAL_STAGE12_INBOUND_ONLY_IPC_OK_GAME_NOT_JOINED")
        return
    }
    if err:=runOffline(ctx,*worker);err!=nil {
        fmt.Fprintln(os.Stderr,"LOCAL_BRIDGE_IPC_FAILED")
        os.Exit(1)
    }
    fmt.Println("LOCAL_BRIDGE_IPC_OK_GAME_NOT_JOINED")
}
