package neteasecompat

import (
    "strconv"
    "strings"
)

// The screenshot-provided China Edition 3.10.x release/patch numbers are
// launcher build strings, NOT the Bedrock wire protocol or an identity proof.
//
// Public references disagree about the standard 1.21.130 protocol number:
// the AllayMC/Protocol README lists 897 for 1.21.130–1.21.132,
// while Minecraft Wiki lists 898 for the final 1.21.130–1.21.132.
// Neither source demonstrates a NetEase 897/898 codec or the user's
// *phone-hosted* room's actual game protocol.
const (
    China310CandidateOnly = "STAGE12_8_CHINA_310_REFERENCE_CANDIDATES_UNVERIFIED_GAME_NOT_JOINED"
    ChinaBuildUnsupported = "STAGE12_8_CHINA_BUILD_REFERENCE_UNKNOWN_GAME_NOT_JOINED"
    Standard897Source = "https://github.com/AllayMC/Protocol/blob/main/README.md"
    Standard898Source = "https://minecraft.wiki/w/Protocol_version"
    China310Source = "https://minecraft.wiki/w/China_Edition"
)

// ClientBuildEvidence is deliberately non-authoritative. It provides a
// research shortlist, never a real game-protocol detection result.
type ClientBuildEvidence struct {
    Status string
    CandidateStandardProtocols []uint32
    AssumedBedrockFamily string
    ClientBuildFamilyMatched bool
    ActualPhoneVersionVerified bool
    ActualNetEaseProtocolVerified bool
    GameIdentityVerified bool
    LoginAttempted bool
    WorldJoined bool
}

// parseChinaBuild accepts only 4-part numerical *client build* strings.
// The current offline research is restricted to the 3.10 family and six-digit
// build numbers. Never pass raw input into a shell or network request.
func parseChinaBuild(text string) (bool,bool) {
    if len(text)<11 || len(text)>24 {return false,false}
    parts:=strings.Split(text,".")
    if len(parts)!=4 {return false,false}
    for _,p:=range parts {
        if len(p)==0 || (len(p)>1 && p[0]=='0') {return false,false}
        for _,ch:=range p {if ch<'0'||ch>'9'{return false,false}}
    }
    if len(parts[3])!=6 || len(parts[2])>3 {return false,false}
    major,err:=strconv.Atoi(parts[0]);if err!=nil{return false,false}
    minor,err:=strconv.Atoi(parts[1]);if err!=nil{return false,false}
    patch,err:=strconv.Atoi(parts[2]);if err!=nil || patch>999{return false,false}
    return true,major==3 && minor==10
}

// InspectClientBuildPair takes only explicitly supplied *client* and patch
// build strings, not room metadata, platform auth mode, or packet contents.
// This currently supports a research *candidate* for the 3.10 family only.
func InspectClientBuildPair(gameBuild, patchBuild string) ClientBuildEvidence {
    r:=ClientBuildEvidence{Status:ChinaBuildUnsupported}
    gameValid,game310:=parseChinaBuild(gameBuild)
    patchValid,patch310:=parseChinaBuild(patchBuild)
    if !gameValid || !patchValid || !game310 || !patch310{return r}
    r.Status=China310CandidateOnly
    r.AssumedBedrockFamily="1.21.130 (public China 3.10 series reference only)"
    r.ClientBuildFamilyMatched=true
    r.CandidateStandardProtocols=[]uint32{897,898}
    return r
}

// A lobby's "room_version", even when it matches 897/898, is not proof of
// actual game-version or game-identity equivalence.
func InspectChinaLobbyVersion(_ uint32) ClientBuildEvidence {
    return ClientBuildEvidence{Status:ChinaBuildUnsupported}
}
