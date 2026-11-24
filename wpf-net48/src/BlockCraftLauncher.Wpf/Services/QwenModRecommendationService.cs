using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class QwenModRecommendationService
    {
        const string Instructions =
            "为玩家在 Minecraft Java 模组目录中查找一个项目。按玩家描述的用途、游戏版本和加载器推荐一个模组。"
            + "只返回 JSON 对象，keyword 为该项目在 Modrinth 或 CurseForge 的英文 slug，例如 {\"keyword\":\"sodium\"}。"
            + "无法确定时返回 {\"keyword\":null}。玩家的需求如下：\n";

        // Accept exactly one catalog slug, never a link, filename, argument list, or additional action.
        static readonly Regex ResultShape = new Regex(
            @"\A\{\s*""keyword""\s*:\s*""(?<slug>[a-zA-Z0-9](?:[a-zA-Z0-9_-]{0,62}[a-zA-Z0-9])?)""\s*\}\z",
            RegexOptions.CultureInvariant);

        internal static async Task<string> RecommendKeywordAsync(string question,
            Func<string, CancellationToken, Task<QwenSearchResult>> ask, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            question = question?.Trim();
            if (string.IsNullOrEmpty(question) || question.Length > QwenSearchService.MaxQuestionLength - Instructions.Length)
                throw new QwenSearchException("请用一句话描述想找的模组，例如“帮我找一个提高帧率的模组”。");

            var result = await ask(Instructions + question, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadKeyword(result?.Answer, out string keyword)) return keyword;
            throw new QwenSearchException("千问没有给出可搜索的模组名。可以换个说法，或直接输入“搜索模组”加模组名称。");
        }

        internal static bool TryReadKeyword(string response, out string keyword)
        {
            keyword = null;
            if (string.IsNullOrWhiteSpace(response) || response.Length > 256) return false;
            string json = response.Trim().Replace("\r\n", "\n");
            const string fence = "```json\n";
            if (json.Length >= fence.Length + 4 && json.StartsWith(fence, StringComparison.OrdinalIgnoreCase) &&
                json.EndsWith("\n```", StringComparison.Ordinal))
                json = json.Substring(fence.Length, json.Length - fence.Length - 4).Trim();
            var match = ResultShape.Match(json);
            if (!match.Success) return false;
            string slug = match.Groups["slug"].Value;
            if (slug.Equals("null", StringComparison.OrdinalIgnoreCase) || slug.Equals("none", StringComparison.OrdinalIgnoreCase))
                return false;
            keyword = slug;
            return true;
        }
    }
}
