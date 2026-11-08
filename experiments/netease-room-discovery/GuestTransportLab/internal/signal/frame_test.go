package signal

import (
    "strings"
    "testing"
)

func TestRoundTrip(t *testing.T) {
    f := Frame{
        Kind: ConnectRequest,
        ConnectionID: 9302,
        Data: "v=0\r\na=group:BUNDLE 0\nhello:world",
    }
    encoded, err := f.Marshal()
    if err != nil {
        t.Fatal(err)
    }
    decoded, err := Parse(encoded)
    if err != nil {
        t.Fatal(err)
    }
    if decoded != f {
        t.Fatalf("roundtrip mismatch: got %+v, want %+v", decoded, f)
    }
}

func TestBadFrames(t *testing.T) {
    examples := []string{
        "", "CONNECTREQUEST", "CONNECTREQUEST 0 abc",
        "AUTH 123 secret", "CONNECTREQUEST xyz offer",
        "CANDIDATEADD 234", "CONNECTREQUEST 123 ",
        "CONNECTREQUEST 123 a\x00test",
        strings.Repeat("x", MaxFrameBytes+1),
    }
    for _, example := range examples {
        if _, err := Parse(example); err == nil {
            t.Errorf("expected rejection for frame of %d bytes", len(example))
        }
    }
}
