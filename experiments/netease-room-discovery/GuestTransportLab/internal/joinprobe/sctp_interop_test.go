package joinprobe

import (
    "net"
    "testing"
    "time"

    forkSCTP "github.com/Happy2018new/nemc-tan-lobby-solver/core/sctp"
    "github.com/pion/logging"
    standardSCTP "github.com/pion/sctp"
)

// Real in-memory INIT -> INIT ACK -> COOKIE ECHO -> COOKIE ACK round trip
// between the fork actually used by BlockCraft and unmodified Pion SCTP.
// Both sides use net.Pipe: no IP, no NetEase credentials, no external network.
func TestPatchedNetherNetForkSCTPHandshakesWithStandardPionOffline(t *testing.T) {
    clientSocket, hostSocket := net.Pipe()
    defer clientSocket.Close()
    defer hostSocket.Close()

    clientDone := make(chan error, 1)
    serverDone := make(chan error, 1)
    var forkAssociation *forkSCTP.Association
    var pionAssociation *standardSCTP.Association
    defer func() {
        if forkAssociation != nil { _ = forkAssociation.Close() }
        if pionAssociation != nil { _ = pionAssociation.Close() }
    }()

    go func() {
        assoc, err := forkSCTP.Client(forkSCTP.Config{
            NetConn: clientSocket,
            RTOMax: 3000,
            LoggerFactory: logging.NewDefaultLoggerFactory(),
        })
        if err == nil { forkAssociation = assoc }
        clientDone <- err
    }()

    go func() {
        assoc, err := standardSCTP.Server(standardSCTP.Config{
            NetConn: hostSocket,
            LoggerFactory: logging.NewDefaultLoggerFactory(),
        })
        if err == nil { pionAssociation = assoc }
        serverDone <- err
    }()

    timeout := time.NewTimer(15 * time.Second)
    defer timeout.Stop()
    for clientDone != nil || serverDone != nil {
        select {
        case err := <-clientDone:
            if err != nil {
                t.Fatalf("fork SCTP client could not interoperate with standard SCTP: %v", err)
            }
            clientDone = nil
        case err := <-serverDone:
            if err != nil {
                t.Fatalf("standard SCTP server rejected fork wire handshake: %v", err)
            }
            serverDone = nil
        case <-timeout.C:
            t.Fatal("patched fork SCTP handshake timed out against standards-based in-memory server")
        }
    }
}
