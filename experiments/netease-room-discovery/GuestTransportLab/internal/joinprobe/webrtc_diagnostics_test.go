package joinprobe

import (
    "context"
    "errors"
    "fmt"
    "log/slog"
    "strings"
    "testing"
    "time"
)

func TestWebRTCPhaseClassifierUsesOnlyFixedLabels(t *testing.T) {
    tests := []struct{
        name string
        setup func(*webRTCObservation)
        err error
        status string
    }{
        {"before_offer_timeout", nil, context.DeadlineExceeded, "WEBRTC_LOCAL_CANDIDATE_GATHER_TIMEOUT"},
        {"waiting_answer", func(o *webRTCObservation){o.offerSent.Store(true)}, context.DeadlineExceeded, "WEBRTC_ANSWER_TIMEOUT"},
        {"waiting_host_candidates", func(o *webRTCObservation){o.offerSent.Store(true);o.answerSeen.Store(true)}, context.DeadlineExceeded, "WEBRTC_REMOTE_CANDIDATE_TIMEOUT"},
        {"ice_timeout", func(o *webRTCObservation){o.phase.Store(2)}, context.DeadlineExceeded, "WEBRTC_ICE_CONNECT_TIMEOUT"},
        {"dtls_timeout", func(o *webRTCObservation){o.phase.Store(3)}, context.DeadlineExceeded, "WEBRTC_DTLS_HANDSHAKE_TIMEOUT"},
        {"sctp_timeout", func(o *webRTCObservation){o.phase.Store(4)}, context.DeadlineExceeded, "WEBRTC_SCTP_HANDSHAKE_TIMEOUT"},
        {"answer_invalid", nil, errors.New("decode answer: SECRET_CREDENTIAL"), "WEBRTC_ANSWER_INVALID"},
        {"ice_failure", nil, errors.New("start transports: start ICE: SECRET_HOST_IP"), "WEBRTC_ICE_CONNECT_FAILED"},
        {"dtls_failure", nil, errors.New("start transports: start DTLS: SECRET_TOKEN"), "WEBRTC_DTLS_HANDSHAKE_FAILED"},
        {"sctp_failure", nil, errors.New("start transports: start SCTP: SECRET_TOKEN"), "WEBRTC_SCTP_HANDSHAKE_FAILED"},
        {"channel_failure", nil, errors.New("start transports: create ReliableDataChannel: SECRET"), "WEBRTC_DATA_CHANNEL_FAILED"},
        {"offer_failure", nil, errors.New("signal offer: SECRET_SDPS"), "WEBRTC_OFFER_SEND_FAILED"},
        {"candidate_failure", nil, errors.New("signal candidate: 192.168.1.100"), "WEBRTC_CANDIDATE_SEND_FAILED"},
        {"signaling_drop", nil, errors.New("notified error from signaling: PRIVATE_SERVER_DATA"), "WEBRTC_SIGNALING_INTERRUPTED"},
        {"cancellation", nil, context.Canceled, "WEBRTC_PROBE_CANCELLED"},
        {"remote_rejection", func(o *webRTCObservation){o.remoteErrorSeen.Store(true)}, fmt.Errorf("peer rejected SECRET_SESSION"), "WEBRTC_REMOTE_REJECTED"},
        {"unknown", nil, errors.New("PRIVATE_ICE_PWD_NOT_LOGGED"), "WEBRTC_NEGOTIATION_FAILED"},
    }
    for _,tt:=range tests {
        t.Run(tt.name,func(t *testing.T){
            obs:=&webRTCObservation{}
            if tt.setup!=nil {tt.setup(obs)}
            actual:=classifyWebRTCFailure(tt.err,obs)
            if actual!=tt.status {t.Fatalf("got %q want %q",actual,tt.status)}
            if strings.Contains(actual,"SECRET") || strings.Contains(actual,"192.168") ||
                strings.Contains(actual,"PRIVATE") {
                t.Fatal("Classification leaked raw network or account data")
            }
        })
    }
}

func TestOnlyAllowlistedUpstreamMessagesChangePhase(t *testing.T) {
    obs:=&webRTCObservation{}
    h:=stageLogger{observation:obs}
    events:=[]struct{message string;expected int32}{
        {"secret local ICE pwd 192.168.1.20",0},
        {"received first candidate",1},
        {"starting ICE transport as controller",2},
        {"starting DTLS transport as client",3},
        {"starting SCTP transport",4},
        {"received first candidate",4},
    }
    for _,test:=range events {
        record:=slog.NewRecord(time.Now(),slog.LevelDebug,test.message,0)
        record.AddAttrs(slog.String("credentials","PRIVATE_TURN_PASSWORD"))
        if err:=h.Handle(context.Background(),record);err!=nil {t.Fatal(err)}
        if phase:=obs.phase.Load();phase!=test.expected{
            t.Fatalf("upstream fixed phase was %d expected %d",phase,test.expected)
        }
    }
    if !h.Enabled(context.Background(),slog.LevelDebug) {
        t.Fatal("debug messages needed for safe stage classification")
    }
}
