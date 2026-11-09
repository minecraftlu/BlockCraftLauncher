// bedrockworker is an OFFLINE-ONLY binary framing fixture.
// It must not be connected to a live NetEase room or given real account data.
package main

import (
    "errors"
    "fmt"
    "io"
    "os"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
    "github.com/sandertv/gophertunnel/minecraft/protocol/packet"
)

type batchReader struct {
    b []byte
    used bool
}

func (*batchReader) BatchHeader() []byte {return nil}
func (*batchReader) DisableEncryption() bool {return true}
func (r *batchReader) Read(b []byte)(int,error){
    p,err:=r.ReadPacket()
    if err!=nil{return 0,err}
    if len(p)>len(b){return 0,io.ErrShortBuffer}
    return copy(b,p),nil
}
func (r *batchReader) ReadPacket() ([]byte,error){
    if r.used {return nil,io.EOF}
    r.used=true
    return r.b,nil
}

// validateBatch decodes ONLY the packet boundaries. This does not validate
// Minecraft login, user identity, game version or NetEase authorization.
func validateBatch(payload []byte) error {
    pk,err:=packet.NewDecoder(&batchReader{b:payload}).Decode()
    if err!=nil{return errors.New("offline game batch invalid")}
    if len(pk)==0{return errors.New("offline game batch empty")}
    return nil
}

func run(r io.Reader,w io.Writer) error {
    hello,err:=wire.ReadFrame(r)
    if err!=nil{return err}
    if hello.Kind!=wire.Hello{return wire.ErrMalformed}
    if err:=wire.WriteFrame(w,wire.Frame{Kind:wire.Ready,Payload:hello.Payload});err!=nil{return err}
    stream:=wire.NewPacketStream(r,w)
    for {
        payload,err:=stream.ReadPacket()
        if errors.Is(err,wire.ErrRemoteStop) {
            return stream.AcknowledgeStop()
        }
        if err!=nil{return err}
        if err:=validateBatch(payload);err!=nil{return err}
        // Still an offline-only echo, now over a persistent, ordered stream.
        // Payloads are never logged or written to disk.
        if err:=stream.SendPacket(payload);err!=nil{return err}
    }
}

func main() {
    if len(os.Args)==3 && os.Args[1]=="-offline-codec-reference" {
        if err:=checkCodecReference(os.Args[2],os.Stdout);err!=nil {
            // Do not echo arguments: the worker never accepts account data.
            fmt.Fprintln(os.Stderr,"OFFLINE_CODEC_REFERENCE_INVALID")
            os.Exit(2)
        }
        return
    }
    if len(os.Args)!=2 {
        fmt.Fprintln(os.Stderr,"OFFLINE_BRIDGE_ONLY")
        os.Exit(2)
    }
    // Each accepted mode is exclusively local, uses no account sessions,
    // and receives binary payloads only through parent-controlled STDIO.
    var err error
    switch os.Args[1] {
    case "-offline-ipc-selftest":
        err=run(os.Stdin,os.Stdout)
    case "-offline-game-host-selftest":
        err=runOfflineGameProcess(true)
    case "-offline-game-client-selftest":
        err=runOfflineGameProcess(false)
    case "-readonly-inbound-ipc":
        // This mode performs no game login, decoding, or network dialing.
        // It consumes only packets forwarded by its owning local process.
        err=runReceiveOnlyPipe(os.Stdin,os.Stdout)
    default:
        fmt.Fprintln(os.Stderr,"OFFLINE_BRIDGE_ONLY")
        os.Exit(2)
    }
    if err!=nil {
        // Do not print error strings or binary payload contents.
        fmt.Fprintln(os.Stderr,"OFFLINE_BRIDGE_FAILED")
        os.Exit(1)
    }
}

