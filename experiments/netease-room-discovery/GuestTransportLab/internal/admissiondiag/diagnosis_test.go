package admissiondiag

import "testing"

func TestServerReplyCodesHaveOnlyDocumentedReasons(t *testing.T) {
    cases:=[]struct{seen bool;code int8;want string}{
        {false,0,ReplyNotObserved},{false,4,ReplyNotObserved},
        {true,0,Accepted},{true,1,RoomNotFound},
        {true,4,RoomFull},{true,14,WrongPasscode},
        {true,2,RejectedUnknown},{true,3,RejectedUnknown},
        {true,13,RejectedUnknown},{true,15,RejectedUnknown},
        {true,-1,RejectedUnknown},{true,127,RejectedUnknown},
        {true,-128,RejectedUnknown},
    }
    for _,tc:=range cases {
        if got:=Classify(tc.seen,tc.code);got!=tc.want {
            t.Fatalf("reply=%v code=%d: got %q want %q",
                tc.seen,tc.code,got,tc.want)
        }
    }
}

func TestUnknownCodesCannotImplyPermissionsOrGameJoin(t *testing.T) {
    for value:=-128;value<=127;value++ {
        got:=Classify(true,int8(value))
        switch value {
        case 0,1,4,14:
        default:
            if got!=RejectedUnknown {
                t.Fatalf("code=%d was assigned unverified meaning %q",value,got)
            }
        }
    }
}
