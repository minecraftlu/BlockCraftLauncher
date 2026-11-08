package bridgewire

import (
    "errors"
    "io"
    "sync"
)

// ErrRemoteStop reports an explicit Stop frame, not an unexpected EOF.
// An unframed EOF must always be treated as a broken pipe.
var (
    ErrRemoteStop = errors.New("bridgewire: peer requested stop")
    ErrSendClosed = errors.New("bridgewire: outgoing stream closed")
)

// PacketStream turns the control-validated wire format into a bounded,
// message-oriented bidirectional stream *after* Hello/Ready negotiation.
// It never opens a network connection, nor does it store packet contents.
// Only one goroutine may read at a time; writers are serialized so frames
// cannot interleave even when two producers are running concurrently.
type PacketStream struct {
    reader io.Reader
    writer io.Writer
    readMu sync.Mutex
    writeMu sync.Mutex
    sendingClosed bool
}

func NewPacketStream(r io.Reader, w io.Writer) *PacketStream {
    return &PacketStream{reader:r,writer:w}
}

func (s *PacketStream) ReadPacket() ([]byte,error) {
    s.readMu.Lock()
    defer s.readMu.Unlock()
    f,err:=ReadFrame(s.reader)
    if err!=nil{return nil,err}
    switch f.Kind {
    case Data:
        return f.Payload,nil
    case Stop:
        return nil,ErrRemoteStop
    default:
        return nil,ErrMalformed
    }
}

func (s *PacketStream) SendPacket(data []byte) error {
    s.writeMu.Lock()
    defer s.writeMu.Unlock()
    if s.sendingClosed{return ErrSendClosed}
    if err:=WriteFrame(s.writer,Frame{Kind:Data,Payload:data});err!=nil{
        // A validation error is not a broken pipe. The caller may retry a
        // corrected packet; a write error leaves framing potentially partial.
        if !errors.Is(err,ErrTooLarge) && !errors.Is(err,ErrMalformed) {
            s.sendingClosed=true
        }
        return err
    }
    return nil
}

func (s *PacketStream) RequestStop() error {
    return s.closeWith(Stop)
}

// AcknowledgeStop is only valid for a peer that already received a Stop.
func (s *PacketStream) AcknowledgeStop() error {
    return s.closeWith(Stopped)
}

func (s *PacketStream) closeWith(kind Kind) error {
    s.writeMu.Lock()
    defer s.writeMu.Unlock()
    if s.sendingClosed{return ErrSendClosed}
    s.sendingClosed=true
    return WriteFrame(s.writer,Frame{Kind:kind})
}

// ReadStopped consumes the one acknowledgement after RequestStop.
func (s *PacketStream) ReadStopped() error {
    s.readMu.Lock()
    defer s.readMu.Unlock()
    f,err:=ReadFrame(s.reader)
    if err!=nil{return err}
    if f.Kind!=Stopped{return ErrMalformed}
    return nil
}
