// Package bridgewire defines a bounded, local STDIO protocol between the
// existing NetherNet transport module and an isolated Bedrock protocol worker.
// It uses no Pion / Bedrock dependencies and NEVER opens a network connection.
// Treat data payloads as opaque: they may contain credentials or game content.
package bridgewire

import (
    "bytes"
    "encoding/binary"
    "errors"
    "io"
)

const (
    MaxPayload = 1024 * 1024
    NonceBytes = 16
    Version byte = 1
    HeaderBytes = 10
)

var magic = [4]byte{'B','C','1','2'}

type Kind byte
const (
    Hello Kind = 1
    Ready Kind = 2
    Data Kind = 3
    Stop Kind = 4
    Stopped Kind = 5
)

type Frame struct {
    Kind Kind
    Payload []byte
}

var (
    ErrMalformed = errors.New("bridgewire: invalid frame")
    ErrTooLarge = errors.New("bridgewire: payload exceeds limit")
    ErrUnsupported = errors.New("bridgewire: unsupported frame version or type")
)

func valid(k Kind, n int) bool {
    switch k {
    case Hello, Ready:
        return n == NonceBytes
    case Data:
        return n > 0 && n <= MaxPayload
    case Stop, Stopped:
        return n == 0
    default:
        return false
    }
}

// ReadFrame fails closed before allocation on corrupt lengths or control
// frames. Never include raw data in errors, logs or diagnostic records.
func ReadFrame(r io.Reader) (Frame, error) {
    var h [HeaderBytes]byte
    if _,err := io.ReadFull(r,h[:]); err!=nil {return Frame{},err}
    if !bytes.Equal(h[:4],magic[:]) {return Frame{},ErrMalformed}
    if h[4]!=Version {return Frame{},ErrUnsupported}
    k:=Kind(h[5])
    n:=binary.BigEndian.Uint32(h[6:])
    if n>MaxPayload {return Frame{},ErrTooLarge}
    if !valid(k,int(n)) {
        if k<Hello || k>Stopped {return Frame{},ErrUnsupported}
        return Frame{},ErrMalformed
    }
    b:=make([]byte,int(n))
    if _,err:=io.ReadFull(r,b);err!=nil {
        // EOF after a valid non-empty frame header is a truncated packet,
        // not a clean shutdown. Keep cancellation/EOF classification strict.
        if errors.Is(err,io.EOF) {return Frame{},io.ErrUnexpectedEOF}
        return Frame{},err
    }
    return Frame{Kind:k,Payload:b},nil
}

// WriteFrame encodes an entire immutable frame. The caller must serialize
// concurrent writers to the same pipe. A zero-progress write is rejected.
func WriteFrame(w io.Writer, f Frame) error {
    if len(f.Payload)>MaxPayload {return ErrTooLarge}
    if !valid(f.Kind,len(f.Payload)) {
        if f.Kind<Hello || f.Kind>Stopped{return ErrUnsupported}
        return ErrMalformed
    }
    var h [HeaderBytes]byte
    copy(h[:4],magic[:])
    h[4]=Version
    h[5]=byte(f.Kind)
    binary.BigEndian.PutUint32(h[6:],uint32(len(f.Payload)))
    if err:=writeAll(w,h[:]);err!=nil{return err}
    return writeAll(w,f.Payload)
}

func writeAll(w io.Writer, b []byte) error {
    for len(b)>0 {
        n,err:=w.Write(b)
        if n<0 || n>len(b) {return io.ErrShortWrite}
        b=b[n:]
        if err!=nil {return err}
        if n==0 {return io.ErrShortWrite}
    }
    return nil
}
