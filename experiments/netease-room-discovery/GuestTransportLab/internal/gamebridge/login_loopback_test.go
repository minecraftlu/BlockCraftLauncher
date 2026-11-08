package gamebridge

import (
    "context"
    "errors"
    "io"
    "net"
    "sync"
    "testing"
    "time"

    "github.com/sandertv/gophertunnel/minecraft"
)

// messagePipe creates an entirely in-memory message-oriented transport with
// the same already-reassembled packet boundaries as NetherNet ReadPacket.
// Unlike net.Pipe, it guarantees each Write stays one datagram.
type messagePipe struct {
    inbound chan []byte
    outbound chan []byte
    done chan struct{}
    once *sync.Once
}

func pairMessages() (*messagePipe, *messagePipe) {
    a := make(chan []byte, 32)
    b := make(chan []byte, 32)
    done := make(chan struct{})
    once := new(sync.Once)
    return &messagePipe{a,b,done,once}, &messagePipe{b,a,done,once}
}

func (p *messagePipe) ReadPacket() ([]byte, error) {
    select {
    case b := <-p.inbound:
        return b, nil
    case <-p.done:
        return nil, net.ErrClosed
    }
}
func (p *messagePipe) Read(b []byte) (int, error) {
    full, err := p.ReadPacket()
    if err != nil { return 0, err }
    if len(full) > len(b) { return 0, io.ErrShortBuffer }
    return copy(b, full), nil
}
func (p *messagePipe) Write(b []byte) (int, error) {
    copied := append([]byte(nil), b...)
    select {
    case p.outbound <- copied:
        return len(b), nil
    case <-p.done:
        return 0, net.ErrClosed
    }
}
func (p *messagePipe) Close() error {
    p.once.Do(func(){close(p.done)})
    return nil
}
func (*messagePipe) LocalAddr() net.Addr { return &net.UDPAddr{IP:net.IPv4(127,0,0,1),Port:19132} }
func (*messagePipe) RemoteAddr() net.Addr { return &net.UDPAddr{IP:net.IPv4(127,0,0,1),Port:19133} }
func (*messagePipe) SetDeadline(time.Time) error { return nil }
func (*messagePipe) SetReadDeadline(time.Time) error { return nil }
func (*messagePipe) SetWriteDeadline(time.Time) error { return nil }

// gameHostListener is not a real socket or server. It supplies exactly one
// messagePipe to gophertunnel's ordinary ListenConfig login state machine.
type gameHostListener struct {
    conn chan net.Conn
    done chan struct{}
    once sync.Once
}
func (l *gameHostListener) Accept() (net.Conn, error) {
    select {
    case conn := <-l.conn:
        return conn, nil
    case <-l.done:
        return nil, net.ErrClosed
    }
}
func (l *gameHostListener) Close() error {l.once.Do(func(){close(l.done)});return nil}
func (*gameHostListener) Addr() net.Addr {return &net.UDPAddr{IP:net.IPv4(127,0,0,1),Port:19132}}
func (*gameHostListener) ID() int64 {return 1}
func (*gameHostListener) PongData([]byte) {}

type localHostNetwork struct {listener minecraft.NetworkListener}
func (*localHostNetwork) DialContext(context.Context,string)(net.Conn,error){return nil,ErrInvalidTarget}
func (*localHostNetwork) PingContext(context.Context,string)([]byte,error){return nil,ErrInvalidTarget}
func (n *localHostNetwork) Listen(string)(minecraft.NetworkListener,error){return n.listener,nil}

// Exercise the REAL Bedrock library's NetworkSettings -> Login -> PlayStatus
// login success path. Anonymous identity is permitted ONLY because both
// endpoints are synthetic local fixtures and the mock host disables auth.
// Real NetEase rooms never pass through this test or use this identity.
func TestLocalBedrockHandshakeAcrossAttachedNetherNetFraming(t *testing.T) {
    clientPipe, serverPipe := pairMessages()
    defer clientPipe.Close()
    host := &gameHostListener{conn:make(chan net.Conn,1),done:make(chan struct{})}
    host.conn <- &PacketConn{PacketTransport:serverPipe}
    listener, err := (minecraft.ListenConfig{
        AuthenticationDisabled:true,
    }).ListenNetwork(&localHostNetwork{listener:host},"127.0.0.1:19132")
    if err != nil {t.Fatalf("offline fixture listener failed: %v",err)}
    defer listener.Close()
    attached, err := NewAttachedNetwork(clientPipe)
    if err != nil {t.Fatal(err)}
    ctx,cancel := context.WithTimeout(context.Background(),8*time.Second)
    defer cancel()
    accepted := make(chan error,1)
    go func(){
        conn, err := listener.Accept()
        if err == nil {
            if conn==nil {err=errors.New("nil local host player")}
            if conn!=nil {defer conn.Close()}
        }
        accepted <- err
    }()
    client,err := (minecraft.Dialer{}).DialContextNetwork(ctx,attached,ExistingSessionAddress)
    if err != nil {t.Fatalf("local Bedrock game login failed: %v",err)}
    defer client.Close()
    select {
    case err := <-accepted:
        if err!=nil {t.Fatalf("mock host did not accept Bedrock login: %v",err)}
    case <-ctx.Done():
        t.Fatal("local host never observed login, not a game success")
    }
    // The local-only login succeeded. No StartGame, spawn, world chunks or
    // external NetEase server were involved.
}
