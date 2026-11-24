using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class QwenConversationSanityChecks
    {
        internal static void Run(string parent)
        {
            string directory = Path.Combine(parent, "qwen-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string file = Path.Combine(directory, "history.json");
                var first = new QwenConversationStore(file);
                void Check(bool ok, string description)
                {
                    if (!ok) throw new InvalidDataException("千问历史自检失败：" + description);
                }
                var reply = new QwenSearchResult("answer", new[] {
                    new QwenSearchSource(1, "Minecraft", new Uri("https://www.minecraft.net/")) });
                first.CommitTurn(first.Current, "first question", reply);
                Check(!first.HistoryEnabled && !File.Exists(file), "默认应使用内存对话。");
                first.SetHistoryEnabled(true);
                var second = new QwenConversationStore(file);
                Check(second.HistoryEnabled && second.Current.Id == first.Current.Id &&
                    second.Current.Turns.Single().Sources.Single().Title == "Minecraft", "重开时没有恢复回答和来源。");
                Task.WaitAll(Task.Run(() => first.CommitTurn(first.Current, "one", reply)),
                    Task.Run(() => second.CommitTurn(second.Current, "two", reply)));
                var merged = new QwenConversationStore(file);
                Check(merged.Current.Turns.First().Question == "first question" &&
                    merged.Current.Turns.Skip(1).Select(t => t.Question).OrderBy(q => q).SequenceEqual(new[] { "one", "two" }),
                    "两个窗口同时打开时丢失对话。");

                var replacement = new QwenSearchResult("revised", Array.Empty<QwenSearchSource>());
                string last = merged.Current.Turns.Last().Question;
                merged.CommitTurn(merged.Current, last, replacement, true);
                Check(merged.Current.Turns.Count == 3 && merged.Current.Turns.Last().Answer == "revised" &&
                    merged.Current.BuildChatTurns(last, true).Last().Content == last &&
                    merged.Current.BuildChatTurns(last, true).Count == 5, "重新生成应替换最后一次回答。");
                merged.Delete(merged.Current.Id);
                second.CommitTurn(second.Current, "late", reply);
                Check(new QwenConversationStore(file).Current.Turns.Count == 0, "其他窗口不应恢复已删历史。");
                var fresh = second.NewSession();
                second.CommitTurn(fresh, "new subject", reply);
                var beforeReset = new QwenConversationStore(file);
                Check(beforeReset.Current.Title == "new subject", "删除后应能创建另一段历史。");
                second.Clear();
                beforeReset.CommitTurn(beforeReset.Current, "late after clear", reply);
                Check(new QwenConversationStore(file).Current.Turns.Count == 0, "清空后旧窗口不应恢复历史。");
                second.SetHistoryEnabled(false);
                beforeReset.CommitTurn(beforeReset.Current, "late after disable", reply);
                Check(!File.Exists(file), "关闭记录后旧窗口不应重新创建文件。");

                File.WriteAllText(file, "invalid history json");
                var corrupt = new QwenConversationStore(file);
                Check(!corrupt.HistoryEnabled && corrupt.Current.Turns.Count == 0 && corrupt.PersistenceError.Length != 0,
                    "损坏的历史应保留原文件并允许继续在内存对话。");
                corrupt.Clear();
                Check(!File.Exists(file), "显式清空应能重置已损坏的历史。");
                for (int i = 0; i < 70; i++) corrupt.CommitTurn(corrupt.Current, "q" + i, reply);
                Check(corrupt.Current.Turns.Count == 64 && corrupt.Current.Turns[0].Question == "q6",
                    "长对话应保留最近的完整轮次。");
                for (int i = 0; i < 35; i++) corrupt.CommitTurn(corrupt.NewSession(), "session" + i, reply);
                Check(corrupt.Sessions.Count == 32 && corrupt.Current.Title == "session34",
                    "会话列表应优先保留最近的对话。");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
