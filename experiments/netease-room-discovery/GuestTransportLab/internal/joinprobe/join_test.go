package joinprobe

import (
    "bytes"
    "encoding/json"
    "strings"
    "testing"
)

func TestParseSecondAccountSession(t *testing.T) {
    privateToken := "0123456789abcdef"
    raw := "uid=10001\nnickname=LocalGuest\nplatform=2\nlogin_token=" + privateToken + "\n"
    info,err:=parseLogin([]byte(raw))
    if err!=nil {t.Fatal(err)}
    if info.uid!=10001 || info.nickname!="LocalGuest" || info.g79Confirmed || !bytes.Equal(info.token,[]byte(privateToken)){
        t.Fatalf("wrong second-account parsing (DO NOT dump credentials)")
    }
    clear(info.token)
    for _,test:=range []string{
        strings.Replace(raw,"platform=2","platform=1",1),
        strings.Replace(raw,"nickname=LocalGuest","nickname=",1),
        strings.Replace(raw,privateToken,"short",1),
        raw+"auth_mode=x19\n",
    }{
        if _,err:=parseLogin([]byte(test));err==nil {t.Fatal("unsafe session accepted")}
    }
}

func TestReadRoomAndServerWithoutCredentials(t *testing.T) {
    roomJSON:=[]byte(`{"code":0,"list":[{"rid":"280474","name":"280474","hid":100100,"srv":9366,"cnt":1,"cap":30,"need_password":0}]}`)
    roomInfo,err:=parseRoomJSON(roomJSON,"280474")
    if err!=nil {t.Fatal(err)}
    if roomInfo.owner!=100100 || roomInfo.roomID!=280474 || roomInfo.srv!=9366 {t.Fatal("bad room correlation")}
    if _,err:=parseRoomJSON(roomJSON,"280475");err==nil {t.Fatal("wrong room accepted")}
    full:=[]byte(`{"code":0,"list":[{"rid":280474,"hid":100100,"srv":9366,"cnt":30,"cap":30}]}`)
    if _,err:=parseRoomJSON(full,"280474");err==nil {t.Fatal("full room accepted")}
    protected:=[]byte(`{"code":0,"list":[{"rid":280474,"hid":100100,"srv":9366,"need_password":1}]}`)
    if _,err:=parseRoomJSON(protected,"280474");err==nil {t.Fatal("password room accepted")}
    nodeJSON:=[]byte(`[{"id":9366,"ip":"42.186.165.240","ports":[19132,19133],"SignalWebPort":8900}]`)
    node,err:=parseServerJSON(nodeJSON,9366)
    if err!=nil {t.Fatal(err)}
    if node.host!="42.186.165.240" || node.port!=19132 {t.Fatal("bad public node port")}
    if _,err:=parseServerJSON(nodeJSON,99);err==nil {t.Fatal("wrong server accepted")}
}

func TestLegacyWireMaterialAndDisposal(t *testing.T) {
    token:=[]byte("0123456789abcdef")
    material,err:=derive(token)
    if err!=nil {t.Fatal(err)}
    if len(material.rand)!=16 || len(material.aesRand)!=32 ||
        len(material.encKey)!=32 || len(material.decKey)!=32 {t.Fatal("wrong lengths")}
    encrypted:=material.aesRand
    key:=material.encKey
    material.destroy()
    for _,field:=range [][]byte{encrypted,key}{
        for _,value:=range field {if value!=0 {t.Fatal("key material not cleared")}}
    }
    if _,err:=derive([]byte("not16"));err==nil {t.Fatal("bad token length accepted")}
}

func TestStatusNeverClaimsMinecraftWorld(t *testing.T) {
    report:=makeReport("SERVER_ROOM_ADMISSION_ACCEPTED","only a fixture")
    report.RoomAdmissionAccepted=true
    out,err:=json.Marshal(report)
    if err!=nil {t.Fatal(err)}
    if bytes.Contains(out,[]byte(`"minecraft_world_joined":true`)) {
        t.Fatal("lobby admission incorrectly marked game connected")
    }
    if Run(nil,Request{AuthorizedByHost:false}).Status!="HOST_CONSENT_REQUIRED" {
        t.Fatal("host permission gate missing")
    }
}
