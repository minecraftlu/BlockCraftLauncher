package neteasecompat

import (
    "encoding/binary"
    "errors"
    "testing"
)

func syntheticRequest(version uint32) []byte {
    // One uncompressed standard Bedrock packet: VarUint32 length (6),
    // header VarUint32 (193 = 0xc1,0x01), then 4-byte BE client version.
    b:=[]byte{6,0xc1,0x01,0,0,0,0}
    binary.BigEndian.PutUint32(b[3:],version)
    return b
}

func TestPublishedNetEaseCodecDoesNotGrantGameAdmission(t *testing.T) {
    known:=map[uint32]string{
        630:"Bedrock_v630_NetEase",686:"Bedrock_v686_NetEase",
        766:"Bedrock_v766_NetEase",819:"Bedrock_v819_NetEase",
    }
    for version,codec:=range known {
        r,err:=InspectInitialRequest(syntheticRequest(version))
        if err!=nil || r.Status!=PublishedCodecOnly ||
            r.PublishedCodecClass!=codec || !r.PublishedNetEaseCodec {
            t.Fatalf("published version %d not reported correctly: %+v %v",version,r,err)
        }
        if r.GameIdentityVerified || r.LiveRoomProtocolVerified ||
            r.LoginAttempted || r.JoinedWorld {
            t.Fatalf("reference codec %d was mistaken for real room admission: %+v",version,r)
        }
    }
}

func TestStandardBedrockCodecIsNotAssumedToBeNetEaseCodec(t *testing.T) {
    for _,version:=range []uint32{859,860,897,944,975,1001,2225,898} {
        r,err:=InspectInitialRequest(syntheticRequest(version))
        if err!=nil {t.Fatal(err)}
        if r.PublishedNetEaseCodec || r.Status!=CodecNotDocumented {
            t.Fatalf("standard/unknown codec %d accepted as NetEase: %+v",version,r)
        }
    }
}

func TestLobbyVersionCanNeverMasqueradeAsGameVersion(t *testing.T) {
    for _,version:=range []uint32{0,48,630,686,766,819,859,898,2225} {
        r:=InspectLobbyMetadata(version)
        if r.Status!=EvidenceNotEstablished || r.ClaimedClientProtocol!=0 ||
            r.PublishedNetEaseCodec || r.GameIdentityVerified || r.LoginAttempted {
            t.Fatalf("room metadata version %d falsely established game protocol: %+v",version,r)
        }
    }
}

func TestInitialRequestRejectsMalformedOrUnsafeData(t *testing.T) {
    good:=syntheticRequest(819)
    cases:=[]struct{name string;packet []byte}{
        {"empty",nil},
        {"wrong-length",append([]byte{5},good[1:]...)},
        {"extra-packet",append(append([]byte(nil),good...),0)},
        {"not-a-settings-request",[]byte{6,0x01,0x00,0,0,3,0x33}},
        {"split-screen-header",[]byte{6,0xc1,0x09,0,0,3,0x33}},
        {"raknet-envelope",append([]byte{0xfe},good...)},
        {"truncated",good[:len(good)-1]},
        {"overlong-length",append([]byte{0x86,0x00},good[1:]...)},
        {"overlong-header",[]byte{7,0xc1,0x81,0x00,0,0,3,0x33}},
        {"invalid-zero-version",syntheticRequest(0)},
        {"oversized-version",syntheticRequest(100001)},
        {"invalid-varuint",[]byte{0x80,0x80,0x80,0x80,0x80,0x80,0x80,0x80,0x80,0x80}},
        {"huge-payload",make([]byte,MaxInitialBatchBytes+1)},
    }
    for _,tc:=range cases {
        t.Run(tc.name,func(t *testing.T){
            r,err:=InspectInitialRequest(tc.packet)
            if err==nil || r.PublishedNetEaseCodec ||
                r.Status!=EvidenceNotEstablished || r.LoginAttempted {
                t.Fatalf("unsafe initial packet passed: result=%+v err=%v",r,err)
            }
        })
    }
}

func TestWrongPacketIDIsReportedDistinctly(t *testing.T) {
    _,err:=InspectInitialRequest([]byte{6,0x01,0x00,0,0,3,0x33})
    if !errors.Is(err,ErrNotNetworkSettingsRequest){
        t.Fatalf("expected unexpected game packet, got %v",err)
    }
}
