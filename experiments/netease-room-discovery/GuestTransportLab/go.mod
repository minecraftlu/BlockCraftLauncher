module github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/GuestTransportLab

go 1.26.5

require github.com/pion/webrtc/v4 v4.1.0

require github.com/Happy2018new/nemc-tan-lobby-solver v0.2.8

replace github.com/Happy2018new/nemc-tan-lobby-solver => github.com/Zhonger-Yuansi/nemc-tan-lobby-solver v0.0.0-20251008025508-4dc3f1cdc574

require github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire v0.0.0

replace github.com/minecraftlu/BlockCraftLauncher/experiments/netease-room-discovery/BridgeWire => ../BridgeWire
