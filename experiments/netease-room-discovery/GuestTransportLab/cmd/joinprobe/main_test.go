package main

import (
    "encoding/json"
    "io"
    "os"
    "strings"
    "testing"

    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab/internal/joinprobe"
    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab/internal/stage12readiness"
)

func captureReport(t *testing.T, report joinprobe.Report) map[string]any {
    t.Helper()
    reader, writer, err := os.Pipe()
    if err != nil { t.Fatal(err) }
    old := os.Stdout
    os.Stdout = writer
    defer func(){os.Stdout = old; _ = reader.Close()}()
    emitMachine(report)
    _ = writer.Close()
    raw,err := io.ReadAll(reader)
    if err != nil {t.Fatal(err)}
    if !strings.HasPrefix(string(raw),machinePrefix) {t.Fatal("versioned record missing")}
    var parsed map[string]any
    if err:=json.Unmarshal([]byte(strings.TrimPrefix(strings.TrimSpace(string(raw)),machinePrefix)),&parsed);err!=nil {t.Fatal(err)}
    return parsed
}

func TestReadinessMachineOutputCannotUpgradeLobbyToGameSession(t *testing.T) {
    r:=joinprobe.Report{
        Status:"WEBRTC_CHANNELS_OPEN_60S_NO_DATA_GAME_NOT_JOINED",
        LoginAccepted:true, RoomAdmissionAccepted:true, HostReadySeen:true,
        SignalingAuthenticated:true, WebRTCTransportConnected:true, TransportStable:true,
        ReliableChannelOpen:true, UnreliableChannelOpen:true, ObservationSeconds:60,
        ModeConfirmed:true, MinecraftWorldJoined:false,
        Detail:"MOCK_PRIVATE_TOKEN_MUST_NOT_BE_PRINTED",
    }
    m:=captureReport(t,r)
    if m["stage12_readiness"]!=stage12readiness.GameIdentityAndProtocolUnverified {
        t.Fatalf("game identity incorrectly inferred from G79 + WebRTC: %v",m["stage12_readiness"])
    }
    for _,key:=range []string{"stage12_game_identity_verified","stage12_game_protocol_verified","stage12_game_login_attempted","minecraft_world_joined"} {
        if m[key]!=false {t.Fatalf("%s was incorrectly true",key)}
    }
    if _,exists:=m["detail"];exists {t.Fatal("secret diagnostic was serialized")}
}

func TestUnauthenticatedRoomCannotProduceReadyStage12Record(t *testing.T) {
    r:=joinprobe.Report{Status:"PE_AUTH_UNAVAILABLE"}
    m:=captureReport(t,r)
    if m["stage12_readiness"]!=stage12readiness.AdmissionNotVerified {
        t.Fatalf("unauthenticated room upgraded: %v",m["stage12_readiness"])
    }
}
