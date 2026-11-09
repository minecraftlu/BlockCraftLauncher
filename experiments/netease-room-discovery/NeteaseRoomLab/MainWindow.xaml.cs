using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
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
        private bool g79CheckRunning;
        private bool livePreflightRunning;
        private bool joinProbeRunning;
        private bool stage12OfflineRunning;
        private CancellationTokenSource activeStage12Cancellation;
        // All real room tests share this cancellation scope; it is never
        // reused after completion and is cancelled when the window closes.
        private CancellationTokenSource activeProbeCancellation;

        public MainWindow()
        {
            InitializeComponent();
            Stage12OfflineButton.IsEnabled = Stage12OfflineBridge.IsEmbedded;
            Stage12GameButton.IsEnabled = Stage12OfflineBridge.IsEmbedded;
            if (!Stage12OfflineBridge.IsEmbedded)
            {
                Stage12OfflineStatusText.Text = "STAGE12_OFFLINE_BRIDGE_UNAVAILABLE：本 EXE 未嵌入 Stage 12 组件。";
                Stage12GameStatusText.Text = "STAGE12_LOCAL_GAME_SPAWN_UNAVAILABLE：缺少本机游戏协议组件。";
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Let Go receive stdin EOF and close RakNet, WebSocket and
            // WebRTC before the WPF owner disappears; no forced loop.
            if (activeProbeCancellation != null)
                activeProbeCancellation.Cancel();
            if (activeStage12Cancellation != null)
                activeStage12Cancellation.Cancel();
            base.OnClosing(e);
        }

        private async void Stage12Offline_Click(object sender, RoutedEventArgs e)
        {
            if (stage12OfflineRunning || joinProbeRunning || livePreflightRunning
                || authHelperRunning || g79CheckRunning) return;
            if (!Stage12OfflineBridge.IsEmbedded)
            {
                Stage12OfflineStatusText.Text = "STAGE12_OFFLINE_BRIDGE_UNAVAILABLE：缺少内置测试组件。";
                return;
            }
            stage12OfflineRunning = true;
            activeStage12Cancellation = new CancellationTokenSource();
            UpdateAuthButtons();
            Stage12OfflineStatusText.Text = "STAGE12_OFFLINE_BRIDGE_RUNNING：正在本机测试，不访问网易服务器。";
            try
            {
                Stage12OfflineStatusText.Text = await Stage12OfflineBridge.RunAsync(activeStage12Cancellation.Token);
            }
            catch (Exception)
            {
                Stage12OfflineStatusText.Text = "STAGE12_OFFLINE_BRIDGE_FAILED_GAME_NOT_JOINED：本机自检异常。";
            }
            finally
            {
                stage12OfflineRunning = false;
                if (activeStage12Cancellation != null)
                {
                    activeStage12Cancellation.Dispose();
                    activeStage12Cancellation = null;
                }
                UpdateAuthButtons();
            }
        }

        private async void Stage12Game_Click(object sender, RoutedEventArgs e)
        {
            if (stage12OfflineRunning || joinProbeRunning || livePreflightRunning
                || authHelperRunning || g79CheckRunning) return;
            if (!Stage12OfflineBridge.IsEmbedded)
            {
                Stage12GameStatusText.Text = "STAGE12_LOCAL_GAME_SPAWN_UNAVAILABLE：缺少内置本机模拟游戏组件。";
                return;
            }
            stage12OfflineRunning = true;
            activeStage12Cancellation = new CancellationTokenSource();
            UpdateAuthButtons();
            Stage12GameStatusText.Text = "STAGE12_LOCAL_GAME_SPAWN_RUNNING："
                + "两个本地工作进程正在完成模拟游戏登录，不访问网易服务器。";
            try
            {
                Stage12GameStatusText.Text =
                    await Stage12OfflineBridge.RunGameAsync(activeStage12Cancellation.Token);
            }
            catch (Exception)
            {
                Stage12GameStatusText.Text =
                    "STAGE12_LOCAL_GAME_SPAWN_FAILED_REMOTE_NOT_JOINED：本机自检异常。";
            }
            finally
            {
                stage12OfflineRunning = false;
                if (activeStage12Cancellation != null)
                {
                    activeStage12Cancellation.Dispose();
                    activeStage12Cancellation = null;
                }
                UpdateAuthButtons();
            }
        }

        private void CancelProbe_Click(object sender, RoutedEventArgs e)
        {
            var cancellation = activeProbeCancellation;
            if (cancellation == null || !joinProbeRunning) return;
            if (!cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
                CancelProbeButton.IsEnabled = false;
                if (RealWebRTCStatusText != null)
                    RealWebRTCStatusText.Text =
                        "LOCAL_PROBE_CANCELLING：已请求停止连接，正在等待本机探针释放会话。";
                if (RealJoinStatusText != null)
                    RealJoinStatusText.Text =
                        "LOCAL_PROBE_CANCELLING：正在请求结束本机大厅连接。";
            }
        }

        private async void Lookup_Click(object sender, RoutedEventArgs e)
        {
            if (joinProbeRunning) return;
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
                LookupButton.IsEnabled = !joinProbeRunning;
                NodeLookupButton.IsEnabled = !joinProbeRunning
                    && !string.IsNullOrEmpty(lastNodeId);
                UpdateAuthButtons();
            }
        }

        private async void NodeLookup_Click(object sender, RoutedEventArgs e)
        {
            if (joinProbeRunning) return;
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
                LookupButton.IsEnabled = !joinProbeRunning;
                NodeLookupButton.IsEnabled = !joinProbeRunning
                    && !string.IsNullOrEmpty(lastNodeId);
                UpdateAuthButtons();
            }
        }

        private void AuthConsent_Changed(object sender, RoutedEventArgs e)
        {
            UpdateAuthButtons();
        }

        private void UpdateAuthButtons()
        {
            if (EmailAuthButton == null || AuthConsentBox == null) return;
            if (Stage12OfflineButton != null)
                Stage12OfflineButton.IsEnabled = Stage12OfflineBridge.IsEmbedded
                    && !stage12OfflineRunning && !joinProbeRunning && !livePreflightRunning
                    && !authHelperRunning && !g79CheckRunning;
            if (Stage12GameButton != null)
                Stage12GameButton.IsEnabled = Stage12OfflineButton.IsEnabled;
            // Prevent the visible room number and node query from changing
            // during the one expressly authorized real connection attempt.
            if (RoomIdBox != null) RoomIdBox.IsEnabled = !joinProbeRunning;
            if (UidBox != null) UidBox.IsEnabled = !joinProbeRunning;
            bool queryAvailable = !joinProbeRunning &&
                (BusyBar == null || BusyBar.Visibility != Visibility.Visible);
            if (LookupButton != null) LookupButton.IsEnabled = queryAvailable;
            if (NodeLookupButton != null)
                NodeLookupButton.IsEnabled = queryAvailable &&
                    !string.IsNullOrEmpty(lastNodeId);
            EmailAuthButton.IsEnabled = !joinProbeRunning && !livePreflightRunning && !authHelperRunning && !g79CheckRunning
                && AuthConsentBox.IsChecked == true
                && GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                && File.Exists(selectedAuthHelper);
            if (DeleteAuthButton != null) DeleteAuthButton.IsEnabled = !joinProbeRunning && !livePreflightRunning && !authHelperRunning && !g79CheckRunning;
            if (RealJoinButton != null)
                RealJoinButton.IsEnabled = !stage12OfflineRunning && !joinProbeRunning && !livePreflightRunning
                    && !authHelperRunning && !g79CheckRunning && JoinProbeRunner.IsEmbedded
                    && AuthConsentBox.IsChecked == true
                    && GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                    && File.Exists(selectedAuthHelper)
                    && GuestEmailAuth.CredentialFileExists();
            if (ObservationSecondsBox != null)
                ObservationSecondsBox.IsEnabled = !joinProbeRunning
                    && !livePreflightRunning && !authHelperRunning && !g79CheckRunning;
            if (CancelProbeButton != null)
                CancelProbeButton.IsEnabled = joinProbeRunning
                    && activeProbeCancellation != null
                    && !activeProbeCancellation.IsCancellationRequested;
            if (RealWebRTCButton != null)
                RealWebRTCButton.IsEnabled = !stage12OfflineRunning && !joinProbeRunning && !livePreflightRunning
                    && !authHelperRunning && !g79CheckRunning && JoinProbeRunner.IsEmbedded
                    && AuthConsentBox.IsChecked == true
                    && GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                    && File.Exists(selectedAuthHelper)
                    && GuestEmailAuth.CredentialFileExists();
            if (Stage126ReadOnlyButton != null)
                Stage126ReadOnlyButton.IsEnabled = RealWebRTCButton != null
                    && RealWebRTCButton.IsEnabled && Stage12OfflineBridge.IsEmbedded;
            if (Stage127CodecButton != null)
                Stage127CodecButton.IsEnabled = Stage127CodecReference.IsAvailable
                    && !stage12OfflineRunning && !joinProbeRunning && !livePreflightRunning
                    && !authHelperRunning && !g79CheckRunning;
            if (Stage128BuildButton != null)
                Stage128BuildButton.IsEnabled = Stage128ClientBuildReference.IsAvailable
                    && !stage12OfflineRunning && !joinProbeRunning && !livePreflightRunning
                    && !authHelperRunning && !g79CheckRunning;
            if (LivePreflightButton != null)
                LivePreflightButton.IsEnabled = !joinProbeRunning && !livePreflightRunning && !authHelperRunning
                    && !g79CheckRunning && AuthConsentBox.IsChecked == true
                    && GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                    && File.Exists(selectedAuthHelper)
                    && GuestEmailAuth.CredentialFileExists();
            if (G79VerifyButton != null)
                G79VerifyButton.IsEnabled = !joinProbeRunning && !livePreflightRunning && !authHelperRunning && !g79CheckRunning
                    && AuthConsentBox.IsChecked == true
                    && GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                    && File.Exists(selectedAuthHelper)
                    && GuestEmailAuth.CredentialFileExists();
            if (AuthDebugButton != null) AuthDebugButton.IsEnabled = !joinProbeRunning && !livePreflightRunning && !authHelperRunning && !g79CheckRunning
                && GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                && File.Exists(selectedAuthHelper);
        }

        private void ChooseAuthHelper_Click(object sender, RoutedEventArgs e)
        {
            if (authHelperRunning || g79CheckRunning || livePreflightRunning || joinProbeRunning) return;
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
            if (authHelperRunning || g79CheckRunning || livePreflightRunning || joinProbeRunning || !GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
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
            if (authHelperRunning || g79CheckRunning || livePreflightRunning || joinProbeRunning || AuthConsentBox.IsChecked != true ||
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

        private async void G79Verify_Click(object sender, RoutedEventArgs e)
        {
            if (g79CheckRunning || authHelperRunning || livePreflightRunning || joinProbeRunning ||
                AuthConsentBox.IsChecked != true ||
                !GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper) ||
                !File.Exists(selectedAuthHelper) ||
                !GuestEmailAuth.CredentialFileExists())
            {
                AuthStatusText.Text = "G79_CHECK_BLOCKED：请先完成第二邮箱账号登录并选择认证组件。";
                return;
            }

            if (MessageBox.Show(
                "将使用本机已保存的第二账号 Cookie，调用你选定的第三方 connect.exe "
                + "向网易服务发起一次 g79 移动端游戏登录验证。"
                + Environment.NewLine
                + Environment.NewLine
                + "该组件可能返回敏感的游戏 token；BlockCraft 只在进程内临时读取并判断"
                + "状态，不会打印、保存到日志、复制或上传它。"
                + Environment.NewLine
                + "这一步不会向你的手机房间发送入房请求，也不保证网易许可第三方客户端。"
                + Environment.NewLine
                + Environment.NewLine
                + "仅在理解账号风险且为本人账号时继续。",
                "确认执行真实 g79 游戏授权验证",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            g79CheckRunning = true;
            UpdateAuthButtons();
            AuthStatusText.Text = "G79_CHECK_RUNNING：正在本地通过第三方组件验证 g79 游戏会话。"
                + " 最多等待 90 秒。账号与 token 不会显示在界面。";
            try
            {
                AuthStatusText.Text = await GuestG79Verifier.VerifyAsync(selectedAuthHelper);
            }
            catch (Exception)
            {
                AuthStatusText.Text = "G79_CHECK_FAILED：本地授权检查遇到异常，"
                    + "未收集任何游戏 token 或认证详细日志。";
            }
            finally
            {
                g79CheckRunning = false;
                UpdateAuthButtons();
            }
        }

        private void DeleteAuth_Click(object sender, RoutedEventArgs e)
        {
            if (authHelperRunning || g79CheckRunning || livePreflightRunning || joinProbeRunning) return;
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

        private async void RealWebRTC_Click(object sender, RoutedEventArgs e)
        {
            await RunRealWebRTCAsync(false);
        }

        private async void Stage126ReadOnly_Click(object sender, RoutedEventArgs e)
        {
            await RunRealWebRTCAsync(true);
        }

        private async void Stage127Codec_Click(object sender, RoutedEventArgs e)
        {
            if (stage12OfflineRunning || joinProbeRunning || livePreflightRunning
                || authHelperRunning || g79CheckRunning)
                return;
            if (!Stage127CodecReference.IsAvailable)
            {
                Stage127CodecStatusText.Text =
                    "STAGE12_7_PROTOCOL_CHECK_UNAVAILABLE：此启动器没有内嵌检查工具。";
                return;
            }
            string enteredVersion = Stage127ProtocolBox.Text;
            string normalized;
            if (!Stage127CodecReference.TryNormalizeVersion(enteredVersion, out normalized))
            {
                Stage127CodecStatusText.Text =
                    Stage127CodecReference.Classify(enteredVersion, -1, "");
                return;
            }
            stage12OfflineRunning = true;
            activeStage12Cancellation = new CancellationTokenSource();
            UpdateAuthButtons();
            Stage127CodecStatusText.Text =
                "STAGE12_7_PROTOCOL_CHECK_RUNNING：正在本机查询固定公开参考表，不连接服务器。";
            try
            {
                Stage127CodecStatusText.Text = await Stage127CodecReference.RunAsync(
                    normalized, activeStage12Cancellation.Token);
            }
            catch (Exception)
            {
                Stage127CodecStatusText.Text =
                    "STAGE12_7_PROTOCOL_CHECK_FAILED_GAME_NOT_JOINED：本地查询未通过。";
            }
            finally
            {
                stage12OfflineRunning = false;
                if (activeStage12Cancellation != null)
                {
                    activeStage12Cancellation.Dispose();
                    activeStage12Cancellation = null;
                }
                UpdateAuthButtons();
            }
        }

        private async void Stage128Build_Click(object sender, RoutedEventArgs e)
        {
            if (stage12OfflineRunning || joinProbeRunning || livePreflightRunning
                || authHelperRunning || g79CheckRunning) return;
            string gameVersion = Stage128GameBuildBox.Text;
            string patchVersion = Stage128PatchBuildBox.Text;
            string normalizedGame, normalizedPatch;
            if (!Stage128ClientBuildReference.TryNormalizeBuild(gameVersion,out normalizedGame)
                || !Stage128ClientBuildReference.TryNormalizeBuild(patchVersion,out normalizedPatch))
            {
                Stage128BuildStatusText.Text =
                    Stage128ClientBuildReference.Classify(gameVersion,patchVersion,-1,"");
                return;
            }
            if (!Stage128ClientBuildReference.IsAvailable)
            {
                Stage128BuildStatusText.Text =
                    "STAGE12_8_BUILD_REFERENCE_UNAVAILABLE：此启动器没有离线版本参考组件。";
                return;
            }
            stage12OfflineRunning = true;
            activeStage12Cancellation = new CancellationTokenSource();
            UpdateAuthButtons();
            Stage128BuildStatusText.Text =
                "STAGE12_8_BUILD_REFERENCE_RUNNING：正在本机对照公开版本表，不连接网易服务器。";
            try
            {
                Stage128BuildStatusText.Text = await Stage128ClientBuildReference.RunAsync(
                    normalizedGame,normalizedPatch,activeStage12Cancellation.Token);
            }
            catch (Exception)
            {
                Stage128BuildStatusText.Text =
                    "STAGE12_8_BUILD_REFERENCE_FAILED_GAME_NOT_JOINED：本机参考查询发生错误。";
            }
            finally
            {
                stage12OfflineRunning = false;
                if (activeStage12Cancellation != null)
                {
                    activeStage12Cancellation.Dispose();
                    activeStage12Cancellation = null;
                }
                UpdateAuthButtons();
            }
        }

        private async Task RunRealWebRTCAsync(bool inboundOnly)
        {
            var output = inboundOnly ? Stage126ReadOnlyStatusText : RealWebRTCStatusText;
            if (joinProbeRunning || livePreflightRunning || authHelperRunning
                || g79CheckRunning || AuthConsentBox.IsChecked != true
                || !JoinProbeRunner.IsEmbedded
                || !GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                || !File.Exists(selectedAuthHelper)
                || !GuestEmailAuth.CredentialFileExists())
            {
                output.Text = "WEBRTC_PROBE_NOT_READY："
                    + "请使用 Stage 10 单 EXE 并选择原认证助手；请勿发送 Cookie 或 token。";
                return;
            }
            string roomNumber = (RoomIdBox.Text ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(roomNumber, @"^[0-9]{4,10}$"))
            {
                output.Text = "WEBRTC_PROBE_INPUT_INVALID：请输入有效数字房间号。";
                return;
            }

            int observationSeconds = ObservationSecondsBox.SelectedIndex == 2 ? 60
                : ObservationSecondsBox.SelectedIndex == 0 ? 8 : 30;
            if (inboundOnly && observationSeconds==8)
            {
                output.Text = "STAGE12_READONLY_WORKER_INVALID：只读接收请先选择 30 或 60 秒。";
                return;
            }
            if (inboundOnly && !Stage12OfflineBridge.IsEmbedded)
            {
                output.Text = "STAGE12_READONLY_WORKER_UNAVAILABLE_GAME_NOT_JOINED：缺少内置工作进程。";
                return;
            }

            var confirmation = MessageBox.Show(
                "此测试会使用第二账号和当前房间号，向真实网易服务器执行一次"
                + " RakNet 大厅登录、入房请求，并在服务器接受且房主就绪后"
                + "尝试 WebSocket 信令与 ICE/DTLS/SCTP 数据通道连接。"
                + Environment.NewLine + Environment.NewLine
                + "请确认这是你本人手机创建的房间或已获房主明确许可。"
                + " 该实验使用非官方客户端协议，存在服务条款及账号限制风险。"
                + Environment.NewLine + Environment.NewLine
                + (inboundOnly ? "Stage 12.6 还会把手机方向的可靠消息单向送入内嵌本机工作进程，只在内存中接收并清空；不会向手机写入任何游戏数据。" + Environment.NewLine : "")
                + "探针只被动观察 " + observationSeconds + " 秒内两条数据通道是否 OPEN、"
                + "收到消息数量和字节数，不发送 Minecraft 游戏数据包，不加载世界。"
                + "测试结束后关闭本地连接；这不保证服务器立即清除离线玩家。"
                + Environment.NewLine
                + "不会输出或上传 Cookie、token、信令票据、"
                + "ICE 密码、网络 ID 或其他认证凭据。"
                + Environment.NewLine + Environment.NewLine
                + "确认开始一次真实手机房主 WebRTC 连接测试？",
                inboundOnly ? "Stage 12.6 · 真实房间只读接收验证" : "第十阶段 · 真实 NetherNet / WebRTC 连接确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;

            joinProbeRunning = true;
            activeProbeCancellation = new CancellationTokenSource();
            UpdateAuthButtons();
            output.Text = "WEBRTC_PROBE_RUNNING："
                + "正在申请真实大厅准入并尝试建立房主游戏传输连接。";
            try
            {
                output.Text = inboundOnly
                    ? await JoinProbeRunner.RunReadOnlyIPCAsync(roomNumber, selectedAuthHelper,
                        observationSeconds, activeProbeCancellation.Token)
                    : await JoinProbeRunner.RunAsync(roomNumber, selectedAuthHelper, true,
                        observationSeconds, activeProbeCancellation.Token);
            }
            catch (Exception)
            {
                output.Text = "WEBRTC_PROBE_FAILED："
                    + "本机连接测试异常，未输出任何原始账号或信令信息。";
            }
            finally
            {
                joinProbeRunning = false;
                if (activeProbeCancellation != null)
                {
                    activeProbeCancellation.Dispose();
                    activeProbeCancellation = null;
                }
                UpdateAuthButtons();
            }
        }

        private async void RealJoin_Click(object sender, RoutedEventArgs e)
        {
            if (joinProbeRunning || livePreflightRunning || authHelperRunning
                || g79CheckRunning || AuthConsentBox.IsChecked != true
                || !JoinProbeRunner.IsEmbedded
                || !GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper)
                || !File.Exists(selectedAuthHelper)
                || !GuestEmailAuth.CredentialFileExists())
            {
                RealJoinStatusText.Text = "JOIN_PROBE_NOT_READY："
                    + "请使用 Stage9 专用 EXE、选择认证组件并确保第二账号本地 Cookie 已保存。";
                return;
            }
            string roomNumber = (RoomIdBox.Text ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(roomNumber, @"^[0-9]{4,10}$"))
            {
                RealJoinStatusText.Text = "JOIN_PROBE_INPUT_INVALID：请输入有效的房间号。";
                return;
            }
            var confirmation = MessageBox.Show(
                "这一次将与真实网易 RakNet 大厅通信，使用本人第二账号发送大厅登录请求。"
                + Environment.NewLine
                + "只有服务器明确接受大厅登录后，才向指定房间发送一次真实入房请求。"
                + Environment.NewLine + Environment.NewLine
                + "确认该房间由你本人创建，或你已获得房主明确许可。"
                + " 若服务器允许，账号可能短暂进入房间；探针结束后会关闭本地连接，但服务器列表可能仍保留离线记录。"
                + Environment.NewLine + Environment.NewLine
                + "该程序还不会启动 Minecraft、建立 WebRTC 游戏连接或加载世界。"
                + " 使用第三方协议实现可能违反网易的服务条款，存在账号限制风险。"
                + Environment.NewLine
                + "不会输出邮箱密码、Cookie、登录 token、加密密钥或完整玩家 ID。"
                + Environment.NewLine + Environment.NewLine
                + "确定只针对你自己的房间执行一次测试吗？",
                "第九阶段 · 确认真实大厅登录和入房请求",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;

            joinProbeRunning = true;
            activeProbeCancellation = new CancellationTokenSource();
            UpdateAuthButtons();
            RealJoinStatusText.Text = "JOIN_PROBE_RUNNING：正在请求真实大厅登录/入房回执。"
                + " 只尝试一次；测试期间请保持手机房间在线。";
            try
            {
                RealJoinStatusText.Text =
                    await JoinProbeRunner.RunAsync(roomNumber, selectedAuthHelper, false,
                        activeProbeCancellation.Token);
            }
            catch (Exception)
            {
                RealJoinStatusText.Text = "JOIN_PROBE_FAILED：本机连接测试异常。"
                    + " 没有输出或上传任何账号凭据。";
            }
            finally
            {
                joinProbeRunning = false;
                if (activeProbeCancellation != null)
                {
                    activeProbeCancellation.Dispose();
                    activeProbeCancellation = null;
                }
                UpdateAuthButtons();
            }
        }

        private async void LivePreflight_Click(object sender, RoutedEventArgs e)
        {
            if (livePreflightRunning || authHelperRunning || g79CheckRunning || joinProbeRunning ||
                AuthConsentBox.IsChecked != true ||
                !GuestEmailAuth.IsAllowedHelperPath(selectedAuthHelper) ||
                !File.Exists(selectedAuthHelper) ||
                !GuestEmailAuth.CredentialFileExists())
            {
                LivePreflightStatusText.Text = "LIVE_PREFLIGHT_BLOCKED："
                    + "请先确认第二账号的本地 Cookie，并选择可信的 connect.exe。";
                return;
            }

            string roomNumber = (RoomIdBox.Text ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(roomNumber, @"^[0-9]{4,10}$"))
            {
                LivePreflightStatusText.Text = "LIVE_PREFLIGHT_INPUT_ERROR：请输入有效的数字房间号。";
                return;
            }

            if (MessageBox.Show(
                "此操作将使用已保存的第二账号 Cookie，调用你自选的第三方助手"
                + "在本机获取临时游戏令牌，并向网易的只读房间查询接口发送房间号及玩家 UID。"
                + Environment.NewLine + Environment.NewLine
                + "程序将验证房间和公共节点是否匹配、尝试在内存中计算临时连接材料。"
                + "不会打印、上传或保存游戏令牌、加密密钥或随机数。"
                + Environment.NewLine
                + "不会向游戏大厅发登录或 EnterRoom 数据包，也不会连接手机世界。"
                + Environment.NewLine + Environment.NewLine
                + "仅当你拥有该账号且理解第三方客户端及平台条款风险时继续。",
                "确认开始第八阶段真实房间预检",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            livePreflightRunning = true;
            UpdateAuthButtons();
            LivePreflightStatusText.Text = "LIVE_PREFLIGHT_RUNNING："
                + "正在检查第二账号会话、真实房间与节点元数据；不发送入房请求。";
            try
            {
                LivePreflightStatusText.Text =
                    await RoomSessionPreflight.RunAsync(selectedAuthHelper, roomNumber);
            }
            catch (Exception)
            {
                LivePreflightStatusText.Text = "LIVE_PREFLIGHT_INCONCLUSIVE："
                    + "本机预检遇到错误，未显示任何登录凭据或连接密钥。";
            }
            finally
            {
                livePreflightRunning = false;
                UpdateAuthButtons();
            }
        }

        private void AdmissionSchema_Click(object sender, RoutedEventArgs e)
        {
            // Stage 7 is a synthetic, network-free authorization contract test.
            // Not a verified GetAccess response; no cookies or token handling.
            string roomNumber = (RoomIdBox.Text ?? "").Trim();
            AdmissionStatusText.Text = RoomAdmissionSchema.OfflineSelfTest(roomNumber);
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
