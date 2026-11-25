using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BlockCraftLauncher.Wpf.Services
{
    internal enum LauncherSearchKind { Invalid, Game, Mod, ModRecommendation, Resource }

    internal sealed class LauncherSearchCommand
    {
        public LauncherSearchKind Kind { get; private set; }
        public bool Install { get; private set; }
        public string Query { get; private set; }
        public string GameVersion { get; private set; }
        public string Loader { get; private set; }
        public string InstanceName { get; private set; }
        public string SourceName { get; private set; }
        public string ResourceType { get; private set; }
        public string Error { get; private set; }

        LauncherSearchCommand()
        {
            Query = GameVersion = Loader = InstanceName = SourceName = ResourceType = Error = "";
        }

        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        const string LoaderPattern = @"(?<![a-z0-9])(?:NeoForge|Forge|Fabric|Quilt)(?![a-z0-9])";
        const string SourcePattern = @"(?<![a-z0-9])(?:CurseForge|Modrinth)(?![a-z0-9])";
        const string VersionPattern = @"(?<![a-z0-9.])(?:[abr]?\d{1,2}(?:\.\d{1,3}){1,3}(?:[-_](?:pre|rc)?\d+[a-z]?)?|\d{2}w\d{2}[a-z]+)(?![a-z0-9.])";
        const string ModMarker = @"(?:模组|模組|模块|(?<![a-z0-9])mods?\b)";
        const string ResourceMarker = @"(?<resourcepack>资源包|資源包|材质包|材質包|(?<![a-z0-9])(?:resource|texture)[ -]?packs?(?![a-z0-9]))"
            + @"|(?<shader>光影(?:包)?|(?<![a-z0-9])shader(?:[ -]?packs?)?s?(?![a-z0-9]))"
            + @"|(?<modpack>整合包|模[组組]包|(?<![a-z0-9])mod[ -]?packs?(?![a-z0-9]))";
        const string GameMarker = @"(?:Minecraft(?:\s+Java(?:\s+Edition)?)?|我的世界(?:\s*Java(?:版)?)?|游戏(?:本体|版本)?|(?<![a-z])(?:MC|game)(?![a-z]))";
        const string FindVerb = @"(?:搜索|搜尋|查找|查一查|搜一下|搜一搜|搜|寻找|找一下|找|search(?:\s+for)?\b|find\b|look\s+up\b)";
        const string InstallVerb = @"(?:下载并?安装|下載並?安裝|下载|下載|下(?=(?:一个|一款|个|款))|安装|安裝|装上|装|download\s+and\s+install\b|download\b|install\b)";
        const string Action = @"^(?:(?<install>(?:(?:" + FindVerb + @"|(?<rec>推荐|推薦|recommend\b))\s*(?:(?:并且|并|並|和|后|後|and(?:\s+then)?)\s*)?)?" + InstallVerb + @")|(?<rec>推荐|推薦|recommend\b)|(?<search>" + FindVerb + @"))\s*";
        const string Qualifier = @"(?:的|版本|版|加载器|載入器|loader\b|versions?\b|for\b|with\b|on\b)";

        static readonly Dictionary<string, string> ModAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "钠", "Sodium" }, { "鈉", "Sodium" }, { "sodium", "Sodium" },
            { "锂", "Lithium" }, { "鋰", "Lithium" }, { "lithium", "Lithium" },
            { "虹膜", "Iris" }, { "iris", "Iris" }, { "jei", "JEI" },
            { "玉", "Jade" }, { "jade", "Jade" }, { "铷", "Rubidium" }, { "銣", "Rubidium" },
            { "Fabric API", "Fabric API" }, { "JourneyMap", "JourneyMap" }, { "旅行地图", "JourneyMap" },
            { "AppleSkin", "AppleSkin" }, { "苹果皮", "AppleSkin" }, { "Sodium Extra", "Sodium Extra" },
            { "Just Enough Items", "Just Enough Items" }
        };
        static readonly char[] Edge = " \t:：,，()（）[]【】「」『』“”\"。！!.".ToCharArray();
        static Match Match(string s, string pattern) { return Regex.Match(s, pattern, Options); }
        static string Trim(string s) { return s.Trim(Edge); }
        static string Without(string s, Match m) { return m.Success ? (s.Remove(m.Index, m.Length).Insert(m.Index, " ")).Trim() : s; }

        // Keep delimiters for the instance grammar while hiding literal titles from the filter parser.
        static string MaskQuotedNames(string text, List<string> names)
        {
            const string quotes = @"「(?<name>[^「」]*)」|『(?<name>[^『』]*)』|“(?<name>[^“”]*)”|""(?<name>[^""]*)""";
            return Regex.Replace(text, quotes, m =>
            {
                names.Add(m.Groups["name"].Value);
                return m.Value[0] + "\u001f" + (names.Count - 1) + "\u001f" + m.Value[m.Length - 1];
            });
        }

        static string RestoreQuotedNames(string text, List<string> names)
        {
            return names.Count == 0 ? text : Regex.Replace(text, @"\u001f(\d+)\u001f",
                m => names[int.Parse(m.Groups[1].Value)]);
        }

        public static bool TryParse(string text, out LauncherSearchCommand command)
        {
            command = null;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2000 || text.IndexOf('\u001f') >= 0) return false;
            var quotedNames = new List<string>();
            string s = MaskQuotedNames(text.Trim(), quotedNames);
            // Questions and negation always stay in the ordinary question/answer flow.
            if (Match(s, @"[?？]|怎么|如何|为什么|是否|是什么|会不会|需不需要|要多久|需要多(?:长|久|少)|有什么(?:区别|影响)|(?:吗|么)[。！!\s]*$|(?:失败|闪退|报错|没反应)(?:了|的原因|原因)?[。！!\s]*$|不要|(?:不|别)(?:给|搜|找|装|下载|安装)|请勿|do\s+not\b|don['’]t\b|\b(?:how|why|whether)\b").Success) return false;
            s = Regex.Replace(s, @"^(?:(?:请|請|麻烦(?:你)?|帮我|幫我|帮忙|给我|我想要?|我要|直接|在启动器[里内中]|让千问|千问[，,：:]?|please\b|I(?:'d\s+like|\s+want)\s+to\b)\s*)+", "", Options);
            var c = new LauncherSearchCommand();
            string target = null, source = null;
            // Targets must have an explicit instance marker. Unrecognized targets never fall back to the current instance.
            var targetPrefix = Match(s, @"^(?:给|給|为|為|在)\s*(?<target>当前实例|这个实例|本实例|(?:实例\s*[「“""][^」”""]+[」”""]|[「“""][^」”""]+[」”""]\s*实例|[^\s，,]{1,42}?实例|实例\s*[^\s，,]{1,42}?(?=\s*(?:里|中)?\s*(?:" + InstallVerb + "|" + FindVerb + @"))))\s*(?:里|中)?\s*");
            if (targetPrefix.Success) { target = targetPrefix.Groups["target"].Value; s = Without(s, targetPrefix); }
            var sourcePrefix = Match(s, @"^(?:从|在|去)\s*(?<source>" + SourcePattern + @")\s*(?:上|里|中)?\s*");
            if (sourcePrefix.Success) { source = sourcePrefix.Groups["source"].Value; s = Without(s, sourcePrefix); }
            var move = Match(s, @"^把\s*(?<name>.+?)\s*(?:装到|安裝到|安装到|装进)\s*(?<target>.+)$");
            if (move.Success)
            {
                if (target != null) return Invalid(c, out command, "请一次指定一个目标实例。");
                target = move.Groups["target"].Value;
                string name = move.Groups["name"].Value;
                s = (Match(name, ResourceMarker).Success ? "安装 " : "安装模组 ") + name;
            }
            var action = Match(s, Action);
            if (!action.Success) return false;
            c.Install = action.Groups["install"].Success;
            bool recommendation = action.Groups["rec"].Success;
            s = s.Substring(action.Length).Trim();
            // A recommendation or search can explicitly end with a request to install that same result.
            var thenInstall = Match(s, @"(?:然后|并且|并|再|and\s+then|and)\s*(?:帮我)?(?:安装|安裝|下载|裝上|装上|install|download)(?:它|\s+it)?\s*(?=[。！!\s]*$|到|\b(?:in|into|for)\b)");
            if (thenInstall.Success) { c.Install = true; s = Without(s, thenInstall); }
            if (Match(RestoreQuotedNames(s, quotedNames), @"[\r\n;&|<>\\/]|[:：]//|[\w.-]+\.(?:exe|ps1|bat|cmd|jar|zip)\b|\bfile:").Success ||
                Match(s, @"然后|之后|接着|并且|并(?:安装|下载|运行|执行)|(?:和|以及|、|[，,])\s*(?:再)?(?:安装|下载|执行|运行)|\b(?:then|also install)\b").Success)
                return Invalid(c, out command, "请一次输入一个游戏版本或资源名称。");
            var targetSuffix = Match(s, @"\s*(?:安装到|装到|下载到|到|给|for\s+(?:the\s+)?|in(?:to)?\s+(?:the\s+)?)\s*(?<target>(?:当前实例|这个实例|本实例|current\s+instance|实例\s*.+|instance\s+.+|.+?实例))\s*[。！!]*$");
            if (targetSuffix.Success)
            {
                if (target != null) return Invalid(c, out command, "请一次指定一个目标实例。");
                target = targetSuffix.Groups["target"].Value; s = Without(s, targetSuffix);
            }
            if (target != null)
            {
                target = target.Trim();
                if (!Match(target, @"^(?:当前实例|这个实例|本实例|current\s+instance)$").Success)
                {
                    var named = Match(target, @"^(?:实例\s*(?<n>.+)|instance\s+(?<n>.+)|(?<n>.+?)实例)$");
                    if (!named.Success) return Invalid(c, out command, "请写出目标实例名称，例如「给生存实例安装钠」。");
                    c.InstanceName = RestoreQuotedNames(Trim(named.Groups["n"].Value), quotedNames);
                    if (c.InstanceName.Length == 0 || c.InstanceName.Length > 64 || Match(c.InstanceName, @"[<>/\\:|?*]").Success)
                        return Invalid(c, out command, "目标实例名称无效。");
                }
            }
            // Players may quote a version in a game command just as they quote a project name.
            if (quotedNames.Count == 1 && target == null && source == null &&
                !Match(s, ResourceMarker + "|" + ModMarker + "|" + SourcePattern).Success &&
                Match(quotedNames[0], "^" + VersionPattern + "$").Success)
                s = RestoreQuotedNames(s, quotedNames);
            var sources = Regex.Matches(s, SourcePattern, Options);
            if (sources.Count + (source != null ? 1 : 0) > 1) return Invalid(c, out command, "请一次选择一个模组来源。");
            if (sources.Count == 1)
            {
                source = sources[0].Value;
                s = Regex.Replace(s, @"(?:从|在|来源\s*[:：]?|\bon\s+|\bfrom\s+)?\s*" + SourcePattern + @"(?:\s*(?:上|中))?", " ", Options);
            }
            if (source != null) c.SourceName = source.Equals("CurseForge", StringComparison.OrdinalIgnoreCase) ? "CurseForge" : "Modrinth";

            string qualifiedText = s;
            var versions = Regex.Matches(s, VersionPattern, Options);
            if (versions.Count > 1) return Invalid(c, out command, "请一次指定一个 Minecraft 版本。");
            if (versions.Count == 1) { c.GameVersion = versions[0].Value.ToLowerInvariant(); s = Without(s, versions[0]); }
            // Only a type word outside a quoted project name can select a catalog.
            // For example, 模组「Iris Shaders」 belongs to the mod catalog.
            var resource = Match(s, ResourceMarker);
            var mod = Match(s, ModMarker);
            bool modMarker = mod.Success;
            if (resource.Success && (!modMarker || resource.Index <= mod.Index))
            {
                c.ResourceType = resource.Groups["resourcepack"].Success ? "resourcepack" :
                    resource.Groups["shader"].Success ? "shader" : "modpack";
                if (target != null && c.ResourceType == "modpack" && c.Install)
                    return Invalid(c, out command, "整合包会创建独立实例，请直接指定整合包名称、游戏版本或加载器。");
                s = Without(s, resource);
            }
            bool gameMarker = Match(s, GameMarker).Success;
            bool latest = Match(s, @"最新(?:正式版|稳定版|版本|版)?|\blatest(?:\s+(?:stable\s+)?release|\s+version)?\b").Success;
            bool namedMinecraftMod = modMarker && Match(s, @"Minecraft\s+[a-z]").Success;
            string words = Trim(Regex.Replace(s, c.ResourceType.Length != 0 ? GameMarker :
                namedMinecraftMod ? ModMarker : ModMarker + "|" + GameMarker, " ", Options));
            if (latest) words = Regex.Replace(words, @"最新(?:正式版|稳定版|版本|版)?|\blatest(?:\s+(?:stable\s+)?release|\s+version)?\b", " ", Options);
            words = CleanWords(words);
            var loaders = Regex.Matches(words, LoaderPattern, Options);
            if (loaders.Count > 1) return Invalid(c, out command, "请一次指定一个加载器。");
            if (loaders.Count == 1)
            {
                var loader = loaders[0];
                // Keep real titles such as Fabric API or Forge Config API Port intact. Strip a loader only in a filter context.
                string remaining = CleanWords(Without(words, loader));
                bool bundledTitle = Match(words, @"\b(?:Fabric\s+(?:API|Language\s+Kotlin)|(?:Neo)?Forge\s+Config\s+API)\b").Success;
                bool adjacentVersion = Match(qualifiedText, VersionPattern + @"\s*" + LoaderPattern + "|" + LoaderPattern + @"\s*" + VersionPattern).Success;
                if (!bundledTitle && (remaining.Length == 0 || adjacentVersion || ModAliases.ContainsKey(RestoreQuotedNames(remaining, quotedNames)) ||
                    Match(words, @"(?:加载器|\bloader\b|\bfor\b|\bon\b|[\(（])\s*[:：]?\s*" + LoaderPattern).Success ||
                    Match(s, LoaderPattern + @"\s*(?:版(?:本)?|的|模组|加载器)").Success))
                {
                    string v = loader.Value.ToLowerInvariant();
                    c.Loader = v == "neoforge" ? "NeoForge" : char.ToUpperInvariant(v[0]) + v.Substring(1);
                    words = remaining;
                }
            }
            string projectName = RestoreQuotedNames(words, quotedNames);
            bool hasAlias = ModAliases.ContainsKey(projectName);
            bool catalogItem = c.ResourceType.Length != 0 || modMarker || target != null || c.SourceName.Length != 0 || hasAlias;
            if (latest && c.GameVersion.Length != 0) return Invalid(c, out command, "请在指定版本和最新版中选择一个。");
            if (!catalogItem && (gameMarker || c.GameVersion.Length != 0 || c.Loader.Length != 0))
            {
                if (words.Length != 0) return Invalid(c, out command, "请写出有效的游戏版本，例如「下载 Minecraft 1.21.1」。");
                c.Kind = LauncherSearchKind.Game; c.Query = latest ? "latest" : c.GameVersion;
            }
            else if (catalogItem)
            {
                if ((gameMarker || latest) && c.ResourceType.Length == 0 && !modMarker && !hasAlias && target != null)
                    return Invalid(c, out command, "安装游戏时请只指定游戏版本和加载器。");
                if (Match(words, @"\b(?:and|or)\b|和|以及|、|[，,；;]").Success && c.Install)
                    return Invalid(c, out command, "请一次指定一个要安装的项目。");
                if (Match(projectName, @"[<>/\\:|?*]").Success || Match(words, @"实例|安装到|装到|\binstance\b").Success || projectName.Length > 100)
                    return Invalid(c, out command, "请核对项目名称和目标实例。");
                if (string.IsNullOrWhiteSpace(projectName) && c.Install) return Invalid(c, out command, "请写出要安装的项目名称。");
                c.Query = projectName;
                if (c.ResourceType.Length != 0) c.Kind = LauncherSearchKind.Resource;
                else if (hasAlias) { c.Kind = LauncherSearchKind.Mod; c.Query = ModAliases[projectName]; }
                else
                {
                    bool purpose = recommendation || Match(qualifiedText,
                        @"(?:可以|能够|能|用于|用来|适合|提高|提升|优化|改善|降低|减少|增加|添加|增强|支持|显示)[^“”「」\""]{1,60}?的\s*" + ModMarker +
                        @"|(?:一个|一款|个|款)\s*(?:性能优化|帧率优化|小地图|整理背包|背包整理|配方查询)\s*(?:的)?\s*" + ModMarker +
                        @"|\bmods?\s+(?:that\s+|which\s+|to\s+|for\s+).+" +
                        @"|\b(?:a|an)\s+(?:[\w-]+\s+){0,3}(?:performance|optimi[sz]ation|fps|minimap|inventory|backpack|recipe)\s+(?:[\w-]+\s+){0,2}mods?\b").Success;
                    c.Kind = purpose && words.Length != 0 && !Match(qualifiedText, @"[「『“""]").Success
                        ? LauncherSearchKind.ModRecommendation : LauncherSearchKind.Mod;
                }
            }
            else return false;
            command = c; return true;
        }

        static string CleanWords(string words)
        {
            words = Trim(words);
            words = Regex.Replace(words, @"^(?:(?:一个|一款|一下|个|款|\ban?\b|\bthe\b|\bme\b|" + Qualifier + @")\s*)+", "", Options);
            words = Regex.Replace(words, @"(?:\s*(?:的|版本|加载器|版|\bfor\b|\bon\b|\bversion\b))+$", "", Options);
            return Trim(Regex.Replace(words, @"\s+", " "));
        }

        static bool Invalid(LauncherSearchCommand candidate, out LauncherSearchCommand command, string message)
        {
            candidate.Kind = LauncherSearchKind.Invalid;
            candidate.Error = message;
            command = candidate;
            return true;
        }
    }
}
