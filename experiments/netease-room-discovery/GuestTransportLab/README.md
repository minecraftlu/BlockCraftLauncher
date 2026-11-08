# BlockCraft GuestTransportLab — third-stage transport proof

This is a Windows **guest-side transport research prototype**, NOT a NetEase Minecraft room connector. Built with Go and Pion WebRTC as a self-contained EXE; the primary BlockCraft Launcher UI remains C# + WPF + .NET Framework 4.8.

## Why this exists

Previous user-owned room tests have already established:

- Room `453649` is discoverable without NetEase PC client.
- It maps to public lobby node ID `9366` via the public listing.

That is only **discovery**, not authorization, admission, peer negotiation, or playable gameplay. With no independent guest account available, no second player can be admitted to the real phone room yet.

This lab tests the next reusable building block: **can a PC guest and a mock host exchange bytes via the actual ICE / DTLS / SCTP ReliableDataChannel transport?**

## Windows usage

Download the unsigned `BlockCraft-GuestTransportLab-win-unsigned` GitHub Actions artifact from the project's experimental workflow. It contains one `BlockCraft.GuestTransportLab.exe`; no Go installation is needed to run it.

From PowerShell:

```powershell
.\BlockCraft.GuestTransportLab.exe -selftest
```

Success means:

```text
"status": "LOCAL_WEBRTC_DATA_CHANNEL_OK"
"scope": "same_process_loopback_only"
"guest_transport_ready": true
"official_room_joined": false
"authenticated": false
LOCAL_SELFTEST_PASS (no external network session)
```

The `guest_transport_ready` flag **only certifies the locally simulated guest**. It has no relationship to a real NetEase account or Minecraft server.

## Network isolation

- Pion `SettingEngine` restricts transport candidates to `127.0.0.0/8` IPv4 loopback.
- There is **no** STUN, TURN, remote signaling server, public node dialing, credential collection, login or room joining.
- The host and guest are separate `PeerConnection` objects inside a single local process.
- Local session descriptions are exchanged in memory.
- The guest opens `ReliableDataChannel` and sends a synthetic handshake; the host returns a synthetic acknowledgement.
- The test emits only status strings; it does not print local SDP, IP addresses or authentication material.

## Source organization

- `cmd/guestlab/main.go`: command-line launcher with bounded `-selftest` and timeout.
- `internal/transport/loopback.go`: real local WebRTC guest+mock-host round trip.
- `internal/signal/frame.go`: strict bounded parser/serializer for known `CONNECTREQUEST`, `CONNECTRESPONSE`, `CANDIDATEADD`, `CONNECTERROR` framing. These are **protocol envelopes only**; we have not implemented NetEase network signaling or authentication.
- Tests verify actual local data channel and protocol envelope validation.

Build from source with Go 1.26.5+:

```sh
go mod tidy
go test ./...
go build -trimpath -o BlockCraft.GuestTransportLab.exe ./cmd/guestlab
```

## What must be done before real room join

1. Obtain a **separate legitimately authenticated** guest identity (if an official guest mode meets platform requirements, test it only through authorized login).
2. Document an **actual authorized join reply** for the same `room-with-name` six-digit local-room system. Do not substitute the different `online-lobby-room-enter/` HTTP API without verification.
3. Receive the actual host **NetherNet NetworkID**, routing ID and signaling credentials from that authorized session.
4. Implement a bounded, authenticated signaling adapter and connect the Pion transport to the real host.
5. Confirm gameplay packet compatibility, resource packs, and legal Java/Bedrock client execution.

The `srv` node field identifies shared infrastructure, **not** a remote NetworkID or Minecraft game server. Do not send raw WebRTC offers to the public lobby node based on this field.

## Open source / license

This lab is newly written implementation against the publicly documented `github.com/pion/webrtc/v4` API and does **not** copy code from CubeDock, Opentransfer or AGPL projects. The upstream projects and component licenses should still be reviewed before any future code reuse.
