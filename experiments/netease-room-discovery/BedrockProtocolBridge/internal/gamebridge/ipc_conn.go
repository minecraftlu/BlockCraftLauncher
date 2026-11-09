package gamebridge

import (
    "context"
    "errors"
    "io"
    "net"
    "sync"
    "time"

    wire "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire"
)

// IPCPacketConn is a message-oriented net.Conn used exclusively AFTER a
// version/nonce Hello -> Ready handshake between the two local processes.
// It translates one NetherNet game batch to one bounded local IPC Data frame.
// It does not dial a server, authenticate a user, or grant room admission.
//
// Its input/output must each be independently closable: closing them must
// interrupt blocked reads/writes so cancellation cannot leave a ghost process.
type IPCPacketConn struct {
    packets *wire.PacketStream
    input io.ReadCloser
    output io.WriteCloser
    once sync.Once
    closed chan struct{}
}

var (
    ErrMissingIPCEndpoint = errors.New("bedrock IPC: missing local pipe endpoint")
    ErrIPCDeadlineUnsupported = errors.New("bedrock IPC: deadlines require a bounded owning context")
)

var _ PacketTransport = (*IPCPacketConn)(nil)

func NewIPCPacketConn(ctx context.Context, input io.ReadCloser, output io.WriteCloser) (*IPCPacketConn,error) {
    if ctx==nil || input==nil || output==nil {return nil,ErrMissingIPCEndpoint}
    if err:=ctx.Err();err!=nil { _=input.Close();_ =output.Close();return nil,err }
    c:=&IPCPacketConn{
        packets:wire.NewPacketStream(input,output),
        input:input,output:output,closed:make(chan struct{}),
    }
    // Never read or log raw game bytes. Cancelling the owner closes both
    // local pipes, unblocking gophertunnel's blocked decoder and writer.
    context.AfterFunc(ctx,func(){_ = c.Close()})
    return c,nil
}

func (c *IPCPacketConn) ReadPacket() ([]byte,error) {
    select {case <-c.closed:return nil,net.ErrClosed;default:}
    p,err:=c.packets.ReadPacket()
    if errors.Is(err,wire.ErrRemoteStop) {
        _=c.Close()
        return nil,io.EOF
    }
    return p,err
}

// Prefer ReadPacket: net.Conn.Read cannot preserve message boundaries if the
// supplied buffer is too short. Never silently truncate a Bedrock batch.
func (c *IPCPacketConn) Read(b []byte)(int,error) {
    p,err:=c.ReadPacket()
    if err!=nil{return 0,err}
    if len(p)>len(b){return 0,io.ErrShortBuffer}
    return copy(b,p),nil
}

func (c *IPCPacketConn) Write(b []byte)(int,error) {
    select {case <-c.closed:return 0,net.ErrClosed;default:}
    if err:=c.packets.SendPacket(b);err!=nil{return 0,err}
    return len(b),nil
}

func (c *IPCPacketConn) Close() error {
    c.once.Do(func(){
        close(c.closed)
        _=c.input.Close()
        _=c.output.Close()
    })
    return nil
}

// These addresses exist solely as Bedrock library metadata: there is no
// real UDP listener, network interface or server address at these endpoints.
func (*IPCPacketConn) LocalAddr() net.Addr {
    return &net.UDPAddr{IP:net.IPv4(127,0,0,1),Port:19132}
}
func (*IPCPacketConn) RemoteAddr() net.Addr {
    return &net.UDPAddr{IP:net.IPv4(127,0,0,1),Port:19133}
}

// The pipe is bounded by the owning context, not an OS socket deadline.
// Reject unsupported deadlines rather than silently claiming to enforce one.
func (*IPCPacketConn) SetDeadline(time.Time) error {return ErrIPCDeadlineUnsupported}
func (*IPCPacketConn) SetReadDeadline(time.Time) error {return ErrIPCDeadlineUnsupported}
func (*IPCPacketConn) SetWriteDeadline(time.Time) error {return ErrIPCDeadlineUnsupported}
