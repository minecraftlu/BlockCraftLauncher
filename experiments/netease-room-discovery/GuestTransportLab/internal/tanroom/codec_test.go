package tanroom

import (
    "bytes"
    "errors"
    "io"
    "reflect"
    "testing"
)

func TestEnterRequestRoundTrip(t *testing.T) {
    req := EnterRequest{
        OwnerID: 123, RoomID: 453649, Password: "中文🧱",
        EnterTeamID: 0, EnterToken: 0, FollowTeamID: 0,
        GuestNetherNetID: "9876543210123", Compressed: true,
    }
    raw, err := EncodeEnterRequest(req)
    if err != nil { t.Fatal(err) }
    if len(raw) < 2 || !bytes.Equal(raw[:2], []byte{0, 3}) {
        t.Fatalf("expected packet header 0003, got %x", raw)
    }
    actual, err := DecodeEnterRequest(raw)
    if err != nil { t.Fatal(err) }
    if !reflect.DeepEqual(actual, req) {
        t.Fatalf("incorrect decode: got %+v expected %+v", actual, req)
    }
    if _, err := DecodeEnterRequest(raw[:len(raw)-1]); err == nil {
        t.Fatal("truncated request accepted")
    }
    if _, err := DecodeEnterRequest(append(raw, 0)); err == nil {
        t.Fatal("trailing request data accepted")
    }
}

func TestEnterRequestRejectsIdentifiers(t *testing.T) {
    for _, req := range []EnterRequest{
        {OwnerID: 0, RoomID: 453649, GuestNetherNetID: "11"},
        {OwnerID: 1, RoomID: 0, GuestNetherNetID: "11"},
        {OwnerID: 1, RoomID: 453649, GuestNetherNetID: "0"},
        {OwnerID: 1, RoomID: 453649, GuestNetherNetID: "abcd"},
    } {
        if _, err := EncodeEnterRequest(req); err == nil {
            t.Fatalf("invalid request accepted %+v", req)
        }
    }
}

func TestReceiptStatusAndBounds(t *testing.T) {
    fixtures := []struct {
        code byte
        status string
    }{
        {Accepted, "ADMISSION_ACCEPTED"},
        {RoomNotFound, "ROOM_NOT_FOUND"},
        {RoomFull, "ROOM_FULL"},
        {WrongPassword, "WRONG_PASSWORD"},
        {99, "ADMISSION_REJECTED_CODE_99"},
    }
    for _, test := range fixtures {
        expected := EnterReceipt{ResultCode: test.code}
        if test.code == Accepted {
            expected.PlayerIDs = []uint32{101, 202}
            expected.ItemIDs = []uint64{5555}
        }
        raw, err := EncodeEnterReceiptFixture(expected)
        if err != nil { t.Fatal(err) }
        got, err := DecodeEnterReceipt(raw)
        if err != nil { t.Fatal(err) }
        if got.Status() != test.status || !reflect.DeepEqual(got, expected) {
            t.Fatalf("receipt mismatch: %+v", got)
        }
        if _, err := DecodeEnterReceipt(append(raw, 0x00)); err == nil {
            t.Fatal("unexpected extra bytes accepted")
        }
    }
    if _, err := DecodeEnterReceipt([]byte{0, 3, 0, 2, 0}); !errors.Is(err, io.ErrUnexpectedEOF) {
        t.Fatalf("truncated accepted receipt not rejected correctly: %v", err)
    }
}

func TestServerReadyRoundTrip(t *testing.T) {
    sample := ServerReady{
        ServerAddress: "example.test:9999", ServerRaknetGUID: "123",
        RTCRoomID: "12345", HostNetherNetID: "54321098765",
        Compressed: true,
    }
    b, err := EncodeServerReadyFixture(sample)
    if err != nil { t.Fatal(err) }
    got, err := DecodeServerReady(b)
    if err != nil { t.Fatal(err) }
    if got != sample { t.Fatalf("decoded ready mismatch: %+v", got) }
    if _, err := DecodeServerReady(b[:len(b)-1]); err == nil {
        t.Fatal("short server ready erroneously accepted")
    }
}
