package joinprobe

import (
    "context"
    "errors"
    "sync"
    "sync/atomic"
    "testing"
    "time"

    "github.com/Happy2018new/nemc-tan-lobby-solver/core/nethernet"
)

// Does not require a source-level method reference: the unpatched negative
// control must still COMPILE, then fail its old CRC32 wire test as intended.
func TestPatchedNetherNetChannelReadOnlyInterface(t *testing.T) {
    var conn *nethernet.Conn
    if _, ok := any(conn).(channelObservationSource); !ok {
        t.Fatal("Stage11.1 build omitted the channel status shim in local NetherNet copy")
    }
}

type testChannelConn struct {
    reliable atomic.Bool
    unreliable atomic.Bool
    packetCh chan []byte
    done chan struct{}
    once sync.Once
    callbackMu sync.Mutex
    callback func(int)
}

func newTestChannelConn() *testChannelConn {
    c:=&testChannelConn{packetCh:make(chan []byte,4),done:make(chan struct{})}
    c.reliable.Store(true)
    c.unreliable.Store(true)
    return c
}
func (c *testChannelConn) Close() error {
    c.once.Do(func(){close(c.done)})
    return nil
}
func (c *testChannelConn) ReadPacket() ([]byte,error) {
    select {
    case <-c.done: return nil,errors.New("closed")
    case b:=<-c.packetCh: return b,nil
    }
}
func (c *testChannelConn) BlockCraftChannelStates() (bool,bool) {
    return c.reliable.Load(), c.unreliable.Load()
}
func (c *testChannelConn) BlockCraftObserveUnreliableLengths(cb func(int)) {
    c.callbackMu.Lock()
    c.callback=cb
    c.callbackMu.Unlock()
}
func (c *testChannelConn) emitUnreliable(n int) {
    c.callbackMu.Lock()
    cb:=c.callback
    c.callbackMu.Unlock()
    if cb!=nil {cb(n)}
}

func TestStage11PassiveChannelObservation(t *testing.T) {
    t.Run("both_open_idle_30ms",func(t *testing.T){
        conn:=newTestChannelConn()
        result:=observeDataChannels(context.Background(),conn,30*time.Millisecond)
        if result.outcome!=stabilityWindowComplete || !result.reliableOpen || !result.unreliableOpen {
            t.Fatal("healthy idle data channels did not remain open")
        }
        if result.reliableMessages!=0 || result.unreliableMessages!=0 ||
            result.reliableBytes!=0 || result.unreliableBytes!=0 {
            t.Fatal("idle data channels invented traffic")
        }
    })
    t.Run("counts_sizes_without_retaining_payload",func(t *testing.T){
        conn:=newTestChannelConn()
        secret:=[]byte("PRIVATE_GAME_DATA_DO_NOT_LOG")
        go func() {
            time.Sleep(9*time.Millisecond)
            conn.packetCh<-secret
            conn.emitUnreliable(37)
        }()
        result:=observeDataChannels(context.Background(),conn,45*time.Millisecond)
        if result.outcome!=stabilityWindowComplete ||
            result.reliableMessages!=1 || result.reliableBytes!=uint64(len(secret)) ||
            result.unreliableMessages!=1 || result.unreliableBytes!=37 {
            t.Fatalf("wrong bounded passive counters: %+v", result)
        }
        for _,b:=range secret {if b!=0 {t.Fatal("sensitive incoming packet was not cleared")}}
    })
    t.Run("one_channel_not_open_is_not_success",func(t *testing.T){
        conn:=newTestChannelConn()
        conn.unreliable.Store(false)
        result:=observeDataChannels(context.Background(),conn,20*time.Millisecond)
        if result.outcome!=stabilityWindowComplete || !result.reliableOpen || result.unreliableOpen {
            t.Fatal("a connecting/unopened unreliable channel was falsely reported open")
        }
    })
    t.Run("remote_disconnect_detected",func(t *testing.T){
        conn:=newTestChannelConn()
        go func(){time.Sleep(12*time.Millisecond);_ = conn.Close()}()
        result:=observeDataChannels(context.Background(),conn,80*time.Millisecond)
        if result.outcome!=stabilityRemoteClosed {t.Fatal("remote early closure treated as stable")}
    })
    t.Run("user_cancel_not_success",func(t *testing.T){
        conn:=newTestChannelConn()
        ctx,cancel:=context.WithCancel(context.Background())
        go func(){time.Sleep(8*time.Millisecond);cancel()}()
        result:=observeDataChannels(ctx,conn,80*time.Millisecond)
        if result.outcome!=stabilityCancelled {t.Fatal("cancelled session reported stable")}
    })
}

func TestStage11ObservationSecondsRejectedBeforeAnyNetwork(t *testing.T) {
    result:=Run(context.Background(),Request{
        RoomID:"643871",
        AuthorizedByHost:true,
        AttemptWebRTC:true,
        ObservationSeconds:19,
    })
    if result.Status!="OBSERVATION_DURATION_INVALID" || result.LoginAccepted ||
       result.RoomAdmissionAccepted || result.WebRTCTransportConnected {
        t.Fatalf("unexpected observation request left offline safety gate: %+v",result)
    }
}
