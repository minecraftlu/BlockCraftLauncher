package signal

import (
    "errors"
    "fmt"
    "strconv"
    "strings"
)

const MaxFrameBytes = 256 * 1024

// Kind uses the signal names observed in the community NetherNet
// implementation. These are protocol names, not proof of a NetEase session.
type Kind string

const (
    ConnectRequest  Kind = "CONNECTREQUEST"
    ConnectResponse Kind = "CONNECTRESPONSE"
    CandidateAdd    Kind = "CANDIDATEADD"
    ConnectError    Kind = "CONNECTERROR"
)

// Frame is the in-band envelope only. The remote network ID is external
// routing metadata and MUST originate from an authorized join session.
type Frame struct {
    Kind         Kind
    ConnectionID uint64
    Data         string
}

func (f Frame) Marshal() (string, error) {
    if !validKind(f.Kind) {
        return "", errors.New("unsupported signal kind")
    }
    if f.ConnectionID == 0 {
        return "", errors.New("zero connection ID")
    }
    if f.Data == "" || strings.ContainsRune(f.Data, '\x00') {
        return "", errors.New("invalid signal payload")
    }
    result := fmt.Sprintf("%s %d %s", f.Kind, f.ConnectionID, f.Data)
    if len(result) > MaxFrameBytes {
        return "", errors.New("signal payload too large")
    }
    return result, nil
}

func Parse(raw string) (Frame, error) {
    if raw == "" || len(raw) > MaxFrameBytes {
        return Frame{}, errors.New("empty or oversized signal")
    }
    parts := strings.SplitN(raw, " ", 3)
    if len(parts) != 3 || parts[2] == "" {
        return Frame{}, errors.New("invalid signaling envelope")
    }
    kind := Kind(parts[0])
    if !validKind(kind) {
        return Frame{}, errors.New("unsupported signal kind")
    }
    id, err := strconv.ParseUint(parts[1], 10, 64)
    if err != nil || id == 0 {
        return Frame{}, errors.New("invalid connection ID")
    }
    if strings.ContainsRune(parts[2], '\x00') {
        return Frame{}, errors.New("invalid signal payload")
    }
    return Frame{Kind: kind, ConnectionID: id, Data: parts[2]}, nil
}

func validKind(k Kind) bool {
    switch k {
    case ConnectRequest, ConnectResponse, CandidateAdd, ConnectError:
        return true
    default:
        return false
    }
}
