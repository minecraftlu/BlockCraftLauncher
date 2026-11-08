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
                Console.WriteLine("All room and public node parser tests passed. No network requests made.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
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
