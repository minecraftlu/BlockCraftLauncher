using System;
using System.Globalization;
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
