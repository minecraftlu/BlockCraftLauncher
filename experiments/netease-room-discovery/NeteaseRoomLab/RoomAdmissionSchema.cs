using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// Network-free, secrets-safe structural checks for the Tan Lobby access
    /// envelope returned by the community AuthService/Bunker compatibility API.
    /// A JSON object's "success":true is never proof of server authorization.
    /// </summary>
    public static class RoomAdmissionSchema
    {
        public const int MaxEnvelopeLength = 64 * 1024;
        public const string CompleteStatus = "LOCAL_ADMISSION_SCHEMA_OK_UNVERIFIED";
        public const string BlockedStatus = "ADMISSION_PROVIDER_NOT_CONFIGURED";

        private static readonly string[] ByteFields = {
            "raknet_rand", "raknet_aes_rand", "encrypt_key_bytes",
            "decrypt_key_bytes", "signaling_seed", "signaling_ticket"
        };

        public static string Inspect(string json, string roomNumber)
        {
            if (!Regex.IsMatch(roomNumber ?? "", @"^[0-9]{4,10}$"))
                return "ADMISSION_INPUT_ERROR: 房间号必须是 4 至 10 位数字。";

            if (String.IsNullOrWhiteSpace(json) || json.Length > MaxEnvelopeLength)
                return "ADMISSION_ENVELOPE_INVALID: 准入回执为空或超过安全大小限制。";

            Dictionary<string, object> envelope;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxEnvelopeLength };
                envelope = serializer.Deserialize<Dictionary<string, object>>(json);
            }
            catch (Exception)
            {
                return "ADMISSION_ENVELOPE_INVALID: 不是有效的准入 JSON 对象。";
            }
            if (envelope == null)
                return "ADMISSION_ENVELOPE_INVALID: 缺少准入 JSON 对象。";

            object flag;
            if (!envelope.TryGetValue("success", out flag) || !(flag is bool))
                return "ADMISSION_ENVELOPE_INVALID: 缺少布尔 success 标志。";

            if (!(bool)flag)
                return "ADMISSION_RESPONSE_REJECTED_UNVERIFIED: 回执标志为拒绝。"
                    + " 未展示原始 error_info，不能断定是否为真实服务器拒绝。";

            // The response does not contain an authenticated room_id binding.
            // Therefore not even a full response should be treated as permission
            // to dial the target. Only an authenticated, user-approved provider
            // bound to the requested room may establish real authority.
            foreach (string name in new[] { "user_unique_id", "room_owner_id" })
            {
                if (!HasPositiveUInt(envelope, name))
                    return MissingOrBad(name);
            }
            foreach (string name in new[] {
                "raknet_server_address", "signaling_server_address" })
            {
                if (!HasAddress(envelope, name))
                    return MissingOrBad(name);
            }
            foreach (string name in ByteFields)
            {
                if (!HasEncodedBytes(envelope, name))
                    return MissingOrBad(name);
            }
            return CompleteStatus + ": 仅检查合成或授权回执的字段结构。"
                + " 必要大厅地址、随机数、加解密材料与信令字段结构存在。"
                + " 不能确认来源、房间号绑定、真实准入或游戏连接。"
                + " 实际入房功能仍需可信授权提供者和服务端入房回执。";
        }

        public static string OfflineSelfTest(string roomNumber)
        {
            if (!Regex.IsMatch(roomNumber ?? "", @"^[0-9]{4,10}$"))
                return "ADMISSION_INPUT_ERROR: 房间号必须是 4 至 10 位数字。";
            return Inspect(SyntheticEnvelope(), roomNumber)
                + Environment.NewLine
                + BlockedStatus + ": 当前没有经核实的真实房间准入提供者。"
                + " 自检不会读取 Cookie、发起任何网络请求或进入房间。";
        }

        // Synthetic metadata uses reserved .invalid domains and unrelated mock
        // bytes; never contains real credentials and is never sent over a socket.
        public static string SyntheticEnvelope()
        {
            var response = new Dictionary<string, object> {
                { "success", true },
                { "user_unique_id", 10001 },
                { "room_owner_id", 20002 },
                { "raknet_server_address", "lobby.example.invalid:19132" },
                { "signaling_server_address", "signal.example.invalid:8900" },
                { "raknet_rand", MockBytes(16) },
                { "raknet_aes_rand", MockBytes(16) },
                { "encrypt_key_bytes", MockBytes(32) },
                { "decrypt_key_bytes", MockBytes(32) },
                { "signaling_seed", MockBytes(16) },
                { "signaling_ticket", MockBytes(16) }
            };
            return new JavaScriptSerializer().Serialize(response);
        }

        private static string MockBytes(int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)(i + 1);
            return Convert.ToBase64String(bytes);
        }

        private static bool HasPositiveUInt(Dictionary<string, object> fields, string key)
        {
            object raw;
            if (!fields.TryGetValue(key, out raw) || raw == null) return false;
            uint value;
            return UInt32.TryParse(Convert.ToString(raw,
                System.Globalization.CultureInfo.InvariantCulture), out value)
                && value > 0;
        }

        private static bool HasAddress(Dictionary<string, object> fields, string key)
        {
            object raw;
            if (!fields.TryGetValue(key, out raw) || !(raw is string)) return false;
            string address = (string)raw;
            if (address.Length < 3 || address.Length > 255) return false;

            // No DNS lookups or sockets. Accept IP:port, [IPv6]:port or DNS:port
            // only for structural validation, NOT remote destination approval.
            int colon = address.LastIndexOf(':');
            if (colon <= 0 || colon == address.Length - 1) return false;
            string host = address.Substring(0, colon);
            if (String.IsNullOrWhiteSpace(host) || host.Contains(" ")
                || host.Contains("/") || host.Contains("\\"))
                return false;
            int port;
            return Int32.TryParse(address.Substring(colon + 1), out port)
                && port >= 1 && port <= 65535;
        }

        private static bool HasEncodedBytes(Dictionary<string, object> fields, string key)
        {
            object raw;
            if (!fields.TryGetValue(key, out raw) || !(raw is string)) return false;
            string text = (string)raw;
            if (text.Length < 4 || text.Length > 8192) return false;
            try
            {
                // Go encoding/json marshals []byte fields as base64 strings.
                byte[] bytes = Convert.FromBase64String(text);
                bool valid = bytes.Length >= 8 && bytes.Length <= 4096;
                Array.Clear(bytes, 0, bytes.Length);
                return valid;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string MissingOrBad(string field)
        {
            // These keys are hard-coded trusted labels, never error data copied
            // from untrusted JSON or private remote responses.
            return "ADMISSION_ENVELOPE_INCOMPLETE: 必需字段缺失或格式不合法："
                + field + "。没有展示任何原始密钥或 token。";
        }
    }

    /// <summary>
    /// Later, a lawful user-approved provider could implement this interface.
    /// No live provider is shipped or invoked by the current application.
    /// </summary>
    public interface IRoomAdmissionProvider
    {
        System.Threading.Tasks.Task<string> RequestAccessEnvelopeAsync(
            string roomNumber, System.Threading.CancellationToken cancellation);
    }
}
