package joinprobe

import (
    "context"
    "net"
    "time"
)

// holdTransport keeps the authenticated, already-negotiated NetherNet
// connection open briefly and passively monitors for early remote closure.
// It never sends gameplay data, reads no application payload into a log and
// is not a substitute for a full Minecraft client/long-running keepalive.
//
// A short observation is intentional: users have reported ghost/offline
// entries in mobile rooms after a probe exits. Keeping a test account
// connected indefinitely would make that worse.
const defaultStabilityWindow = 8 * time.Second

type stabilityOutcome uint8

const (
    stabilityWindowComplete stabilityOutcome = iota
    stabilityRemoteClosed
    stabilityCancelled
)

// nethernet.Conn implements net.Conn and Read unblocks on Conn.Close.
// The caller always closes the connection immediately after this function.
// If the remote closes, it is a real premature close, not a stable result.
func holdTransport(ctx context.Context, conn net.Conn, duration time.Duration) stabilityOutcome {
    if ctx == nil || conn == nil || duration <= 0 {
        return stabilityCancelled
    }
    if ctx.Err() != nil {
        return stabilityCancelled
    }
    closed := make(chan struct{}, 1)
    go func() {
        // Only accept/discard packets; payload is intentionally never
        // inspected, copied to files or displayed. The world is not joined.
        buf := make([]byte, 2048)
        defer clear(buf)
        for {
            if _, err := conn.Read(buf); err != nil {
                closed <- struct{}{}
                return
            }
            clear(buf)
            select {
            case <-ctx.Done():
                return
            default:
            }
        }
    }()

    timer := time.NewTimer(duration)
    defer timer.Stop()
    select {
    case <-ctx.Done():
        _ = conn.Close()
        return stabilityCancelled
    case <-closed:
        return stabilityRemoteClosed
    case <-timer.C:
        // A simultaneous close/cancel is never reported as stable.
        if ctx.Err() != nil {
            _ = conn.Close()
            return stabilityCancelled
        }
        select {
        case <-closed:
            return stabilityRemoteClosed
        default:
            return stabilityWindowComplete
        }
    }
}
