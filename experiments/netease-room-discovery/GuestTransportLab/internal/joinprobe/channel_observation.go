package joinprobe

import (
    "context"
    "sync/atomic"
    "time"
)

// channelObservationSource is implemented by the pinned NetherNet Conn ONLY
// after applying BlockCraft's vetted, build-local diagnostics shim. Do not
// alter the known-good SCTP CRC32c transport or send gameplay packets.
type channelObservationSource interface {
    ReadPacket() ([]byte, error)
    Close() error
    BlockCraftChannelStates() (reliableOpen bool, unreliableOpen bool)
    BlockCraftObserveUnreliableLengths(func(int))
}

type channelObservation struct {
    outcome stabilityOutcome
    reliableOpen bool
    unreliableOpen bool
    reliableMessages uint64
    unreliableMessages uint64
    reliableBytes uint64
    unreliableBytes uint64
}

type passiveChannelCounters struct {
    reliableMessages atomic.Uint64
    unreliableMessages atomic.Uint64
    reliableBytes atomic.Uint64
    unreliableBytes atomic.Uint64
}

const maxReportedPassiveMessages = uint64(1000000)
const maxReportedPassiveBytes = uint64(1000000000)

// Observe only application-message counts and sizes; never copy payload data
// into a report, process argument, log, file or external service. The reliable
// packet buffer is cleared immediately after counting. NetherNet's existing
// reliable channel handler owns reassembly; the previously unused unreliable
// callback is length-only. No data is sent to the mobile host.
func observeDataChannels(ctx context.Context, conn channelObservationSource, duration time.Duration) channelObservation {
    obs := channelObservation{outcome: stabilityCancelled}
    if ctx == nil || conn == nil || duration <= 0 || ctx.Err() != nil {
        return obs
    }
    // Close after snapshot to ensure a blocked ReadPacket goroutine terminates.
    // *nethernet.Conn.Close* is idempotent; the existing caller also defers it.
    defer conn.Close()
    counters := &passiveChannelCounters{}
    conn.BlockCraftObserveUnreliableLengths(func(length int) {
        if length <= 0 { return }
        counters.unreliableMessages.Add(1)
        counters.unreliableBytes.Add(uint64(length))
    })
    closed := make(chan struct{}, 1)
    go func() {
        for {
            data, err := conn.ReadPacket()
            if err != nil {
                select { case closed <- struct{}{}: default: }
                return
            }
            // Ignore empty control/noise frames; they do not prove gameplay.
            if len(data) > 0 {
                counters.reliableMessages.Add(1)
                counters.reliableBytes.Add(uint64(len(data)))
            }
            clear(data)
            if ctx.Err() != nil { return }
        }
    }()
    timer := time.NewTimer(duration)
    defer timer.Stop()
    select {
    case <-ctx.Done():
        obs.outcome = stabilityCancelled
    case <-closed:
        obs.outcome = stabilityRemoteClosed
    case <-timer.C:
        if ctx.Err() != nil {
            obs.outcome = stabilityCancelled
        } else {
            select {
            case <-closed:
                obs.outcome = stabilityRemoteClosed
            default:
                obs.outcome = stabilityWindowComplete
            }
        }
    }
    // Snapshot only after timer/read outcome; channels can report CONNECTING
    // despite the SCTP transport being established. Never equate SCTP
    // handshake success with both Pion data channels actually being OPEN.
    if obs.outcome == stabilityWindowComplete {
        obs.reliableOpen, obs.unreliableOpen = conn.BlockCraftChannelStates()
    }
    obs.reliableMessages = min(counters.reliableMessages.Load(), maxReportedPassiveMessages)
    obs.unreliableMessages = min(counters.unreliableMessages.Load(), maxReportedPassiveMessages)
    obs.reliableBytes = min(counters.reliableBytes.Load(), maxReportedPassiveBytes)
    obs.unreliableBytes = min(counters.unreliableBytes.Load(), maxReportedPassiveBytes)
    return obs
}
