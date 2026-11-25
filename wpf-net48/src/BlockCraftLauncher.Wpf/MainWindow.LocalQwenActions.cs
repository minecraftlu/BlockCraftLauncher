using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        // Exact commands work before players download their first local model.
        async Task<string> TryRunLocalQwenExactCommandAsync(string question, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!LauncherSearchCommand.TryParse(question, out var command)) return null;
            switch (command.Kind)
            {
                case LauncherSearchKind.Invalid:
                    return command.Error;
                case LauncherSearchKind.Game:
                    return await RunQwenGameActionAsync(command.Query, command.Loader, command.Install, token);
                case LauncherSearchKind.Mod:
                    return await RunQwenModActionAsync(command.Query, command.Install, command.GameVersion,
                        command.Loader, command.InstanceName, command.SourceName, token);
                default:
                    return null;
            }
        }

        async Task<string> RunLocalQwenAsync(Uri endpoint, IReadOnlyList<QwenChatTurn> history, Action<string> append, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var last = history != null && history.Count > 0 ? history[history.Count - 1] : null;
            if (last?.Role != "user" || string.IsNullOrWhiteSpace(last.Content))
                throw new QwenSearchException("请输入要发给本地千问的消息。");
            string status = await TryRunLocalQwenExactCommandAsync(last.Content, token);
            if (status != null) return status;
            using (var chat = new LocalQwenChatService(endpoint))
            {
                if (!LauncherSearchCommand.TryParse(last.Content, out var command) ||
                    command.Kind != LauncherSearchKind.ModRecommendation)
                    return await chat.ChatAsync(history, append, token);

                // A suggestion supplies only a validated catalog slug. Its download button belongs to the player.
                string slug = await chat.SuggestModAsync(last.Content, token);
                token.ThrowIfCancellationRequested();
                return await RunQwenModActionAsync(slug, false, command.GameVersion, command.Loader,
                    command.InstanceName, command.SourceName, token);
            }
        }

        internal async Task<string> VerifyLiveLocalQwenAsync()
        {
            if (qwenLiveFolder == null || activeProfile == null)
                throw new InvalidOperationException("Local model check requires a fresh isolated live-check window.");
            string folder = Environment.GetEnvironmentVariable("BLOCKCRAFT_UI_CAPTURE_DIR");
            string log = string.IsNullOrWhiteSpace(folder) ? null :
                Path.Combine(Directory.CreateDirectory(folder).FullName, "local-live-progress.txt");
            void Log(string text)
            {
                if (log != null) File.AppendAllText(log, DateTime.UtcNow.ToString("HH:mm:ss") + " UTC " + text + Environment.NewLine);
            }
            string phase = "正在准备本地模型";
            var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            heartbeat.Tick += (s, e) => Log(phase);
            heartbeat.Start();
            Log(phase);
            try
            {
                Show();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(65));
                var token = timeout.Token;
                using var runtime = new LocalModelRuntime(settings);
                var progress = new Progress<string>(value => phase = value);
                Uri endpoint = await runtime.EnsureReadyAsync(qwenLiveFolder, true, progress, token);
                Log("本地 Ollama 服务就绪。");
                Uri warmed = await runtime.EnsureReadyAsync(qwenLiveFolder, false, null, token);
                if (warmed != endpoint) throw new InvalidDataException("A second use did not find the same model without downloading.");
                phase = "正在运行中文多轮流式对话";
                var deltas = new StringBuilder();
                var turns = new[] {
                    new QwenChatTurn("user", "我最喜欢的方块是青金石块。"),
                    new QwenChatTurn("assistant", "记住了。"),
                    new QwenChatTurn("user", "我刚才说最喜欢的方块叫什么？请直接用中文回答。")
                };
                string answer = await RunLocalQwenAsync(endpoint, turns, chunk => deltas.Append(chunk), token);
                if (answer != deltas.ToString() || !answer.Contains("青金石"))
                    throw new InvalidDataException("The model did not recall the Chinese block name through the streamed chat.");
                Log("已验证中文流式回答、连续对话和已下载模型复用。");
                preferredModSource = ModSource.Modrinth;
                phase = "正在通过本地模型查找提升帧率的模组";
                string status = await RunLocalQwenAsync(endpoint,
                    new[] { new QwenChatTurn("user", "帮我找一个提升帧率的模组") }, null, token);
                var exact = resourceQuery == null ? null : MatchQwenMod(resourceQuery.Text, resourceResults, resourceHasMore);
                if (currentPage != "资源中心" || exact == null || downloads.Jobs.Any() ||
                    !Regex.IsMatch(exact.Name + " " + exact.Description + " " + exact.CategoryLabel,
                        @"performance|optimi[sz]|frame.?rate|render|\bfps\b|帧率|优化|性能", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("Live catalog could not confirm a performance mod. " + status);
                string result = "Local Qwen3.5-4B streamed a Chinese multi-turn answer, reused its installed model, and found " +
                    exact.Name + " (" + exact.Slug + ") in Modrinth without queuing a download.";
                Log(result);
                return result;
            }
            finally
            {
                heartbeat.Stop();
                Close();
                // Windows may briefly retain memory-mapped model files after the runtime closes its process job.
                for (int attempt = 0; Directory.Exists(qwenLiveFolder); attempt++)
                {
                    try { Directory.Delete(qwenLiveFolder, true); }
                    catch (Exception ex) when (attempt < 39 && (ex is IOException || ex is UnauthorizedAccessException))
                    { await Task.Delay(250); }
                }
            }
        }
    }
}
