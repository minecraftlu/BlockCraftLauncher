package joinprobe

import (
    "bytes"
    "context"
    "crypto/rand"
    "errors"
    "io"
    "os"
    "os/exec"
    "path/filepath"
    "strings"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

var ErrReadOnlyWorkerUnavailable=errors.New("stage12: local read-only worker unavailable")
var ErrReadOnlyWorkerProtocol=errors.New("stage12: local read-only worker handshake failed")

// This is a NO-NETWORK, NO-GAME-LOGIN local worker. The parent reads from
// the already authorised NetherNet connection. The worker receives raw
// bytes only over STDIN, and stdout carries ONLY control records.
type readOnlyWorker struct {
    cmd *exec.Cmd
    stdin io.WriteCloser
    stdout io.ReadCloser
    waited bool
}

func validReadOnlyWorkerPath(path string) bool {
    if !filepath.IsAbs(path) || !strings.EqualFold(filepath.Base(path),"Stage12Worker.exe") {
        return false
    }
    info,err:=os.Stat(path)
    return err==nil && info.Mode().IsRegular()
}

func startReadOnlyWorker(ctx context.Context,path string)(*readOnlyWorker,error) {
    if ctx==nil || ctx.Err()!=nil || !validReadOnlyWorkerPath(path) {
        return nil,ErrReadOnlyWorkerUnavailable
    }
    cmd:=exec.CommandContext(ctx,path,"-readonly-inbound-ipc")
    cmd.Stderr=io.Discard
    stdin,err:=cmd.StdinPipe()
    if err!=nil{return nil,ErrReadOnlyWorkerUnavailable}
    stdout,err:=cmd.StdoutPipe()
    if err!=nil{_ =stdin.Close();return nil,ErrReadOnlyWorkerUnavailable}
    if err=cmd.Start();err!=nil{
        _=stdin.Close();_ =stdout.Close()
        return nil,ErrReadOnlyWorkerUnavailable
    }
    worker:=&readOnlyWorker{cmd:cmd,stdin:stdin,stdout:stdout}
    nonce:=make([]byte,wire.NonceBytes)
    if _,err=rand.Read(nonce);err!=nil {worker.abort();return nil,ErrReadOnlyWorkerProtocol}
    if err=wire.WriteFrame(stdin,wire.Frame{Kind:wire.Hello,Payload:nonce});err!=nil {
        worker.abort();return nil,ErrReadOnlyWorkerProtocol
    }
    ack,err:=wire.ReadFrame(stdout)
    if err!=nil || ack.Kind!=wire.Ready || !bytes.Equal(ack.Payload,nonce) {
        worker.abort();return nil,ErrReadOnlyWorkerProtocol
    }
    return worker,nil
}

func (w *readOnlyWorker) consume(data []byte) error {
    if w==nil || w.stdin==nil {return ErrReadOnlyWorkerProtocol}
    return wire.WriteFrame(w.stdin,wire.Frame{Kind:wire.Data,Payload:data})
}

// finish terminates the worker using STDIN EOF and requires a valid Stopped
// response plus a zero process exit. It does NOT indicate game login success.
func (w *readOnlyWorker) finish() error {
    if w==nil{return ErrReadOnlyWorkerProtocol}
    _=w.stdin.Close()
    ack,err:=wire.ReadFrame(w.stdout)
    if err!=nil || ack.Kind!=wire.Stopped {w.abort();return ErrReadOnlyWorkerProtocol}
    if err=w.cmd.Wait();err!=nil {w.waited=true;return ErrReadOnlyWorkerProtocol}
    w.waited=true
    return nil
}

func (w *readOnlyWorker) abort(){
    if w==nil{return}
    if w.stdin!=nil{_ =w.stdin.Close()}
    if w.stdout!=nil{_ =w.stdout.Close()}
    if w.cmd!=nil && !w.waited {
        if w.cmd.Process!=nil && w.cmd.ProcessState==nil {_=w.cmd.Process.Kill()}
        _=w.cmd.Wait()
        w.waited=true
    }
}
