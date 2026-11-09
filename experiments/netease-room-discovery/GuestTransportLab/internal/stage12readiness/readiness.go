// Package stage12readiness produces fixed, evidence-based summaries of the
// REAL room transport state. A successful TanEnterRoomResponse and OPEN
// NetherNet DataChannels are NOT a Minecraft game identity or login grant.
package stage12readiness

type Evidence struct {
    LobbyLoginAccepted bool
    RoomAdmissionAccepted bool
    HostReadySeen bool
    SignalingAuthenticated bool
    WebRTCTransportConnected bool
    TransportStable bool
    ReliableChannelOpen bool
    UnreliableChannelOpen bool
    // G79ModeConfirmed is deliberately not a Minecraft game identity proof.
    // It must never be used to skip or change game-level authentication.
    G79ModeConfirmed bool
}

const (
    AdmissionNotVerified = "STAGE12_ADMISSION_NOT_VERIFIED"
    HostNotReady = "STAGE12_HOST_NOT_READY"
    SignalingNotVerified = "STAGE12_SIGNALING_NOT_VERIFIED"
    TransportNotConnected = "STAGE12_TRANSPORT_NOT_CONNECTED"
    TransportNotStable = "STAGE12_TRANSPORT_NOT_STABLE"
    DataChannelsNotVerified = "STAGE12_DATA_CHANNELS_NOT_VERIFIED"
    GameIdentityAndProtocolUnverified = "STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED"
)

// Assess is a diagnostic gate, not a grant issuer. No state can return a
// GAME_JOINED or GAME_LOGIN_ALLOWED result. A future real-game connector
// must independently verify game identity, protocol version and host consent.
func Assess(e Evidence) string {
    if !e.LobbyLoginAccepted || !e.RoomAdmissionAccepted {
        return AdmissionNotVerified
    }
    if !e.HostReadySeen {
        return HostNotReady
    }
    if !e.SignalingAuthenticated {
        return SignalingNotVerified
    }
    if !e.WebRTCTransportConnected {
        return TransportNotConnected
    }
    if !e.TransportStable {
        return TransportNotStable
    }
    if !e.ReliableChannelOpen || !e.UnreliableChannelOpen {
        return DataChannelsNotVerified
    }
    return GameIdentityAndProtocolUnverified
}
