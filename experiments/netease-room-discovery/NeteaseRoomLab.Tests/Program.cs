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
                Console.WriteLine("Room/node parser and local email-helper safety tests passed. No network requests or credentials used.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
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
