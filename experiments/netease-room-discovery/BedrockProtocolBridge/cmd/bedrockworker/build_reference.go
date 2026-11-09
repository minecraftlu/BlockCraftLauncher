package main

import (
    "fmt"
    "io"

    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BedrockProtocolBridge/internal/neteasecompat"
)

// This offline lookup cannot inspect or verify the user's actual phone
// client, read Minecraft packets, create an account or dial NetEase servers.
// Both inputs are screenshot-style local launcher build identifiers.
func checkChinaBuildReference(gameBuild,patchBuild string,w io.Writer) error {
    evidence:=neteasecompat.InspectClientBuildPair(gameBuild,patchBuild)
    if evidence.Status!=neteasecompat.China310CandidateOnly &&
        evidence.Status!=neteasecompat.ChinaBuildUnsupported {
        return fmt.Errorf("unexpected reference status")
    }
    // Only a fixed machine code, never the provided strings or game data.
    _,err:=fmt.Fprintln(w,evidence.Status)
    return err
}
