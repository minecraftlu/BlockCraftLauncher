package main

import (
    "errors"
    "io"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

// runReceiveOnlyPipe consumes locally forwarded, already reassembled
// NetherNet packets. No Bedrock Login is attempted: the current PE game
// protocol version and game identity are unverified. There are no sockets,
// outbound game packets, cookies, accounts, token paths or data logs here.
//
// The parent authenticates only the LOCAL worker process using an ephemeral
// nonce. This handshake is never evidence of server admission.
func runReceiveOnlyPipe(input io.Reader, output io.Writer) error {
    hello,err:=wire.ReadFrame(input)
    if err!=nil{return err}
    if hello.Kind!=wire.Hello{return wire.ErrMalformed}
    if err:=wire.WriteFrame(output,wire.Frame{Kind:wire.Ready,Payload:hello.Payload});err!=nil{return err}
    var messages uint64
    var bytesSeen uint64
    const maxMessages=uint64(256)
    const maxBytes=uint64(8*1024*1024)
    for {
        frame,err:=wire.ReadFrame(input)
        if errors.Is(err,io.EOF) {
            // The owning process closed STDIN. Return only a fixed control
            // ack; never echo any received bytes back to the parent or host.
            return wire.WriteFrame(output,wire.Frame{Kind:wire.Stopped})
        }
        if err!=nil{return err}
        if frame.Kind!=wire.Data{return wire.ErrMalformed}
        n:=uint64(len(frame.Payload))
        if messages>=maxMessages || n>maxBytes-bytesSeen {
            for i:=range frame.Payload {frame.Payload[i]=0}
            return wire.ErrTooLarge
        }
        messages++
        bytesSeen+=n
        // Payload intentionally remains opaque until the real game's
        // version/identity has been verified independently.
        for i:=range frame.Payload {frame.Payload[i]=0}
    }
}
