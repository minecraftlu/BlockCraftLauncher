using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// A deliberately read-only probe. This is NOT a login, join, or gameplay client.
    /// The endpoint is community documented, not a confirmed public third-party API.
    /// Do not add cookies, session tokens, or credential logging here.
    /// </summary>
    public sealed class RoomDiscoveryClient
    {
        private const string Endpoint = "https://g79mcltransfer.minecraft.cn/room-with-name";
        private const int MaxResponseCharacters = 1024 * 1024;

        public async Task<RoomDiscoveryResult> LookupAsync(string roomNumber, uint uid)
        {
            if (string.IsNullOrWhiteSpace(roomNumber) ||
                !Regex.IsMatch(roomNumber, @"^[0-9]{4,10}$"))
            {
                return RoomDiscoveryResult.Inconclusive("房间号格式错误：请输入 4–10 位数字。");
            }

            var json = new JavaScriptSerializer().Serialize(
                new Dictionary<string, object>
                {
                    { "name", roomNumber },
                    { "uid", uid }
                });

            using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            using (var body = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                try
                {
                    using (var response = await client.PostAsync(Endpoint, body).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return RoomDiscoveryResult.Inconclusive(
                                "HTTP " + (int)response.StatusCode + "：服务端拒绝或接口发生变化，不能据此判断房间不存在。");
                        }

                        if (response.Content.Headers.ContentLength.HasValue &&
                            response.Content.Headers.ContentLength.Value > MaxResponseCharacters)
                        {
                            return RoomDiscoveryResult.Inconclusive("响应过大，已拒绝解析。");
                        }

                        var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (responseText.Length > MaxResponseCharacters)
                        {
                            return RoomDiscoveryResult.Inconclusive("响应过大，已拒绝解析。");
                        }

                        return ParseResponse(roomNumber, responseText);
                    }
                }
                catch (TaskCanceledException)
                {
                    return RoomDiscoveryResult.Inconclusive("网络请求超时（15 秒）；不能据此判断房间不存在。");
                }
                catch (HttpRequestException)
                {
                    return RoomDiscoveryResult.Inconclusive("HTTPS 连接失败；请检查本机网络及 DNS，或服务端可能已调整。");
                }
                catch (Exception)
                {
                    return RoomDiscoveryResult.Inconclusive("请求或解析出现未预期错误；未输出任何凭据或原始响应。");
                }
            }
        }

        /// <summary>
        /// Offline-testable response parser. Does not call a network endpoint.
        /// </summary>
        public static RoomDiscoveryResult ParseResponse(string roomNumber, string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return RoomDiscoveryResult.Inconclusive("服务端返回空响应。");
            }

            IDictionary<string, object> data;
            try
            {
                data = new JavaScriptSerializer().DeserializeObject(responseText)
                    as IDictionary<string, object>;
            }
            catch (ArgumentException)
            {
                return RoomDiscoveryResult.Inconclusive("服务端响应不符合 JSON 格式。");
            }
            catch (InvalidOperationException)
            {
                return RoomDiscoveryResult.Inconclusive("无法解析服务端 JSON。");
            }

            if (data == null)
            {
                return RoomDiscoveryResult.Inconclusive("服务端未返回 JSON 对象。");
            }

            object code;
            if (data.TryGetValue("code", out code) && SafeString(code) != "0")
            {
                return RoomDiscoveryResult.Inconclusive(
                    "网易服务返回状态码 " + SafeStatusCode(code) + "；可能需要账号身份或接口已变化。");
            }

            object listValue;
            if (!data.TryGetValue("list", out listValue))
            {
                return RoomDiscoveryResult.Inconclusive("响应没有 list 字段；接口格式可能已发生变化。");
            }

            var list = listValue as IList;
            if (list == null)
            {
                return RoomDiscoveryResult.Inconclusive("list 字段不是数组；接口格式可能已变化。");
            }

            foreach (var item in list)
            {
                var room = item as IDictionary<string, object>;
                if (room == null) continue;

                var rid = Read(room, "rid");
                var name = Read(room, "name");
                if (rid != roomNumber && name != roomNumber) continue;

                var lines = new[]
                {
                    "查询到与房间号匹配的元数据（不是联机成功）",
                    "房间号：" + roomNumber,
                    "服务端房间 ID：" + Display(rid),
                    "名称标识：" + Display(name),
                    "当前人数：" + Display(Read(room, "cnt")) + " / " + Display(Read(room, "cap")),
                    "游戏版本标记：" + Display(Read(room, "version")),
                    "平台标记：" + Display(Read(room, "platform")),
                    "节点标记：" + Display(Read(room, "srv")),
                    "下一关：合法入房会话与游戏协议连接，尚未验证。"
                };
                return RoomDiscoveryResult.Found(string.Join(Environment.NewLine, lines));
            }

            return RoomDiscoveryResult.Inconclusive(
                "查询响应里没有匹配的房间。可能是过期、可见性、账号上下文或接口变化，不能断定房间不存在。");
        }

        private static string Read(IDictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) ? SafeString(value) : "";
        }

        private static string SafeString(object value)
        {
            if (value == null) return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        private static string Display(string value)
        {
            // Numeric metadata only: avoid displaying untrusted arbitrary text or user IDs.
            return Regex.IsMatch(value ?? "", @"^[0-9]{1,20}$") ? value : "(未提供)";
        }

        private static string SafeStatusCode(object value)
        {
            var code = SafeString(value);
            return Regex.IsMatch(code, @"^-?[0-9]{1,9}$") ? code : "(未知)";
        }
    }

    public sealed class RoomDiscoveryResult
    {
        public bool IsMetadataFound { get; private set; }
        public string Status { get; private set; }
        public string Details { get; private set; }

        private RoomDiscoveryResult(bool isMetadataFound, string status, string details)
        {
            IsMetadataFound = isMetadataFound;
            Status = status;
            Details = details;
        }

        public static RoomDiscoveryResult Found(string details)
        {
            return new RoomDiscoveryResult(true, "ROOM_METADATA_FOUND", details);
        }

        public static RoomDiscoveryResult Inconclusive(string details)
        {
            return new RoomDiscoveryResult(false, "INCONCLUSIVE", details);
        }
    }
}
