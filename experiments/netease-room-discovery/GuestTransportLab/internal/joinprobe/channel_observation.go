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
    // A failed local IPC sink is NOT a remote game/server rejection.
    forwardError bool
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
    return observeDataChannelsWithSink(ctx,conn,duration,nil)
}

// The optional sink consumes reliable messages on the SAME reader used by
// Stage11.1. Never launch a competing ReadPacket goroutine on the same RTC
// connection. A receive-only worker has no path to send game packets back.
func observeDataChannelsWithSink(ctx context.Context, conn channelObservationSource,
    duration time.Duration, sink func([]byte) error) channelObservation {
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
    readerExited:=make(chan struct{})
    var sinkFailed atomic.Bool
    go func() {
        defer close(readerExited)
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
                if sink!=nil {
                    if err:=sink(data);err!=nil {
                        sinkFailed.Store(true)
                        clear(data)
                        _=conn.Close()
                        select {case closed<-struct{}{}:default:}
                        return
                    }
                }
            }
            clear(data)
            if ctx.Err() != nil { return }
        }
    }()
    timer := time.NewTimer(duration)
    defer timer.Stop()

    // A final OPEN snapshot cannot prove that the data channels remained OPEN
    // throughout the window. Poll their read-only states while the packet
    // reader is blocked; this catches persistent mid-window drops without
    // sending Minecraft packets or recording any channel payload.
    pollEvery := 100 * time.Millisecond
    if duration/4 < pollEvery { pollEvery = duration/4 }
    if pollEvery < time.Millisecond { pollEvery = time.Millisecond }
    poll := time.NewTicker(pollEvery)
    defer poll.Stop()
    reliableOpen, unreliableOpen := conn.BlockCraftChannelStates()
    bothOpenSeen := reliableOpen && unreliableOpen
    for {
        select {
        case <-ctx.Done():
            obs.outcome = stabilityCancelled
        case <-closed:
            obs.outcome = stabilityRemoteClosed
        case <-poll.C:
            if ctx.Err() != nil {
                obs.outcome = stabilityCancelled
                break
            }
            reliableOpen, unreliableOpen = conn.BlockCraftChannelStates()
            if bothOpenSeen && (!reliableOpen || !unreliableOpen) {
                obs.outcome = stabilityRemoteClosed
                break
            }
            if reliableOpen && unreliableOpen { bothOpenSeen = true }
            continue
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
        break
    }
    if obs.outcome == stabilityWindowComplete {
        obs.reliableOpen, obs.unreliableOpen = conn.BlockCraftChannelStates()
        // A close racing with the final poll is still a failed observation.
        // Sampling cannot prove continuous OPEN between polls.
        if bothOpenSeen && (!obs.reliableOpen || !obs.unreliableOpen) {
            obs.outcome = stabilityRemoteClosed
        }
    }
    obs.reliableMessages = min(counters.reliableMessages.Load(), maxReportedPassiveMessages)
    obs.unreliableMessages = min(counters.unreliableMessages.Load(), maxReportedPassiveMessages)
    obs.reliableBytes = min(counters.reliableBytes.Load(), maxReportedPassiveBytes)
    obs.unreliableBytes = min(counters.unreliableBytes.Load(), maxReportedPassiveBytes)
    // On a worker-backed session, return only after the single network
    // reader has released its potential STDIN writer. Otherwise Stop/EOF
    // can race an unfinished DATA frame and falsely fail the worker.
    _=conn.Close()
    if sink!=nil {
        select {
        case <-readerExited:
        case <-time.After(2*time.Second):
            obs.forwardError=true
        }
    }
    obs.forwardError=obs.forwardError || sinkFailed.Load()
    return obs
}
