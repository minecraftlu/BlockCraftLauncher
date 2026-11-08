// Package transport provides an isolated client/host WebRTC transport test.
// It does not contact NetEase, perform login, or touch an existing room.
package transport

import (
    "context"
    "errors"
    "fmt"
    "net"
    "time"

    "github.com/pion/webrtc/v4"
)

const (
    ChannelLabel = "ReliableDataChannel"
    Hello = "BLOCKCRAFT_LOCAL_GUEST_HELLO"
    Ack   = "BLOCKCRAFT_LOCAL_HOST_ACK"
)

type LocalReport struct {
    Status              string `json:"status"`
    Scope               string `json:"scope"`
    GuestTransportReady bool   `json:"guest_transport_ready"`
    OfficialRoomJoined  bool   `json:"official_room_joined"`
    Authenticated       bool   `json:"authenticated"`
    Details             string `json:"details"`
}

func RunLocalSelfTest(ctx context.Context) (LocalReport, error) {
    report := LocalReport{
        Status: "LOCAL_WEBRTC_NOT_VERIFIED",
        Scope: "same_process_loopback_only",
        Details: "No external host, no STUN or TURN, no NetEase authentication.",
    }

    // Limit all ICE candidates to the computer's IPv4 loopback interface.
    // This is a transport self-test, NOT a connection attempt to a game room.
    var settings webrtc.SettingEngine
    settings.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
    settings.SetIncludeLoopbackCandidate(true)
    settings.SetIPFilter(func(ip net.IP) bool {
        return ip.IsLoopback() && ip.To4() != nil
    })
    api := webrtc.NewAPI(webrtc.WithSettingEngine(settings))

    guest, err := api.NewPeerConnection(webrtc.Configuration{})
    if err != nil {
        return report, fmt.Errorf("create local guest peer: %w", err)
    }
    defer guest.Close()
    host, err := api.NewPeerConnection(webrtc.Configuration{})
    if err != nil {
        return report, fmt.Errorf("create local host peer: %w", err)
    }
    defer host.Close()

    result := make(chan struct{}, 1)
    problems := make(chan error, 2)

    host.OnDataChannel(func(dc *webrtc.DataChannel) {
        if dc.Label() != ChannelLabel {
            select { case problems <- errors.New("unexpected data channel"): default: }
            return
        }
        dc.OnMessage(func(m webrtc.DataChannelMessage) {
            if string(m.Data) != Hello {
                select { case problems <- errors.New("unexpected guest handshake"): default: }
                return
            }
            if sendErr := dc.SendText(Ack); sendErr != nil {
                select { case problems <- sendErr: default: }
            }
        })
    })

    dc, err := guest.CreateDataChannel(ChannelLabel, nil)
    if err != nil {
        return report, fmt.Errorf("open reliable data channel: %w", err)
    }
    dc.OnOpen(func() {
        if sendErr := dc.SendText(Hello); sendErr != nil {
            select { case problems <- sendErr: default: }
        }
    })
    dc.OnMessage(func(m webrtc.DataChannelMessage) {
        if string(m.Data) == Ack {
            select { case result <- struct{}{}: default: }
        } else {
            select { case problems <- errors.New("unexpected host acknowledgement"): default: }
        }
    })

    offer, err := guest.CreateOffer(nil)
    if err != nil {
        return report, fmt.Errorf("create local offer: %w", err)
    }
    waitGuest := webrtc.GatheringCompletePromise(guest)
    if err = guest.SetLocalDescription(offer); err != nil {
        return report, fmt.Errorf("set local offer: %w", err)
    }
    if err = waitICE(ctx, waitGuest); err != nil {
        return report, fmt.Errorf("guest ICE: %w", err)
    }
    if err = host.SetRemoteDescription(*guest.LocalDescription()); err != nil {
        return report, fmt.Errorf("host accept local offer: %w", err)
    }

    answer, err := host.CreateAnswer(nil)
    if err != nil {
        return report, fmt.Errorf("create local answer: %w", err)
    }
    waitHost := webrtc.GatheringCompletePromise(host)
    if err = host.SetLocalDescription(answer); err != nil {
        return report, fmt.Errorf("set local answer: %w", err)
    }
    if err = waitICE(ctx, waitHost); err != nil {
        return report, fmt.Errorf("host ICE: %w", err)
    }
    if err = guest.SetRemoteDescription(*host.LocalDescription()); err != nil {
        return report, fmt.Errorf("guest accept local answer: %w", err)
    }

    select {
    case <-result:
        report.Status = "LOCAL_WEBRTC_DATA_CHANNEL_OK"
        report.GuestTransportReady = true
        report.Details = "Guest and mock host exchanged a message via ICE/DTLS/SCTP ReliableDataChannel on loopback."
        return report, nil
    case problem := <-problems:
        return report, fmt.Errorf("local data channel error: %w", problem)
    case <-ctx.Done():
        return report, fmt.Errorf("local transport timeout: %w", ctx.Err())
    }
}

func waitICE(ctx context.Context, done <-chan struct{}) error {
    select {
    case <-done:
        return nil
    case <-ctx.Done():
        return ctx.Err()
    }
}

func RunWithTimeout(timeout time.Duration) (LocalReport, error) {
    ctx, cancel := context.WithTimeout(context.Background(), timeout)
    defer cancel()
    return RunLocalSelfTest(ctx)
}
