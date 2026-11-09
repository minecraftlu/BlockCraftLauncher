package main

import (
    "bytes"
    "strings"
    "testing"

    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BedrockProtocolBridge/internal/neteasecompat"
)

func TestChinaBuildOfflineCLIProducesOnlyFixedEvidenceStatus(t *testing.T) {
    tests:=[]struct{game,patch,want string}{
        {"3.10.7.301588","3.10.9.301882",neteasecompat.China310CandidateOnly},
        {"3.9.50.301056","3.9.42.301055",neteasecompat.ChinaBuildUnsupported},
        {"898","898",neteasecompat.ChinaBuildUnsupported},
        {"3.10.7.301588;TOKEN","3.10.9.301882",neteasecompat.ChinaBuildUnsupported},
    }
    for _,tc:=range tests{
        var out bytes.Buffer
        if err:=checkChinaBuildReference(tc.game,tc.patch,&out);err!=nil {t.Fatal(err)}
        if got:=strings.TrimSpace(out.String());got!=tc.want {t.Fatalf("want %q got %q",tc.want,got)}
        if strings.Contains(out.String(),tc.game)||strings.Contains(out.String(),tc.patch)||
            strings.Contains(out.String(),"TOKEN") {t.Fatalf("unsafe CLI echoed input")}
    }
}
