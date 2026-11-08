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
    flag.Parse()

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
