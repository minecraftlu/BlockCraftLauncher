package main

import (
    "encoding/json"
    "flag"
    "fmt"
    "os"
    "time"

    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab/internal/transport"
)

func main() {
    local := flag.Bool("selftest", false, "run an isolated WebRTC client/host transport test")
    timeout := flag.Duration("timeout", 20*time.Second, "maximum time spent on the local self-test")
    flag.Parse()

    if !*local {
        fmt.Println("BlockCraft Guest Transport Lab (research only)")
        fmt.Println("Run: BlockCraft.GuestTransportLab.exe -selftest")
        fmt.Println("This executable does not login, join NetEase rooms, or launch Minecraft.")
        return
    }

    report, err := transport.RunWithTimeout(*timeout)
    out, marshalErr := json.MarshalIndent(report, "", "  ")
    if marshalErr == nil {
        fmt.Println(string(out))
    }
    if err != nil {
        fmt.Fprintf(os.Stderr, "LOCAL_SELFTEST_FAILED: %v\n", err)
        os.Exit(2)
    }
    fmt.Println("LOCAL_SELFTEST_PASS (no external network session)")
}
