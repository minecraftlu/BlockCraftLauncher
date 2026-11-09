package neteasecompat

import "testing"

func TestChina310ScreenshotBuildsProduceOnlyUnverifiedStandardCandidates(t *testing.T) {
    evidence:=InspectClientBuildPair("3.10.7.301588","3.10.9.301882")
    if evidence.Status!=China310CandidateOnly || !evidence.ClientBuildFamilyMatched ||
        evidence.AssumedBedrockFamily=="" ||
        len(evidence.CandidateStandardProtocols)!=2 ||
        evidence.CandidateStandardProtocols[0]!=897 ||
        evidence.CandidateStandardProtocols[1]!=898 {
        t.Fatalf("China 3.10 candidate reference not classified: %+v",evidence)
    }
    if evidence.ActualPhoneVersionVerified || evidence.ActualNetEaseProtocolVerified ||
        evidence.GameIdentityVerified || evidence.LoginAttempted || evidence.WorldJoined {
        t.Fatalf("client screenshot or standard protocol table falsely granted NetEase game access: %+v",evidence)
    }
}

func TestUnrelatedBuildsCannotBeMappedTo310Candidates(t *testing.T) {
    tests:=[]struct{game,patch string}{
        {"","3.10.9.301882"},
        {"3.10.7.301588",""},
        {"3.9.50.301056","3.9.42.301055"},
        {"3.10.7.301588","3.9.9.301882"},
        {"3.10.7.301588","3.11.9.301882"},
        {"1.21.130","1.21.130"},
        {"898","898"},
        {"819","819"},
        {"3.10.7.301588;SECRET","3.10.9.301882"},
        {"3.10.7.301588\nNETEASE_TOKEN","3.10.9.301882"},
        {"3.10.007.301588","3.10.9.301882"},
        {"3.10.7.30158","3.10.9.301882"},
        {"3.10.7.301588","3.10.9.301882-extra"},
    }
    for _,tc:=range tests {
        ev:=InspectClientBuildPair(tc.game,tc.patch)
        if ev.Status!=ChinaBuildUnsupported || ev.ClientBuildFamilyMatched ||
            len(ev.CandidateStandardProtocols)!=0 || ev.ActualNetEaseProtocolVerified ||
            ev.GameIdentityVerified || ev.LoginAttempted || ev.WorldJoined {
            t.Fatalf("untrusted/out-of-family build mapped to game protocol: %+v",ev)
        }
    }
}

func TestLobbyVersionCannotUpgradeToGameProtocolEvenIfNumberMatches(t *testing.T) {
    for _,v:=range []uint32{48,630,819,860,897,898,301588,301882} {
        ev:=InspectChinaLobbyVersion(v)
        if ev.Status!=ChinaBuildUnsupported ||
            len(ev.CandidateStandardProtocols)!=0 ||
            ev.ActualNetEaseProtocolVerified || ev.GameIdentityVerified || ev.WorldJoined {
            t.Fatalf("lobby version %d used as game protocol: %+v",v,ev)
        }
    }
}

func TestUnreleased860ReferenceIsNotPublishedCodecSuccess(t *testing.T) {
    r:=InspectClaimedVersion(860)
    if r.Status!=CodecNotDocumented || r.PublishedNetEaseCodec ||
        r.GameIdentityVerified || r.LiveRoomProtocolVerified || r.LoginAttempted {
        t.Fatalf("unreleased NetEase 860 changelog was treated as released verified codec: %+v",r)
    }
}
