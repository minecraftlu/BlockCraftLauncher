package main

import (
    "context"
    "encoding/json"
    "flag"
    "fmt"
    "os"
    "path/filepath"
    "time"

    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab/internal/joinprobe"
)

func main() {
    room := flag.String("room", "", "your own six-digit NetEase mobile lobby room")
    helper := flag.String("helper", "", "absolute path of your previously reviewed connect.exe")
    cookie := flag.String("cookie", "", "absolute path of your locally stored second-account cookie JSON")
    authorized := flag.Bool("host-consent", false, "I own this room or have explicit permission from the host")
    attempt := flag.Bool("try-join", false, "allow exactly one real RakNet login and room admission attempt")
    timeout := flag.Duration("timeout", 60*time.Second, "maximum total duration (10s-120s)")
    machine := flag.Bool("machine-json", false, "emit one versioned ASCII-safe JSON record without secrets")
    ipcSelftest := flag.Bool("ipc-selftest", false, "emit synthetic IPC report without network, account or room access")
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

    if *room == "" || *helper == "" || *cookie == "" || !*authorized || !*attempt {
        fmt.Println("BlockCraft Stage 9 genuine NetEase lobby join probe (NOT a game client)")
        fmt.Println("Required: -room ROOM -helper ABSOLUTE_CONNECT_EXE -cookie ABSOLUTE_COOKIE_JSON -host-consent -try-join")
        fmt.Println("The second account must already be legitimately logged in on this PC.")
        fmt.Println("No cookies, login tokens, encryption keys or player IDs are printed.")
        fmt.Println("This tool may briefly enter the real phone room and then leave when the probe exits.")
        fmt.Println("It never bypasses account verification, anti-cheat, real-name requirements or server admission.")
        return
    }

    if *timeout < 10*time.Second || *timeout > 120*time.Second {
        fmt.Fprintln(os.Stderr, "Timeout must be between 10s and 120s.")
        os.Exit(2)
    }

    ctx,cancel:=context.WithTimeout(context.Background(),*timeout)
    defer cancel()
    report:=joinprobe.Run(ctx,joinprobe.Request{
        HelperPath:filepath.Clean(*helper),
        CookiePath:filepath.Clean(*cookie),
        RoomID:*room,
        AuthorizedByHost:*authorized,
    })
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
        RoomAdmissionAccepted bool `json:"room_admission_accepted"`
        HostReadySeen bool `json:"host_ready_seen"`
        MinecraftWorldJoined bool `json:"minecraft_world_joined"`
        ModeConfirmed bool `json:"g79_mode_confirmed"`
    }{
        Status: report.Status,
        LoginAccepted: report.LoginAccepted,
        RoomAdmissionAccepted: report.RoomAdmissionAccepted,
        HostReadySeen: report.HostReadySeen,
        MinecraftWorldJoined: false,
        ModeConfirmed: report.ModeConfirmed,
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
