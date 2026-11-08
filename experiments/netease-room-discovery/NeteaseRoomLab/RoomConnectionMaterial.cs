using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// A strictly read-only room+public-node correlation. Values are parsed
    /// from known official-host read-only endpoints; they do not grant access.
    /// Source format verified against FunAuth's TanLobbyLogin implementation.
    /// </summary>
    public static class RoomConnectionMetadata
    {
        private const int MaxJsonLength = 1024 * 1024;

        public sealed class Room
        {
            public uint OwnerID { get; internal set; }
            public string RoomID { get; internal set; }
            public string NodeID { get; internal set; }
            public bool IsFull { get; internal set; }
            public bool NeedsPassword { get; internal set; }
        }

        public sealed class Node
        {
            public string PublicHost { get; internal set; }
            public int LobbyPort { get; internal set; }
            public int SignalingPort { get; internal set; }
        }

        public static bool TryParseRoom(string json, string roomNumber,
            out Room room)
        {
            room = null;
            if (!Regex.IsMatch(roomNumber ?? "", @"^[0-9]{4,10}$")
                || String.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
                return false;
            try
            {
                var obj = new JavaScriptSerializer { MaxJsonLength = MaxJsonLength }
                    .DeserializeObject(json) as IDictionary<string, object>;
                if (obj == null || Numeric(Value(obj, "code")) != "0") return false;
                var rows = Value(obj, "list") as IList;
                if (rows == null) return false;
                foreach (var row in rows)
                {
                    var entry = row as IDictionary<string, object>;
                    if (entry == null) continue;
                    string rid = Numeric(Value(entry, "rid"));
                    string name = Convert.ToString(Value(entry, "name"), CultureInfo.InvariantCulture);
                    string unique = Convert.ToString(Value(entry, "room_unique_id"), CultureInfo.InvariantCulture);
                    if (roomNumber != rid && roomNumber != name && roomNumber != unique)
                        continue;

                    uint owner;
                    string ownerText = Numeric(Value(entry, "hid"));
                    string srv = Numeric(Value(entry, "srv"));
                    if (!UInt32.TryParse(ownerText, out owner) || owner == 0
                        || srv == "" || srv == "0") return false;

                    uint count, capacity;
                    bool full = UInt32.TryParse(Numeric(Value(entry, "cnt")), out count)
                        && UInt32.TryParse(Numeric(Value(entry, "cap")), out capacity)
                        && capacity > 0 && count >= capacity;

                    room = new Room {
                        OwnerID = owner,
                        RoomID = rid == "" ? roomNumber : rid,
                        NodeID = srv,
                        IsFull = full,
                        NeedsPassword = Numeric(Value(entry, "need_password")) == "1"
                    };
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        public static bool TryParseNode(string json, string nodeId,
            out Node node)
        {
            node = null;
            if (!Regex.IsMatch(nodeId ?? "", @"^[0-9]{1,10}$")
                || String.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
                return false;
            try
            {
                var rows = new JavaScriptSerializer { MaxJsonLength = MaxJsonLength }
                    .DeserializeObject(json) as IList;
                if (rows == null) return false;
                foreach (var row in rows)
                {
                    var entry = row as IDictionary<string, object>;
                    if (entry == null || Numeric(Value(entry, "id")) != nodeId)
                        continue;
                    string host = Convert.ToString(Value(entry, "ip"),
                        CultureInfo.InvariantCulture) ?? "";
                    if (!Regex.IsMatch(host, @"^[a-zA-Z0-9.:-]{3,253}$"))
                        return false;
                    var ports = Value(entry, "ports") as IList;
                    if (ports == null || ports.Count == 0) return false;
                    int lobbyPort, signalPort;
                    if (!Int32.TryParse(Numeric(ports[0]), out lobbyPort)
                        || !Int32.TryParse(Numeric(Value(entry, "SignalWebPort")),
                            out signalPort)
                        || lobbyPort < 1 || lobbyPort > 65535
                        || signalPort < 1 || signalPort > 65535)
                        return false;
                    node = new Node { PublicHost = host,
                        LobbyPort = lobbyPort, SignalingPort = signalPort };
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static object Value(IDictionary<string, object> source, string key)
        {
            object value;
            return source.TryGetValue(key, out value) ? value : null;
        }

        private static string Numeric(object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            return Regex.IsMatch(text, @"^[0-9]{1,20}$") ? text : "";
        }
    }

    /// <summary>
    /// Independently computes the *candidate* cryptographic session parameters
    /// described in FunAuth. Not a room admission response from NetEase.
    /// Materials are ephemeral and are cleared by Dispose; the source login
    /// token itself is never persisted by this class.
    /// </summary>
    public sealed class RoomConnectionMaterial : IDisposable
    {
        public byte[] RaknetRand { get; private set; }
        public byte[] RaknetAESRand { get; private set; }
        public byte[] EncryptKeyBytes { get; private set; }
        public byte[] DecryptKeyBytes { get; private set; }
        public byte[] SignalingSeed { get; private set; }
        public byte[] SignalingTicket { get; private set; }

        private RoomConnectionMaterial() { }

        public static bool SupportsTokenKey(string loginToken)
        {
            int length = loginToken == null ? 0
                : Encoding.UTF8.GetByteCount(loginToken);
            return length == 16 || length == 24 || length == 32;
        }

        public static RoomConnectionMaterial Derive(string loginToken,
            byte[] raknetRandom, byte[] signalingRandom)
        {
            if (!SupportsTokenKey(loginToken))
                throw new ArgumentException("Unsupported login token key length.");
            if (raknetRandom == null || raknetRandom.Length != 16
                || signalingRandom == null || signalingRandom.Length != 16)
                throw new ArgumentException("Nonce length must be 16 bytes.");
            byte[] tokenBytes = Encoding.UTF8.GetBytes(loginToken);
            byte[] digest = null;
            try
            {
                using (var md5 = MD5.Create())
                    digest = md5.ComputeHash(tokenBytes);

                var result = new RoomConnectionMaterial();
                result.RaknetRand = (byte[])raknetRandom.Clone();
                result.SignalingSeed = (byte[])signalingRandom.Clone();
                result.RaknetAESRand = EncryptPaddedECB(raknetRandom, digest);
                result.SignalingTicket = EncryptPaddedECB(signalingRandom, tokenBytes);
                result.EncryptKeyBytes = new byte[32];
                result.DecryptKeyBytes = new byte[32];
                Buffer.BlockCopy(digest, 0, result.EncryptKeyBytes, 0, 16);
                Buffer.BlockCopy(raknetRandom, 0, result.EncryptKeyBytes, 16, 16);
                Buffer.BlockCopy(raknetRandom, 0, result.DecryptKeyBytes, 0, 16);
                Buffer.BlockCopy(digest, 0, result.DecryptKeyBytes, 16, 16);
                return result;
            }
            finally
            {
                Array.Clear(tokenBytes, 0, tokenBytes.Length);
                if (digest != null) Array.Clear(digest, 0, digest.Length);
            }
        }

        public static RoomConnectionMaterial CreateRandom(string loginToken)
        {
            byte[] raknet = new byte[16];
            byte[] signal = new byte[16];
            try
            {
                using (var random = RandomNumberGenerator.Create())
                {
                    random.GetBytes(raknet);
                    random.GetBytes(signal);
                }
                return Derive(loginToken, raknet, signal);
            }
            finally
            {
                Array.Clear(raknet, 0, raknet.Length);
                Array.Clear(signal, 0, signal.Length);
            }
        }

        private static byte[] EncryptPaddedECB(byte[] data, byte[] key)
        {
            using (var algorithm = Aes.Create())
            {
                algorithm.Mode = CipherMode.ECB;
                algorithm.Padding = PaddingMode.PKCS7;
                algorithm.Key = key;
                using (var encryptor = algorithm.CreateEncryptor())
                    return encryptor.TransformFinalBlock(data, 0, data.Length);
            }
        }

        public void Dispose()
        {
            Clear(RaknetRand);
            Clear(RaknetAESRand);
            Clear(EncryptKeyBytes);
            Clear(DecryptKeyBytes);
            Clear(SignalingSeed);
            Clear(SignalingTicket);
        }

        private static void Clear(byte[] bytes)
        {
            if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
        }
    }
}
