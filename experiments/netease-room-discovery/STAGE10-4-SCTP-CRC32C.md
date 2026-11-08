# Stage10.4 — 修复 NetherNet forked SCTP 实际发送的 INIT 校验和

## 用户真实症状

Stage10.3 Windows 实机在房间 `643871` 返回 `WEBRTC_SCTP_INIT_ACK_NOT_RECEIVED`。此前真实账号大厅认证、服务器允许进入自有网易手机房间、WebSocket 信令及 ICE/DTLS 已推进，只有 SCTP INIT 无应答，不能声称已经进入 Minecraft 世界。

## 发现的具体线协议 bug

锁定依赖 `Zhonger-Yuansi/nemc-tan-lobby-solver` commit `4dc3f1cdc57455398edb0bbd96b4e54832c6dad9` 的 `core/sctp/packet.go`：

- 其 SCTP 校验使用 `crc32.MakeTable(crc32.IEEE)`，而 RFC 4960 SCTP 校验要求 **CRC32c Castagnoli**，不能用 IEEE CRC32 代替。
- 它还将 CRC 校验字段以 `binary.BigEndian` 写入、读取，但规范对应 Go 反射型 CRC32c 的线序使用 `binary.LittleEndian`。
- 标准 `github.com/pion/sctp` v1.8.39 的 `packet.go` 同时正确使用 `crc32.Castagnoli` 和 `binary.LittleEndian`，对照可直接验证这一源码缺陷。**尚不知网易手机版是否使用了非标准的专有变体，故不能保证这就是唯一实机原因。**

公网用户许可连接流程不改变：仍需合法第二账号会话、大厅 `TanLoginResponse` 成功、服务器 `TanEnterRoomResponse` 成功、房主 NetherNet 就绪后才发送 WebRTC 数据。不得对无权限房间执行。

## 可重复修正方式

Go 1.26 禁止使用 `go build -overlay` 覆盖模块缓存 `GOMODCACHE` 内的文件；因此项目的 `tools/create-sctp-crc32c-overlay.ps1`（历史文件名沿用）实际上采用**复制锁定依赖到 Windows Actions 临时 `.blockcraft/nethernet-fork/`** 的安全方式，仅对被审计的 `core/sctp/packet.go` 四处做严格数量校验的替换，并在此次 CI 工作目录中通过 `go mod edit -replace` 指向本地修正副本。

每次发布 Windows EXE 都会在 CI 中首先测试未修补依赖的实际 SCTP INIT **应该不符合 CRC32c**，然后在修正版上运行全部 Go 单测、编译内嵌 Go 二进制并运行 C# 与 Go 的跨进程检查。编译临时副本不改变 GitHub 上游源码或 Go 下载缓存，源码主分支未修改。

## 网络字节级测试（不涉及用户网络）

- `TestForkedSCTPInitHasStandardsCompliantCRC32cWireBytes`：让生产实际使用的 forked SCTP `Client` 经本机 `net.Pipe` 发送 INIT，从真实报文按 little endian 提取校验字段，计算 CRC32c Castagnoli，并断言不能是 IEEE CRC32。
- `TestPatchedNetherNetForkSCTPHandshakesWithStandardPionOffline`：使用 `net.Pipe` 在修正 fork 的 Client 和标准 Pion SCTP 的 Server 之间执行 INIT → INIT ACK → COOKIE ECHO → COOKIE ACK 的完整关联握手。两端都是本机模拟，没有网易流量，也不需要 Cookie。

两项测试均对最初修改仅用于诊断的 Stage10.3 有实质升级：**真正改变底层实际发出的 INIT 字节，并用标准独立实现做兼容性验证**。

## 实机判断

如果修正版在用户自己手机房间返回 `WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED`，说明 WebRTC 传输建连已完成，但**仍无 Minecraft 游戏协议握手和世界加载**。如果还是 `WEBRTC_SCTP_INIT_ACK_NOT_RECEIVED`，说明即使符合标准 SCTP 校验，手机仍未对该 INIT 返回正确响应，需要进一步确认实际网易版本/NetherNet 的 SCTP 变体或原来的 DTLS 数据交互。

新的 EXE 不需要安装网易电脑版，不应要求用户重新发送 Cookie/密码/token；只需要自己在 Windows 上按要求确认测试自有房间，并分享固定状态枚举。
