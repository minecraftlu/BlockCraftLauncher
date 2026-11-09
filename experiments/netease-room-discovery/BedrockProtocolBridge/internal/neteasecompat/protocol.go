// Package neteasecompat performs read-only, offline examination of a
// *single uncompressed* Bedrock RequestNetworkSettings message and reports
// whether a third-party NetEase-specific codec is documented for its claimed
// protocol number. Neither this package nor its result authenticates a
// remote account, proves the real room's protocol or initiates game login.
package neteasecompat

import (
    "bytes"
    "encoding/binary"
    "errors"
)

const (
    RequestNetworkSettingsID uint64 = 193 // Official Bedrock Packet ID 0xc1.
    MaxInitialBatchBytes = 32
    EvidenceNotEstablished = "NETEASE_GAME_PROTOCOL_UNKNOWN_GAME_NOT_JOINED"
    PublishedCodecOnly = "NETEASE_CODEC_REFERENCE_FOUND_GAME_NOT_JOINED"
    CodecNotDocumented = "NETEASE_CODEC_UNVERIFIED_GAME_NOT_JOINED"
    ReferenceURL = "https://github.com/AllayMC/Protocol/blob/main/README.md"
)

var (
    ErrInvalidInitialBatch = errors.New("neteasecompat: invalid uncompressed initial request")
    ErrNotNetworkSettingsRequest = errors.New("neteasecompat: not an initial RequestNetworkSettings")
)

// PublishedCodec documents a third-party code path, NOT compatibility with
// a particular NetEase room or acceptance of any login credentials.
type PublishedCodec struct {
    Number uint32
    MinecraftVersion string
    CodecClass string
}

var knownNetEase = map[uint32]PublishedCodec{
    630:{Number:630,MinecraftVersion:"1.20.50–1.20.51",CodecClass:"Bedrock_v630_NetEase"},
    686:{Number:686,MinecraftVersion:"1.21.2",CodecClass:"Bedrock_v686_NetEase"},
    766:{Number:766,MinecraftVersion:"1.21.50",CodecClass:"Bedrock_v766_NetEase"},
    819:{Number:819,MinecraftVersion:"1.21.93–1.21.94",CodecClass:"Bedrock_v819_NetEase"},
}

// Result is a non-authoritative offline diagnosis. All four verification
// flags intentionally remain false even if a published codec was found.
type Result struct {
    Status string
    ClaimedClientProtocol uint32
    PublishedNetEaseCodec bool
    PublishedCodecClass string
    MinecraftVersion string
    GameIdentityVerified bool
    LiveRoomProtocolVerified bool
    LoginAttempted bool
    JoinedWorld bool
}

// InspectClaimedVersion accepts a version number ONLY as a user-provided,
// untrusted value. It is not a protocol detector for room metadata.
func InspectClaimedVersion(version uint32) Result {
    r:=Result{Status:CodecNotDocumented,ClaimedClientProtocol:version}
    if c,ok:=knownNetEase[version];ok {
        r.Status=PublishedCodecOnly
        r.PublishedNetEaseCodec=true
        r.PublishedCodecClass=c.CodecClass
        r.MinecraftVersion=c.MinecraftVersion
    }
    return r
}

// InspectInitialRequest parses exactly one uncompressed, unencrypted Bedrock
// packet in the standard VarUint-length batch format. 0xFE RakNet envelopes,
// compression frames, multiple packets, split-screen headers, extra fields,
// overlong VarInts and truncated payloads fail closed. A packet can *claim*
// a protocol version without being trusted or coming from a real client.
func InspectInitialRequest(batch []byte) (Result,error) {
    if len(batch)==0 || len(batch)>MaxInitialBatchBytes || batch[0]==0xfe {
        return Result{Status:EvidenceNotEstablished},ErrInvalidInitialBatch
    }
    packetLength,n:=binary.Uvarint(batch)
    if n<=0 || !canonicalUvarint(batch[:n],packetLength) {
        return Result{Status:EvidenceNotEstablished},ErrInvalidInitialBatch
    }
    if packetLength!=uint64(len(batch)-n) || packetLength!=6 {
        return Result{Status:EvidenceNotEstablished},ErrInvalidInitialBatch
    }
    body:=batch[n:]
    header,headLen:=binary.Uvarint(body)
    if headLen<=0 || !canonicalUvarint(body[:headLen],header) {
        return Result{Status:EvidenceNotEstablished},ErrInvalidInitialBatch
    }
    if header!=RequestNetworkSettingsID {
        return Result{Status:EvidenceNotEstablished},ErrNotNetworkSettingsRequest
    }
    if len(body)-headLen!=4 {
        return Result{Status:EvidenceNotEstablished},ErrInvalidInitialBatch
    }
    version:=binary.BigEndian.Uint32(body[headLen:])
    if version==0 || version>100000 {
        return Result{Status:EvidenceNotEstablished},ErrInvalidInitialBatch
    }
    return InspectClaimedVersion(version),nil
}

func canonicalUvarint(b []byte,n uint64) bool {
    var canonical [binary.MaxVarintLen64]byte
    written:=binary.PutUvarint(canonical[:],n)
    return len(b)==written && bytes.Equal(b,canonical[:written])
}

// Room metadata versions, including "房间版本标记"/room_version, are
// not Bedrock game protocol numbers. No numeric coincidence can change this.
func InspectLobbyMetadata(_ uint32) Result {
    return Result{Status:EvidenceNotEstablished}
}
