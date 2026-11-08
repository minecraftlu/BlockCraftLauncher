// Package gamebridge adapts an ALREADY authorised NetherNet connection to
// gophertunnel's Minecraft Bedrock packet transport. It does not establish
// NetEase sessions, manufacture player identity or bypass game login.
package gamebridge

import (
    "context"
    "errors"
    "net"
    "sync"

    "github.com/sandertv/gophertunnel/minecraft"
    "github.com/sandertv/gophertunnel/minecraft/protocol/packet"
)

// Gophertunnel embeds the supplied address into Bedrock client data and
// requires a syntactically valid UDP host:port. This value is metadata only:
// AttachedNetwork never opens any socket to it, and rejects any other target.
const ExistingSessionAddress = "127.0.0.1:19132"

var (
    ErrNoTransport = errors.New("bedrock bridge: no established packet transport")
    ErrInvalidTarget = errors.New("bedrock bridge: refusing to dial a new destination")
    ErrAlreadyAttached = errors.New("bedrock bridge: existing connection already claimed")
    ErrNoGameIdentity = errors.New("bedrock bridge: no verified Minecraft game identity provider")
    ErrNoListener = errors.New("bedrock bridge: accepting unsolicited connections is disabled")
)

// PacketTransport is implemented by the existing forked NetherNet Conn.
// Use ReadPacket rather than net.Conn.Read: the latter can silently truncate
// a complete reliable DataChannel message when its buffer is too small.
type PacketTransport interface {
    net.Conn
    ReadPacket() ([]byte, error)
}

// PacketConn preserves gophertunnel v1.62.0's optional capabilities.
// The old fork implements PacketHeader() (0, false), whereas newer
// gophertunnel uses BatchHeaderer and EncryptionDisabler. Without this
// adapter the default 0xfe RakNet batch prefix would be injected into the
// NetherNet packet stream, and the decoder could misread message boundaries.
type PacketConn struct {
    PacketTransport
}

var _ packet.TransportCapabilities = (*PacketConn)(nil)

// NetherNet carries raw Bedrock packet batches without RakNet's 0xfe prefix.
// This is verified against the pinned fork, not guessed from net.Conn alone.
func (*PacketConn) BatchHeader() []byte { return nil }

// Transport DTLS already provides link encryption. Do not silently enable an
// additional, mismatched legacy Bedrock packet-encryption layer here.
func (*PacketConn) DisableEncryption() bool { return true }

func (c *PacketConn) ReadPacket() ([]byte, error) {
    return c.PacketTransport.ReadPacket()
}

// AttachedNetwork implements minecraft.Network over ONE previously
// authenticated connection. It intentionally offers no new dialing or pinging.
type AttachedNetwork struct {
    mu sync.Mutex
    transport *PacketConn
    claimed bool
}

var _ minecraft.Network = (*AttachedNetwork)(nil)

func NewAttachedNetwork(conn PacketTransport) (*AttachedNetwork, error) {
    if conn == nil {
        return nil, ErrNoTransport
    }
    return &AttachedNetwork{transport: &PacketConn{PacketTransport: conn}}, nil
}

func (n *AttachedNetwork) DialContext(ctx context.Context, address string) (net.Conn, error) {
    if ctx == nil || ctx.Err() != nil {
        return nil, context.Canceled
    }
    if address != ExistingSessionAddress {
        return nil, ErrInvalidTarget
    }
    n.mu.Lock()
    defer n.mu.Unlock()
    if n.claimed {
        return nil, ErrAlreadyAttached
    }
    n.claimed = true
    conn := n.transport
    // Closing the owner context releases all game transport resources,
    // including while the library is blocked waiting for login.
    context.AfterFunc(ctx, func() { _ = conn.Close() })
    return conn, nil
}

func (*AttachedNetwork) PingContext(context.Context, string) ([]byte, error) {
    // gophertunnel treats ping errors as non-fatal and dials the provided
    // address unchanged. There must never be a separate discovery request.
    return nil, ErrInvalidTarget
}

func (*AttachedNetwork) Listen(string) (minecraft.NetworkListener, error) {
    return nil, ErrNoListener
}

// DialAuthenticated is a *future integration seam*, not called by the current
// NetherNet probe. A TanLobby PE cookie alone does not prove game-level
// Minecraft identity; gophertunnel otherwise falls back to an offline JWT.
// Reject that fallback on real remote sessions rather than sending a fake
// authentication chain to an official host. The caller must provide a
// genuine compatible identity provider, and must verify server compatibility.
func DialAuthenticated(ctx context.Context, conn PacketTransport, dialer minecraft.Dialer) (*minecraft.Conn, error) {
    if dialer.TokenSource == nil && dialer.XBLClient == nil {
        return nil, ErrNoGameIdentity
    }
    n, err := NewAttachedNetwork(conn)
    if err != nil {
        return nil, err
    }
    return dialer.DialContextNetwork(ctx, n, ExistingSessionAddress)
}
