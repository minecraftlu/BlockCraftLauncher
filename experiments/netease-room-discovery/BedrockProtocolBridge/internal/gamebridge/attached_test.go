package gamebridge

import (
    "bytes"
    "context"
    "errors"
    "net"
    "testing"
    "time"

    "github.com/sandertv/gophertunnel/minecraft"
    "github.com/sandertv/gophertunnel/minecraft/protocol/packet"
)

// Local in-memory transport. No NetEase account, room or external network.
type memoryPacketConn struct {
    net.Conn
    frames chan []byte
}

func (c *memoryPacketConn) ReadPacket() ([]byte, error) {
    select {
    case p := <-c.frames:
        return p, nil
    case <-time.After(2 * time.Second):
        return nil, errors.New("local fixture timeout")
    }
}

func TestBedrockFramingMatchesHeaderlessNetherNet(t *testing.T) {
    local, remote := net.Pipe()
    defer local.Close()
    defer remote.Close()
    transport := &memoryPacketConn{Conn: local, frames: make(chan []byte, 1)}
    wrapped := &PacketConn{PacketTransport: transport}

    if wrapped.BatchHeader() != nil || !wrapped.DisableEncryption() {
        t.Fatal("NetherNet framing/encryption capability changed")
    }
    var sent = make(chan error, 1)
    go func() { sent <- packet.NewEncoder(wrapped).Encode([][]byte{{0x2a, 0x2b}}) }()
    actual := make([]byte, 16)
    n, err := remote.Read(actual)
    if err != nil { t.Fatal(err) }
    // 0x02 is the varuint length of the game packet. No RakNet 0xfe header.
    if !bytes.Equal(actual[:n], []byte{0x02, 0x2a, 0x2b}) {
        t.Fatalf("unexpected NetherNet game framing: %x", actual[:n])
    }
    if err = <-sent; err != nil { t.Fatal(err) }

    transport.frames <- []byte{0x02, 0x2a, 0x2b}
    decoded, err := packet.NewDecoder(wrapped).Decode()
    if err != nil || len(decoded) != 1 || !bytes.Equal(decoded[0], []byte{0x2a, 0x2b}) {
        t.Fatalf("packet decoder lost framing or read boundary: %v", err)
    }
}

func TestAttachedNetworkRejectsRedialAndClosesOnCancellation(t *testing.T) {
    local, remote := net.Pipe()
    defer local.Close()
    defer remote.Close()
    attached, err := NewAttachedNetwork(&memoryPacketConn{Conn: local})
    if err != nil { t.Fatal(err) }
    ctx, cancel := context.WithCancel(context.Background())
    defer cancel()
    if _, err := attached.DialContext(ctx, "other-host"); !errors.Is(err, ErrInvalidTarget) {
        t.Fatal("arbitrary host redial was not rejected")
    }
    connected, err := attached.DialContext(ctx, ExistingSessionAddress)
    if err != nil || connected == nil { t.Fatalf("cannot claim authorised session: %v", err) }
    if _, err := attached.DialContext(ctx, ExistingSessionAddress); !errors.Is(err, ErrAlreadyAttached) {
        t.Fatal("second dial unexpectedly claimed the same private session")
    }
    if _, err := attached.PingContext(ctx, "other-host"); !errors.Is(err, ErrInvalidTarget) {
        t.Fatal("network discovery was not disabled")
    }
    if _, err := attached.Listen("anything"); !errors.Is(err, ErrNoListener) {
        t.Fatal("unexpected listening socket allowed")
    }
    cancel()
    _ = remote.SetReadDeadline(time.Now().Add(time.Second))
    var b [1]byte
    if _, err := remote.Read(b[:]); err == nil {
        t.Fatal("cancel did not close the underlying attached connection")
    }
}

func TestGameLoginRequiresSeparateVerifiedIdentity(t *testing.T) {
    local, remote := net.Pipe()
    defer local.Close()
    defer remote.Close()
    transport := &memoryPacketConn{Conn: local}
    result, err := DialAuthenticated(context.Background(), transport, minecraft.Dialer{})
    if result != nil || !errors.Is(err, ErrNoGameIdentity) {
        t.Fatal("PE lobby session may not be silently substituted with an offline Minecraft login")
    }
    if _, err := NewAttachedNetwork(nil); !errors.Is(err, ErrNoTransport) {
        t.Fatal("nil transport was accepted")
    }
    // No game bytes were written to the peer as identity was unavailable.
    _ = remote.SetReadDeadline(time.Now().Add(15*time.Millisecond))
    var b [1]byte
    if n, err := remote.Read(b[:]); n != 0 || err == nil {
        t.Fatalf("game login was sent without a verified identity: n=%d", n)
    }
}
