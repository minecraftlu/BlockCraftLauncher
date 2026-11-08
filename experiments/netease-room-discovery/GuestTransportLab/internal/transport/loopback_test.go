package transport

import (
    "context"
    "testing"
    "time"
)

func TestLocalWebRTCDataChannel(t *testing.T) {
    ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
    defer cancel()

    report, err := RunLocalSelfTest(ctx)
    if err != nil {
        t.Fatal(err)
    }
    if report.Status != "LOCAL_WEBRTC_DATA_CHANNEL_OK" ||
        !report.GuestTransportReady ||
        report.OfficialRoomJoined ||
        report.Authenticated {
        t.Fatalf("unexpected proof scope: %+v", report)
    }
}
