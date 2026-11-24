using System;
using System.Diagnostics;
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
        StackPanel qwenPage, qwenSources;
        Border qwenResult;
        TextBox qwenQuery, qwenEndpoint, qwenModel;
        TextBlock qwenSearchStatus, qwenAnswer;
        PasswordBox qwenApiKey;
        CheckBox qwenRememberKey;
        Expander qwenConnection;
        Button qwenSubmit, qwenCancel;
        CancellationTokenSource qwenRequest;
        string qwenSettingsPath, qwenCompanionPath;
        Func<string, string, string, string, CancellationToken, Task<QwenSearchResult>> qwenRunSearch;
        Action<Uri> qwenOpenUri;

        UIElement BuildQwenSearchPage()
        {
            if (qwenPage != null) return qwenPage;
            qwenSearchStatus = Txt("", 12, false, true);
            AutomationProperties.SetLiveSetting(qwenSearchStatus, AutomationLiveSetting.Polite);
            qwenSearchStatus.Visibility = Visibility.Collapsed;
            qwenQuery = new TextBox
            {
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64,
                MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxLength = QwenSearchService.MaxQuestionLength
            };
            ApplySearchInputStyle(qwenQuery, "例如：下载 Minecraft 1.21.1、给当前实例装钠。按 Ctrl + Enter 提交");
            AutomationProperties.SetName(qwenQuery, "问题或下载指令");
            qwenQuery.PreviewKeyDown += async (sender, args) =>
            {
                if (args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
                args.Handled = true;
                await OpenQwenPageAsync();
            };
            qwenSubmit = Btn("搜索", async () => await OpenQwenPageAsync(), true);
            qwenCancel = Btn("取消搜索", CancelQwenSearch);
            qwenCancel.IsEnabled = false;
            qwenCancel.Visibility = Visibility.Collapsed;
            var clear = Btn("清空", () =>
            {
                CancelQwenSearch();
                qwenQuery.Clear();
                ClearQwenResult();
                SetQwenSearchStatus("");
                qwenQuery.Focus();
            });
            var searchCard = Card(Col(Txt("问题或下载指令", 16, true),
                Txt("试试「下载 Minecraft 1.21.1」或「给当前实例装钠」。", 12, false, true), qwenQuery,
                Row(qwenSubmit, qwenCancel, clear), qwenSearchStatus));

            qwenAnswer = Txt("", 14);
            AutomationProperties.SetName(qwenAnswer, "千问回答");
            qwenSources = new StackPanel();
            var copy = Btn("复制回答", () =>
            {
                try { Clipboard.SetText(qwenAnswer.Text); SetQwenSearchStatus("已复制回答。"); }
                catch (System.Runtime.InteropServices.ExternalException) { SetQwenSearchStatus("剪贴板正被占用，请重试。"); }
            });
            qwenResult = Card(Col(Txt("千问回答", 16, true), qwenAnswer, qwenSources, copy));
            qwenResult.Visibility = Visibility.Collapsed;
            qwenConnection = BuildQwenConnection();
            qwenPage = Col(PageHeading(QwenSearchPage, "游戏和模组指令由启动器处理。向千问提问或请它推荐模组时，将使用你的百炼账户。"),
                searchCard, qwenConnection, qwenResult);
            return qwenPage;
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
                    SetQwenSearchStatus("已填入桌面伙伴的接口和模型。填写自己的密钥后即可搜索。");
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

        async Task OpenQwenPageAsync()
        {
            if (qwenPage == null || currentPage != QwenSearchPage || qwenRequest != null) return;
            string question = qwenQuery.Text.Trim();
            if (question.Length == 0 || question.Length > QwenSearchService.MaxQuestionLength)
            {
                SetQwenSearchStatus("请输入 1 至 " + QwenSearchService.MaxQuestionLength + " 字的问题。");
                qwenQuery.Focus();
                return;
            }
            bool isCommand = LauncherSearchCommand.TryParse(question, out var command);
            if (isCommand && command.Kind == LauncherSearchKind.Invalid)
            {
                ClearQwenResult();
                SetQwenSearchStatus(command.Error);
                return;
            }
            bool useModel = !isCommand || command.Kind == LauncherSearchKind.ModRecommendation;
            QwenConnectionSettings connection = null;
            string key = null;
            if (useModel && !TryReadQwenCredentials(out connection, out key)) return;

            var operation = new CancellationTokenSource();
            qwenRequest = operation;
            qwenSubmit.IsEnabled = false;
            qwenCancel.IsEnabled = true;
            qwenCancel.Visibility = Visibility.Visible;
            ClearQwenResult();
            SetQwenSearchStatus(useModel ? "正在搜索…" : "正在启动器内查找…");
            try
            {
                if (!isCommand)
                {
                    var result = await AskQwenAsync(question, key, connection, operation.Token);
                    if (ReferenceEquals(qwenRequest, operation))
                    {
                        ShowQwenResult(result);
                        SetQwenSearchStatus("");
                    }
                    return;
                }

                string status;
                if (command.Kind == LauncherSearchKind.Game)
                    status = await RunQwenGameActionAsync(command.Query, command.Loader, command.Install, operation.Token);
                else
                {
                    string query = command.Query;
                    bool install = command.Install;
                    if (command.Kind == LauncherSearchKind.ModRecommendation)
                    {
                        query = await QwenModRecommendationService.RecommendKeywordAsync(question,
                            (prompt, ct) => AskQwenAsync(prompt, key, connection, ct), operation.Token);
                        operation.Token.ThrowIfCancellationRequested();
                        install = false;
                    }
                    status = await RunQwenModActionAsync(query, install, command.GameVersion, command.Loader,
                        command.InstanceName, command.SourceName);
                }
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus(status);
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus("搜索已取消。");
            }
            catch (QwenSearchException ex)
            {
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus(ex.Message);
            }
            catch (Exception)
            {
                if (ReferenceEquals(qwenRequest, operation)) SetQwenSearchStatus(isCommand
                    ? "启动器未能完成指令，请检查网络后重试。" : "暂时无法完成搜索，请稍后重试。");
            }
            finally
            {
                if (ReferenceEquals(qwenRequest, operation)) ResetQwenSearch();
                operation.Dispose();
            }
        }

        bool TryReadQwenCredentials(out QwenConnectionSettings connection, out string key)
        {
            connection = ReadQwenConnection();
            key = null;
            if (!QwenSearchService.TryGetEndpoint(connection.Endpoint, out _))
            {
                qwenConnection.IsExpanded = true;
                SetQwenSearchStatus("请填写阿里云百炼北京或新加坡地域的 HTTPS 接口地址。");
                qwenEndpoint.Focus();
                return false;
            }
            if (!QwenSearchService.IsValidModel(connection.Model))
            {
                qwenConnection.IsExpanded = true;
                SetQwenSearchStatus("请填写有效的千问模型 ID，例如 qwen4-max。");
                qwenModel.Focus();
                return false;
            }
            key = connection.ResolveApiKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                qwenConnection.IsExpanded = true;
                SetQwenSearchStatus("请在连接设置中填写自己的 API 密钥，再点搜索。");
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

        void ShowQwenResult(QwenSearchResult result)
        {
            qwenAnswer.Text = result.Answer;
            foreach (var source in result.Sources)
            {
                if (!QwenSearchService.IsSafeSource(source.Url)) continue;
                var uri = source.Url;
                var link = Btn("[" + source.Index + "] " + source.Title, () => OpenQwenSource(uri));
                link.HorizontalAlignment = HorizontalAlignment.Left;
                link.ToolTip = uri.AbsoluteUri;
                qwenSources.Children.Add(link);
            }
            qwenSources.Children.Insert(0, qwenSources.Children.Count == 0
                ? Txt("此回答未附来源链接。", 12, false, true) : Txt("参考来源", 14, true));
            qwenResult.Visibility = Visibility.Visible;
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

        void ClearQwenResult()
        {
            qwenAnswer.Text = "";
            qwenSources.Children.Clear();
            qwenResult.Visibility = Visibility.Collapsed;
        }

        void CancelQwenSearch()
        {
            var operation = qwenRequest;
            if (operation == null) return;
            ResetQwenSearch();
            SetQwenSearchStatus("搜索已取消。");
            operation.Cancel();
        }

        void CloseQwenSearchPage() => CancelQwenSearch();

        void ResetQwenSearch()
        {
            qwenRequest = null;
            qwenSubmit.IsEnabled = true;
            qwenCancel.IsEnabled = false;
            qwenCancel.Visibility = Visibility.Collapsed;
        }

        void SetQwenSearchStatus(string message)
        {
            qwenSearchStatus.Text = message;
            qwenSearchStatus.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
