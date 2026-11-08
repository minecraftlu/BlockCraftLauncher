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

            Console.WriteLine("PASS: safe G79 config, field diagnostics and zero credential disclosure");
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
