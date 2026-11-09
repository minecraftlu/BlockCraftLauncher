package main

import (
    "fmt"
    "io"
    "strconv"

    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BedrockProtocolBridge/internal/neteasecompat"
)

// checkCodecReference is a lookup into a documented third-party codec list.
// It never reads a room version from the lobby or creates a game connection.
// stdout is exactly one fixed machine token, without any account/game data.
func checkCodecReference(input string,w io.Writer) error {
    if len(input)==0 || len(input)>5 || input[0]=='0' {
        return fmt.Errorf("invalid offline codec reference input")
    }
    for _,digit:=range input {
        if digit<'0'||digit>'9' {
            return fmt.Errorf("invalid offline codec reference input")
        }
    }
    number,err:=strconv.ParseUint(input,10,32)
    if err!=nil || number==0 || number>99999 {
        return fmt.Errorf("invalid offline codec reference input")
    }
    result:=neteasecompat.InspectClaimedVersion(uint32(number))
    if result.Status!=neteasecompat.PublishedCodecOnly &&
        result.Status!=neteasecompat.CodecNotDocumented {
        return fmt.Errorf("unexpected offline codec status")
    }
    _,err=fmt.Fprintln(w,result.Status)
    return err
}
