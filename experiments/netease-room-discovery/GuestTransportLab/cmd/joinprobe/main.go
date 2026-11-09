package main

import (
    "context"
    "encoding/json"
    "flag"
    "fmt"
    "io"
    "os"
    "path/filepath"
    "sync/atomic"
    "time"

    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab/internal/joinprobe"
    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab/internal/stage12readiness"
)

func main() {
    room := flag.String("room", "", "your own six-digit NetEase mobile lobby room")
    helper := flag.String("helper", "", "absolute path of your previously reviewed connect.exe")
    cookie := flag.String("cookie", "", "absolute path of your locally stored second-account cookie JSON")
    authorized := flag.Bool("host-consent", false, "I own this room or have explicit permission from the host")
    attempt := flag.Bool("try-join", false, "allow exactly one real RakNet login and room admission attempt")
    tryWebRTC := flag.Bool("try-webrtc", false, "after real room acceptance attempt WebSocket signaling + ICE/DTLS/SCTP")
    observation := flag.Int("observe-seconds", 8, "read-only connected data-channel observation: 8, 30 or 60 seconds")
    readOnlyIPC := flag.Bool("try-readonly-ipc",false,"after actual room admission: forward inbound reliable messages to a receive-only local worker, never send gameplay")
    readOnlyWorker := flag.String("stage12-worker","","absolute path to the reviewed Stage12Worker.exe")
    timeout := flag.Duration("timeout", 60*time.Second, "maximum total duration (10s-120s)")
    machine := flag.Bool("machine-json", false, "emit one versioned ASCII-safe JSON record without secrets")
    ipcSelftest := flag.Bool("ipc-selftest", false, "emit synthetic IPC report without network, account or room access")
    cancelOnStdin := flag.Bool("cancel-on-stdin-close", false, "cancel active probe and release sockets when the WPF host closes standard input")
    ipcCancelSelftest := flag.Bool("ipc-cancel-selftest", false, "offline only: wait for stdin-close cancellation and report without any network")

    flag.Parse()

    // The WPF client uses this deterministic local fixture to verify the
    // *actual compiled Go executable* -> redirected UTF-8 stream -> .NET
    // Framework parser pipeline. It cannot authenticate or send packets.
    if *ipcSelftest {
        if !*machine {
            fmt.Fprintln(os.Stderr, "-ipc-selftest requires -machine-json")
            os.Exit(2)
        }
        emitMachine(joinprobe.Report{
            Status: "PE_AUTH_UNAVAILABLE",
            Detail: "模拟会话暂不可用；未使用真实账户",
            Scope: "offline_ipc_selftest",
        })
        return
    }

    if *ipcCancelSelftest {
        if !*machine || !*cancelOnStdin {
            fmt.Fprintln(os.Stderr,"ipc cancel selftest requires -machine-json -cancel-on-stdin-close")
            os.Exit(2)
        }
        // No credentials, no network, no room lookup. The stdin EOF is
        // intentionally supplied by a Windows .NET Framework integration test.
        _, _ = io.Copy(io.Discard,os.Stdin)
        emitMachine(joinprobe.Report{Status:"LOCAL_PROBE_CANCELLED"})
        return
    }

    if *room == "" || *helper == "" || *cookie == "" || !*authorized || !*attempt {
        fmt.Println("BlockCraft Stage 11.1 read-only channel diagnostics (NOT a game client)")
        fmt.Println("Required: -room ROOM -helper ABSOLUTE_CONNECT_EXE -cookie ABSOLUTE_COOKIE_JSON -host-consent -try-join [-try-webrtc]")
        fmt.Println("The second account must already be legitimately logged in on this PC.")
        fmt.Println("No cookies, login tokens, encryption keys or player IDs are printed.")
        fmt.Println("This tool may briefly enter the room. It closes local connections on exit; the server may retain an offline roster entry.")
        fmt.Println("It never bypasses account verification, anti-cheat, real-name requirements or server admission.")
        return
    }

    if *timeout < 10*time.Second || *timeout > 180*time.Second {
        fmt.Fprintln(os.Stderr, "Timeout must be between 10s and 180s.")
        os.Exit(2)
    }

    ctx,cancel:=context.WithTimeout(context.Background(),*timeout)
    defer cancel()
    var stdinClosed atomic.Bool
    if *cancelOnStdin {
        // The WPF owner keeps stdin open while this subprocess is alive.
        // If the user cancels, closes the window, or the UI crashes, EOF
        // closes all owned RakNet, signaling and WebRTC connections through
        // the existing context-aware cleanup paths. No input is parsed.
        go func() {
            _, _ = io.Copy(io.Discard,os.Stdin)
            stdinClosed.Store(true)
            cancel()
        }()
    }
    report:=joinprobe.Run(ctx,joinprobe.Request{
        HelperPath:filepath.Clean(*helper),
        CookiePath:filepath.Clean(*cookie),
        RoomID:*room,
        AuthorizedByHost:*authorized,
        AttemptWebRTC:*tryWebRTC,
        ObservationSeconds:*observation,
        ReadOnlyIPC:*readOnlyIPC,
        ReadOnlyWorkerPath:*readOnlyWorker,
    })
    if stdinClosed.Load() {
        // Explicit cancellation is a neutral local operation, not a server
        // denial or proof of a successful room/world session.
        report=joinprobe.Report{Status:"LOCAL_PROBE_CANCELLED"}
    }
    if *machine {
        emitMachine(report)
        // Machine transport always returns process exit code 0 after a
        // valid report, even for legitimate login/room refusal. The report
        // status is authoritative; no stderr or banner parsing is needed.
        return
    }
    reportJSON,_:=json.MarshalIndent(report,"","  ")
    fmt.Println(string(reportJSON))
    if report.RoomAdmissionAccepted {
        fmt.Println("TAN_LOBBY_ROOM_ADMISSION_VERIFIED (game world NOT connected)")
    } else if report.LoginAccepted {
        fmt.Println("TAN_LOBBY_LOGIN_VERIFIED (room admission NOT verified)")
        os.Exit(3)
    } else {
        fmt.Println("TAN_LOBBY_STAGE_UNVERIFIED_OR_REJECTED")
        os.Exit(4)
    }
}

// machinePrefix is a rigid framing marker. Unlike the former pretty-printed
// human JSON, it permits strict one-record parsing and is always ASCII.
const machinePrefix = "BLOCKCRAFT_JOIN_PROBE_V1\t"

func emitMachine(report joinprobe.Report) {
    // Never marshal report.Detail, room/player IDs, login tokens or remote
    // errors into machine stdout. Only finite, ASCII status and booleans.
    record := struct {
        Status string `json:"status"`
        LoginAccepted bool `json:"login_accepted"`
        LoginReplyObserved bool `json:"login_reply_observed"`
        LoginReplyCode int8 `json:"login_reply_code"`
        RoomAdmissionAccepted bool `json:"room_admission_accepted"`
        AdmissionReplyObserved bool `json:"admission_reply_observed"`
        AdmissionReplyCode int8 `json:"admission_reply_code"`
        AdmissionDiagnosis string `json:"admission_diagnosis"`
        HostReadySeen bool `json:"host_ready_seen"`
        SignalingAuthenticated bool `json:"signaling_authenticated"`
        WebRTCTransportConnected bool `json:"webrtc_transport_connected"`
        TransportStable bool `json:"transport_stable"`
        ReliableChannelOpen bool `json:"reliable_channel_open"`
        UnreliableChannelOpen bool `json:"unreliable_channel_open"`
        ReliableMessages uint64 `json:"reliable_messages"`
        UnreliableMessages uint64 `json:"unreliable_messages"`
        ReliableBytes uint64 `json:"reliable_bytes"`
        UnreliableBytes uint64 `json:"unreliable_bytes"`
        ObservationSeconds int `json:"observation_seconds"`
        MinecraftWorldJoined bool `json:"minecraft_world_joined"`
        ModeConfirmed bool `json:"g79_mode_confirmed"`
        ReadOnlyIPCVerified bool `json:"stage12_readonly_ipc_verified"`
        ReadOnlyIPCMessages uint64 `json:"stage12_readonly_ipc_messages"`
        Stage12Readiness string `json:"stage12_readiness"`
        GameIdentityVerified bool `json:"stage12_game_identity_verified"`
        GameProtocolVerified bool `json:"stage12_game_protocol_verified"`
        GameLoginAttempted bool `json:"stage12_game_login_attempted"`
    }{
        Status: report.Status,
        LoginAccepted: report.LoginAccepted,
        LoginReplyObserved: report.LoginReplyObserved,
        LoginReplyCode: report.LoginReplyCode,
        RoomAdmissionAccepted: report.RoomAdmissionAccepted,
        AdmissionReplyObserved: report.AdmissionReplyObserved,
        AdmissionReplyCode: report.AdmissionReplyCode,
        AdmissionDiagnosis: report.AdmissionDiagnosis,
        HostReadySeen: report.HostReadySeen,
        SignalingAuthenticated: report.SignalingAuthenticated,
        WebRTCTransportConnected: report.WebRTCTransportConnected,
        TransportStable: report.TransportStable,
        ReliableChannelOpen: report.ReliableChannelOpen,
        UnreliableChannelOpen: report.UnreliableChannelOpen,
        ReliableMessages: report.ReliableMessages,
        UnreliableMessages: report.UnreliableMessages,
        ReliableBytes: report.ReliableBytes,
        UnreliableBytes: report.UnreliableBytes,
        ObservationSeconds: report.ObservationSeconds,
        MinecraftWorldJoined: false,
        ModeConfirmed: report.ModeConfirmed,
        ReadOnlyIPCVerified: report.ReadOnlyIPCVerified,
        ReadOnlyIPCMessages: report.ReadOnlyIPCMessages,
        Stage12Readiness: stage12readiness.Assess(stage12readiness.Evidence{
            LobbyLoginAccepted: report.LoginAccepted,
            RoomAdmissionAccepted: report.RoomAdmissionAccepted,
            HostReadySeen: report.HostReadySeen,
            SignalingAuthenticated: report.SignalingAuthenticated,
            WebRTCTransportConnected: report.WebRTCTransportConnected,
            TransportStable: report.TransportStable,
            ReliableChannelOpen: report.ReliableChannelOpen,
            UnreliableChannelOpen: report.UnreliableChannelOpen,
            G79ModeConfirmed: report.ModeConfirmed,
        }),
        // This probe never invokes Bedrock game login. The successful PE
        // lobby entry and NetherNet ICE/DTLS/SCTP are not proof of game JWTs.
        GameIdentityVerified: false,
        GameProtocolVerified: false,
        GameLoginAttempted: false,
    }
    payload, err := json.Marshal(record)
    if err != nil {
        // Constant fallback; no untrusted error string is shown.
        fmt.Println(machinePrefix + `{"status":"PE_AUTH_UNAVAILABLE","minecraft_world_joined":false}`)
        return
    }
    fmt.Print(machinePrefix)
    fmt.Println(string(payload))
}
