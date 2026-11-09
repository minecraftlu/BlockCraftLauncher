package main

import (
    "bytes"
    "strings"
    "testing"

    "github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BedrockProtocolBridge/internal/neteasecompat"
)

func TestOfflineCodecReferenceOnlyMentionsPublicSupport(t *testing.T) {
    cases:=[]struct{value string;want string}{
        {"630",neteasecompat.PublishedCodecOnly},
        {"686",neteasecompat.PublishedCodecOnly},
        {"766",neteasecompat.PublishedCodecOnly},
        {"819",neteasecompat.PublishedCodecOnly},
        {"859",neteasecompat.CodecNotDocumented},
        {"860",neteasecompat.CodecNotDocumented},
        {"898",neteasecompat.CodecNotDocumented},
    }
    for _,tc:=range cases {
        var out bytes.Buffer
        if err:=checkCodecReference(tc.value,&out);err!=nil {t.Fatal(err)}
        if got:=strings.TrimSpace(out.String());got!=tc.want {
            t.Fatalf("version=%s, want %s, got %s",tc.value,tc.want,got)
        }
        if strings.Contains(out.String(),tc.value) || strings.Contains(out.String(),"LOGIN_ALLOWED") {
            t.Fatalf("CLI printed user-supplied input or implied authentication: %q",out.String())
        }
    }
}

func TestOfflineCodecReferenceRejectsNonNumericInputWithoutEcho(t *testing.T) {
    for _,input:=range []string{"","0","00","0630"," 819","819 ","-1","+819","819;rm","123456","abc","9\nSECRET"}{
        var out bytes.Buffer
        if err:=checkCodecReference(input,&out);err==nil || out.Len()!=0 {
            t.Fatalf("unsafe input accepted or echoed: input length=%d",len(input))
        }
    }
}
