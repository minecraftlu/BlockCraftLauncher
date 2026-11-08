using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// Optional read-only mapping of room.srv to a public NetEase lobby node listing.
    /// This mapping is a research hypothesis, NOT a playable server address.
    /// No credentials, cookies, or game packets are used.
    /// </summary>
    public sealed class LobbyNodeClient
    {
        private const string Endpoint =
            "https://g79.update.netease.com/transferserver_obt_new.list";
        private const int MaxPayloadLength = 2 * 1024 * 1024;

        public async Task<LobbyNodeResult> LookupAsync(string nodeId)
        {
            if (!Regex.IsMatch(nodeId ?? "", @"^[0-9]{1,10}$"))
                return LobbyNodeResult.Inconclusive("节点标记缺失或格式错误。请先查询房间。");

            using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                try
                {
                    using (var response = await client.GetAsync(
                        Endpoint, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return LobbyNodeResult.Inconclusive(
                                "节点列表 HTTP " + (int)response.StatusCode + "，暂时无法验证节点映射。");

                        var length = response.Content.Headers.ContentLength;
                        if (length.HasValue && length.Value > MaxPayloadLength)
                            return LobbyNodeResult.Inconclusive("节点列表响应过大，已停止读取。");

                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new System.IO.StreamReader(stream))
                        {
                            var buffer = new char[4096];
                            var text = new System.Text.StringBuilder();
                            int count;
                            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)
                                .ConfigureAwait(false)) != 0)
                            {
                                text.Append(buffer, 0, count);
                                if (text.Length > MaxPayloadLength)
                                    return LobbyNodeResult.Inconclusive("节点列表响应过大，已停止读取。");
                            }
                            return ParseResponse(nodeId, text.ToString());
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    return LobbyNodeResult.Inconclusive("节点列表请求超时，不能据此判断节点不可用。");
                }
                catch (HttpRequestException)
                {
                    return LobbyNodeResult.Inconclusive("节点列表网络请求失败，可能是网络或接口限制。");
                }
                catch (Exception)
                {
                    return LobbyNodeResult.Inconclusive("节点列表解析失败，服务端格式可能已变化。");
                }
            }
        }

        public static LobbyNodeResult ParseResponse(string nodeId, string json)
        {
            if (!Regex.IsMatch(nodeId ?? "", @"^[0-9]{1,10}$") ||
                string.IsNullOrWhiteSpace(json))
            {
                return LobbyNodeResult.Inconclusive("节点标记或节点列表为空。");
            }

            object parsed;
            try
            {
                var serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = MaxPayloadLength
                };
                parsed = serializer.DeserializeObject(json);
            }
            catch (ArgumentException)
            {
                return LobbyNodeResult.Inconclusive("节点列表不是有效 JSON。");
            }
            catch (InvalidOperationException)
            {
                return LobbyNodeResult.Inconclusive("节点列表无法解析。");
            }

            var list = parsed as IList;
            if (list == null)
            {
                return LobbyNodeResult.Inconclusive("节点列表结构不匹配，尚未确认服务端格式。");
            }

            foreach (var item in list)
            {
                var node = item as IDictionary<string, object>;
                if (node == null) continue;
                object idValue;
                if (!node.TryGetValue("id", out idValue)) continue;
                if (Numeric(idValue) != nodeId) continue;

                var id = Numeric(idValue);
                var status = Numeric(Get(node, "status"));
                var webPort = Numeric(Get(node, "SignalWebPort"));
                var serverType = CleanString(Get(node, "ServerType"));
                var ip = CleanHost(Get(node, "ip"));
                var ports = Get(node, "ports") as IList;
                var portCount = ports == null ? "未知" : ports.Count.ToString(CultureInfo.InvariantCulture);

                var lines = new[]
                {
                    "公共大厅节点列表中找到 ID " + id + "（仅节点映射，不是入房）。",
                    "节点 ID：" + id,
                    "公开节点地址：" + ip,
                    "节点状态码：" + Safe(status),
                    "信令服务端口标记：" + Safe(webPort),
                    "节点协议/类型标记：" + serverType,
                    "列表内端口数量：" + portCount,
                    "",
                    "警告：节点地址不等于玩家世界 IP，不能直接添加到 Minecraft 服务器列表。",
                    "下一阶段仍需获得合法账号会话、游客入房回执及对端连接信息。"
                };
                return LobbyNodeResult.Mapped(string.Join(Environment.NewLine, lines));
            }

            return LobbyNodeResult.Inconclusive(
                "列表可读取，但没有与 srv=" + nodeId + " 对应的节点 ID；"
                + "可能是不同编号体系或列表已变化，不能推断房间离线。");
        }

        private static object Get(IDictionary<string, object> node, string key)
        {
            object o;
            return node.TryGetValue(key, out o) ? o : null;
        }

        private static string Numeric(object o)
        {
            var v = Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
            return Regex.IsMatch(v, @"^[0-9]{1,10}$") ? v : "";
        }

        private static string Safe(string v)
        {
            return string.IsNullOrEmpty(v) ? "(未提供)" : v;
        }

        private static string CleanHost(object o)
        {
            var v = Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
            return Regex.IsMatch(v, @"^[a-zA-Z0-9.:\-]{1,253}$") ? v : "(未提供)";
        }

        private static string CleanString(object o)
        {
            var v = Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";
            return Regex.IsMatch(v, @"^[a-zA-Z0-9_\-]{1,40}$") ? v : "(未提供)";
        }
    }

    public sealed class LobbyNodeResult
    {
        public bool IsMapped { get; private set; }
        public string Status { get; private set; }
        public string Details { get; private set; }

        private LobbyNodeResult(bool isMapped, string status, string details)
        {
            IsMapped = isMapped;
            Status = status;
            Details = details;
        }

        public static LobbyNodeResult Mapped(string details)
        {
            return new LobbyNodeResult(true, "NODE_METADATA_FOUND", details);
        }

        public static LobbyNodeResult Inconclusive(string details)
        {
            return new LobbyNodeResult(false, "NODE_MAPPING_INCONCLUSIVE", details);
        }
    }
}
