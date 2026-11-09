package stage12readiness

import (
    "strings"
    "testing"
)

func TestAllTransportMilestonesAreNotGameAuthorization(t *testing.T) {
    complete:=Evidence{
        LobbyLoginAccepted:true, RoomAdmissionAccepted:true,
        HostReadySeen:true, SignalingAuthenticated:true,
        WebRTCTransportConnected:true, TransportStable:true,
        ReliableChannelOpen:true, UnreliableChannelOpen:true,
    }
    for _,mode:=range []bool{false,true} {
        complete.G79ModeConfirmed=mode
        actual:=Assess(complete)
        if actual!=GameIdentityAndProtocolUnverified {
            t.Fatalf("G79 mode=%v falsely granted game login: %q",mode,actual)
        }
        if strings.Contains(actual,"GAME_JOINED") || strings.Contains(actual,"AUTHORIZED") {
            t.Fatalf("transport evidence falsely represented game authorization: %q",actual)
        }
    }
}

func TestMissingEvidenceReportedAtEarliestUnverifiedPhase(t *testing.T) {
    all:=Evidence{
        LobbyLoginAccepted:true, RoomAdmissionAccepted:true,
        HostReadySeen:true, SignalingAuthenticated:true,
        WebRTCTransportConnected:true, TransportStable:true,
        ReliableChannelOpen:true, UnreliableChannelOpen:true,
    }
    tests:=[]struct {
        name string; mutate func(*Evidence); want string
    }{
        {"login",func(e *Evidence){e.LobbyLoginAccepted=false},AdmissionNotVerified},
        {"admission",func(e *Evidence){e.RoomAdmissionAccepted=false},AdmissionNotVerified},
        {"host",func(e *Evidence){e.HostReadySeen=false},HostNotReady},
        {"signaling",func(e *Evidence){e.SignalingAuthenticated=false},SignalingNotVerified},
        {"webrtc",func(e *Evidence){e.WebRTCTransportConnected=false},TransportNotConnected},
        {"stability",func(e *Evidence){e.TransportStable=false},TransportNotStable},
        {"reliable",func(e *Evidence){e.ReliableChannelOpen=false},DataChannelsNotVerified},
        {"unreliable",func(e *Evidence){e.UnreliableChannelOpen=false},DataChannelsNotVerified},
    }
    for _,tc:=range tests {
        t.Run(tc.name,func(t *testing.T){
            evidence:=all
            tc.mutate(&evidence)
            if got:=Assess(evidence); got!=tc.want {
                t.Fatalf("expected %s, got %s",tc.want,got)
            }
        })
    }
}

func TestZeroEvidenceCannotClaimGameReadiness(t *testing.T) {
    if actual:=Assess(Evidence{});actual!=AdmissionNotVerified {
        t.Fatalf("empty proof unexpectedly passed: %s",actual)
    }
}
