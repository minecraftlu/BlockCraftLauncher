package main

import (
    "encoding/json"
    "flag"
    "fmt"
    "os"
    "time"

    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab/internal/tanroom"
    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab/internal/transport"
)

func main() {
    local := flag.Bool("selftest", false, "run isolated loopback WebRTC client/host test")
    protocolTest := flag.Bool("protocol-selftest", false, "verify NetEase room admission wire fixtures OFFLINE")
    timeout := flag.Duration("timeout", 20*time.Second, "maximum time spent on the local WebRTC self-test")
    flag.Parse()

    if !*local && !*protocolTest {
        fmt.Println("BlockCraft Guest Transport Lab (research only)")
        fmt.Println("Commands:")
        fmt.Println("  BlockCraft.GuestTransportLab.exe -selftest")
        fmt.Println("  BlockCraft.GuestTransportLab.exe -protocol-selftest")
        fmt.Println("No NetEase login, room admission, or game launch is implemented.")
        return
    }

    if *protocolTest {
        if err := runProtocolFixture(); err != nil {
            fmt.Fprintln(os.Stderr, "PROTOCOL_SELFTEST_FAILED:", err)
            os.Exit(2)
        }
    }

    if *local {
        report, err := transport.RunWithTimeout(*timeout)
        out, marshalErr := json.MarshalIndent(report, "", "  ")
        if marshalErr == nil { fmt.Println(string(out)) }
        if err != nil {
            fmt.Fprintf(os.Stderr, "LOCAL_SELFTEST_FAILED: %v\n", err)
            os.Exit(2)
        }
        fmt.Println("LOCAL_SELFTEST_PASS (no external network session)")
    }
}

func runProtocolFixture() error {
    // All IDs below are synthetic, never taken from any user's account.
    fixture := tanroom.EnterRequest{
        OwnerID: 123, RoomID: 453649, GuestNetherNetID: "1234567",
        EnterTeamID: 0, EnterToken: 0, FollowTeamID: 0, Compressed: true,
    }
    encoded, err := tanroom.EncodeEnterRequest(fixture)
    if err != nil { return err }
    decoded, err := tanroom.DecodeEnterRequest(encoded)
    if err != nil { return err }
    if decoded.OwnerID != fixture.OwnerID || decoded.RoomID != fixture.RoomID ||
        decoded.GuestNetherNetID != fixture.GuestNetherNetID {
        return fmt.Errorf("synthetic join request did not roundtrip")
    }

    mockAck, err := tanroom.EncodeEnterReceiptFixture(tanroom.EnterReceipt{
        ResultCode: tanroom.Accepted, PlayerIDs: []uint32{10, 11},
    })
    if err != nil { return err }
    ack, err := tanroom.DecodeEnterReceipt(mockAck)
    if err != nil { return err }

    readyBytes, err := tanroom.EncodeServerReadyFixture(tanroom.ServerReady{
        ServerAddress: "example.test:0", ServerRaknetGUID: "test",
        RTCRoomID: "mock", HostNetherNetID: "987654321",
    })
    if err != nil { return err }
    ready, err := tanroom.DecodeServerReady(readyBytes)
    if err != nil { return err }
    if ack.Status() != "ADMISSION_ACCEPTED" || ready.HostNetherNetID != "987654321" {
        return fmt.Errorf("synthetic admission state was not decoded")
    }
    output := map[string]any{
        "status": "LOCAL_TANROOM_CODEC_OK",
        "scope": "offline_synthetic_fixture_only",
        "request_encoding": "PASSED",
        "response_decoding": "PASSED",
        "server_ready_decoding": "PASSED",
        "real_room_joined": false,
        "authenticated": false,
        "note": "The synthetic success code does not represent a real server response.",
    }
    b, err := json.MarshalIndent(output, "", "  ")
    if err != nil { return err }
    fmt.Println(string(b))
    fmt.Println("PROTOCOL_SELFTEST_PASS (no external network session)")
    return nil
}
