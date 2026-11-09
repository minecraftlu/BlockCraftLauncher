package bridgewire

import (
    "bytes"
    "encoding/binary"
    "errors"
    "io"
    "testing"
)

func TestRoundTripAllKinds(t *testing.T) {
    for _,f:=range []Frame{
        {Hello,bytes.Repeat([]byte{0x42},NonceBytes)},
        {Ready,bytes.Repeat([]byte{0x77},NonceBytes)},
        {Data,[]byte{0,0xfe,0x01,0x80,0xff}},
        {Stop,nil},{Stopped,nil},
    }{
        var b bytes.Buffer
        if err:=WriteFrame(&b,f);err!=nil{t.Fatal(err)}
        got,err:=ReadFrame(&b)
        if err!=nil || got.Kind!=f.Kind || !bytes.Equal(got.Payload,f.Payload) {
            t.Fatalf("round trip invalid: %v",err)
        }
    }
}

func TestProtocolFailsClosedWithoutUnboundedAllocation(t *testing.T) {
    var good bytes.Buffer
    if err:=WriteFrame(&good,Frame{Kind:Data,Payload:[]byte{0x2a}});err!=nil {t.Fatal(err)}
    frame:=good.Bytes()
    corrupt:=func(fn func([]byte)) []byte {
        cp:=append([]byte(nil),frame...)
        fn(cp)
        return cp
    }
    tests:=[]struct{name string;data []byte;want error}{
        {"bad-magic",corrupt(func(b []byte){b[0]='X'}),ErrMalformed},
        {"wrong-version",corrupt(func(b []byte){b[4]=255}),ErrUnsupported},
        {"unknown-kind",corrupt(func(b []byte){b[5]=0xff}),ErrUnsupported},
        {"oversized",corrupt(func(b []byte){binary.BigEndian.PutUint32(b[6:],MaxPayload+1)}),ErrTooLarge},
        {"invalid-empty-data",corrupt(func(b []byte){binary.BigEndian.PutUint32(b[6:],0)}),ErrMalformed},
        {"truncated-header",frame[:3],io.ErrUnexpectedEOF},
        {"truncated-payload",frame[:len(frame)-1],io.ErrUnexpectedEOF},
    }
    for _,tt:=range tests {
        t.Run(tt.name,func(t *testing.T){
            _,err:=ReadFrame(bytes.NewReader(tt.data))
            if !errors.Is(err,tt.want){t.Fatalf("want %v, got %v",tt.want,err)}
        })
    }
}

func TestMaxPayloadAndForbiddenControlPayload(t *testing.T) {
    var out bytes.Buffer
    if err:=WriteFrame(&out,Frame{Data,make([]byte,MaxPayload)});err!=nil{t.Fatal(err)}
    if _,err:=ReadFrame(&out);err!=nil{t.Fatal(err)}
    if err:=WriteFrame(&out,Frame{Data,make([]byte,MaxPayload+1)});!errors.Is(err,ErrTooLarge){t.Fatal(err)}
    if err:=WriteFrame(&out,Frame{Stop,[]byte{1}});!errors.Is(err,ErrMalformed){t.Fatal(err)}
    if err:=WriteFrame(&out,Frame{Hello,nil});!errors.Is(err,ErrMalformed){t.Fatal(err)}
}

type noProgress struct{}
func (noProgress) Write([]byte)(int,error){return 0,nil}
func TestWriterFailsOnNoProgress(t *testing.T) {
    if err:=WriteFrame(noProgress{},Frame{Stop,nil});!errors.Is(err,io.ErrShortWrite){t.Fatal(err)}
}
