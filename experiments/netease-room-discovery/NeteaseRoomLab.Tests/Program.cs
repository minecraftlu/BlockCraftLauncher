using System;
using BlockCraft.NeteaseRoomLab;

namespace BlockCraft.NeteaseRoomLab.Tests
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--probe-ipc-exe")
                {
                    TestCompiledProbeIPC(args[1]);
                    return 0;
                }
                Expect(true, "found by room ID",
                    "{\"code\":0,\"list\":[{\"rid\":453649,\"name\":\"example\",\"cnt\":1,\"cap\":30,\"version\":898}]}");
                Expect(true, "found by room name",
                    "{\"code\":0,\"list\":[{\"rid\":9988,\"name\":\"453649\",\"cnt\":1,\"cap\":30}]}");
                Expect(false, "not matched",
                    "{\"code\":0,\"list\":[{\"rid\":453650,\"name\":\"other\"}]}");
                Expect(false, "service code",
                    "{\"code\":12015,\"list\":[]}");
                Expect(false, "no list", "{\"code\":0}");
                Expect(false, "invalid JSON", "{not-json}");
                Expect(false, "unexpected structure", "[]");
                ExpectNode(true, "node in public server list", "[{\"id\":9366,\"ip\":\"example.test\",\"ports\":[19132],\"status\":3,\"SignalWebPort\":8899,\"ServerType\":\"relay\"}]");
                ExpectNode(false, "node not matched", "[{\"id\":123,\"ip\":\"example.test\"}]");
                ExpectNode(false, "node bad JSON", "not-json");
                ExpectNode(false, "node unexpected data", "{\"id\":9366}");
                TestEmailAuthLaunchContract();
                TestG79AuthConfigAndRedaction();
                TestAdmissionResponseContract();
                TestLiveRoomPreflightContracts();
                TestRealJoinProbeResultRedaction();
                TestMachineTransportParsing();
                TestStage12OfflineResultContract();
                TestStage127CodecReference();
                TestStage128ChinaBuildReference();
                Console.WriteLine("Room/node parser and local email-helper safety tests passed. No network requests or credentials used.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        private static void TestStage128ChinaBuildReference()
        {
            const string candidate =
                "STAGE12_8_CHINA_310_REFERENCE_CANDIDATES_UNVERIFIED_GAME_NOT_JOINED";
            const string unknown =
                "STAGE12_8_CHINA_BUILD_REFERENCE_UNKNOWN_GAME_NOT_JOINED";
            string game="3.10.7.301588";
            string patch="3.10.9.301882";
            string result=Stage128ClientBuildReference.Classify(game,patch,0,candidate+"\r\n");
            if (!result.Contains("STAGE12_8_CHINA_310_CANDIDATES_897_898_UNVERIFIED_GAME_NOT_JOINED")
                || !result.Contains("手机房间") || !result.Contains("未验证")
                || result.Contains("GAME_JOINED"))
                throw new InvalidOperationException("China 3.10 research candidate misreported as real phone connection.");
            string normalized;
            if (!Stage128ClientBuildReference.TryNormalizeBuild(game,out normalized)
                || normalized!=game)
                throw new InvalidOperationException("Valid screenshot build not recognized.");
            foreach (string untrusted in new[] {
                "", "819", "898", "3.9.7.301588", "3.10.7.301588;token",
                "3.10.7.301588\nTOKEN", "3.10.07.301588", "3.10.7.30158"
            })
            {
                if (Stage128ClientBuildReference.TryNormalizeBuild(untrusted,out normalized))
                    throw new InvalidOperationException("Untrusted or unsupported build was accepted.");
            }
            string fake=Stage128ClientBuildReference.Classify(game,patch,0,
                candidate+"\nSENSITIVE_COOKIE_MUST_NOT_LEAK");
            if (!fake.Contains("STAGE12_8_BUILD_REFERENCE_FAILED_GAME_NOT_JOINED")
                || fake.Contains("SENSITIVE_COOKIE"))
                throw new InvalidOperationException("Unsafe worker output was displayed.");
            if (!Stage128ClientBuildReference.Classify(game,patch,1,candidate)
                    .Contains("STAGE12_8_BUILD_REFERENCE_FAILED_GAME_NOT_JOINED"))
                throw new InvalidOperationException("Nonzero return code falsely marked valid build.");
            if (!Stage128ClientBuildReference.Classify(game,patch,0,unknown)
                    .Contains("STAGE12_8_BUILD_REFERENCE_UNKNOWN_GAME_NOT_JOINED"))
                throw new InvalidOperationException("Unknown mapping must fail closed.");
            Console.WriteLine("PASS: Stage12.8 China 3.10 candidate evidence remains distinct from phone game protocol.");
        }

        private static void TestStage127CodecReference()
        {
            const string reference = "NETEASE_CODEC_REFERENCE_FOUND_GAME_NOT_JOINED";
            const string notPublished = "NETEASE_CODEC_UNVERIFIED_GAME_NOT_JOINED";
            string known = Stage127CodecReference.Classify("819",0,reference+"\r\n");
            if (!known.Contains("STAGE12_7_NETEASE_CODEC_REFERENCE_ONLY_GAME_NOT_JOINED")
                || !known.Contains("不证明当前手机房间")
                || known.Contains("GAME_JOINED"))
                throw new InvalidOperationException("NetEase codec documentation was mistaken for real game entry.");
            string unknown = Stage127CodecReference.Classify("859",0,notPublished+"\n");
            if (!unknown.Contains("STAGE12_7_NETEASE_CODEC_UNVERIFIED_GAME_NOT_JOINED"))
                throw new InvalidOperationException("Standard Bedrock 859 was incorrectly treated as NetEase codec.");
            foreach (string invalid in new [] {"","0","000","819abc","819;login",
                "abcdef","123456","-1","8\n19"})
            {
                string normalized;
                if (Stage127CodecReference.TryNormalizeVersion(invalid,out normalized)
                    || !Stage127CodecReference.Classify(invalid,0,reference)
                        .Contains("STAGE12_7_PROTOCOL_INPUT_INVALID"))
                    throw new InvalidOperationException("Unsafe protocol version input passed the UI gate.");
            }
            string forged = Stage127CodecReference.Classify("819",0,
                reference+"\nPRIVATE_TOKEN_SHOULD_NOT_LEAK");
            if (!forged.Contains("STAGE12_7_PROTOCOL_CHECK_FAILED_GAME_NOT_JOINED")
                || forged.Contains("PRIVATE_TOKEN"))
                throw new InvalidOperationException("Untrusted subprocess output was displayed.");
            if (!Stage127CodecReference.Classify("819",1,reference)
                    .Contains("STAGE12_7_PROTOCOL_CHECK_FAILED_GAME_NOT_JOINED"))
                throw new InvalidOperationException("Failed offline protocol process falsely classified as success.");
            Console.WriteLine("PASS: Stage12.7 codec lookup has no game admission claim or sensitive output");
        }

        private static void TestStage12OfflineResultContract()
        {
            var expected = Stage12OfflineBridge.ClassifyResult(0,
                "LOCAL_BRIDGE_IPC_OK_GAME_NOT_JOINED\r\n");
            if (!expected.Contains("STAGE12_OFFLINE_BRIDGE_OK_GAME_NOT_JOINED")
                || !expected.Contains("未连接网易服务器")
                || expected.Contains("GAME_JOINED"))
                throw new InvalidOperationException("Stage 12 offline result incorrectly claimed gameplay.");

            string sensitive = "LOCAL_BRIDGE_IPC_OK_GAME_NOT_JOINED\n"
                + "PRIVATE_TOKEN_SHOULD_NOT_LEAK";
            string failed = Stage12OfflineBridge.ClassifyResult(0, sensitive);
            if (!failed.Contains("FAILED_GAME_NOT_JOINED")
                || failed.Contains("PRIVATE_TOKEN"))
                throw new InvalidOperationException("Stage12 child output not strictly validated.");

            if (!Stage12OfflineBridge.ClassifyResult(1,
                    "LOCAL_BRIDGE_IPC_OK_GAME_NOT_JOINED")
                    .Contains("FAILED_GAME_NOT_JOINED"))
                throw new InvalidOperationException("Nonzero child exit incorrectly marked OK.");
            string fullGame = Stage12OfflineBridge.ClassifyGameResult(0,
                "LOCAL_STAGE12_GAME_IPC_OK_OFFLINE_GAME_NOT_JOINED\r\n");
            if (!fullGame.Contains("STAGE12_LOCAL_GAME_SPAWN_OK_REMOTE_NOT_JOINED")
                || !fullGame.Contains("未连接网易服务器")
                || fullGame.Contains("GAME_JOINED"))
                throw new InvalidOperationException("Stage 12.4 offline spawn implied real game join.");
            string injected = Stage12OfflineBridge.ClassifyGameResult(0,
                "LOCAL_STAGE12_GAME_IPC_OK_OFFLINE_GAME_NOT_JOINED\nLEAKED_PRIVATE_TOKEN");
            if (!injected.Contains("FAILED_REMOTE_NOT_JOINED")
                || injected.Contains("LEAKED_PRIVATE_TOKEN"))
                throw new InvalidOperationException("Untrusted Stage12.4 output exposed secrets.");
            if (!Stage12OfflineBridge.ClassifyGameResult(1,
                    "LOCAL_STAGE12_GAME_IPC_OK_OFFLINE_GAME_NOT_JOINED")
                    .Contains("FAILED_REMOTE_NOT_JOINED"))
                throw new InvalidOperationException("Non-zero game worker falsely marked spawn success.");
            Console.WriteLine("PASS: Stage12 offline-only IPC output verification and redaction");
        }

        private static void TestEmailAuthLaunchContract()
        {
            var valid = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "connect.exe");
            var invalid = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "not-connect.exe");
            if (!GuestEmailAuth.IsAllowedHelperPath(valid) ||
                 GuestEmailAuth.IsAllowedHelperPath(invalid) ||
                 GuestEmailAuth.IsAllowedHelperPath("connect.exe"))
                throw new InvalidOperationException("External helper selection validation failed.");

            var cmd = GuestEmailAuth.PreviewArgs(
                @"C:\Local Profile\cookie.json", @"C:\Local Profile\device");
            if (!cmd.Contains("-login-method email") ||
                !cmd.Contains("\"C:\\Local Profile\\cookie.json\"") ||
                !cmd.Contains("-auth-data-dir") ||
                cmd.Contains("-dump-auth"))
                throw new InvalidOperationException("Unexpected helper launch arguments.");

            if (!GuestEmailAuth.ExplainExit(0, true).Contains("UNVERIFIED") ||
                !GuestEmailAuth.ExplainExit(0, false).Contains("NO_CREDENTIAL") ||
                !GuestEmailAuth.ExplainExit(1, true).Contains("WITH_ERROR"))
                throw new InvalidOperationException("Credential state incorrectly implies authorization.");

            string displayCommand = GuestEmailAuth.GetPowerShellDiagnosticCommand(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "user's folder", "connect.exe"));
            if (!displayCommand.Contains("user''s folder") ||
                !displayCommand.Contains("-login-method email") ||
                !displayCommand.Contains("AUTH_HELPER_EXIT_CODE=") ||
                !displayCommand.Contains("-cookie-out") ||
                !displayCommand.Contains("-auth-data-dir") ||
                displayCommand.Contains("-dump-auth") ||
                displayCommand.Contains("-password"))
                throw new InvalidOperationException("Diagnostic command quoting or safety failed.");

            Console.WriteLine("PASS: email helper opt-in argument construction and unverified status handling");
        }

        private static void TestG79AuthConfigAndRedaction()
        {
            const string localCookiePath = @"C:\Users\tester\secure folder\cookie.json";
            string config = GuestG79Verifier.BuildConfig(localCookiePath);
            var settings = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<System.Collections.Generic.Dictionary<string, object>>(config);
            if ((string)settings["auth_mode"] != "g79" ||
                (string)settings["cookie_file"] != localCookiePath ||
                !object.Equals(settings["transfer"], true) ||
                settings.ContainsKey("token") ||
                settings.ContainsKey("password") ||
                settings.ContainsKey("uid"))
                throw new InvalidOperationException("Unsafe G79 probe configuration.");

            string syntheticSensitiveOutput = "uid=10001\\nlogin_token=MOCK_SECRET_DO_NOT_PRINT\\n"
                + "nickname=SENSITIVE_NAME\\nauth_mode=g79\\nplatform=2\\n";
            string verdict = GuestG79Verifier.ClassifyResult(
                0, syntheticSensitiveOutput.Replace("\\n", "\n"), false);
            if (!verdict.Contains("G79_HELPER_LOGIN_ACCEPTED") ||
                verdict.Contains("MOCK_SECRET") ||
                verdict.Contains("SENSITIVE_NAME") ||
                verdict.Contains("10001"))
                throw new InvalidOperationException("G79 success classification leaked sensitive output.");

            if (GuestG79Verifier.ClassifyResult(1, syntheticSensitiveOutput, false)
                    .Contains("ACCEPTED") ||
                GuestG79Verifier.ClassifyResult(0, "uid=1\\nauth_mode=x19\\nplatform=1", false)
                    .Contains("ACCEPTED") ||
                GuestG79Verifier.ClassifyResult(0, syntheticSensitiveOutput, true)
                    .Contains("ACCEPTED"))
                throw new InvalidOperationException("Invalid G79 status promoted to success.");

            string partial = GuestG79Verifier.ClassifyResult(
                0,
                "uid = 10001\r\nlogin_token = MOCK_SECRET_DO_NOT_PRINT\r\n"
                + "auth_mode = x19\r\nplatform = 1\r\n",
                false);
            if (!partial.Contains("G79_CHECK_INCONCLUSIVE") ||
                !partial.Contains("uid_valid=yes") ||
                !partial.Contains("auth_mode_g79=no") ||
                !partial.Contains("platform_pe=no") ||
                !partial.Contains("token_present=yes") ||
                partial.Contains("10001") ||
                partial.Contains("MOCK_SECRET_DO_NOT_PRINT"))
                throw new InvalidOperationException("Safe G79 field diagnostics missing or leaking secrets.");

            string flexible = GuestG79Verifier.ClassifyResult(
                0,
                "\uFEFF UID = 10001\r\n LOGIN_TOKEN = REDACTED_MOCK_SECRET\r\n"
                + " Auth_Mode = G79\r\nPlatform = 2\r\n",
                false);
            if (!flexible.Contains("G79_HELPER_LOGIN_ACCEPTED") ||
                flexible.Contains("REDACTED_MOCK_SECRET") ||
                flexible.Contains("10001"))
                throw new InvalidOperationException("G79 label normalization or secrecy failed.");

            string missingMode = GuestG79Verifier.ClassifyResult(0,
                "uid=2345\nlogin_token=DO_NOT_PRINT\nplatform=2\n", false);
            if (!missingMode.Contains("PE_SESSION_REPORTED_MODE_UNCONFIRMED") ||
                missingMode.Contains("G79_HELPER_LOGIN_ACCEPTED") ||
                missingMode.Contains("DO_NOT_PRINT") ||
                missingMode.Contains("2345"))
                throw new InvalidOperationException("Missing-mode PE session classification or secret leak.");

            string unexpectedMode = GuestG79Verifier.ClassifyResult(0,
                "uid=2345\nlogin_token=DO_NOT_PRINT\nauth_mode=SECRET_MODE_VALUE\nplatform=2\n", false);
            if (!unexpectedMode.Contains("mode_category=other") ||
                unexpectedMode.Contains("SECRET_MODE_VALUE") ||
                unexpectedMode.Contains("DO_NOT_PRINT"))
                throw new InvalidOperationException("Unknown auth mode leak.");

            string x19Mode = GuestG79Verifier.ClassifyResult(0,
                "uid=2345\nlogin_token=DO_NOT_PRINT\nauth_mode=x19\nplatform=2\n", false);
            if (!x19Mode.Contains("mode_category=x19") ||
                x19Mode.Contains("G79_HELPER_LOGIN_ACCEPTED"))
                throw new InvalidOperationException("Cross-platform mode mismatch classified as success.");

            Console.WriteLine("PASS: safe G79 config, field diagnostics and zero credential disclosure");
        }

        private static void TestAdmissionResponseContract()
        {
            const string room = "280474";
            string fixture = RoomAdmissionSchema.SyntheticEnvelope();
            string complete = RoomAdmissionSchema.Inspect(fixture, room);
            if (!complete.Contains(RoomAdmissionSchema.CompleteStatus) ||
                complete.Contains("SERVER_ROOM_ADMITTED") ||
                complete.Contains("GAME_CONNECTED"))
                throw new InvalidOperationException("Offline schema check claimed real admission.");

            string offline = RoomAdmissionSchema.OfflineSelfTest(room);
            if (!offline.Contains(RoomAdmissionSchema.BlockedStatus) ||
                !offline.Contains(RoomAdmissionSchema.CompleteStatus))
                throw new InvalidOperationException("Offline test did not mark provider unconfigured.");

            if (!RoomAdmissionSchema.Inspect(fixture, "not-room")
                    .Contains("ADMISSION_INPUT_ERROR") ||
                !RoomAdmissionSchema.Inspect("bad-json", room)
                    .Contains("ADMISSION_ENVELOPE_INVALID") ||
                !RoomAdmissionSchema.Inspect(new string('x',
                    RoomAdmissionSchema.MaxEnvelopeLength + 1), room)
                    .Contains("ADMISSION_ENVELOPE_INVALID"))
                throw new InvalidOperationException("Bad payload or room not rejected.");

            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            var fields = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(fixture);
            fields["success"] = false;
            fields["error_info"] = "PRIVATE_SERVER_ERROR_DO_NOT_EXPOSE";
            string denied = RoomAdmissionSchema.Inspect(serializer.Serialize(fields), room);
            if (!denied.Contains("ADMISSION_RESPONSE_REJECTED_UNVERIFIED") ||
                denied.Contains("PRIVATE_SERVER_ERROR"))
                throw new InvalidOperationException("Denied admission leaked remote error text.");

            fields["success"] = true;
            fields.Remove("signaling_ticket");
            string missing = RoomAdmissionSchema.Inspect(serializer.Serialize(fields), room);
            if (!missing.Contains("ADMISSION_ENVELOPE_INCOMPLETE") ||
                !missing.Contains("signaling_ticket"))
                throw new InvalidOperationException("Missing signaling ticket not detected.");

            fields["signaling_ticket"] = "PRIVATE_SECRET_NOT_BASE64";
            string invalid = RoomAdmissionSchema.Inspect(serializer.Serialize(fields), room);
            if (!invalid.Contains("ADMISSION_ENVELOPE_INCOMPLETE") ||
                invalid.Contains("PRIVATE_SECRET"))
                throw new InvalidOperationException("Invalid ticket leaked a credential.");

            fields["signaling_ticket"] = Convert.ToBase64String(new byte[16]);
            fields["raknet_server_address"] = "malformed address";
            string address = RoomAdmissionSchema.Inspect(serializer.Serialize(fields), room);
            if (!address.Contains("ADMISSION_ENVELOPE_INCOMPLETE") ||
                !address.Contains("raknet_server_address"))
                throw new InvalidOperationException("Invalid RakNet address accepted.");

            Console.WriteLine("PASS: TanLobby access envelope structure, gating and secret redaction");
        }

        private static void TestLiveRoomPreflightContracts()
        {
            const string response = "{\"code\":0,\"list\":[{\"rid\":280474,\"name\":\"280474\","
                + "\"room_unique_id\":\"280474\",\"hid\":984321,\"srv\":9366,"
                + "\"cnt\":1,\"cap\":30,\"need_password\":0}]}";
            RoomConnectionMetadata.Room room;
            if (!RoomConnectionMetadata.TryParseRoom(response, "280474", out room)
                || room.OwnerID != 984321 || room.NodeID != "9366"
                || room.IsFull || room.NeedsPassword)
                throw new InvalidOperationException("Authenticated room metadata parse failed.");

            if (RoomConnectionMetadata.TryParseRoom(response, "123456", out room)
                || RoomConnectionMetadata.TryParseRoom(response.Replace("\"hid\":984321", "\"hid\":0"),
                    "280474", out room)
                || RoomConnectionMetadata.TryParseRoom("not-json", "280474", out room))
                throw new InvalidOperationException("Invalid room metadata accepted.");

            string nodeJson = "[{\"id\":9366,\"ip\":\"42.186.165.240\","
                + "\"ports\":[19132,19133],\"SignalWebPort\":8900}]";
            RoomConnectionMetadata.Node node;
            if (!RoomConnectionMetadata.TryParseNode(nodeJson, "9366", out node)
                || node.LobbyPort != 19132 || node.SignalingPort != 8900 ||
                node.PublicHost != "42.186.165.240" ||
                RoomConnectionMetadata.TryParseNode(nodeJson, "1000", out node))
                throw new InvalidOperationException("Public node mapping parser failed.");

            RoomSessionPreflight.LocalSession session;
            const string validToken = "0123456789abcdef";
            string mockOutput = "uid=12345\nlogin_token=" + validToken +
                "\nplatform=2\nnickname=MOCK_SENSITIVE_NICK\n";
            if (!RoomSessionPreflight.TryParseLocalSession(mockOutput, out session)
                || session.PlayerID != 12345 || session.Token != validToken
                || session.ModeCategory != "missing" || !session.IsPE)
                throw new InvalidOperationException("PE mode-missing session not accepted.");

            if (RoomSessionPreflight.TryParseLocalSession(
                mockOutput + "auth_mode=x19\n", out session)
                || RoomSessionPreflight.TryParseLocalSession(
                    mockOutput.Replace("platform=2", "platform=1"), out session)
                || RoomSessionPreflight.TryParseLocalSession(
                    mockOutput.Replace(validToken, "private token with spaces"), out session))
                throw new InvalidOperationException("Malformed or incompatible token accepted.");

            var randomA = new byte[16];
            var randomB = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                randomA[i] = (byte)i;
                randomB[i] = (byte)(i + 16);
            }
            if (!RoomConnectionMaterial.SupportsTokenKey(validToken)
                || RoomConnectionMaterial.SupportsTokenKey("short"))
                throw new InvalidOperationException("Invalid AES token key length accepted.");

            RoomConnectionMaterial material = RoomConnectionMaterial.Derive(
                validToken, randomA, randomB);
            byte[] encryptedKey = material.EncryptKeyBytes;
            if (material.RaknetRand.Length != 16 ||
                material.RaknetAESRand.Length != 32 ||
                material.SignalingSeed.Length != 16 ||
                material.SignalingTicket.Length != 32 ||
                encryptedKey.Length != 32 ||
                material.DecryptKeyBytes.Length != 32)
                throw new InvalidOperationException("Access material shape mismatch.");

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var expectedHash = md5.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(validToken));
                for (int i = 0; i < 16; i++)
                {
                    if (encryptedKey[i] != expectedHash[i] ||
                        encryptedKey[16 + i] != randomA[i] ||
                        material.DecryptKeyBytes[i] != randomA[i] ||
                        material.DecryptKeyBytes[16 + i] != expectedHash[i])
                        throw new InvalidOperationException("Session material key layout mismatch.");
                }
            }
            material.Dispose();
            foreach (byte b in encryptedKey)
                if (b != 0) throw new InvalidOperationException("Secret material not cleared.");

            Console.WriteLine("PASS: live-room preflight parsers and volatile session key derivation (offline mocks)");
        }

        private static void TestRealJoinProbeResultRedaction()
        {
            const string secret = "DO_NOT_SHOW_MOCK_SESSION_TOKEN";
            string accepted = JoinProbeRunner.ReportStatus(
                "{\"status\":\"SERVER_ROOM_ADMISSION_ACCEPTED\","
                + "\"login_accepted\":true,\"room_admission_accepted\":true,"
                + "\"minecraft_world_joined\":false,"
                + "\"detail\":\""+secret+"\"}");
            if (!accepted.Contains("SERVER_ROOM_ADMISSION_ACCEPTED")
                || accepted.Contains(secret)
                || accepted.Contains("进入 Minecraft 世界成功"))
                throw new InvalidOperationException("Join probe room admission statement leaked a credential.");

            string unknown = JoinProbeRunner.ReportStatus(
                "{\"status\":\"MALICIOUS_STATUS_"+secret+"\","
                + "\"detail\":\""+secret+"\"}");
            if (unknown.Contains(secret) || !unknown.Contains("JOIN_PROBE_STATUS_UNKNOWN"))
                throw new InvalidOperationException("Unknown status echoed private payload.");

            string unverified = JoinProbeRunner.ReportStatus(
                "{\"status\":\"TAN_LOGIN_RESPONSE_MISSING\",\"detail\":\""+secret+"\"}");
            if (!unverified.Contains("TAN_LOGIN_RESPONSE_MISSING")
                || unverified.Contains(secret)
                || unverified.Contains("SERVER_ROOM_ADMISSION_ACCEPTED"))
                throw new InvalidOperationException("Unverified admission improperly promoted.");

            if (!JoinProbeRunner.ReportStatus("not-json").Contains("JOIN_PROBE_OUTPUT_INVALID"))
                throw new InvalidOperationException("Malformed probe output was not rejected.");

            Console.WriteLine("PASS: real RakNet join probe allowlist and user-secret redaction");
        }

        private static void TestMachineTransportParsing()
        {
            const string token = "DO_NOT_SHARE_PRIVATE_TOKEN";
            const string prefix = "BLOCKCRAFT_JOIN_PROBE_V1\t";
            const string failed = "{\"status\":\"PE_AUTH_UNAVAILABLE\",\"login_accepted\":false,"
                + "\"room_admission_accepted\":false,\"host_ready_seen\":false,"
                + "\"minecraft_world_joined\":false,\"g79_mode_confirmed\":false}";
            const string admitted = "{\"status\":\"SERVER_ROOM_ADMISSION_ACCEPTED\","
                + "\"login_accepted\":true,\"room_admission_accepted\":true,"
                + "\"host_ready_seen\":false,\"minecraft_world_joined\":false}";
            string fixture = JoinProbeRunner.ParseMachineOutput(prefix + failed + "\r\n");
            if (!fixture.Contains("PE_AUTH_UNAVAILABLE"))
                throw new InvalidOperationException("Versioned machine frame was not parsed.");
            if (!JoinProbeRunner.ParseMachineOutput(prefix + admitted + "\n")
                .Contains("SERVER_ROOM_ADMISSION_ACCEPTED"))
                throw new InvalidOperationException("Verified response was incorrectly rejected.");

            // Stage 12.9: report only server-returned signed int8 codes.
            // Never infer code 4 from a stale room occupancy snapshot.
            const string admissionEvidence = @"{""status"":""ENTER_ROOM_REJECTED"","
                + @"""login_accepted"":true,""room_admission_accepted"":false,"
                + @"""host_ready_seen"":false,""minecraft_world_joined"":false,"
                + @"""login_reply_observed"":true,""login_reply_code"":0,"
                + @"""admission_reply_observed"":true,""admission_reply_code"":4,"
                + @"""admission_diagnosis"":""ADMISSION_ROOM_FULL""}";
            string realFull = JoinProbeRunner.ParseMachineOutput(prefix + admissionEvidence);
            if (!realFull.Contains("STAGE12_9_SERVER_ROOM_REPLY")
                || !realFull.Contains("回执码 4")
                || !realFull.Contains("服务器报告房间人数已满")
                || realFull.Contains("JOIN_PROBE_IPC_"))
                throw new InvalidOperationException("Server admission code 4 not displayed accurately.");

            string notFound = admissionEvidence.Replace(@"""admission_reply_code"":4",
                    @"""admission_reply_code"":1")
                .Replace("ADMISSION_ROOM_FULL","ADMISSION_ROOM_NOT_FOUND");
            string wrongPassword = admissionEvidence.Replace(@"""admission_reply_code"":4",
                    @"""admission_reply_code"":14")
                .Replace("ADMISSION_ROOM_FULL","ADMISSION_WRONG_PASSCODE");
            string unknownCode = admissionEvidence.Replace(@"""admission_reply_code"":4",
                    @"""admission_reply_code"":-127")
                .Replace("ADMISSION_ROOM_FULL","ADMISSION_REJECTED_REASON_UNKNOWN");
            foreach (var row in new[] {
                new {Json=notFound,Expected="回执码 1"},
                new {Json=wrongPassword,Expected="回执码 14"},
                new {Json=unknownCode,Expected="回执码 -127"}
            })
            {
                string parsed=JoinProbeRunner.ParseMachineOutput(prefix+row.Json);
                if (!parsed.Contains(row.Expected) || parsed.Contains("JOIN_PROBE_IPC_"))
                    throw new InvalidOperationException("Genuine server int8 response misclassified.");
                if (row.Expected=="回执码 -127" && !parsed.Contains("具体原因尚不明确"))
                    throw new InvalidOperationException("Unknown room rejection got an invented cause.");
            }
            string roomMissing = admissionEvidence
                .Replace("ENTER_ROOM_REJECTED","ENTER_ROOM_RESPONSE_MISSING")
                .Replace(@"""admission_reply_observed"":true",@"""admission_reply_observed"":false")
                .Replace(@"""admission_reply_code"":4",@"""admission_reply_code"":0")
                .Replace("ADMISSION_ROOM_FULL","ADMISSION_REPLY_NOT_OBSERVED");
            string missingDisplay=JoinProbeRunner.ParseMachineOutput(prefix+roomMissing);
            if (!missingDisplay.Contains("STAGE12_9_ROOM_REPLY_MISSING")
                || missingDisplay.Contains("STAGE12_9_SERVER_ROOM_REPLY"))
                throw new InvalidOperationException("Missing server response was described as a rejection.");
            string roomAccepted=admissionEvidence.Replace("ENTER_ROOM_REJECTED","SERVER_ROOM_ADMISSION_ACCEPTED")
                .Replace(@"""room_admission_accepted"":false",@"""room_admission_accepted"":true")
                .Replace(@"""admission_reply_code"":4",@"""admission_reply_code"":0")
                .Replace("ADMISSION_ROOM_FULL","ADMISSION_SERVER_ACCEPTED_GAME_NOT_JOINED");
            if (!JoinProbeRunner.ParseMachineOutput(prefix+roomAccepted)
                    .Contains("SERVER_ROOM_ADMISSION_ACCEPTED"))
                throw new InvalidOperationException("Server admission success code 0 was rejected.");

            string loginRejected = admissionEvidence
                .Replace("ENTER_ROOM_REJECTED","TAN_LOGIN_REJECTED")
                .Replace(@"""login_accepted"":true",@"""login_accepted"":false")
                .Replace(@"""login_reply_code"":0",@"""login_reply_code"":-2")
                .Replace(@"""admission_reply_observed"":true",@"""admission_reply_observed"":false")
                .Replace(@"""admission_reply_code"":4",@"""admission_reply_code"":0")
                .Replace("ADMISSION_ROOM_FULL","ADMISSION_REPLY_NOT_OBSERVED");
            if (!JoinProbeRunner.ParseMachineOutput(prefix+loginRejected)
                    .Contains("大厅拒绝码 -2"))
                throw new InvalidOperationException("Lobby login rejected code was not surfaced.");
            string[] forgedAdmission = {
                admissionEvidence.Replace("ADMISSION_ROOM_FULL","ADMISSION_WRONG_PASSCODE"),
                admissionEvidence.Replace(@"""admission_reply_observed"":true",
                    @"""admission_reply_observed"":false"),
                admissionEvidence.Replace(@"""room_admission_accepted"":false",
                    @"""room_admission_accepted"":true"),
                admissionEvidence.Replace(@"""admission_reply_code"":4",
                    @"""admission_reply_code"":0"),
                admissionEvidence.Replace(@"""login_reply_observed"":true",
                    @"""login_reply_observed"":false"),
                admissionEvidence.Replace(@"""admission_reply_code"":4",
                    @"""admission_reply_code"":127"),
                admissionEvidence.Replace("ENTER_ROOM_REJECTED","ENTER_ROOM_RESPONSE_MISSING"),
                roomAccepted.Replace(@"""room_admission_accepted"":true",
                    @"""room_admission_accepted"":false")
            };
            foreach (string forged in forgedAdmission)
            {
                if (!JoinProbeRunner.ParseMachineOutput(prefix + forged)
                        .Contains("JOIN_PROBE_IPC_INCONSISTENT"))
                    throw new InvalidOperationException("Forged admission attestation was accepted.");
            }
            string[] badCodeFormats = {
                admissionEvidence.Replace(@"""admission_reply_code"":4",
                    @"""admission_reply_code"":999"),
                admissionEvidence.Replace(@"""admission_reply_code"":4",
                    @"""admission_reply_code"":""4"""),
                admissionEvidence.Replace(@"""admission_reply_code"":4",
                    @"""admission_reply_code"":4.2"),
                admissionEvidence.Replace(@"""admission_reply_code"":4,",""),
                admissionEvidence.Replace(@"""login_reply_code"":0",
                    @"""login_reply_code"":-200")
            };
            foreach (string invalid in badCodeFormats)
            {
                if (!JoinProbeRunner.ParseMachineOutput(prefix+invalid)
                        .Contains("JOIN_PROBE_IPC_SCHEMA_ERROR"))
                    throw new InvalidOperationException("Invalid server admission code was not rejected.");
            }
            string secretRoomReply=admissionEvidence.Substring(0,admissionEvidence.Length-1)
                + @",""raw_server_payload"":""NEVER_LOG_COOKIE_OR_TOKEN""}";
            if (JoinProbeRunner.ParseMachineOutput(prefix+secretRoomReply)
                    .Contains("NEVER_LOG_COOKIE_OR_TOKEN"))
                throw new InvalidOperationException("Untrusted server payload leaked into WPF UI.");
            Console.WriteLine("PASS: Stage12.9 signed admission codes, missing reply and tamper protection");

            if (!JoinProbeRunner.ParseMachineOutput("noise\n" + prefix + failed)
                    .Contains("JOIN_PROBE_IPC_UNEXPECTED_OUTPUT")
                || !JoinProbeRunner.ParseMachineOutput(prefix + failed + "\n" + prefix + failed)
                    .Contains("JOIN_PROBE_IPC_UNEXPECTED_OUTPUT")
                || !JoinProbeRunner.ParseMachineOutput("{\"status\":\"PE_AUTH_UNAVAILABLE\"}")
                    .Contains("JOIN_PROBE_IPC_UNEXPECTED_OUTPUT"))
                throw new InvalidOperationException("Unsafe or legacy stdout was accepted.");

            string contradictory = admitted.Replace("\"login_accepted\":true",
                "\"login_accepted\":false");
            if (!JoinProbeRunner.ParseMachineOutput(prefix + contradictory)
                .Contains("JOIN_PROBE_IPC_INCONSISTENT"))
                throw new InvalidOperationException("Unauthenticated room admission was accepted.");

            string world = admitted.Replace("\"minecraft_world_joined\":false",
                "\"minecraft_world_joined\":true");
            if (!JoinProbeRunner.ParseMachineOutput(prefix + world)
                .Contains("JOIN_PROBE_IPC_INCONSISTENT"))
                throw new InvalidOperationException("Probe improperly claimed a game session.");

            string secretPayload = prefix + failed.Substring(0, failed.Length - 1)
                + ",\"detail\":\"" + token + "\"}";
            string safe = JoinProbeRunner.ParseMachineOutput(secretPayload);
            if (!safe.Contains("PE_AUTH_UNAVAILABLE") || safe.Contains(token))
                throw new InvalidOperationException("IPC exposed child process private text.");

            if (!JoinProbeRunner.ParseMachineOutput(prefix + "{not-json}")
                .Contains("JOIN_PROBE_IPC_SCHEMA_ERROR"))
                throw new InvalidOperationException("Corrupt IPC payload was accepted.");

            // Stage10: signaling auth is not yet a WebRTC connection and the
            // game world must remain unjoined even if ICE/DTLS/SCTP succeed.
            const string rtcSuccess = "{\"status\":\"WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED\","
                + "\"login_accepted\":true,\"room_admission_accepted\":true,"
                + "\"host_ready_seen\":true,\"signaling_authenticated\":true,"
                + "\"webrtc_transport_connected\":true,\"minecraft_world_joined\":false}";
            const string sigFailed = "{\"status\":\"SIGNALING_CONNECTION_FAILED\","
                + "\"login_accepted\":true,\"room_admission_accepted\":true,"
                + "\"host_ready_seen\":true,\"signaling_authenticated\":false,"
                + "\"webrtc_transport_connected\":false,\"minecraft_world_joined\":false}";
            string rtcResult = JoinProbeRunner.ParseMachineOutput(prefix + rtcSuccess + "\n");
            if (!rtcResult.Contains("WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED")
                || rtcResult.Contains("JOIN_PROBE_IPC_"))
                throw new InvalidOperationException("Real WebRTC transport status not recognized.");
            if (!JoinProbeRunner.ParseMachineOutput(prefix + sigFailed)
                .Contains("SIGNALING_CONNECTION_FAILED"))
                throw new InvalidOperationException("Signaling failure status was not recognized.");

            string falsePositive = rtcSuccess.Replace(
                "\"room_admission_accepted\":true", "\"room_admission_accepted\":false");
            string missingTransport = rtcSuccess.Replace(
                "\"webrtc_transport_connected\":true", "\"webrtc_transport_connected\":false");
            string leaked = rtcSuccess.Substring(0,rtcSuccess.Length - 1)
                + ",\"secret\":\"SENSITIVE_SIGNALING_TICKET\"}";
            if (!JoinProbeRunner.ParseMachineOutput(prefix + falsePositive)
                    .Contains("JOIN_PROBE_IPC_INCONSISTENT")
                || !JoinProbeRunner.ParseMachineOutput(prefix + missingTransport)
                    .Contains("JOIN_PROBE_IPC_INCONSISTENT")
                || JoinProbeRunner.ParseMachineOutput(prefix + leaked)
                    .Contains("SENSITIVE_SIGNALING_TICKET"))
                throw new InvalidOperationException("WebRTC acceptance bypassed server admission or leaked secrets.");

            // Stage10.1: fixed classifications can be displayed only after
            // server admission, host ready and signaling authenticated.
            string[] classifications = {
                "WEBRTC_ANSWER_TIMEOUT", "WEBRTC_REMOTE_CANDIDATE_TIMEOUT",
                "WEBRTC_ICE_CONNECT_FAILED", "WEBRTC_ICE_CONNECT_TIMEOUT",
                "WEBRTC_DTLS_HANDSHAKE_FAILED", "WEBRTC_DTLS_HANDSHAKE_TIMEOUT",
                "WEBRTC_SCTP_HANDSHAKE_FAILED", "WEBRTC_SCTP_HANDSHAKE_TIMEOUT",
                "WEBRTC_SCTP_PROBE_DEADLINE",
                "WEBRTC_SCTP_INIT_ACK_NOT_RECEIVED",
                "WEBRTC_SCTP_COOKIE_ACK_NOT_RECEIVED",
                "WEBRTC_SCTP_ASSOCIATION_CLOSED",
                "WEBRTC_LOCAL_CANDIDATE_GATHER_FAILED", "WEBRTC_ANSWER_INVALID",
                "WEBRTC_REMOTE_REJECTED", "WEBRTC_PROBE_CANCELLED"
            };
            const string diagnosticTemplate = "{\"status\":\"CODE\","
                + "\"login_accepted\":true,\"room_admission_accepted\":true,"
                + "\"host_ready_seen\":true,\"signaling_authenticated\":true,"
                + "\"webrtc_transport_connected\":false,\"minecraft_world_joined\":false}";
            foreach (string code in classifications)
            {
                string valid = JoinProbeRunner.ParseMachineOutput(prefix
                    + diagnosticTemplate.Replace("CODE", code));
                if (!valid.Contains(code) || valid.Contains("JOIN_PROBE_IPC_"))
                    throw new InvalidOperationException("Stage10.1 diagnostic not allowed: " + code);
                string invalid = JoinProbeRunner.ParseMachineOutput(prefix
                    + diagnosticTemplate.Replace("CODE", code)
                        .Replace("\"room_admission_accepted\":true",
                            "\"room_admission_accepted\":false"));
                if (!invalid.Contains("JOIN_PROBE_IPC_INCONSISTENT"))
                    throw new InvalidOperationException("Stage10.1 diagnostic without room admission: " + code);
            }
            // Stage10.5 never promotes a simple WebRTC dial success into a
            // claim of continuous transport health or Minecraft world join.
            const string stableFixture = @"{""status"":""WEBRTC_TRANSPORT_STABLE_8S_GAME_NOT_JOINED"","
                + @"""login_accepted"":true,""room_admission_accepted"":true,"
                + @"""host_ready_seen"":true,""signaling_authenticated"":true,"
                + @"""webrtc_transport_connected"":true,""transport_stable"":true,"
                + @"""minecraft_world_joined"":false}";
            string healthy = JoinProbeRunner.ParseMachineOutput(prefix + stableFixture);
            if (!healthy.Contains("WEBRTC_TRANSPORT_STABLE_8S_GAME_NOT_JOINED")
                || healthy.Contains("JOIN_PROBE_IPC_"))
                throw new InvalidOperationException("Stage10.5 stable transport result not accepted.");

            string fakeStable = stableFixture.Replace(@"""room_admission_accepted"":true",
                @"""room_admission_accepted"":false");
            string fakeConnected = stableFixture.Replace(@"""webrtc_transport_connected"":true",
                @"""webrtc_transport_connected"":false");
            string misleading = stableFixture.Replace(@"""minecraft_world_joined"":false",
                @"""minecraft_world_joined"":true");
            foreach (var invalid in new[] {fakeStable, fakeConnected, misleading})
            {
                if (!JoinProbeRunner.ParseMachineOutput(prefix + invalid)
                    .Contains("JOIN_PROBE_IPC_INCONSISTENT"))
                    throw new InvalidOperationException("False stable-game status was accepted.");
            }

            string earlyClose = stableFixture
                .Replace("WEBRTC_TRANSPORT_STABLE_8S_GAME_NOT_JOINED",
                    "WEBRTC_EARLY_DISCONNECT_GAME_NOT_JOINED")
                .Replace(@"""transport_stable"":true", @"""transport_stable"":false")
                .Replace(@"""webrtc_transport_connected"":true",
                    @"""webrtc_transport_connected"":false");
            if (!JoinProbeRunner.ParseMachineOutput(prefix + earlyClose)
                .Contains("WEBRTC_EARLY_DISCONNECT_GAME_NOT_JOINED"))
                throw new InvalidOperationException("Early close was incorrectly promoted to stable.");

            const string cancelFrame = @"{""status"":""LOCAL_PROBE_CANCELLED"","
                + @"""login_accepted"":false,""room_admission_accepted"":false,"
                + @"""host_ready_seen"":false,""signaling_authenticated"":false,"
                + @"""webrtc_transport_connected"":false,""transport_stable"":false,"
                + @"""minecraft_world_joined"":false}";
            if (!JoinProbeRunner.ParseMachineOutput(prefix + cancelFrame)
                .Contains("LOCAL_PROBE_CANCELLED"))
                throw new InvalidOperationException("Local cancellation was reported as a server refusal.");

            // Stage11.1: no codepath may display DataChannel OPEN or
            // application-message statistics without admission, signaling,
            // reliable+unreliable OPEN states and an observed time window.
            const string channelFrame = @"{""status"":""WEBRTC_CHANNELS_OPEN_30S_NO_DATA_GAME_NOT_JOINED"","
                + @"""login_accepted"":true,""room_admission_accepted"":true,"
                + @"""host_ready_seen"":true,""signaling_authenticated"":true,"
                + @"""webrtc_transport_connected"":true,""transport_stable"":true,"
                + @"""reliable_channel_open"":true,""unreliable_channel_open"":true,"
                + @"""reliable_messages"":0,""unreliable_messages"":0,"
                + @"""reliable_bytes"":0,""unreliable_bytes"":0,"
                + @"""observation_seconds"":30,""minecraft_world_joined"":false}";
            string noData = JoinProbeRunner.ParseMachineOutput(prefix + channelFrame);
            if (!noData.Contains("WEBRTC_CHANNELS_OPEN_30S_NO_DATA_GAME_NOT_JOINED")
                || !noData.Contains("可靠 0 条 / 0 字节")
                || noData.Contains("JOIN_PROBE_IPC_"))
                throw new InvalidOperationException("Valid Stage11.1 idle data channels not recognized.");

            string active = channelFrame
                .Replace("OPEN_30S_NO_DATA", "OPEN_30S_DATA_OBSERVED")
                .Replace(@"""reliable_messages"":0", @"""reliable_messages"":2")
                .Replace(@"""reliable_bytes"":0", @"""reliable_bytes"":128");
            string activeDisplay = JoinProbeRunner.ParseMachineOutput(prefix + active);
            if (!activeDisplay.Contains("WEBRTC_CHANNELS_OPEN_30S_DATA_OBSERVED_GAME_NOT_JOINED")
                || !activeDisplay.Contains("可靠 2 条 / 128 字节")
                || activeDisplay.Contains("JOIN_PROBE_IPC_"))
                throw new InvalidOperationException("Valid read-only channel packet counters rejected.");

            string minute = channelFrame.Replace("OPEN_30S", "OPEN_60S")
                .Replace(@"""observation_seconds"":30", @"""observation_seconds"":60");
            if (!JoinProbeRunner.ParseMachineOutput(prefix + minute)
                    .Contains("WEBRTC_CHANNELS_OPEN_60S_NO_DATA_GAME_NOT_JOINED"))
                throw new InvalidOperationException("60 second read-only observation rejected.");

            string[] invalidChannelFrames = {
                active.Replace(@"""room_admission_accepted"":true", @"""room_admission_accepted"":false"),
                active.Replace(@"""minecraft_world_joined"":false", @"""minecraft_world_joined"":true"),
                active.Replace(@"""unreliable_channel_open"":true", @"""unreliable_channel_open"":false"),
                active.Replace(@"""observation_seconds"":30", @"""observation_seconds"":60"),
                active.Replace(@"""transport_stable"":true", @"""transport_stable"":false"),
                active.Replace(@"""webrtc_transport_connected"":true", @"""webrtc_transport_connected"":false"),
                channelFrame.Replace(@"""reliable_messages"":0", @"""reliable_messages"":2"),
            };
            foreach (string invalidFrame in invalidChannelFrames)
            {
                if (!JoinProbeRunner.ParseMachineOutput(prefix + invalidFrame)
                    .Contains("JOIN_PROBE_IPC_INCONSISTENT"))
                    throw new InvalidOperationException("Unverified Stage11.1 data-channel state was accepted.");
            }
            if (!JoinProbeRunner.ParseMachineOutput(prefix
                + active.Replace(@"""reliable_messages"":2", @"""reliable_messages"":-1"))
                    .Contains("JOIN_PROBE_IPC_SCHEMA_ERROR"))
                throw new InvalidOperationException("Negative received-packet count was accepted.");
            // Stage12.5: only read-only server/transport prerequisites can
            // be attested. Full OPEN DataChannels + an auth_mode flag must
            // still be classified as missing game identity and protocol.
            const string stage125Extra = @",""g79_mode_confirmed"":true,"
                + @"""stage12_readiness"":""STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED"","
                + @"""stage12_game_identity_verified"":false,"
                + @"""stage12_game_protocol_verified"":false,"
                + @"""stage12_game_login_attempted"":false";
            string attested = channelFrame.Substring(0,channelFrame.Length - 1)
                + stage125Extra + "}";
            string gateResult = JoinProbeRunner.ParseMachineOutput(prefix + attested);
            if (!gateResult.Contains("STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED")
                || !gateResult.Contains("缺少网易认可的游戏身份")
                || gateResult.Contains("JOIN_PROBE_IPC_"))
                throw new InvalidOperationException("Stage12.5 missing game authorization not displayed.");
            string[] forgedEvidence = {
                attested.Replace("STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED",
                    "STAGE12_ADMISSION_NOT_VERIFIED"),
                attested.Replace(@"""stage12_game_identity_verified"":false",
                    @"""stage12_game_identity_verified"":true"),
                attested.Replace(@"""stage12_game_protocol_verified"":false",
                    @"""stage12_game_protocol_verified"":true"),
                attested.Replace(@"""stage12_game_login_attempted"":false",
                    @"""stage12_game_login_attempted"":true")
            };
            foreach (string forged in forgedEvidence)
            {
                if (!JoinProbeRunner.ParseMachineOutput(prefix + forged)
                    .Contains("JOIN_PROBE_IPC_INCONSISTENT"))
                    throw new InvalidOperationException("Forged Stage12.5 game readiness was accepted.");
            }
            string assumedProtocol = attested.Substring(0,attested.Length - 1)
                + @",""room_version"":898}";
            if (!JoinProbeRunner.ParseMachineOutput(prefix + assumedProtocol)
                    .Contains("STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED"))
                throw new InvalidOperationException("Lobby metadata was mistaken for Bedrock game version.");
            if (JoinProbeRunner.ComputeStage12Readiness(true,true,true,true,true,
                    true,true,true) != "STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED"
                || JoinProbeRunner.ComputeStage12Readiness(false,true,true,true,true,
                    true,true,true) != "STAGE12_ADMISSION_NOT_VERIFIED")
                throw new InvalidOperationException("Stage12.5 preflight gate did not fail closed.");
            Console.WriteLine("PASS: Stage12.5 real transport evidence gate and explicit game identity/version unknown");

            // Stage12.6: a confirmed receive-only local worker is not a
            // Minecraft game login. C# recomputes consistency from true
            // admission, channel states and per-packet counters.
            string readonlyPositive = channelFrame.Substring(0,channelFrame.Length-1)
                + @",""stage12_readonly_ipc_verified"":true,"
                + @"""stage12_readonly_ipc_messages"":0}";
            string readonlyOutput=JoinProbeRunner.ParseMachineOutput(prefix+readonlyPositive);
            if (!readonlyOutput.Contains("STAGE12_READONLY_IPC_VERIFIED_GAME_NOT_JOINED")
                || !readonlyOutput.Contains("0 条")
                || readonlyOutput.Contains("JOIN_PROBE_IPC_"))
                throw new InvalidOperationException("Verified receive-only IPC never surfaced.");
            string[] fakeReadonly = {
                readonlyPositive.Replace(@"""room_admission_accepted"":true",
                    @"""room_admission_accepted"":false"),
                readonlyPositive.Replace(@"""stage12_readonly_ipc_messages"":0",
                    @"""stage12_readonly_ipc_messages"":1"),
                readonlyPositive.Replace(@"""stage12_readonly_ipc_verified"":true",
                    @"""stage12_readonly_ipc_verified"":false")
                    .Replace(@"""stage12_readonly_ipc_messages"":0",
                        @"""stage12_readonly_ipc_messages"":2"),
                readonlyPositive.Replace(@"""observation_seconds"":30",
                    @"""observation_seconds"":8")
            };
            foreach (string falseRecord in fakeReadonly)
            {
                if (!JoinProbeRunner.ParseMachineOutput(prefix+falseRecord)
                    .Contains("JOIN_PROBE_IPC_INCONSISTENT"))
                    throw new InvalidOperationException("Forged Stage12.6 observation was accepted.");
            }
            var badDuration = JoinProbeRunner.RunReadOnlyIPCAsync(
                "643871","",8,System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            if (!badDuration.Contains("STAGE12_READONLY_WORKER_INVALID"))
                throw new InvalidOperationException("Stage12.6 accepted an unsupported observation mode.");
            Console.WriteLine("PASS: Stage12.6 no-outbound read-only IPC evidence and fail-closed WPF parser");

            string durationRejected = JoinProbeRunner.RunAsync("643871", "", true, 17,
                System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            if (!durationRejected.Contains("OBSERVATION_DURATION_INVALID"))
                throw new InvalidOperationException("Non-allowlisted observation duration accepted.");
            Console.WriteLine("PASS: Stage11.1 two channels OPEN, 30/60s, safe passive counts and strict admission");

            Console.WriteLine("PASS: Stage10.5 8s stability window, early disconnect, and cancellation status integrity");
            Console.WriteLine("PASS: Stage10.1 fixed signaling, answer, ICE/DTLS/SCTP failure statuses");
            Console.WriteLine("PASS: Stage10 signaling and WebRTC success/failure state validation");

            Console.WriteLine("PASS: versioned Stage9.1 machine IPC, strict flags and redaction");
        }

        private static void TestCompiledProbeIPC(string exe)
        {
            if (!System.IO.File.Exists(exe))
                throw new InvalidOperationException("Cannot locate built join-probe executable.");
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Path.GetFullPath(exe),
                Arguments = "-ipc-selftest -machine-json",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false, true),
                StandardErrorEncoding = new System.Text.UTF8Encoding(false, true),
                CreateNoWindow = true
            };
            using (var process = System.Diagnostics.Process.Start(info))
            {
                if (process == null)
                    throw new InvalidOperationException("Cannot execute compiled Stage9.1 probe.");
                string output = process.StandardOutput.ReadToEnd();
                string errorOutput = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(10000))
                {
                    process.Kill();
                    throw new InvalidOperationException("Compiled probe IPC selftest timeout.");
                }
                if (process.ExitCode != 0 || errorOutput.Length > 0)
                    throw new InvalidOperationException("Compiled probe IPC selftest failed.");
                string result = JoinProbeRunner.ParseMachineOutput(output);
                if (!result.Contains("PE_AUTH_UNAVAILABLE")
                    || !result.Contains("STAGE12_ADMISSION_NOT_VERIFIED")
                    || result.Contains("JOIN_PROBE_IPC_"))
                    throw new InvalidOperationException("Actual Go stdout failed Stage12.5 C# readiness IPC parser.");
            }
            Console.WriteLine("PASS: actual compiled Go EXE to .NET Framework UTF-8 Stage9.1 IPC");

            // End-to-end local cancellation test: the real compiled helper
            // waits for stdin EOF, releases its scope, and emits a single
            // safe cancellation frame. No account, cookie or network is used.
            var cancelInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Path.GetFullPath(exe),
                Arguments = "-ipc-cancel-selftest -machine-json -cancel-on-stdin-close",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false, true),
                StandardErrorEncoding = new System.Text.UTF8Encoding(false, true),
                CreateNoWindow = true
            };
            using (var cancelled = System.Diagnostics.Process.Start(cancelInfo))
            {
                if (cancelled == null)
                    throw new InvalidOperationException("Cannot launch local cancellation fixture.");
                cancelled.StandardInput.Close();
                if (!cancelled.WaitForExit(10000))
                {
                    cancelled.Kill();
                    throw new InvalidOperationException("Go probe ignored WPF stdin EOF.");
                }
                if (cancelled.ExitCode != 0)
                    throw new InvalidOperationException("Go probe cancellation fixture failed.");
                string cancellationResult = JoinProbeRunner.ParseMachineOutput(
                    cancelled.StandardOutput.ReadToEnd());
                if (!cancellationResult.Contains("LOCAL_PROBE_CANCELLED")
                    || cancellationResult.Contains("JOIN_PROBE_IPC_"))
                    throw new InvalidOperationException(
                        "Windows Go-to-WPF cancellation IPC returned an unsafe result.");
                if (cancelled.StandardError.ReadToEnd().Length > 0)
                    throw new InvalidOperationException("Cancellation fixture printed unexpected stderr.");
            }
            Console.WriteLine("PASS: compiled Go probe exits cleanly on WPF stdin EOF without networking");
        }

        private static void ExpectNode(bool shouldFind, string testName, string json)
        {
            var result = LobbyNodeClient.ParseResponse("9366", json);
            if (result.IsMapped != shouldFind)
            {
                throw new InvalidOperationException(
                    "Test failed: " + testName + " -> " + result.Status + ", " + result.Details);
            }
            Console.WriteLine("PASS: " + testName);
        }

        private static void Expect(bool shouldFind, string testName, string json)
        {
            var result = RoomDiscoveryClient.ParseResponse("453649", json);
            if (result.IsMetadataFound != shouldFind)
            {
                throw new InvalidOperationException(
                    "Test failed: " + testName + " -> " + result.Status + ", " + result.Details);
            }
            Console.WriteLine("PASS: " + testName);
        }
    }
}
