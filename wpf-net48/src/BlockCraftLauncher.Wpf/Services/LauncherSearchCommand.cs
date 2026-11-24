using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BlockCraftLauncher.Wpf.Services
{
    internal enum LauncherSearchKind { Invalid, Game, Mod, ModRecommendation }

    internal sealed class LauncherSearchCommand
    {
        public LauncherSearchKind Kind { get; private set; }
        public bool Install { get; private set; }
        public string Query { get; private set; }
        public string GameVersion { get; private set; }
        public string Loader { get; private set; }
        public string InstanceName { get; private set; }
        public string SourceName { get; private set; }
        public string Error { get; private set; }

        LauncherSearchCommand()
        {
            Query = GameVersion = Loader = InstanceName = SourceName = Error = "";
        }

        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        const string LoaderPattern = @"(?<![a-z0-9])(?:NeoForge|Forge|Fabric|Quilt)(?![a-z0-9])";
        const string SourcePattern = @"(?<![a-z0-9])(?:CurseForge|Modrinth)(?![a-z0-9])";
        const string VersionPattern = @"(?<![a-z0-9.])(?:[abr]?\d{1,2}(?:\.\d{1,3}){1,3}(?:[-_](?:pre|rc)?\d+[a-z]?)?|\d{2}w\d{2}[a-z]+)(?![a-z0-9.])";
        const string ModMarker = @"(?:模组|模組|模块|(?<![a-z0-9])mods?\b)";
        const string GameMarker = @"(?:Minecraft(?:\s+Java(?:\s+Edition)?)?|我的世界(?:\s*Java(?:版)?)?|游戏(?:本体|版本)?|(?<![a-z])(?:MC|game)(?![a-z]))";
        const string FindVerb = @"(?:搜索|搜尋|查找|查一查|搜一下|搜一搜|搜|寻找|找一下|找|search(?:\s+for)?\b|find\b|look\s+up\b)";
        const string InstallVerb = @"(?:下载并?安装|下載並?安裝|下载|下載|安装|安裝|装上|装|download\s+and\s+install\b|download\b|install\b)";
        const string Action = @"^(?:(?<rec>推荐|推薦|recommend\b)|(?<install>(?:" + FindVerb + @"\s*(?:(?:并且|并|並|后|後|and)\s*)?)?" + InstallVerb + @")|(?<search>" + FindVerb + @"))\s*";
        const string Qualifier = @"(?:的|版本|版|加载器|載入器|loader\b|versions?\b|for\b|with\b|on\b)";

        static readonly Dictionary<string, string> ModAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "钠", "Sodium" }, { "鈉", "Sodium" }, { "sodium", "Sodium" },
            { "锂", "Lithium" }, { "鋰", "Lithium" }, { "lithium", "Lithium" },
            { "虹膜", "Iris" }, { "iris", "Iris" }, { "jei", "JEI" },
            { "玉", "Jade" }, { "jade", "Jade" }, { "铷", "Rubidium" }, { "銣", "Rubidium" }
        };
        static readonly char[] Edge = " \t:：,，()（）[]【】「」『』“”\"。！!.".ToCharArray();
        static Match Match(string s, string pattern) { return Regex.Match(s, pattern, Options); }
        static string Trim(string s) { return s.Trim(Edge); }
        static string Without(string s, Match m) { return m.Success ? (s.Remove(m.Index, m.Length).Insert(m.Index, " ")).Trim() : s; }

        public static bool TryParse(string text, out LauncherSearchCommand command)
        {
            command = null;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) return false;
            string s = text.Trim();
            // Questions and negation always stay in the ordinary question/answer flow.
            if (Match(s, @"[?？]|怎么|如何|为什么|是否|是什么|(?:吗|么)[。！!\s]*$|(?:失败|闪退|报错|没反应)(?:了|的原因|原因)?[。！!\s]*$|不要|别(?:给|搜|找|装|下载|安装)|请勿|do\s+not\b|don't\b|\b(?:how|why|whether)\b").Success) return false;
            s = Regex.Replace(s, @"^(?:(?:请|請|麻烦(?:你)?|帮我|幫我|帮忙|给我|我想要?|我要|直接|在启动器[里内中]|让千问|千问[，,：:]?|please\b)\s*)+", "", Options);
            var c = new LauncherSearchCommand();
            string target = null, source = null;
            // Targets must have an explicit instance marker. Unrecognized targets never fall back to the current instance.
            var targetPrefix = Match(s, @"^(?:给|給|为|為|在)\s*(?<target>当前实例|这个实例|本实例|(?:实例\s*[「“""][^」”""]+[」”""]|[「“""][^」”""]+[」”""]\s*实例|[^\s，,]{1,42}?实例|实例\s*[^\s，,]{1,42}?(?=\s*(?:里|中)?\s*(?:" + InstallVerb + "|" + FindVerb + @"))))\s*(?:里|中)?\s*");
            if (targetPrefix.Success) { target = targetPrefix.Groups["target"].Value; s = Without(s, targetPrefix); }
            var sourcePrefix = Match(s, @"^(?:从|在|去)\s*(?<source>" + SourcePattern + @")\s*(?:上|里|中)?\s*");
            if (sourcePrefix.Success) { source = sourcePrefix.Groups["source"].Value; s = Without(s, sourcePrefix); }
            var move = Match(s, @"^把\s*(?<name>.+?)\s*(?:装到|安裝到|安装到|装进)\s*(?<target>.+)$");
            if (move.Success) { if (target != null) return Invalid(c, out command, "请一次指定一个目标实例。"); target = move.Groups["target"].Value; s = "安装模组 " + move.Groups["name"].Value; }
            var action = Match(s, Action);
            if (!action.Success) return false;
            c.Install = action.Groups["install"].Success;
            bool recommendation = action.Groups["rec"].Success;
            s = s.Substring(action.Length).Trim();
            // A recommendation or search can explicitly end with a request to install that same result.
            var thenInstall = Match(s, @"(?:然后|并且|并|再|and\s+then|and)\s*(?:帮我)?(?:安装|安裝|下载|装上|install|download)(?:它|它们|\s+it)?[。！!\s]*$");
            if (thenInstall.Success) { c.Install = true; s = Without(s, thenInstall); }
            if (Match(s, @"(?:[\r\n;&|<>\\/]|[:：]//|[\w.-]+\.(?:exe|ps1|bat|cmd|jar|zip)\b|\bfile:|然后|之后|接着|并且|并(?:安装|下载|运行|执行)|(?:和|以及|、|[，,])\s*(?:再)?(?:安装|下载|执行|运行)|\b(?:then|also install)\b)").Success)
                return Invalid(c, out command, "请一次输入一个游戏版本或模组名称。");
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
                    c.InstanceName = Trim(named.Groups["n"].Value);
                    if (c.InstanceName.Length == 0 || c.InstanceName.Length > 64 || Match(c.InstanceName, @"[<>/\\:|?*]").Success)
                        return Invalid(c, out command, "目标实例名称无效。");
                }
            }
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
            bool modMarker = Match(s, ModMarker).Success;
            bool gameMarker = Match(s, GameMarker).Success;
            bool latest = Match(s, @"最新(?:正式版|稳定版|版本|版)?|\blatest(?:\s+(?:stable\s+)?release|\s+version)?\b").Success;
            bool namedMinecraftMod = modMarker && Match(s, @"Minecraft\s+[a-z]").Success;
            string words = Trim(Regex.Replace(s, namedMinecraftMod ? ModMarker : ModMarker + "|" + GameMarker, " ", Options));
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
                if (!bundledTitle && (remaining.Length == 0 || adjacentVersion || ModAliases.ContainsKey(remaining) ||
                    Match(words, @"(?:加载器|\bloader\b|\bfor\b|\bon\b|[\(（])\s*[:：]?\s*" + LoaderPattern).Success ||
                    Match(s, LoaderPattern + @"\s*(?:版(?:本)?|的|模组|加载器)").Success))
                {
                    string v = loader.Value.ToLowerInvariant();
                    c.Loader = v == "neoforge" ? "NeoForge" : char.ToUpperInvariant(v[0]) + v.Substring(1);
                    words = remaining;
                }
            }
            bool hasAlias = ModAliases.ContainsKey(words);
            bool isMod = modMarker || c.InstanceName.Length != 0 || target != null || c.SourceName.Length != 0 || hasAlias;
            if (latest && c.GameVersion.Length != 0) return Invalid(c, out command, "请在指定版本和最新版中选择一个。");
            if (!isMod && (gameMarker || c.GameVersion.Length != 0 || c.Loader.Length != 0))
            {
                if (words.Length != 0) return Invalid(c, out command, "请写出有效的游戏版本，例如「下载 Minecraft 1.21.1」。");
                c.Kind = LauncherSearchKind.Game; c.Query = latest ? "latest" : c.GameVersion;
            }
            else if (isMod)
            {
                if ((gameMarker || latest) && !modMarker && !hasAlias && target != null)
                    return Invalid(c, out command, "安装游戏时请只指定游戏版本和加载器。");
                if (Match(words, @"\b(?:and|or)\b|和|以及|、|[，,；;]").Success && c.Install)
                    return Invalid(c, out command, "请一次指定一个要安装的模组。");
                if (Match(words, @"[<>/\\:|?*]|(?:实例|当前实例|安装到|装到|\binstance\b)").Success || words.Length > 100)
                    return Invalid(c, out command, "请核对模组名称和目标实例。");
                if (words.Length == 0 && c.Install) return Invalid(c, out command, "请写出要安装的模组名称。");
                bool byDescription = Match(words, @"(?:提高|提升|优化|改善|降低|减少|增加|添加|增强|支持|能让|可以让|用来|适合|用于|能显示|能看到|(?:improv|reduc|boost|optimi)\w*\s+)").Success;
                c.Kind = !hasAlias && words.Length != 0 && (recommendation || byDescription)
                    ? LauncherSearchKind.ModRecommendation : LauncherSearchKind.Mod;
                c.Query = hasAlias ? ModAliases[words] : words;
            }
            else return false;
            command = c; return true;
        }

        static string CleanWords(string words)
        {
            words = Trim(words);
            words = Regex.Replace(words, @"^(?:(?:一个|一款|一下|个|款|\ba\b|\bthe\b|\bme\b|" + Qualifier + @")\s*)+", "", Options);
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
