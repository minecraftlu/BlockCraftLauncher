package ipcrelay

import (
    "context"
    "errors"
    "io"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

// InboundSource intentionally omits Write. A real NetherNet source is passed
// through this narrow interface only after the server has admitted the user.
// No worker output can ever be routed back to the mobile game host.
type InboundSource interface {
    ReadPacket() ([]byte,error)
    Close() error
}

type InboundStats struct {
    Messages uint64
    Bytes uint64
}

const (
    MaxInboundMessages = uint64(256)
    MaxInboundBytes = uint64(8*1024*1024)
)

var (
    ErrInboundLimit = errors.New("ipcrelay: read-only observation limit reached")
    ErrInboundStreamMissing = errors.New("ipcrelay: missing read-only source or pipe")
)

// ForwardInbound copies only inbound, fully reassembled application messages
// to the locally owned worker's STDIN (never in the other direction). The
// caller controls duration using ctx and must treat EOF as disconnection,
// not success entering the game. The worker gets a terminal EOF when done.
//
// To avoid retaining credentials or game bytes, every consumed message is
// cleared after framing; no payload is written to logs or disk.
func ForwardInbound(ctx context.Context, source InboundSource, local io.WriteCloser) (InboundStats,error) {
    var stats InboundStats
    if ctx==nil || source==nil || local==nil {return stats,ErrInboundStreamMissing}
    defer source.Close()
    defer local.Close()
    if err:=ctx.Err();err!=nil{return stats,err}
    // Context cancellation must release an idle NetherNet ReadPacket() and
    // an output write blocked on a worker that died unexpectedly.
    stopped:=context.AfterFunc(ctx,func(){
        _=source.Close()
        _=local.Close()
    })
    defer stopped()
    for {
        if err:=ctx.Err();err!=nil{return stats,err}
        packet,err:=source.ReadPacket()
        if err!=nil {
            if ctx.Err()!=nil{return stats,ctx.Err()}
            return stats,err
        }
        size:=len(packet)
        if size==0 {
            clearBytes(packet)
            continue
        }
        if size>wire.MaxPayload || stats.Messages>=MaxInboundMessages ||
            uint64(size)>MaxInboundBytes-stats.Bytes {
            clearBytes(packet)
            return stats,ErrInboundLimit
        }
        err=wire.WriteFrame(local,wire.Frame{Kind:wire.Data,Payload:packet})
        clearBytes(packet)
        if err!=nil {
            if ctx.Err()!=nil{return stats,ctx.Err()}
            return stats,err
        }
        stats.Messages++
        stats.Bytes+=uint64(size)
    }
}

func clearBytes(p []byte) {
    for i:=range p {p[i]=0}
}
