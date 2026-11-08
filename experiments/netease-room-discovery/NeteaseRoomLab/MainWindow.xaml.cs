using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace BlockCraft.NeteaseRoomLab
{
    public partial class MainWindow : Window
    {
        private readonly RoomDiscoveryClient discoveryClient = new RoomDiscoveryClient();
        private readonly LobbyNodeClient lobbyNodeClient = new LobbyNodeClient();
        private string lastNodeId = "";
        private string lastRoomReport = "";
        private string selectedAuthHelper = "";
        private bool authHelperRunning;

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void Lookup_Click(object sender, RoutedEventArgs e)
        {
            var number = (RoomIdBox.Text ?? "").Trim();
            var uidText = (UidBox.Text ?? "").Trim();
            uint uid;

            if (!System.Text.RegularExpressions.Regex.IsMatch(number, @"^[0-9]{4,10}$"))
            {
                ShowResult("INPUT_ERROR", "请输入 4 到 10 位数字房间号。", false);
                return;
            }

            if (!uint.TryParse(uidText, NumberStyles.None, CultureInfo.InvariantCulture, out uid))
            {
                ShowResult("INPUT_ERROR", "查询 UID 必须为非负整数（最大 4294967295）。默认填 0 即可。", false);
                return;
            }

            LookupButton.IsEnabled = false;
            NodeLookupButton.IsEnabled = false;
            lastNodeId = "";
            lastRoomReport = "";
            BusyBar.Visibility = Visibility.Visible;
            ShowResult("CONNECTING", "正在通过 HTTPS 查询房间元数据；仅发送一次只读请求。", false);

            try
            {
                var result = await discoveryClient.LookupAsync(number, uid);
                lastNodeId = result.IsMetadataFound ? result.NodeId : "";
                if (result.IsMetadataFound) lastRoomReport = result.Details;
                ShowResult(result.Status, result.Details, result.IsMetadataFound);
            }
            catch (Exception)
            {
                ShowResult("INCONCLUSIVE", "客户端发生未预期错误；没有提交账号凭据。", false);
            }
            finally
            {
                BusyBar.Visibility = Visibility.Collapsed;
                LookupButton.IsEnabled = true;
                NodeLookupButton.IsEnabled = !string.IsNullOrEmpty(lastNodeId);
            }
        }

        private async void NodeLookup_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(lastNodeId))
            {
                ShowResult("NODE_MAPPING_INCONCLUSIVE", "请先成功查询真实房间，再执行节点匹配。", false);
                return;
            }

            LookupButton.IsEnabled = false;
            NodeLookupButton.IsEnabled = false;
            BusyBar.Visibility = Visibility.Visible;
            ShowResult("LOOKING_UP_NODE", "正在读取一次公开大厅节点列表，不建立游戏连接。", false);
            try
            {
                var result = await lobbyNodeClient.LookupAsync(lastNodeId);
                var text = (string.IsNullOrEmpty(lastRoomReport) ? "" : lastRoomReport + Environment.NewLine + Environment.NewLine)
                           + result.Details;
                ShowResult(result.Status, text, result.IsMapped);
            }
            catch (Exception)
            {
                ShowResult("NODE_MAPPING_INCONCLUSIVE", "节点查询意外失败；未接入游戏服务。", false);
            }
            finally
            {
                BusyBar.Visibility = Visibility.Collapsed;
                LookupButton.IsEnabled = true;
                NodeLookupButton.IsEnabled = !string.IsNullOrEmpty(lastNodeId);
            }
        }

        private void AuthConsent_Changed(object sender, RoutedEventArgs e)
        {
            UpdateAuthButtons();
        }

        private void UpdateAuthButtons()
        {
            if (EmailAuthButton == null || AuthConsentBox == null) return;
            EmailAuthButton.IsEnabled = !authHelperRunning
                && AuthConsentBox.IsChecked == true
                && GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                && File.Exists(selectedAuthHelper);
            if (DeleteAuthButton != null) DeleteAuthButton.IsEnabled = !authHelperRunning;
            if (AuthDebugButton != null) AuthDebugButton.IsEnabled = !authHelperRunning
                && GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                && File.Exists(selectedAuthHelper);
        }

        private void ChooseAuthHelper_Click(object sender, RoutedEventArgs e)
        {
            if (authHelperRunning) return;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "手动选择你已审查且信任的 Opentransfer connect.exe",
                Filter = "connect.exe|connect.exe",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                if (!GuestEmailAuth.IsAllowedHelperPath(dialog.FileName))
                {
                    AuthStatusText.Text = "AUTH_HELPER_REJECTED：只接受明确选择的 connect.exe。";
                    return;
                }
                selectedAuthHelper = Path.GetFullPath(dialog.FileName);
                string hash = GuestEmailAuth.GetHelperSha256(selectedAuthHelper);
                AuthStatusText.Text = "AUTH_HELPER_SELECTED：本机已选择外部工具；SHA-256 "
                    + hash + Environment.NewLine
                    + "请独立核对工具的来源与哈希值。选择工具并不意味着它安全或得到网易授权。";
            }
            catch (Exception)
            {
                selectedAuthHelper = "";
                AuthStatusText.Text = "AUTH_HELPER_REJECTED：读取本地工具失败，未尝试登录。";
            }
            finally
            {
                UpdateAuthButtons();
            }
        }

        private void CopyAuthDebug_Click(object sender, RoutedEventArgs e)
        {
            if (authHelperRunning || !GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                || !File.Exists(selectedAuthHelper))
            {
                AuthStatusText.Text = "AUTH_DIAGNOSTIC_UNAVAILABLE：请先手动选择 connect.exe。";
                return;
            }

            try
            {
                string cmd = GuestEmailAuth.GetPowerShellDiagnosticCommand(selectedAuthHelper);
                Clipboard.SetText(cmd);
                AuthStatusText.Text = "AUTH_DIAGNOSTIC_COMMAND_COPIED：已复制不含密码的 PowerShell 命令。"
                    + " 请自己打开 PowerShell 粘贴执行；这样程序退出后错误信息不会自动消失。"
                    + " 注意输出可能包含敏感信息，分享截图前请遮挡邮箱、验证码、Cookie、"
                    + " token、sessionid 和身份证信息。";
            }
            catch (Exception)
            {
                AuthStatusText.Text = "AUTH_DIAGNOSTIC_COPY_FAILED：无法复制诊断命令。"
                    + " 请在 connect.exe 所在文件夹手动打开 PowerShell 执行"
                    + " .\\connect.exe -login -login-method email。";
            }
        }

        private async void EmailAuth_Click(object sender, RoutedEventArgs e)
        {
            if (authHelperRunning || AuthConsentBox.IsChecked != true ||
                !GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper) ||
                !File.Exists(selectedAuthHelper)) return;

            var confirmation = MessageBox.Show(
                "登录将启动第三方 connect.exe 独立窗口。你会在该窗口输入网易邮箱与密码。"
                + Environment.NewLine
                + "BlockCraft 不读取凭据；但第三方程序本身会处理密码，存在账户与服务条款风险。"
                + Environment.NewLine
                + "仅当你已审查其来源并接受风险时继续。",
                "确认启动外部认证助手", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;

            Process helper = null;
            try
            {
                helper = GuestEmailAuth.StartEmailLogin(selectedAuthHelper);
                authHelperRunning = true;
                UpdateAuthButtons();
                AuthStatusText.Text = "AUTH_HELPER_RUNNING：认证助手已在独立窗口启动。"
                    + " 请只在本机输入第二个账号的信息，不要发给任何人。";

                // The external helper owns the interactive console. No stdout,
                // stdin, credentials or cookie bytes are accessed by WPF.
                int exitCode = await Task.Run(() =>
                {
                    helper.WaitForExit();
                    return helper.ExitCode;
                });
                AuthStatusText.Text = GuestEmailAuth.ExplainExit(
                    exitCode, GuestEmailAuth.CredentialFileExists());
            }
            catch (Win32Exception)
            {
                AuthStatusText.Text = "AUTH_HELPER_START_FAILED：Windows 未能启动选定程序。";
            }
            catch (UnauthorizedAccessException)
            {
                AuthStatusText.Text = "AUTH_HELPER_ACCESS_DENIED：当前用户无法启动或创建私有目录。";
            }
            catch (Exception)
            {
                AuthStatusText.Text = "AUTH_HELPER_FAILED：本机启动过程遇到错误。"
                    + " 未输出邮箱、密码、Cookie 或原始日志。";
            }
            finally
            {
                if (helper != null) helper.Dispose();
                authHelperRunning = false;
                UpdateAuthButtons();
            }
        }

        private void DeleteAuth_Click(object sender, RoutedEventArgs e)
        {
            if (authHelperRunning) return;
            if (MessageBox.Show(
                "仅删除 BlockCraft 实验目录中的邮箱 Cookie 输出文件；"
                + "第三方助手保存的设备绑定信息不会被清除。是否继续？",
                "删除本地文件", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;
            try
            {
                AuthStatusText.Text = GuestEmailAuth.DeleteCredentialFile()
                    ? "LOCAL_CREDENTIAL_FILE_DELETED：仅删除了本次实验的 Cookie 文件。"
                    : "LOCAL_CREDENTIAL_FILE_NOT_FOUND：实验目录下没有 Cookie 文件。";
            }
            catch (Exception)
            {
                AuthStatusText.Text = "LOCAL_CREDENTIAL_DELETE_FAILED：删除失败，请检查当前用户权限。";
            }
        }

        private void ParserCheck_Click(object sender, RoutedEventArgs e)
        {
            lastNodeId = "";
            lastRoomReport = "";
            NodeLookupButton.IsEnabled = false;
            // Fixed synthetic JSON. NEVER confuse with a live room lookup.
            const string sample =
                "{\"code\":0,\"list\":[{\"rid\":453649,\"name\":\"453649\",\"cnt\":1,\"cap\":30,\"version\":898,\"platform\":2,\"srv\":1}]}";
            var result = RoomDiscoveryClient.ParseResponse("453649", sample);

            if (result.IsMetadataFound)
            {
                ShowResult("LOCAL_PARSER_OK",
                    "离线解析自检成功（以下全部是模拟数据，不代表房间在线）。"
                    + Environment.NewLine + Environment.NewLine + result.Details, true);
            }
            else
            {
                ShowResult("LOCAL_PARSER_FAILED", result.Details, false);
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var report = "BlockCraft 网易房间诊断（不含账号凭据）"
                + Environment.NewLine + "状态：" + StatusText.Text
                + Environment.NewLine + "结果：" + Environment.NewLine + ResultBox.Text;

            try
            {
                Clipboard.SetText(report);
            }
            catch (Exception)
            {
                MessageBox.Show("无法复制到剪贴板。可直接选中下方文本复制。",
                    "复制失败", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ShowResult(string status, string message, bool success)
        {
            StatusText.Text = status;
            StatusText.Foreground = success ? Brushes.LightGreen : Brushes.LightSteelBlue;
            ResultBox.Text = message;
        }
    }
}
