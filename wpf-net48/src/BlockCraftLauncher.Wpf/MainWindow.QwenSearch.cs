using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        const string QwenSearchPage = "千问 AI 搜索";
        Grid qwenPage;
        StackPanel qwenSources;
        Border qwenResult;
        TextBox qwenQuery, qwenEndpoint, qwenModel, qwenAnswer;
        TextBlock qwenSearchStatus;
        PasswordBox qwenApiKey;
        CheckBox qwenRememberKey;
        Expander qwenConnection;
        Button qwenSubmit, qwenCancel;
        CancellationTokenSource qwenRequest;
        bool openingQwenActionPage;
        string qwenSettingsPath, qwenCompanionPath;
        Func<string, string, string, string, CancellationToken, Task<QwenSearchResult>> qwenRunSearch;
        Action<Uri> qwenOpenUri;

        void BuildQwenComposer()
        {
            qwenSearchStatus = Txt("", 12, false, true);
            AutomationProperties.SetLiveSetting(qwenSearchStatus, AutomationLiveSetting.Polite);
            qwenSearchStatus.Visibility = Visibility.Collapsed;
            qwenQuery = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64,
                MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxLength = QwenChatInputLimit
            };
            ApplySearchInputStyle(qwenQuery, "输入问题或指令，例如「下载 Minecraft 1.21.1」「给当前实例装钠」。");
            AutomationProperties.SetName(qwenQuery, "问题或下载指令");
            qwenQuery.PreviewKeyDown += async (sender, args) =>
            {
                if (args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
                args.Handled = true;
                await OpenQwenPageAsync();
            };
            qwenSubmit = Btn("发送", async () => await OpenQwenPageAsync(), true);
            qwenCancel = Btn("停止", CancelQwenSearch);
            qwenCancel.IsEnabled = false;
            qwenCancel.Visibility = Visibility.Collapsed;
            qwenConnection = BuildQwenConnection();
        }

        Expander BuildQwenConnection()
        {
            QwenConnectionSettings connection;
            try { connection = QwenConnectionSettings.Load(qwenSettingsPath); }
            catch (Exception)
            {
                connection = new QwenConnectionSettings();
                SetQwenSearchStatus("连接设置未能读取，可以重新填写并保存。");
            }
            qwenEndpoint = new TextBox { Text = connection.Endpoint ?? QwenSearchService.DefaultEndpoint };
            qwenModel = new TextBox { Text = string.IsNullOrWhiteSpace(connection.Model) ? QwenSearchService.DefaultModel : connection.Model };
            qwenApiKey = new PasswordBox { Password = connection.ApiKey ?? "", MinHeight = 30, Padding = new Thickness(8, 4, 8, 4) };
            qwenApiKey.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            qwenApiKey.SetResourceReference(PasswordBox.CaretBrushProperty, "TextBrush");
            qwenApiKey.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
            qwenApiKey.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
            qwenApiKey.ToolTip = "填写自己的百炼 API 密钥，或使用本机的 DASHSCOPE_API_KEY 环境变量。";
            qwenRememberKey = new CheckBox { Content = "在此 Windows 账户中记住密钥", IsChecked = connection.RememberApiKey };
            ApplySearchInputStyle(qwenEndpoint, "阿里云百炼的 OpenAI 兼容接口地址");
            ApplySearchInputStyle(qwenModel, "服务商提供的模型 ID");
            AutomationProperties.SetName(qwenEndpoint, "接口地址");
            AutomationProperties.SetName(qwenModel, "模型名称");
            AutomationProperties.SetName(qwenApiKey, "API 密钥");
            qwenEndpoint.TextChanged += (sender, args) =>
            {
                qwenApiKey.Clear();
                qwenRememberKey.IsChecked = false;
            };
            var import = Btn("使用桌面伙伴配置", () =>
            {
                try
                {
                    var desktop = QwenConnectionSettings.ImportCompanion(qwenCompanionPath);
                    qwenEndpoint.Text = desktop.Endpoint;
                    qwenModel.Text = desktop.Model;
                    SetQwenSearchStatus("已填入桌面伙伴的接口和模型。填写自己的百炼密钥后即可对话。");
                }
                catch (InvalidOperationException ex) { SetQwenSearchStatus(ex.Message); }
                catch (Exception) { SetQwenSearchStatus("读取桌面伙伴配置失败，请手动填写接口和模型。"); }
            });
            var save = Btn("保存连接", () =>
            {
                try
                {
                    var saved = ReadQwenConnection();
                    saved.Save(qwenSettingsPath);
                    SetQwenSearchStatus(saved.RememberApiKey && !string.IsNullOrEmpty(saved.ApiKey)
                        ? "已保存连接和本机加密密钥。" : "已保存接口和模型。");
                }
                catch (InvalidOperationException ex) { SetQwenSearchStatus(ex.Message); }
                catch (Exception) { SetQwenSearchStatus("保存连接失败，请检查本机设置目录是否可写。"); }
            });
            var content = Col(Txt("接口地址"), qwenEndpoint, Txt("模型名称"), qwenModel,
                Txt("API 密钥"), qwenApiKey, qwenRememberKey, Row(import, save));
            var heading = Txt("连接设置", 14, true);
            heading.Margin = new Thickness(0);
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var expander = new Expander { Header = heading, Content = Card(content), Margin = new Thickness(0, 2, 0, 10) };
            expander.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(expander, heading.Text);
            return expander;
        }

        QwenConnectionSettings ReadQwenConnection() => new QwenConnectionSettings
        {
            Endpoint = qwenEndpoint.Text.Trim(), Model = qwenModel.Text.Trim(),
            ApiKey = qwenApiKey.Password.Trim(), RememberApiKey = qwenRememberKey.IsChecked == true
        };

        Task OpenQwenPageAsync() => SubmitQwenAsync(qwenQuery.Text.Trim(), false);

        async Task SubmitQwenAsync(string question, bool regenerate)
        {
            if (currentPage != QwenSearchPage || qwenSelectedTab != QwenAssistantTab.Chat || qwenRequest != null) return;
            if (question.Length == 0 || question.Length > QwenChatInputLimit)
            {
                SetQwenSearchStatus("请输入 1 至 " + QwenChatInputLimit + " 字的问题。");
                qwenQuery.Focus();
                return;
            }
            if (regenerate && !CanRegenerateQwenAnswer()) return;
            if (!regenerate && TryOpenQwenCreativeDraft(question)) return;
            LauncherSearchCommand command = null;
            bool isCommand = !regenerate && LauncherSearchCommand.TryParse(question, out command);
            if (isCommand && command.Kind == LauncherSearchKind.Invalid)
            {
                SetQwenSearchStatus(command.Error);
                return;
            }
            bool recommend = isCommand && command.Kind == LauncherSearchKind.ModRecommendation;
            bool useModel = !isCommand || recommend;
            bool local = UseLocalQwenChat || (recommend && command.Install &&
                string.IsNullOrWhiteSpace(ReadQwenConnection().ResolveApiKey()));
            if (local && useModel && localQwenOperation != null)
            {
                SetQwenSearchStatus("请等待当前本地模型任务完成，或先停止该任务。");
                return;
            }
            QwenConnectionSettings connection = null;
            string key = null;
            if (useModel && !local && !TryReadQwenCredentials(out connection, out key)) return;

            var session = qwenConversations.Current;
            var operation = new CancellationTokenSource();
            qwenRequest = operation;
            qwenSubmit.IsEnabled = false;
            qwenCancel.IsEnabled = true;
            qwenCancel.Visibility = Visibility.Visible;
            qwenRegenerate.IsEnabled = false;
            RefreshQwenProviderControls();
            SetQwenSearchStatus(useModel ? "正在等待千问回答…" : "正在启动器内查找…");
            try
            {
                if (!isCommand)
                {
                    await RunQwenChatAsync(question, regenerate, key, connection, operation);
                    return;
                }

                string status;
                if (!recommend)
                    status = await TryRunLocalQwenExactCommandAsync(question, operation.Token);
                else if (local)
                {
                    var known = UseLocalQwenChat ? null : LocalModSearchPlan.KnownInstallProject(command.Query);
                    status = known != null
                        ? await InstallKnownQwenProjectAsync(known, command, operation.Token)
                        : await RunLocalQwenRecommendationAsync(question, operation.Token);
                }
                else
                {
                    string query = await QwenModRecommendationService.RecommendKeywordAsync(question,
                        (prompt, ct) => AskQwenAsync(prompt, key, connection, ct), operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    status = await RunQwenModActionAsync(query, command.Install, command.GameVersion, command.Loader,
                        command.InstanceName, command.SourceName, operation.Token);
                }
                if (!ReferenceEquals(qwenRequest, operation) || string.IsNullOrWhiteSpace(status)) return;
                qwenConversations.CommitTurn(session, question, new QwenSearchResult(status, null));
                if (string.Equals(qwenQuery.Text.Trim(), question, StringComparison.Ordinal)) qwenQuery.Clear();
                RefreshQwenSessions();
                SetQwenSearchStatus(qwenConversations.PersistenceError);
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus("请求已取消。");
            }
            catch (QwenSearchException ex)
            {
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus(ex.Message);
            }
            catch (LocalModelNotInstalledException)
            {
                if (ReferenceEquals(qwenRequest, operation))
                    SetQwenSearchStatus("请打开「工具」→「本地模型」，选择「聊天模型」并点击「准备模型」。");
            }
            catch (System.IO.DirectoryNotFoundException ex) when (local && useModel)
            {
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus(ex.Message);
            }
            catch (Exception)
            {
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus(isCommand
                    ? "启动器未能完成指令，请检查网络后重试。" : "千问暂时未能完成回答，请稍后重试。");
            }
            finally
            {
                if (ReferenceEquals(qwenRequest, operation))
                {
                    ResetQwenSearch();
                    RenderQwenConversation();
                }
                operation.Dispose();
            }
        }

        bool CanRegenerateQwenAnswer()
        {
            var turns = qwenConversations?.Current.Turns;
            return turns != null && turns.Count > 0 &&
                !LauncherSearchCommand.TryParse(turns[turns.Count - 1].Question, out _);
        }

        async Task<string> RunLocalQwenRecommendationAsync(string question, CancellationToken token)
        {
            Uri endpoint;
            try { endpoint = await PrepareLocalModelAsync(LocalQwenMode.Chat, false, null, token); }
            catch (LocalModelNotInstalledException)
            {
                var known = KnownQwenInstallProject(question, out var command);
                if (known == null) throw;
                return await InstallKnownQwenProjectAsync(known, command, token);
            }
            token.ThrowIfCancellationRequested();
            var reply = localQwenReply ?? RunLocalQwenAsync;
            return await reply(endpoint, new[] { new QwenChatTurn("user", question) }, null, token);
        }

        bool TryReadQwenCredentials(out QwenConnectionSettings connection, out string key, bool requireChatModel = true)
        {
            connection = ReadQwenConnection();
            key = null;
            if (!QwenSearchService.TryGetEndpoint(connection.Endpoint, out _))
            {
                OpenQwenConnectionSettings();
                SetQwenSearchStatus("请填写阿里云百炼北京或新加坡地域的 HTTPS 接口地址。");
                qwenEndpoint.Focus();
                return false;
            }
            if (requireChatModel && !QwenSearchService.IsValidModel(connection.Model))
            {
                OpenQwenConnectionSettings();
                SetQwenSearchStatus("请填写有效的千问模型 ID，例如 qwen4-max。");
                qwenModel.Focus();
                return false;
            }
            key = connection.ResolveApiKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                OpenQwenConnectionSettings();
                SetQwenSearchStatus("请在连接设置中填写自己的百炼 API 密钥。");
                qwenApiKey.Focus();
                return false;
            }

            return true;
        }

        async Task<QwenSearchResult> AskQwenAsync(string question, string key, QwenConnectionSettings connection, CancellationToken token)
        {
            if (qwenRunSearch != null)
                return await qwenRunSearch(question, key, connection.Endpoint, connection.Model, token);
            using (var search = new QwenSearchService(settings))
                return await search.SearchAsync(question, key, connection.Endpoint, connection.Model, token);
        }

        void NavigateToQwenActionPage(string page)
        {
            // Keep the originating request and conversation until the catalog returns its result.
            openingQwenActionPage = true;
            try { Navigate(page); }
            finally { openingQwenActionPage = false; }
        }

        void RenderQwenSources(StackPanel list, IReadOnlyList<QwenSearchSource> sources)
        {
            list.Children.Clear();
            foreach (var source in sources)
            {
                if (!QwenSearchService.IsSafeSource(source.Url)) continue;
                var uri = source.Url;
                var link = Btn("[" + source.Index + "] " + source.Title, () => OpenQwenSource(uri));
                link.HorizontalAlignment = HorizontalAlignment.Left;
                link.ToolTip = uri.AbsoluteUri;
                list.Children.Add(link);
            }
            if (list.Children.Count != 0)
                list.Children.Insert(0, Txt("参考来源", 12, true, true));
        }

        void OpenQwenSource(Uri uri)
        {
            if (!QwenSearchService.IsSafeSource(uri)) return;
            try
            {
                if (qwenOpenUri != null) qwenOpenUri(uri);
                else Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception) { SetQwenSearchStatus("无法打开链接，请检查 Windows 的默认浏览器设置。"); }
        }

        void CancelQwenSearch()
        {
            var operation = qwenRequest;
            if (operation == null) return;
            ResetQwenSearch();
            SetQwenSearchStatus("请求已取消。");
            operation.Cancel();
            RenderQwenConversation();
        }

        void CloseQwenSearchPage()
        {
            if (!openingQwenActionPage) CancelQwenSearch();
            CloseQwenMediaPage();
            if (qwenPage != null)
            {
                qwenSelectedTab = QwenAssistantTab.Chat;
                SetQwenWebLayout(false);
            }
        }

        void ResetQwenSearch()
        {
            qwenRequest = null;
            qwenSubmit.IsEnabled = true;
            qwenCancel.IsEnabled = false;
            qwenCancel.Visibility = Visibility.Collapsed;
            qwenRegenerate.IsEnabled = CanRegenerateQwenAnswer();
            RefreshQwenProviderControls();
        }

        void SetQwenSearchStatus(string message)
        {
            qwenSearchStatus.Text = message;
            qwenSearchStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
