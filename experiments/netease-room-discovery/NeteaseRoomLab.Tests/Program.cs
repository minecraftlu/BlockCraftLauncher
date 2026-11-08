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
                Console.WriteLine("All room metadata parser tests passed. No network requests made.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
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
