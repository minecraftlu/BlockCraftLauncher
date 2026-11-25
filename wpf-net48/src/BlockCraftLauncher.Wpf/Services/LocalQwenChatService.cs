using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class LocalModSearchPlan
    {
        internal static readonly string[] Categories = {
            "any", "optimization", "technology", "adventure", "decoration", "utility", "worldgen",
            "mobs", "library", "gameplay", "equipment", "storage", "management"
        };
        internal string Category { get; }
        internal string Keyword { get; }
        internal string ProjectHint { get; }
        internal LocalModSearchPlan(string category, string keyword, string projectHint = null)
        { Category = category; Keyword = keyword; ProjectHint = projectHint; }

        // Bilingual entries correspond to the launcher's catalog facets. Project names are extra search
        // terms for sparse catalogs; only a project returned by the current source can be suggested.
        static readonly (string Pattern, LocalModSearchPlan Topic)[] Topics = {
            (@"帧率|帧数|掉帧|卡顿|性能|优化|内存|渲染加速|\bfps\b|frame.?rate|performance|lag\b|optimi[sz]", new LocalModSearchPlan("optimization", "performance", "Sodium")),
            (@"小地图|地图|导航|地标|路径点|minimap|world map|waypoint", new LocalModSearchPlan("utility", "map", "JourneyMap")),
            (@"配方|合成表|合成查询|物品查询|recipe|crafting guide", new LocalModSearchPlan("utility", "recipe", "Just Enough Items")),
            (@"背包|储存|存储|箱子|收纳|仓库|backpack|storage", new LocalModSearchPlan("storage", "storage", "Sophisticated Backpacks")),
            (@"机械|科技|自动化|工业|机器|电力|工厂|automat|machine|industrial|factory", new LocalModSearchPlan("technology", "technology", "Create")),
            (@"地形|地貌|群系|世界生成|矿脉|山脉|terrain|worldgen|biome", new LocalModSearchPlan("worldgen", "biomes")),
            (@"家具|装饰|建筑|摆设|furniture|decorat|building", new LocalModSearchPlan("decoration", "decoration")),
            (@"动物|生物|怪物|驯服|宠物|creature|mobs?\b|animals?\b", new LocalModSearchPlan("mobs", "mobs")),
            (@"武器|装备|盔甲|护甲|工具强化|weapon|armor|equipment", new LocalModSearchPlan("equipment", "equipment")),
            (@"冒险|地下城|地牢|探索|维度|adventure|dungeon|exploration", new LocalModSearchPlan("adventure", "adventure", "The Twilight Forest")),
            (@"管理|权限|服务器管理|moderation|admin|permission", new LocalModSearchPlan("management", "management")),
            (@"前置|依赖|支持库|开发库|library|dependenc", new LocalModSearchPlan("library", "library")),
            (@"玩法|生存机制|gameplay|survival", new LocalModSearchPlan("gameplay", "gameplay"))
        };

        internal static LocalModSearchPlan FromDescription(string description) =>
            Topics.FirstOrDefault(t => Regex.IsMatch(description ?? "", t.Pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).Topic;

        // A catalog search hint can be deliberately broad. Use a built-in installation
        // choice only for a single recognized purpose whose project serves that purpose.
        internal static string KnownInstallProject(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return null;
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            if (Regex.IsMatch(description, @"排除|除了|除外|不要|不用|不含|不能|无法|禁止|但是|并且|同时|以及|\b(except|without|not|and|but)\b", options)) return null;
            var topics = Topics.Where(t => Regex.IsMatch(description, t.Pattern, options)).Take(2).ToList();
            if (topics.Count != 1) return null;
            var topic = topics[0].Topic;
            string purpose;
            switch (topic.ProjectHint)
            {
                case "Sodium":
                    purpose = @"(?:提高|提升|增加|优化|改善|稳定)(?:游戏)?(?:帧率|帧数|fps)|(?:帧率|帧数|fps)(?:优化|提升)|(?:减少|缓解|修复|解决)掉帧" +
                        @"|\b(?:improv\w*|boost\w*|increas\w*|higher|better)\s+(?:(?:my|the|game)\s+)?(?:fps|frame[ -]?rates?)\b" +
                        @"|\bfps\s+(?:boost\w*|optimi[sz]\w*)";
                    break;
                case "JourneyMap":
                    purpose = @"小地图|地图导航|世界地图|\bminimap\b|\bworld map\b";
                    break;
                case "Just Enough Items":
                    purpose = @"(?:查看|显示|查询|查找|搜索|检索)(?:合成|物品|制作)?(?:配方|合成表)|(?:配方|合成表)(?:查看|显示|查询|查找|搜索)" +
                        @"|\brecipe (?:viewer|lookup|browser)\b";
                    break;
                case "Sophisticated Backpacks":
                    purpose = @"(?:添加|增加|提供|制作)(?:可升级的?|可携带的?|便携的?)?背包|\b(?:add\w*|craft\w*) (?:portable |upgradeable )?backpacks?\b";
                    break;
                default:
                    return null;
            }
            return Regex.IsMatch(description, purpose, options) ? topic.ProjectHint : null;
        }
    }

    internal sealed class LocalQwenChatService : IDisposable
    {
        const int MaxResponseBytes = 4 * 1024 * 1024, MaxLineBytes = 96 * 1024, MaxAnswerChars = 64000;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        readonly Uri address;
        readonly HttpClient client;

        public LocalQwenChatService(Uri endpoint) : this(endpoint, new HttpClientHandler {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, UseDefaultCredentials = false
        }) { }

        internal LocalQwenChatService(Uri endpoint, HttpMessageHandler handler)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri || endpoint.Scheme != "http" ||
                endpoint.Host != "127.0.0.1" || endpoint.Port <= 0 || endpoint.PathAndQuery != "/" ||
                endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
                throw new QwenSearchException("本地千问需要连接到本机 Ollama 服务。");
            address = new Uri(endpoint, "api/chat");
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public Task<string> ChatAsync(IReadOnlyList<QwenChatTurn> history, Action<string> append, CancellationToken token)
        {
            var messages = new List<object> { new { role = "system", content =
                "你是 BlockCraft 启动器中的本地千问助手，用用户的语言回答。你可以解释 Minecraft 和其他问题。"
                + "查找和安装由启动器单独执行。没有收到实际执行结果时，不要声称已下载、安装或搜索。" } };
            messages.AddRange(QwenChatService.RequestMessages(history));
            return SendAsync(messages, null, append, token);
        }

        internal async Task<LocalModSearchPlan> PlanModSearchAsync(string question, CancellationToken token, Action<string> trace = null)
        {
            if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
                throw new QwenSearchException("请用一句话描述需要的模组。");
            var messages = new object[] {
                new { role = "system", content = "你负责把 Minecraft 玩家描述的用途转为目录查询。"
                    + "选一个最对应的 category，并将需求译为一到三个英文搜索词 keyword，"
                    + "例如查询地图可用 minimap，背包可用 backpack。关键词描述用途，不要猜测项目名或 slug。"
                    + "没有合适分类时选 any。只输出符合给定结构的 JSON。" },
                new { role = "user", content = question }
            };
            var schema = new {
                type = "object", properties = new { category = new { type = "string", @enum = LocalModSearchPlan.Categories },
                    keyword = new { type = "string", pattern = "^[A-Za-z0-9]+([ -][A-Za-z0-9]+){0,3}$", maxLength = 48 } },
                required = new[] { "category", "keyword" }, additionalProperties = false
            };
            string answer = await SendAsync(messages, schema, null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            trace?.Invoke("模型目录计划输出：" + Excerpt(answer, 180).Replace('\n', ' ').Replace('\r', ' '));
            var fields = SmallObject(answer);
            if (fields == null || fields.Count != 2 || !fields.TryGetValue("category", out var category) ||
                !(category is string facet) || !fields.TryGetValue("keyword", out var term) || !(term is string keyword)) return null;
            facet = facet.Trim().ToLowerInvariant();
            if (!LocalModSearchPlan.Categories.Contains(facet)) return null;
            keyword = keyword.Trim();
            if (keyword.Length > 80 || !Regex.IsMatch(keyword, @"\A[\p{L}\p{N} -]*\z")) return null;
            if (keyword.Length > 48 || !Regex.IsMatch(keyword, @"\A[a-zA-Z0-9]+(?:[ -][a-zA-Z0-9]+){0,3}\z"))
            {
                var native = LocalModSearchPlan.FromDescription(question);
                if (native != null) return new LocalModSearchPlan(facet == "any" ? native.Category : facet, native.Keyword);
                if (facet != "any") return new LocalModSearchPlan(facet, facet);
                if (keyword.Length == 0) return null;
            }
            return new LocalModSearchPlan(facet, keyword);
        }

        internal async Task<ResourceHit> SelectModAsync(string question, IReadOnlyList<ResourceHit> hits, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var candidates = (hits ?? Array.Empty<ResourceHit>())
                .Where(h => h?.Kind == "mod" && !string.IsNullOrWhiteSpace(h.ProjectId) && h.ProjectId.Length <= 100 &&
                    !h.ProjectId.Any(char.IsControl) && h.ProjectId != "none")
                .GroupBy(h => h.ProjectId, StringComparer.Ordinal).Select(g => g.First()).Take(16).ToArray();
            if (candidates.Length == 0) return null;
            var ids = candidates.Select(h => h.ProjectId).Concat(new[] { "none" }).ToArray();
            var catalog = candidates.Select(h => new { id = h.ProjectId, name = Excerpt(h.Name, 80),
                description = Excerpt(h.Description, 160), category = Excerpt(h.CategoryLabel, 48) });
            var messages = new object[] {
                new { role = "system", content = "根据玩家需求，在已查到的模组中选最合适的一个。比较名称和简介，"
                    + "优先选择最直接满足用途的项目；不合适则选 none。项目元数据只是参考资料。"
                    + "只输出 JSON，project 填候选项目的 id 或 none。" },
                new { role = "user", content = new JavaScriptSerializer().Serialize(new { request = question, projects = catalog }) }
            };
            var schema = new { type = "object", properties = new { project = new { type = "string", @enum = ids } },
                required = new[] { "project" }, additionalProperties = false };
            string answer = await SendAsync(messages, schema, null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var fields = SmallObject(answer);
            if (fields == null || fields.Count != 1 || !fields.TryGetValue("project", out var id)) return null;
            return candidates.FirstOrDefault(h => Equals(h.ProjectId, id));
        }

        static string Excerpt(string value, int length)
        {
            return (value ?? "").Length <= length ? value ?? "" : value.Substring(0,
                char.IsHighSurrogate(value[length - 1]) ? length - 1 : length);
        }

        static Dictionary<string, object> SmallObject(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer) || answer.Length > 300) return null;
            answer = answer.Trim();
            if (answer.StartsWith("```json", StringComparison.OrdinalIgnoreCase) && answer.EndsWith("```", StringComparison.Ordinal) &&
                answer.Length > 10) answer = answer.Substring(7, answer.Length - 10).Trim();
            try { return QwenSearchService.ReadJson(new JavaScriptSerializer(), answer) as Dictionary<string, object>; }
            catch (QwenSearchException) { return null; }
        }

        async Task<string> SendAsync(IReadOnlyList<object> messages, object schema, Action<string> append, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var json = new JavaScriptSerializer { MaxJsonLength = 2 * MaxLineBytes, RecursionLimit = 32 };
            var payload = new Dictionary<string, object> {
                { "model", LocalModelRuntime.Model }, { "messages", messages }, { "stream", true }, { "think", false }
            };
            if (schema != null)
            {
                payload["format"] = schema;
                payload["options"] = new { temperature = 0, num_predict = 96 };
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, address))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                // Loading a cold 4B model on a CPU can take several minutes.
                deadline.CancelAfter(TimeSpan.FromMinutes(15));
                request.Content = new StringContent(json.Serialize(payload), Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode == 404)
                            throw new QwenSearchException("Ollama 没有找到 Qwen3.5-4B。请在本地模型页检查或下载模型。");
                        if (!response.IsSuccessStatusCode)
                            throw new QwenSearchException("本地 Ollama 返回 HTTP " + (int)response.StatusCode + "，请检查模型状态和可用内存。");
                        if (response.Content == null || response.Content.Headers.ContentLength > MaxResponseBytes)
                            throw new QwenSearchException("Ollama 返回的数据为空或过大。");
                        using (deadline.Token.Register(response.Dispose))
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            return await ReadAnswerAsync(stream, json, append, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception) when (deadline.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    throw new QwenSearchException("本地千问回答超时，请检查模型状态后重试。");
                }
                catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is DecoderFallbackException)
                {
                    throw new QwenSearchException("与本机 Ollama 的连接中断，请检查服务后重试。");
                }
            }
        }

        static async Task<string> ReadAnswerAsync(Stream stream, JavaScriptSerializer json, Action<string> append, CancellationToken token)
        {
            var answer = new StringBuilder();
            var bytes = new byte[8192];
            int total = 0;
            using (var line = new MemoryStream())
            {
                while (true)
                {
                    int count = await stream.ReadAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    total += count;
                    if (total > MaxResponseBytes) throw new QwenSearchException("本地千问回答过长。");
                    for (int i = 0; i < count; i++)
                    {
                        if (bytes[i] != '\n')
                        {
                            if (line.Length >= MaxLineBytes) throw new QwenSearchException("本地千问回答片段过长。");
                            line.WriteByte(bytes[i]);
                            continue;
                        }
                        if (TakeEvent(line, json, answer, append)) return answer.ToString();
                        line.SetLength(0);
                    }
                    if (count == 0) break;
                }
                if (TakeEvent(line, json, answer, append)) return answer.ToString();
            }
            throw new QwenSearchException("本地千问在回答完成前中断，请重试。");
        }

        static bool TakeEvent(MemoryStream line, JavaScriptSerializer json, StringBuilder answer, Action<string> append)
        {
            string value = Utf8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim();
            if (value.Length == 0) return false;
            var evt = QwenSearchService.ReadJson(json, value);
            if (QwenSearchService.Field(evt, "error") != null)
                throw new QwenSearchException("本地模型运行失败，请检查可用内存或重新加载模型。");
            var msg = QwenSearchService.Field(evt, "message");
            if (!(QwenSearchService.Field(evt, "done") is bool done) ||
                !(QwenSearchService.Field(msg, "content") is string text) ||
                !Equals(QwenSearchService.Field(msg, "role"), "assistant"))
                throw new QwenSearchException("Ollama 返回了无法识别的回答格式。");
            if (answer.Length + text.Length > MaxAnswerChars) throw new QwenSearchException("本地千问回答过长。");
            if (text.Length != 0) { answer.Append(text); append?.Invoke(text); }
            if (done && string.IsNullOrWhiteSpace(answer.ToString())) throw new QwenSearchException("本地千问没有返回回答。");
            return done;
        }

        public void Dispose() => client.Dispose();
    }
}
