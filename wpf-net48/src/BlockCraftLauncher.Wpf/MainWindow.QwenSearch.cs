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
            if (!regenerate && TryOpenQwenCreativeDraft(question)) return;
            LauncherSearchCommand command = null;
            bool isCommand = !regenerate && LauncherSearchCommand.TryParse(question, out command);
            if (isCommand && command.Kind == LauncherSearchKind.Invalid)
            {
                SetQwenSearchStatus(command.Error);
                return;
            }
            bool recommend = isCommand && command.Kind == LauncherSearchKind.ModRecommendation;
            bool installWithoutCloud = recommend && command.Install &&
                string.IsNullOrWhiteSpace(ReadQwenConnection().ResolveApiKey());
            bool useModel = !isCommand || recommend && !installWithoutCloud;
            QwenConnectionSettings connection = null;
            string key = null;
            if (useModel && !TryReadQwenCredentials(out connection, out key)) return;

            var operation = new CancellationTokenSource();
            qwenRequest = operation;
            qwenSubmit.IsEnabled = false;
            qwenCancel.IsEnabled = true;
            qwenCancel.Visibility = Visibility.Visible;
            qwenRegenerate.IsEnabled = false;
            SetQwenSearchStatus(useModel ? "正在等待千问回答…" : "正在启动器内查找…");
            try
            {
                if (!isCommand)
                {
                    await RunQwenChatAsync(question, regenerate, key, connection, operation);
                    return;
                }

                string status;
                if (command.Kind == LauncherSearchKind.Game)
                    status = await RunQwenGameActionAsync(command.Query, command.Loader, command.Install, operation.Token);
                else if (command.Kind == LauncherSearchKind.Resource)
                    status = await RunQwenResourceActionAsync(command, operation.Token);
                else if (installWithoutCloud)
                {
                    var project = LocalModSearchPlan.KnownInstallProject(command.Query);
                    if (project != null)
                        status = await InstallKnownQwenProjectAsync(project, command, operation.Token);
                    else
                    {
                        Uri endpoint = await PrepareLocalModelAsync(LocalQwenMode.Chat, false, null, operation.Token);
                        status = await RunLocalQwenAsync(endpoint, new[] { new QwenChatTurn("user", question) }, null, operation.Token);
                    }
                }
                else
                {
                    string query = command.Query;
                    if (recommend)
                    {
                        query = await QwenModRecommendationService.RecommendKeywordAsync(question,
                            (prompt, ct) => AskQwenAsync(prompt, key, connection, ct), operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                    }
                    status = await RunQwenModActionAsync(query, command.Install, command.GameVersion, command.Loader,
                        command.InstanceName, command.SourceName, operation.Token);
                }
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus(status);
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus("请求已取消。");
            }
            catch (QwenSearchException ex)
            {
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus(ex.Message);
            }
            catch (LocalModelNotInstalledException ex)
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
                    if (!isCommand) RenderQwenConversation();
                }
                operation.Dispose();
            }
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
            if (currentPage == QwenSearchPage && qwenRequest != null)
            {
                ResetQwenSearch();
                SetQwenSearchStatus("已打开启动器的下载页面。");
            }
            Navigate(page);
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
            CancelQwenSearch();
            CloseQwenMediaPage();
            if (qwenPage != null)
            {
                qwenSelectedTab = QwenAssistantTab.Web;
                SetQwenWebLayout(false);
            }
        }

        void ResetQwenSearch()
        {
            qwenRequest = null;
            qwenSubmit.IsEnabled = true;
            qwenCancel.IsEnabled = false;
            qwenCancel.Visibility = Visibility.Collapsed;
            qwenRegenerate.IsEnabled = qwenConversations.Current.Turns.Count != 0;
        }

        void SetQwenSearchStatus(string message)
        {
            qwenSearchStatus.Text = message;
            qwenSearchStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
