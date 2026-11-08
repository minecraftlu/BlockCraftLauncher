package joinprobe

import (
    "context"
    "errors"
    "log/slog"
    "strings"
    "sync/atomic"

    "github.com/Happy2018new/nemc-tan-lobby-solver/core/nethernet"
    "github.com/pion/sctp"
)

// webRTCObservation only keeps presence/phase bits. It intentionally does
// not record any candidate address, SDP, fingerprint, ICE password, TURN
// credential, NetherNet ID, signal.Data or server error body.
type webRTCObservation struct {
    offerSent atomic.Bool
    localCandidateSent atomic.Bool
    answerSeen atomic.Bool
    remoteCandidateSeen atomic.Bool
    remoteErrorSeen atomic.Bool
    // phase is updated only by known constant messages in upstream dial.go.
    // 0 = before remote candidate; 1 = remote candidate consumed;
    // 2 = starting ICE; 3 = starting DTLS; 4 = starting SCTP;
    // 5 = creating reliable channel; 6 = creating unreliable channel.
    phase atomic.Int32
}

func advancePhase(phase *atomic.Int32, level int32) {
    for {
        current := phase.Load()
        if current >= level || phase.CompareAndSwap(current, level) {
            return
        }
    }
}

// stageLogger records only exact fixed log messages and discards all attrs.
// In particular never use slog's default handler or log raw Pion errors.
type stageLogger struct{ observation *webRTCObservation }

func (h stageLogger) Enabled(context.Context, slog.Level) bool { return true }
func (h stageLogger) Handle(_ context.Context, r slog.Record) error {
    switch r.Message {
    case "received first candidate":
        advancePhase(&h.observation.phase, 1)
    case "starting ICE transport as controller":
        advancePhase(&h.observation.phase, 2)
    case "starting DTLS transport as client":
        advancePhase(&h.observation.phase, 3)
    case "starting SCTP transport":
        advancePhase(&h.observation.phase, 4)
    }
    return nil
}
func (h stageLogger) WithAttrs(_ []slog.Attr) slog.Handler { return h }
func (h stageLogger) WithGroup(_ string) slog.Handler { return h }

// trackedSignaling is a transparent wrapper around the already-authorized
// WebSocket connection. It does not modify, replay or persist signals.
type trackedSignaling struct {
    nethernet.Signaling
    observation *webRTCObservation
}

func (s trackedSignaling) Signal(signal *nethernet.Signal) error {
    err := s.Signaling.Signal(signal)
    if err == nil && signal != nil {
        switch signal.Type {
        case nethernet.SignalTypeOffer:
            s.observation.offerSent.Store(true)
        case nethernet.SignalTypeCandidate:
            s.observation.localCandidateSent.Store(true)
        }
    }
    return err
}
func (s trackedSignaling) Notify(n nethernet.Notifier) func() {
    return s.Signaling.Notify(trackedNotifier{Notifier:n, observation:s.observation})
}

type trackedNotifier struct {
    nethernet.Notifier
    observation *webRTCObservation
}
func (n trackedNotifier) NotifySignal(signal *nethernet.Signal) {
    if signal != nil {
        switch signal.Type {
        case nethernet.SignalTypeAnswer:
            n.observation.answerSeen.Store(true)
        case nethernet.SignalTypeCandidate:
            n.observation.remoteCandidateSeen.Store(true)
        case nethernet.SignalTypeError:
            n.observation.remoteErrorSeen.Store(true)
        }
    }
    n.Notifier.NotifySignal(signal)
}
func (n trackedNotifier) NotifyError(err error) {
    n.Notifier.NotifyError(err)
}

// classifyWebRTCFailure maps upstream library stages to only constant error
// categories. Do not return err.Error() or strings derived from signal.Data.
func classifyWebRTCFailure(err error, o *webRTCObservation) string {
    if err == nil { return "WEBRTC_NEGOTIATION_FAILED" }
    if o.remoteErrorSeen.Load() { return "WEBRTC_REMOTE_REJECTED" }
    msg := err.Error()

    // Use Pion's stable typed errors instead of displaying raw SCTP/DTLS
    // errors. These identify which SCTP handshake message was not answered.
    // In the prior Stage10.1 classifier the broad "start SCTP" prefix was
    // checked first and hid the INIT ACK / COOKIE ECHO distinction.
    switch {
    case errors.Is(err, sctp.ErrHandshakeInitAck):
        return "WEBRTC_SCTP_INIT_ACK_NOT_RECEIVED"
    case errors.Is(err, sctp.ErrHandshakeCookieEcho):
        return "WEBRTC_SCTP_COOKIE_ACK_NOT_RECEIVED"
    case errors.Is(err, sctp.ErrAssociationClosedBeforeConn),
        errors.Is(err, sctp.ErrAssociationClosed):
        return "WEBRTC_SCTP_ASSOCIATION_CLOSED"
    case errors.Is(err, context.Canceled):
        return "WEBRTC_PROBE_CANCELLED"
    case errors.Is(err, context.DeadlineExceeded) ||
        strings.Contains(msg, "context deadline exceeded"):
        // Check before generic "start transports" prefixes, otherwise a
        // timed-out SCTP Start is incorrectly classified as a non-timeout.
        switch o.phase.Load() {
        case 6, 5, 4:
            return "WEBRTC_SCTP_HANDSHAKE_TIMEOUT"
        case 3:
            return "WEBRTC_DTLS_HANDSHAKE_TIMEOUT"
        case 2:
            return "WEBRTC_ICE_CONNECT_TIMEOUT"
        }
        if o.answerSeen.Load() && !o.remoteCandidateSeen.Load() {
            return "WEBRTC_REMOTE_CANDIDATE_TIMEOUT"
        }
        if o.offerSent.Load() && !o.answerSeen.Load() {
            return "WEBRTC_ANSWER_TIMEOUT"
        }
        return "WEBRTC_LOCAL_CANDIDATE_GATHER_TIMEOUT"
    case strings.Contains(msg, "obtain credentials"):
        return "WEBRTC_ICE_CREDENTIALS_FAILED"
    case strings.Contains(msg, "create ICE gatherer"), strings.Contains(msg, "gather local candidates"):
        return "WEBRTC_LOCAL_CANDIDATE_GATHER_FAILED"
    case strings.Contains(msg, "signal offer"):
        return "WEBRTC_OFFER_SEND_FAILED"
    case strings.Contains(msg, "signal candidate"):
        return "WEBRTC_CANDIDATE_SEND_FAILED"
    case strings.Contains(msg, "decode answer"), strings.Contains(msg, "parse offer"),
        strings.Contains(msg, "received signal for non-answer"), strings.Contains(msg, "received nil signal"):
        return "WEBRTC_ANSWER_INVALID"
    case strings.Contains(msg, "start transports: start ICE"):
        return "WEBRTC_ICE_CONNECT_FAILED"
    case strings.Contains(msg, "start transports: start DTLS"):
        return "WEBRTC_DTLS_HANDSHAKE_FAILED"
    case strings.Contains(msg, "start transports: start SCTP"):
        return "WEBRTC_SCTP_HANDSHAKE_FAILED"
    case strings.Contains(msg, "create ReliableDataChannel"),
        strings.Contains(msg, "create UnreliableDataChannel"):
        return "WEBRTC_DATA_CHANNEL_FAILED"
    case strings.Contains(msg, "notified error from signaling"):
        return "WEBRTC_SIGNALING_INTERRUPTED"
    }

    if o.answerSeen.Load() && !o.remoteCandidateSeen.Load() {
        return "WEBRTC_REMOTE_CANDIDATE_UNAVAILABLE"
    }
    return "WEBRTC_NEGOTIATION_FAILED"
}
