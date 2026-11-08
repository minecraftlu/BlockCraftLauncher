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

            Console.WriteLine("PASS: email helper opt-in argument construction and unverified status handling");
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
