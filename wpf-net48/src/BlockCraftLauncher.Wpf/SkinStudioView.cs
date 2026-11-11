using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    /// <summary>
    /// Built-in, offline Minecraft Java skin DIY studio. No DLLs, browser
    /// editor, third-party account access or upload services are required.
    /// </summary>
    internal sealed class SkinStudioView : UserControl
    {
        readonly SkinEditorDocument document = new SkinEditorDocument();
        readonly Func<byte[], bool, System.Threading.Tasks.Task> uploadToMicrosoft;
        readonly Func<string> accountName;
        readonly Action openEditor;
        readonly Action openDownloads;
        readonly Func<OnlineSkinEntry, DownloadJob> queueSkinDownload;
        readonly Action openDownloadTasks;
        UIElement editorPage;
        UIElement downloadPage;
        bool downloadMode;
        internal bool DownloadMode => downloadMode;
        bool uploading, savingOnline, importingOnline;
        internal bool HasUnsavedEdits => document.Dirty;
        internal bool HasActiveOperation => uploading || savingOnline || importingOnline ||
            searchingOnline || onlinePreviewCancellation != null;
        readonly Image texture = new Image();
        readonly Image front = new Image();
        readonly Image back = new Image();
        readonly Skin3DPreview editor3D = new Skin3DPreview();
        readonly Skin3DPreview online3D = new Skin3DPreview();
        readonly CheckBox onlineOuter = new CheckBox();
        System.Windows.Media.Imaging.BitmapSource selectedOnlineTexture;
        SkinEditorDocument selectedOnlineDocument;
        bool showFlatPreview;
        readonly Image onlinePreviewBack = new Image();
        readonly Grid paintingSurface = new Grid();
        readonly Canvas guideCanvas = new Canvas { IsHitTestVisible = false };
        readonly ComboBox tool = new ComboBox();
        readonly ComboBox part = new ComboBox();
        readonly ComboBox layer = new ComboBox();
        readonly ComboBox armModel = new ComboBox();
        readonly CheckBox showGrid = new CheckBox();
        readonly CheckBox showOuter = new CheckBox();
        readonly Slider zoom = new Slider();
        readonly Slider brush = new Slider();
        readonly Slider transparency = new Slider();
        readonly Slider red = new Slider();
        readonly Slider green = new Slider();
        readonly Slider blue = new Slider();
        readonly TextBox hex = new TextBox();
        readonly TextBox skinName = new TextBox();
        readonly ComboBox savedSkins = new ComboBox();
        readonly ComboBox onlineSource = new ComboBox();
        readonly ComboBox onlineCategory = new ComboBox();
        readonly ComboBox onlineArms = new ComboBox();
        readonly ComboBox onlineSort = new ComboBox();
        readonly CheckBox onlineOpenOnly = new CheckBox();
        readonly TextBox onlineQuery = new TextBox();
        readonly ListBox onlineResults = new ListBox();
        readonly Image onlinePreview = new Image();
        readonly TextBlock onlineStatus = new TextBlock();
        readonly TextBlock onlinePageStatus = new TextBlock();
        readonly TextBlock onlineAttribution = Label("");
        readonly TextBlock onlineSkinTitle = Label("选择一个皮肤", 19, true);
        readonly TextBlock onlineSkinModel = Label("", 10.5, true);
        readonly TextBlock onlineSkinSource = Label("", 10.5, true);
        readonly TextBlock onlinePreviewMessage = Label("从结果中选择皮肤查看预览");
        readonly TextBlock onlineDownloadStatus = Label("");
        readonly ProgressBar onlineDownloadProgress = new ProgressBar
        {
            Height = 5, Maximum = 100, Margin = new Thickness(0, 0, 0, 8)
        };
        readonly Dictionary<string, DownloadJob> onlineJobs =
            new Dictionary<string, DownloadJob>(StringComparer.OrdinalIgnoreCase);
        DownloadJob watchedOnlineJob;
        UIElement onlineModelInfo, onlineFlatPanel, onlineOrbitControls, onlineDownloadFeedback, onlineDetailPanel;
        Button onlinePreviewMode, onlineExpandPreview, onlineRetryPreview, onlineCancelSearch;
        Button onlineSearchButton;
        Button onlinePrevButton;
        Button onlineNextButton;
        Button onlineSaveButton;
        Button onlineEditButton;
        Button onlineDetailButton;
        OnlineSkinEntry selectedOnlineSkin;
        OnlineSkinEntry editorSource;
        byte[] selectedOnlinePng;
        CancellationTokenSource onlineSearchCancellation;
        CancellationTokenSource onlinePreviewCancellation;
        CancellationTokenSource onlineThumbnailCancellation;
        int onlinePage = 1;
        int onlinePages = 1;
        bool searchingOnline;
        readonly Border colorSwatch = new Border();
        readonly TextBlock info = new TextBlock();
        readonly TextBlock pointInfo = new TextBlock();
        Color selected = Color.FromArgb(255, 61, 150, 177);
        bool changingColor;
        bool dragging;
        bool recoveryChecked;
        readonly DispatcherTimer draftTimer = new DispatcherTimer {
            Interval = TimeSpan.FromSeconds(2)
        };
        static string RecoveryFile => System.IO.Path.Combine(
            SkinEditorDocument.LocalLibrary, ".blockcraft-recovery.png");
        const string OfficialArticle = "https://www.minecraft.net/en-us/article/what-is-minecraft-skin";

        sealed class SavedSkin
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public override string ToString() => Name;
        }

        static TextBlock Label(string title, double font = 12, bool strong = false)
        {
            var label = new TextBlock
            {
                Text = title, FontSize = font,
                FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty,
                strong ? "TextBrush" : "MutedBrush");
            return label;
        }

        static StackPanel Column(params UIElement[] children)
        {
            var p = new StackPanel();
            foreach (var child in children) p.Children.Add(child);
            return p;
        }

        static WrapPanel Row(params UIElement[] children)
        {
            var p = new WrapPanel { Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center };
            foreach (var child in children) p.Children.Add(child);
            return p;
        }

        static Grid AdaptivePanels(UIElement main, UIElement side, double sideWidth, double gap)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sideWidth) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(main);
            Grid.SetColumn(side, 2);
            grid.Children.Add(side);
            grid.SizeChanged += (sender, args) =>
            {
                if (!args.WidthChanged) return;
                bool stacked = args.NewSize.Width < sideWidth + 325;
                grid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : gap);
                grid.ColumnDefinitions[2].Width = new GridLength(stacked ? 0 : sideWidth);
                Grid.SetColumn(side, stacked ? 0 : 2);
                Grid.SetRow(side, stacked ? 1 : 0);
            };
            return grid;
        }

        static Border Card(UIElement content, bool search = false)
        {
            var border = new Border { Child = content };
            border.SetResourceReference(FrameworkElement.StyleProperty,
                search ? "SearchPanel" : "Panel");
            return border;
        }

        static Button ActionButton(string caption, Action action, bool accent = false)
        {
            var b = new Button { MinHeight = 34 };
            var label = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap };
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Button.Foreground))
                { Source = b, Mode = System.Windows.Data.BindingMode.OneWay });
            b.Content = label;
            System.Windows.Automation.AutomationProperties.SetName(b, caption);
            if (accent)
                b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
            b.Click += (sender, args) => action();
            return b;
        }

        static void StyleSearch(FrameworkElement control)
        {
            control.SetResourceReference(FrameworkElement.StyleProperty,
                control is TextBox ? "SearchTextBox" : "SearchComboBox");
        }

        public SkinStudioView(
            Func<byte[], bool, System.Threading.Tasks.Task> uploadToMicrosoft = null,
            Func<string> accountName = null,
            Action openEditor = null,
            Func<OnlineSkinEntry, DownloadJob> queueSkinDownload = null,
            Action openDownloads = null, bool enableDraftRecovery = true, Action openDownloadTasks = null)
        {
            this.uploadToMicrosoft = uploadToMicrosoft;
            this.accountName = accountName;
            this.openEditor = openEditor;
            this.openDownloads = openDownloads;
            this.queueSkinDownload = queueSkinDownload;
            this.openDownloadTasks = openDownloadTasks;
            this.enableDraftRecovery = enableDraftRecovery;
            document.Changed += () =>
            {
                RefreshImages();
                if (enableDraftRecovery && document.Dirty)
                {
                    draftTimer.Stop();
                    draftTimer.Start();
                }
            };
            draftTimer.Tick += (sender, args) =>
            {
                draftTimer.Stop();
                SaveRecoveryDraft();
            };
            Build();
            RefreshLibrary();
            RefreshImages();
            SetColor(selected);
            Loaded += (sender, args) =>
            {
                if (!downloadMode)
                {
                    Keyboard.Focus(paintingSurface);
                    OfferRecoveryOnce();
                }
            };
            Unloaded += (sender, args) =>
            {
                CancelOnlineRequests();
                WatchOnlineDownload(null);
                draftTimer.Stop();
                SaveRecoveryDraft();
            };
            PreviewKeyDown += OnShortcut;
        }

        void Build()
        {
            var page = Column(Card(Column(
                Label("皮肤编辑", 20, true),
                Label("绘制 Minecraft 皮肤，预览人物正反面，或导入已有 PNG。"),
                Row(ActionButton("导入 PNG", ImportPng),
                    ActionButton("保存到皮肤库", SaveToLibrary, true),
                    ActionButton("导出 PNG", ExportPng),
                    ActionButton("新建皮肤", ResetTemplate),
                    ActionButton("前往皮肤下载", () => openDownloads?.Invoke())))));
            // Keep the online catalog out of the DIY editing page.
            // Both navigation entries reuse this one instance so unsaved
            // edits and the selected search result survive page switches.

            var toolRow = new WrapPanel();
            tool.ItemsSource = new[] { "画笔", "橡皮擦", "取色器", "油漆桶" };
            tool.SelectedIndex = 0; tool.Width = 116;
            StyleSearch(tool);
            part.ItemsSource = new[] { "全部部位", "头部", "身体", "右臂", "左臂", "右腿", "左腿" };
            part.SelectedIndex = 0; part.Width = 112;
            StyleSearch(part);
            layer.ItemsSource = new[] { "基础层", "外层", "全部图层" };
            layer.SelectedIndex = 0; layer.Width = 116;
            StyleSearch(layer);
            AddTool("工具", tool);
            AddTool("部位", part);
            AddTool("图层", layer);
            void AddTool(string name, ComboBox control)
            {
                System.Windows.Automation.AutomationProperties.SetName(control, name);
                toolRow.Children.Add(Column(Label(name), control));
            }
            part.SelectionChanged += (sender, args) => DrawGuides();
            layer.SelectionChanged += (sender, args) => DrawGuides();
            var history = Row(ActionButton("撤销（Ctrl+Z）", () => { document.Undo(); }),
                ActionButton("重做（Ctrl+Y）", () => { document.Redo(); }),
                ActionButton("清除所选图层", ClearSelection));

            texture.Stretch = Stretch.Fill;
            RenderOptions.SetBitmapScalingMode(texture, BitmapScalingMode.NearestNeighbor);
            paintingSurface.HorizontalAlignment = HorizontalAlignment.Left;
            paintingSurface.VerticalAlignment = VerticalAlignment.Top;
            paintingSurface.Background = Brushes.Transparent;
            paintingSurface.Children.Add(texture);
            paintingSurface.Children.Add(guideCanvas);
            paintingSurface.MouseLeftButtonDown += OnMouseDown;
            paintingSurface.MouseLeftButtonUp += OnMouseUp;
            paintingSurface.MouseMove += OnMouseMove;
            paintingSurface.MouseRightButtonDown += OnPickRightClick;
            paintingSurface.LostMouseCapture += (sender, args) => EndStroke();

            zoom.Minimum = 4; zoom.Maximum = 12; zoom.Value = 6;
            zoom.TickFrequency = 1; zoom.IsSnapToTickEnabled = true;
            zoom.Width = 102; zoom.Margin = new Thickness(8, 0, 12, 8);
            zoom.ValueChanged += (sender, args) => ResizeEditor();
            brush.Minimum = 1; brush.Maximum = 5; brush.Value = 1;
            brush.TickFrequency = 1; brush.IsSnapToTickEnabled = true;
            brush.Width = 91; brush.Margin = new Thickness(8, 0, 12, 8);
            showGrid.Content = "像素网格"; showGrid.IsChecked = true;
            showGrid.Checked += (sender, args) => DrawGuides();
            showGrid.Unchecked += (sender, args) => DrawGuides();

            var viewport = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 413, Content = paintingSurface
            };
            viewport.SetResourceReference(Control.BackgroundProperty, "SearchSurfaceBrush");
            var editor = Card(Column(Label("像素画布", 16, true),
                toolRow, history,
                Row(Label("缩放"), zoom, Label("画笔大小"), brush, showGrid),
                viewport, pointInfo,
                Label("蓝线标记基础层，金线标记外层。拖动鼠标可连续绘制。")));

            var rightPanel = Column();
            armModel.ItemsSource = new[] { "经典宽臂 · Steve（4px）", "纤细手臂 · Alex（3px）" };
            armModel.SelectedIndex = 0;
            StyleSearch(armModel);
            armModel.SelectionChanged += (sender, args) =>
            {
                document.SetArmModel(armModel.SelectedIndex == 1);
                DrawGuides();
            };
            showOuter.Content = "显示帽子、外套及袖子";
            showOuter.IsChecked = true;
            showOuter.Checked += (sender, args) => RefreshImages();
            showOuter.Unchecked += (sender, args) => RefreshImages();
            rightPanel.Children.Add(Card(Column(Label("人物预览", 16, true),
                armModel, showOuter, BuildPreview())));

            hex.Width = 124; hex.MaxLength = 9;
            StyleSearch(hex);
            hex.ToolTip = "输入 #RRGGBB 或 #AARRGGBB；A 是透明度";
            hex.KeyDown += (sender, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    ApplyHexColor();
                    args.Handled = true;
                }
            };
            hex.LostFocus += (sender, args) => ApplyHexColor();
            colorSwatch.Width = 35; colorSwatch.Height = 35;
            colorSwatch.Margin = new Thickness(0, 0, 8, 8);
            colorSwatch.CornerRadius = new CornerRadius(7);
            colorSwatch.BorderThickness = new Thickness(1);
            colorSwatch.SetResourceReference(Border.BorderBrushProperty, "BorderBrushTheme");
            var colorSliders = Column();
            red.Minimum = green.Minimum = blue.Minimum = transparency.Minimum = 0;
            red.Maximum = green.Maximum = blue.Maximum = transparency.Maximum = 255;
            red.Value = selected.R; green.Value = selected.G;
            blue.Value = selected.B; transparency.Value = selected.A;
            red.Width = green.Width = blue.Width = transparency.Width = 142;
            foreach (var slider in new[] { red, green, blue, transparency })
            {
                slider.IsMoveToPointEnabled = true;
                slider.ValueChanged += (sender, args) => FromColorSliders();
            }
            colorSliders.Children.Add(Row(Label("R"), red));
            colorSliders.Children.Add(Row(Label("G"), green));
            colorSliders.Children.Add(Row(Label("B"), blue));
            colorSliders.Children.Add(Row(Label("不透明度"), transparency));
            rightPanel.Children.Add(Card(Column(Label("画笔颜色", 16, true),
                Row(colorSwatch, hex), colorSliders, BuildPalette())));

            var manage = Column(Label("皮肤库", 16, true));
            skinName.Text = "我的自定义皮肤";
            skinName.MaxLength = 48;
            StyleSearch(skinName);
            manage.Children.Add(Label("保存名称"));
            manage.Children.Add(skinName);
            StyleSearch(savedSkins);
            manage.Children.Add(Label("已保存的皮肤"));
            manage.Children.Add(savedSkins);
            manage.Children.Add(Row(
                ActionButton("打开选中皮肤", LoadFromLibrary),
                ActionButton("刷新列表", RefreshLibrary)));
            manage.Children.Add(ActionButton("上传到 Minecraft 账号", async () =>
                await UploadToMicrosoftAsync(), true));
            manage.Children.Add(ActionButton("查看官方皮肤说明", () =>
                Process.Start(new ProcessStartInfo(OfficialArticle) { UseShellExecute = true })));
            manage.Children.Add(Label("登录微软正版账号后可上传。编辑草稿会自动备份。"));
            rightPanel.Children.Add(Card(manage));
            rightPanel.Children.Add(Card(Column(Label("编辑状态", 16, true), info)));
            page.Children.Add(AdaptivePanels(editor, rightPanel, 288, 12));
            editorPage = page;
            downloadPage = BuildOnlineSearch();
            downloadPage.Visibility = Visibility.Collapsed;
            var sections = new Grid();
            sections.Children.Add(editorPage);
            sections.Children.Add(downloadPage);
            Content = sections;
            ResizeEditor();
        }

        internal void ShowDownloadPage(bool downloads)
        {
            downloadMode = downloads;
            if (editorPage != null)
                editorPage.Visibility = downloads ? Visibility.Collapsed : Visibility.Visible;
            if (downloadPage != null)
                downloadPage.Visibility = downloads ? Visibility.Visible : Visibility.Collapsed;
            if (downloads)
            {
                // Leave the unsaved DIY document intact; prevent CTRL+S
                // while a search TextBox or download result is focused.
                draftTimer.Stop();
                SaveRecoveryDraft();
                var item = onlineResults.SelectedItem as OnlineSkinEntry;
                WatchOnlineDownload(item);
                if (item != null && selectedOnlineTexture == null && onlinePreviewCancellation == null)
                    _ = PreviewOnlineSelectionAsync();
            }
            else
            {
                CancelOnlineRequests();
                WatchOnlineDownload(null);
                RefreshLibrary();
                OfferRecoveryOnce();
            }
        }


        // PCL-style information flow: filter bar -> image + metadata results ->
        // selected preview/detail -> download/apply, similar to mod search.
        UIElement BuildOnlineSearch()
        {
            var root = Column(Label("皮肤下载", 23, true),
                Row(ActionButton("打开皮肤库", OpenSkinLibrary),
                    ActionButton("皮肤 DIY", () => openEditor?.Invoke())));
            if (openDownloadTasks != null)
                ((WrapPanel)root.Children[1]).Children.Add(ActionButton("下载任务", openDownloadTasks));

            onlineSource.ItemsSource = new[] {
                "自由授权图库（Mosaicary）", "全部图库（多来源）",
                "社区图库（DP-Plugins）",
                "全部公开皮肤（授权未知需确认）", "玩家皮肤（Mojang）" };
            onlineSource.SelectedIndex = 1; // all supported galleries, by default
            onlineSource.Width = 186;
            StyleSearch(onlineSource);
            onlineQuery.MaxLength = 60;
            onlineQuery.Width = 226;
            onlineQuery.Text = "";
            onlineQuery.ToolTip = "输入皮肤名称、作者或关键词；留空可以浏览皮肤图库";
            StyleSearch(onlineQuery);
            onlineCategory.ItemsSource = new[] {
                "全部分类", "女生", "男生", "角色", "生物" };
            onlineCategory.SelectedIndex = 0;
            onlineCategory.Width = 124;
            StyleSearch(onlineCategory);
            onlineArms.ItemsSource = new[] { "全部模型", "Steve", "Alex" };
            onlineArms.SelectedIndex = 0;
            onlineArms.Width = 120;
            StyleSearch(onlineArms);
            onlineSort.ItemsSource = new[] { "最新发布", "最多下载", "最受欢迎" };
            onlineSort.SelectedIndex = 0;
            onlineSort.Width = 122;
            StyleSearch(onlineSort);
            onlineOpenOnly.Content = Label("仅显示开放许可");
            onlineOpenOnly.IsChecked = true;
            onlineOpenOnly.ToolTip = "勾选后只显示明确自由许可的作品；取消勾选可以查看其他公开皮肤，下载时会提示版权与使用限制";
            onlineSource.SelectionChanged += (sender, args) => UpdateOnlineFilters();
            onlineQuery.KeyDown += async (sender, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                await SearchOnlineAsync(1);
            };

            StackPanel Filter(string label, FrameworkElement control)
            {
                return new StackPanel
                {
                    Margin = new Thickness(0, 0, 12, 10),
                    Children = { Label(label), control }
                };
            }
            var filters = new WrapPanel { Orientation = Orientation.Horizontal };
            filters.Children.Add(Filter("名称 / 关键词", onlineQuery));
            filters.Children.Add(Filter("下载来源", onlineSource));
            filters.Children.Add(Filter("皮肤分类", onlineCategory));
            filters.Children.Add(Filter("手臂模型", onlineArms));
            filters.Children.Add(Filter("结果排序", onlineSort));
            onlineSearchButton = ActionButton("搜索 / 刷新", async () =>
                await SearchOnlineAsync(1), true);
            onlineSearchButton.MinWidth = 120;
            var reset = ActionButton("重置筛选", async () =>
            {
                onlineSource.SelectedIndex = 1;
                onlineCategory.SelectedIndex = 0;
                onlineArms.SelectedIndex = 0;
                onlineSort.SelectedIndex = 0;
                onlineQuery.Clear();
                onlineOpenOnly.IsChecked = true;
                await SearchOnlineAsync(1);
            });
            onlineCancelSearch = ActionButton("取消搜索", () => onlineSearchCancellation?.Cancel());
            onlineCancelSearch.Visibility = Visibility.Collapsed;
            var search = Column(filters, onlineOpenOnly, Row(onlineSearchButton, reset, onlineCancelSearch));
            root.Children.Add(Card(search, true));

            onlineResults.MinHeight = 280;
            onlineResults.Height = 398;
            onlineResults.ItemTemplate =
                (DataTemplate)Application.Current.FindResource("OnlineSkinResultTemplate");
            onlineResults.SelectionChanged += async (sender, args) =>
                await PreviewOnlineSelectionAsync();
            onlineResults.MouseDoubleClick += (sender, args) =>
            {
                if (onlineResults.SelectedItem != null) (onlineDetailPanel as FrameworkElement)?.BringIntoView();
            };

            onlinePageStatus.Text = "输入关键词搜索，或直接浏览图库。";
            onlinePageStatus.TextWrapping = TextWrapping.Wrap;
            onlinePageStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            onlinePrevButton = ActionButton("上一页", async () =>
                await SearchOnlineAsync(onlinePage - 1));
            onlineNextButton = ActionButton("下一页", async () =>
                await SearchOnlineAsync(onlinePage + 1));
            onlinePrevButton.IsEnabled = onlineNextButton.IsEnabled = false;
            var results = Column(Label("搜索结果", 16, true), onlinePageStatus,
                onlineResults, Row(onlinePrevButton, onlineNextButton));

            foreach (var image in new[] { onlinePreview, onlinePreviewBack })
            {
                image.Width = 81;
                image.Height = 171;
                image.Stretch = Stretch.Uniform;
                image.HorizontalAlignment = HorizontalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            }

            onlineSaveButton = ActionButton("下载到皮肤库", async () => await SaveOnlineSkinAsync(), true);
            onlineEditButton = ActionButton("导入 DIY", async () => await UseOnlineSkinAsync());
            onlineDetailButton = ActionButton("作者与许可证", OpenOnlineSource);
            onlineDetailButton.IsEnabled = false;
            onlinePreviewMode = ActionButton("2D 正反面", () =>
            {
                showFlatPreview = !showFlatPreview;
                RefreshPreviewMode();
            });
            onlineExpandPreview = ActionButton("放大预览", OpenExpandedPreview);
            onlineRetryPreview = ActionButton("重试预览", async () => await PreviewOnlineSelectionAsync());
            onlineRetryPreview.Visibility = Visibility.Collapsed;
            online3D.Height = double.NaN;
            onlineOuter.Content = Label("外层");
            onlineOuter.IsChecked = true;
            onlineOuter.Checked += (sender, args) => UpdateOnline3DOuter();
            onlineOuter.Unchecked += (sender, args) => UpdateOnline3DOuter();
            onlineOrbitControls = PreviewControls(online3D);

            var flat = new System.Windows.Controls.Primitives.UniformGrid
            {
                Columns = 2, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center
            };
            foreach (var panel in new[] {
                Column(Label("正面", 11, true), PreviewFrame(onlinePreview)),
                Column(Label("背面", 11, true), PreviewFrame(onlinePreviewBack)) })
            {
                panel.Margin = new Thickness(4);
                flat.Children.Add(panel);
            }
            onlineFlatPanel = flat;

            onlinePreviewMessage.HorizontalAlignment = HorizontalAlignment.Center;
            onlinePreviewMessage.VerticalAlignment = VerticalAlignment.Center;
            onlinePreviewMessage.TextAlignment = TextAlignment.Center;
            onlinePreviewMessage.Margin = new Thickness(12);
            var previewStage = new Grid { Height = 272 };
            previewStage.Children.Add(online3D);
            previewStage.Children.Add(onlineFlatPanel);
            previewStage.Children.Add(onlinePreviewMessage);
            var stageFrame = new Border
            {
                Child = previewStage, CornerRadius = new CornerRadius(9),
                Margin = new Thickness(0, 3, 0, 9)
            };
            stageFrame.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
            onlineModelInfo = Row(PreviewBadge(onlineSkinSource), PreviewBadge(onlineSkinModel));
            onlineDownloadProgress.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
            onlineDownloadFeedback = Column(onlineDownloadStatus, onlineDownloadProgress);
            var selected = Column(onlineSkinTitle, onlineModelInfo, stageFrame,
                Row(onlinePreviewMode, onlineExpandPreview, onlineOuter, onlineRetryPreview),
                onlineOrbitControls, onlineAttribution,
                Row(onlineSaveButton, onlineEditButton, onlineDetailButton), onlineDownloadFeedback);
            onlineDetailPanel = Card(selected);
            ((Border)onlineDetailPanel).SetResourceReference(StyleProperty, "FeaturePanel");
            root.Children.Add(AdaptivePanels(Card(results), onlineDetailPanel, 329, 14));

            onlineStatus.TextWrapping = TextWrapping.Wrap;
            onlineStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            search.Children.Add(onlineStatus);
            UpdateOnlineFilters();
            RefreshPreviewMode();
            RefreshOnlineActions();
            return root;
        }

        static Border PreviewBadge(TextBlock label)
        {
            label.Margin = new Thickness(0);
            label.SetResourceReference(TextBlock.ForegroundProperty, "AccentInkBrush");
            var badge = new Border { Child = label };
            badge.SetResourceReference(StyleProperty, "SkinMetaBadge");
            return badge;
        }

        void OpenSkinLibrary()
        {
            try
            {
                Directory.CreateDirectory(SkinEditorDocument.LocalLibrary);
                Process.Start(new ProcessStartInfo(SkinEditorDocument.LocalLibrary) { UseShellExecute = true });
            }
            catch (Exception error) { onlineStatus.Text = "无法打开皮肤库：" + error.GetBaseException().Message; }
        }

        void CancelOnlineRequests()
        {
            onlineSearchCancellation?.Cancel();
            onlinePreviewCancellation?.Cancel();
            onlineThumbnailCancellation?.Cancel();
        }

        void UpdateOnlineFilters()
        {
            bool catalog = onlineSource.SelectedIndex == 0 ||
                onlineSource.SelectedIndex == 1 || onlineSource.SelectedIndex == 3;
            bool player = onlineSource.SelectedIndex == 4;
            if (onlineSource.SelectedIndex == 3)
                onlineOpenOnly.IsChecked = false;
            onlineCategory.IsEnabled = onlineSort.IsEnabled = catalog;
            onlineArms.IsEnabled = catalog;
            onlineOpenOnly.IsEnabled = !player;
            if (player) onlineQuery.ToolTip = "输入 Java 版玩家名（如 jeb_）或 UUID";
            else onlineQuery.ToolTip = "可输入关键词；留空浏览公开皮肤";
            // Filter changes are applied on the next explicit Search click.
        }

        string MosaicaryCategory()
        {
            switch (onlineCategory.SelectedIndex)
            {
                case 1: return "girls";
                case 2: return "boys";
                case 3: return "characters";
                case 4: return "creatures";
                default: return "";
            }
        }

        string MosaicaryModel() =>
            onlineArms.SelectedIndex == 1 ? "classic" :
            onlineArms.SelectedIndex == 2 ? "slim" : "";
        string MosaicarySort() =>
            onlineSort.SelectedIndex == 1 ? "downloads" :
            onlineSort.SelectedIndex == 2 ? "popular" : "new";

        async Task SearchOnlineAsync(int page)
        {
            if (searchingOnline || page < 1 || page > 10000) return;
            string query = (onlineQuery.Text ?? "").Trim();
            int source = onlineSource.SelectedIndex;
            bool onlyFree = onlineOpenOnly.IsChecked == true;
            onlinePreviewCancellation?.Cancel();
            onlineThumbnailCancellation?.Cancel();
            onlineResults.ItemsSource = null;
            ClearOnlinePreview();

            using (var cancellation = new CancellationTokenSource())
            {
                onlineSearchCancellation = cancellation;
                var token = cancellation.Token;
                searchingOnline = true;
                onlineSearchButton.IsEnabled = onlineSource.IsEnabled = onlineQuery.IsEnabled = false;
                onlinePrevButton.IsEnabled = onlineNextButton.IsEnabled = false;
                onlineCancelSearch.Visibility = Visibility.Visible;
                onlinePageStatus.Text = "正在搜索皮肤…";
                onlineStatus.Text = "";
                try
                {
                    if (source == 4 && query.Length == 0)
                        throw new ArgumentException("请输入 Minecraft Java 玩家名或 UUID。");
                    var entries = new List<OnlineSkinEntry>();
                    var errors = new List<string>();
                    int maxPage = 1, total = 0;
                    bool includesGallery = source == 0 || source == 1 || source == 3;
                    bool includesCommunity = source == 2 || (source == 1 && page == 1);

                    async Task<T> SearchProvider<T>(Task<T> search, string provider) where T : class
                    {
                        try { return await search; }
                        catch (Exception error) when (source == 1 && !token.IsCancellationRequested)
                        {
                            errors.Add(provider + "：" + error.GetBaseException().Message);
                            return null;
                        }
                    }

                    // The two public galleries can answer independently.
                    var galleryTask = includesGallery ? SearchProvider(
                        OnlineSkinSearchService.BrowseMosaicaryAsync(query, page, MosaicaryCategory(),
                            MosaicaryModel(), MosaicarySort(), onlyFree, token, source == 3), "Mosaicary") :
                        Task.FromResult<OnlineSkinPage>(null);
                    var communityTask = includesCommunity ? SearchProvider(
                        OnlineSkinSearchService.SearchCommunityAsync(query, token), "DP-Plugins") :
                        Task.FromResult<IReadOnlyList<OnlineSkinEntry>>(null);
                    await Task.WhenAll(galleryTask, communityTask);
                    var gallery = await galleryTask;
                    var community = await communityTask;
                    if (gallery != null)
                    {
                        entries.AddRange(gallery.Entries);
                        maxPage = gallery.TotalPages;
                        total = gallery.Total;
                    }
                    if (community != null) entries.AddRange(community.Where(x => !onlyFree || x.OpenLicense));
                    if (source == 4)
                        entries.AddRange(await OnlineSkinSearchService.FindPlayerAsync(query, token));

                    token.ThrowIfCancellationRequested();
                    entries = entries.GroupBy(x => x.TextureUrl, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.First()).ToList();
                    if (entries.Count == 0 && errors.Count > 0 && total == 0)
                        throw new InvalidOperationException(string.Join("。", errors));
                    onlinePage = page;
                    onlinePages = maxPage;
                    onlineResults.ItemsSource = entries;
                    onlinePrevButton.IsEnabled = page > 1 && includesGallery;
                    onlineNextButton.IsEnabled = page < onlinePages && includesGallery;
                    onlinePageStatus.Text = entries.Count == 0 ? "没有符合筛选条件的皮肤" :
                        "第 " + page + " / " + maxPage + " 页 · " + entries.Count + " 个结果";
                    if (errors.Count > 0) onlineStatus.Text = "部分图库暂不可用。" + string.Join("。", errors);
                    if (entries.Count > 0)
                    {
                        onlineResults.SelectedIndex = 0;
                        onlineThumbnailCancellation = new CancellationTokenSource();
                        _ = LoadOnlineThumbnailsAsync(entries, onlineThumbnailCancellation);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { onlinePageStatus.Text = "已取消搜索"; }
                catch (Exception error)
                {
                    onlinePageStatus.Text = "搜索失败";
                    onlineStatus.Text = error.GetBaseException().Message;
                }
                finally
                {
                    onlineSearchCancellation = null;
                    searchingOnline = false;
                    onlineSearchButton.IsEnabled = onlineSource.IsEnabled = onlineQuery.IsEnabled = true;
                    onlineCancelSearch.Visibility = Visibility.Collapsed;
                }
            }
        }

        async Task LoadOnlineThumbnailsAsync(IEnumerable<OnlineSkinEntry> results, CancellationTokenSource cancellation)
        {
            using (cancellation)
            using (var limit = new SemaphoreSlim(3))
            {
                var token = cancellation.Token;
                try
                {
                    var tasks = results.Where(x => x.Source == "Mosaicary").Take(24).Select(async entry =>
                    {
                        bool acquired = false;
                        try
                        {
                            await limit.WaitAsync(token);
                            acquired = true;
                            var bytes = await OnlineSkinSearchService.DownloadThumbnailAsync(entry, token);
                            if (!token.IsCancellationRequested)
                                entry.Thumbnail = OnlineSkinSearchService.DecodeThumbnail(bytes);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception) { /* Individual thumbnails may be unavailable. */ }
                        finally { if (acquired) limit.Release(); }
                    });
                    await Task.WhenAll(tasks);
                }
                finally
                {
                    if (ReferenceEquals(onlineThumbnailCancellation, cancellation))
                        onlineThumbnailCancellation = null;
                }
            }
        }

        void OpenOnlineSource()
        {
            var item = onlineResults.SelectedItem as OnlineSkinEntry;
            if (item == null || !Uri.TryCreate(item.DetailUrl, UriKind.Absolute, out var url) ||
                url.Scheme != "https" || url.IsDefaultPort == false ||
                !(url.Host == "mosaicary.com" || url.Host == "dpplugins.com"))
                return;
            try
            {
                Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                onlineStatus.Text = "无法打开皮肤来源页面：" + ex.GetBaseException().Message;
            }
        }

        static string TemporarySkinFile(byte[] png)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "BlockCraft-Skin-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, png);
            return path;
        }

        static SkinEditorDocument DecodeOnlineSkin(byte[] png, bool slim)
        {
            string temporary = TemporarySkinFile(png);
            try
            {
                var preview = new SkinEditorDocument();
                preview.LoadPng(temporary, slim);
                return preview;
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
            }
        }

        void ClearOnlinePreview()
        {
            selectedOnlineSkin = null;
            selectedOnlinePng = null;
            selectedOnlineDocument = null;
            selectedOnlineTexture = null;
            onlinePreview.Source = onlinePreviewBack.Source = null;
            online3D.SetSkin(null, false, true);
            online3D.StopAutoRotate();
            onlineSkinTitle.Text = "选择一个皮肤";
            onlineModelInfo.Visibility = Visibility.Collapsed;
            onlineAttribution.Text = "";
            onlineDetailButton.IsEnabled = false;
            onlineRetryPreview.Visibility = Visibility.Collapsed;
            onlinePreviewMessage.Text = "从结果中选择皮肤查看预览";
            WatchOnlineDownload(null);
            RefreshPreviewMode();
        }

        async Task PreviewOnlineSelectionAsync()
        {
            var item = onlineResults.SelectedItem as OnlineSkinEntry;
            if (item != null && ReferenceEquals(item, selectedOnlineSkin) && selectedOnlineTexture != null) return;
            onlinePreviewCancellation?.Cancel();
            ClearOnlinePreview();
            if (item == null) return;
            PresentOnlineSkinDetails(item);
            onlinePreviewMessage.Text = "正在加载皮肤…";
            WatchOnlineDownload(item);

            using (var cancellation = new CancellationTokenSource())
            {
                onlinePreviewCancellation = cancellation;
                var token = cancellation.Token;
                try
                {
                    byte[] png = await OnlineSkinSearchService.PreviewPngAsync(item, token);
                    if (token.IsCancellationRequested || !ReferenceEquals(onlineResults.SelectedItem, item)) return;
                    ShowOnlinePreview(item, DecodeOnlineSkin(png, item.SlimArms), png);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error)
                {
                    if (!token.IsCancellationRequested && ReferenceEquals(onlineResults.SelectedItem, item))
                    {
                        onlinePreviewMessage.Text = "预览加载失败。" + error.GetBaseException().Message;
                        onlineRetryPreview.Visibility = Visibility.Visible;
                    }
                }
                finally
                {
                    if (ReferenceEquals(onlinePreviewCancellation, cancellation))
                        onlinePreviewCancellation = null;
                }
            }
        }

        void PresentOnlineSkinDetails(OnlineSkinEntry item)
        {
            onlineSkinTitle.Text = item.Name;
            onlineSkinSource.Text = item.Source;
            onlineSkinModel.Text = item.ModelLabel;
            onlineModelInfo.Visibility = Visibility.Visible;
            onlineAttribution.Text = item.AuthorLabel + "  ·  " + item.LicenseLabel;
            onlineDetailButton.IsEnabled = !string.IsNullOrWhiteSpace(item.DetailUrl);
        }

        void ShowOnlinePreview(OnlineSkinEntry item, SkinEditorDocument preview, byte[] png)
        {
            selectedOnlineSkin = item;
            selectedOnlinePng = png;
            selectedOnlineDocument = preview;
            selectedOnlineTexture = preview.TextureImage();
            UpdateOnline3DOuter();
            RefreshPreviewMode();
            RefreshOnlineActions();
        }

        void RefreshPreviewMode()
        {
            bool ready = selectedOnlineTexture != null;
            onlinePreviewMessage.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            onlineFlatPanel.Visibility = ready && showFlatPreview ? Visibility.Visible : Visibility.Collapsed;
            online3D.Visibility = ready && !showFlatPreview ? Visibility.Visible : Visibility.Collapsed;
            onlineOrbitControls.Visibility = online3D.Visibility;
            if (online3D.Visibility != Visibility.Visible) online3D.StopAutoRotate();
            ((TextBlock)onlinePreviewMode.Content).Text = showFlatPreview ? "切回 3D" : "2D 正反面";
            System.Windows.Automation.AutomationProperties.SetName(onlinePreviewMode,
                ((TextBlock)onlinePreviewMode.Content).Text);
            onlinePreviewMode.IsEnabled = onlineExpandPreview.IsEnabled = onlineOuter.IsEnabled = ready;
        }

        void OpenExpandedPreview()
        {
            if (selectedOnlineTexture == null || selectedOnlineSkin == null) return;
            var viewer = new Skin3DPreview { Height = double.NaN, Margin = new Thickness(14, 0, 14, 10) };
            viewer.SetSkin(selectedOnlineTexture, selectedOnlineSkin.SlimArms, onlineOuter.IsChecked != false);
            var area = SystemParameters.WorkArea;
            var dialog = new Window
            {
                Owner = Window.GetWindow(this), Title = selectedOnlineSkin.Name + " · 皮肤预览",
                Width = Math.Min(650, area.Width - 24), Height = Math.Min(580, area.Height - 24),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
            };
            var content = new DockPanel();
            var heading = Label(selectedOnlineSkin.Name, 18, true);
            heading.Margin = new Thickness(14, 12, 14, 8);
            DockPanel.SetDock(heading, Dock.Top);
            content.Children.Add(heading);
            var controls = Column(PreviewControls(viewer),
                Label("拖动旋转，聚焦后滚轮缩放。双击复位。"));
            controls.Margin = new Thickness(14, 0, 14, 6);
            DockPanel.SetDock(controls, Dock.Bottom);
            content.Children.Add(controls);
            content.Children.Add(viewer);
            dialog.Content = content;
            dialog.ShowDialog();
        }

        void WatchOnlineDownload(OnlineSkinEntry item)
        {
            if (watchedOnlineJob != null) watchedOnlineJob.PropertyChanged -= OnlineJobChanged;
            watchedOnlineJob = item != null && !string.IsNullOrEmpty(item.TextureUrl) &&
                onlineJobs.TryGetValue(item.TextureUrl, out var job) ? job : null;
            if (watchedOnlineJob != null) watchedOnlineJob.PropertyChanged += OnlineJobChanged;
            RefreshOnlineActions();
        }

        void OnlineJobChanged(object sender, PropertyChangedEventArgs args) => RefreshOnlineActions();

        void RefreshOnlineActions()
        {
            bool canDownload = selectedOnlineSkin?.CanDownload == true && selectedOnlinePng != null;
            bool running = watchedOnlineJob?.IsRunning == true;
            onlineSaveButton.IsEnabled = canDownload && !running && !savingOnline;
            onlineEditButton.IsEnabled = canDownload && !importingOnline;
            onlineDownloadFeedback.Visibility = watchedOnlineJob == null ? Visibility.Collapsed : Visibility.Visible;
            if (watchedOnlineJob == null) return;
            onlineDownloadProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            onlineDownloadProgress.IsIndeterminate = running && watchedOnlineJob.IsIndeterminate;
            onlineDownloadProgress.Value = watchedOnlineJob.Percent;
            string detail = !string.IsNullOrWhiteSpace(watchedOnlineJob.Error) ?
                watchedOnlineJob.Error : watchedOnlineJob.Detail;
            onlineDownloadStatus.Text = watchedOnlineJob.Status +
                (running ? " · " + watchedOnlineJob.PercentLabel : "") +
                (string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail);
        }

        string SafeOnlineName(OnlineSkinEntry item)
        {
            string name = Regex.Replace(item.Name ?? "", @"[\\/:*?""<>|\p{C}]", "-")
                .Trim().TrimEnd('.', ' ');
            if (name.Length > 25) name = name.Substring(0, 25);
            if (name.Length == 0) name = "Minecraft";
            return "在线-" + name + "-" +
                DateTime.Now.ToString("MMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        bool ConfirmUnlicensedSkin(OnlineSkinEntry item, string action)
        {
            if (item == null || !item.RequiresLicenseNotice) return true;
            var license = string.IsNullOrWhiteSpace(item.License) ?
                "未声明 / 未经核实" : item.License;
            return MessageBox.Show(Window.GetWindow(this),
                "皮肤：" + item.Name +
                "\n来源：" + item.Source +
                "\n作者：" + (string.IsNullOrWhiteSpace(item.Author) ?
                    "未知" : item.Author) +
                "\n许可证：" + license +
                "\n\n此公开 PNG 未确认获得自由使用、修改、上传或再分发授权。" +
                "下载不代表取得著作权许可。请遵守原作者及图库条款，" +
                "不要用于未经许可的商业、再分发或冒充原作者的用途。" +
                "\n\n仍要" + action + "这张皮肤吗？",
                "公开皮肤的授权提示",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        async Task SaveOnlineSkinAsync()
        {
            var item = selectedOnlineSkin;
            if (item == null || selectedOnlinePng == null || !item.CanDownload ||
                watchedOnlineJob?.IsRunning == true || savingOnline ||
                !ConfirmUnlicensedSkin(item, "下载")) return;
            if (queueSkinDownload != null)
            {
                var job = queueSkinDownload(item);
                if (job != null)
                {
                    if (onlineJobs.Count >= 64)
                        foreach (string old in onlineJobs.Where(pair => !pair.Value.IsRunning)
                            .Select(pair => pair.Key).Take(16).ToArray())
                            onlineJobs.Remove(old);
                    onlineJobs[item.TextureUrl] = job;
                    WatchOnlineDownload(item);
                }
                return;
            }
            savingOnline = true;
            RefreshOnlineActions();
            try
            {
                string path = await OnlineSkinSearchService.DownloadToLibraryAsync(item, CancellationToken.None);
                RefreshLibrary();
                onlineStatus.Text = "已下载到皮肤库：" + path;
            }
            catch (Exception error)
            { onlineStatus.Text = "无法保存皮肤：" + error.GetBaseException().Message; }
            finally
            {
                savingOnline = false;
                RefreshOnlineActions();
            }
        }

        async Task UseOnlineSkinAsync()
        {
            var item = selectedOnlineSkin;
            var png = selectedOnlinePng;
            if (item == null || png == null || !item.CanDownload ||
                importingOnline || !ConfirmUnlicensedSkin(item, "下载并编辑") || !ConfirmDiscard()) return;
            importingOnline = true;
            RefreshOnlineActions();
            string temporary = null;
            try
            {
                byte[] downloaded = await OnlineSkinSearchService.DownloadPngAsync(
                    item, CancellationToken.None);
                temporary = TemporarySkinFile(downloaded);
                document.LoadPng(temporary, item.SlimArms);
                editorSource = item;
                ClearRecoveryDraft();
                skinName.Text = SafeOnlineName(item);
                armModel.SelectedIndex = document.SlimArms ? 1 : 0;
                DrawGuides();
                RefreshImages();
                // A downloaded preview is not yet a saved local skin.
                document.MarkUnsaved();
                onlineStatus.Text = "已导入 DIY 编辑器。请点击“保存到皮肤库”或“导出 PNG”留存。";
                info.Text = "已载入在线皮肤：" + item.Name +
                    "\n来源：" + item.Attribution;
                openEditor?.Invoke();
            }
            catch (Exception ex)
            {
                onlineStatus.Text = "无法打开在线皮肤：" + ex.GetBaseException().Message;
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                }
                importingOnline = false;
                RefreshOnlineActions();
            }
        }

        void UpdateOnline3DOuter()
        {
            if (selectedOnlineTexture == null || selectedOnlineSkin == null || selectedOnlineDocument == null) return;
            bool outer = onlineOuter.IsChecked != false;
            online3D.SetSkin(selectedOnlineTexture, selectedOnlineSkin.SlimArms, outer);
            onlinePreview.Source = selectedOnlineDocument.AvatarImage(false, outer);
            onlinePreviewBack.Source = selectedOnlineDocument.AvatarImage(true, outer);
        }

        UIElement PreviewControls(Skin3DPreview viewer)
        {
            var spinButton = ActionButton("自动旋转", () => viewer.ToggleAutoRotate());
            viewer.AutoRotationChanged += running =>
                ((TextBlock)spinButton.Content).Text = running ? "暂停旋转" : "自动旋转";
            return Row(ActionButton("正面", viewer.ShowFront), ActionButton("背面", viewer.ShowBack),
                ActionButton("重置视角", viewer.ResetView), spinButton);
        }

        UIElement BuildPreview()
        {
            var leftPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var rightPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            leftPanel.Children.Add(Label("正面", 12, true));
            rightPanel.Children.Add(Label("背面", 12, true));
            foreach (var item in new[] { front, back })
            {
                item.Width = 75; item.Height = 151;
                item.Stretch = Stretch.Uniform;
                item.HorizontalAlignment = HorizontalAlignment.Center;
                RenderOptions.SetBitmapScalingMode(item, BitmapScalingMode.NearestNeighbor);
            }
            leftPanel.Children.Add(PreviewFrame(front));
            rightPanel.Children.Add(PreviewFrame(back));
            editor3D.Height = 314;
            return Column(
                Label("360° 立体预览 · 实时同步", 12, true),
                editor3D, PreviewControls(editor3D),
                Label("拖动旋转，聚焦后滚轮缩放。双击复位。"),
                Row(leftPanel, rightPanel));

        }

        static Border PreviewFrame(Image image)
        {
            var frame = new Border
            {
                Child = image, Padding = new Thickness(4),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 4, 10)
            };
            frame.SetResourceReference(Border.BackgroundProperty, "SearchSurfaceBrush");
            frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrushTheme");
            return frame;
        }

        UIElement BuildPalette()
        {
            var colors = new[]
            {
                "#222838", "#F2F6FA", "#A4B7D1", "#E3BA91", "#CB866C",
                "#86533E", "#CE4B55", "#EAA84E", "#F5DC68", "#54AA73",
                "#269CA5", "#3A77C2", "#6C57B0", "#D17EB7", "#3D444F"
            };
            var palette = new WrapPanel { Margin = new Thickness(0, 5, 0, 8) };
            foreach (var value in colors)
            {
                var c = (Color)ColorConverter.ConvertFromString(value);
                var button = new Button
                {
                    Width = 30, Height = 30, Background = new SolidColorBrush(c),
                    Content = "", ToolTip = value, Margin = new Thickness(0, 0, 6, 6),
                    BorderThickness = new Thickness(1)
                };
                button.Click += (sender, args) => SetColor(
                    Color.FromArgb((byte)transparency.Value, c.R, c.G, c.B));
                palette.Children.Add(button);
            }
            return palette;
        }

        void ResizeEditor()
        {
            if (paintingSurface == null) return;
            double size = Math.Max(256, Math.Min(768, 64 * zoom.Value));
            paintingSurface.Width = paintingSurface.Height = size;
            texture.Width = texture.Height = size;
            guideCanvas.Width = guideCanvas.Height = size;
            DrawGuides();
        }

        void DrawGuides()
        {
            if (guideCanvas == null || paintingSurface == null) return;
            guideCanvas.Children.Clear();
            double scale = paintingSurface.Width / 64.0;
            if (scale <= 0) return;
            if (showGrid.IsChecked == true)
            {
                var stroke = new SolidColorBrush(Color.FromArgb(75, 108, 135, 165));
                for (int i = 0; i <= 64; i++)
                {
                    double pos = i * scale;
                    guideCanvas.Children.Add(new Line {
                        X1 = pos, Y1 = 0, X2 = pos, Y2 = 64 * scale,
                        Stroke = stroke, StrokeThickness = 0.6
                    });
                    guideCanvas.Children.Add(new Line {
                        X1 = 0, Y1 = pos, X2 = 64 * scale, Y2 = pos,
                        Stroke = stroke, StrokeThickness = 0.6
                    });
                }
            }
            string activePart = Convert.ToString(part.SelectedItem) ?? "全部部位";
            string activeLayer = Convert.ToString(layer.SelectedItem) ?? "基础层";
            foreach (var region in SkinEditorDocument.Regions(document.SlimArms))
            {
                bool selectedPart = activePart == "全部部位" || region.Part == activePart;
                bool selectedLayer = activeLayer == "全部图层" ||
                    region.Overlay == (activeLayer == "外层");
                var outline = new Rectangle
                {
                    Width = (region.Width * 2 + region.Depth * 2) * scale,
                    Height = (region.Height + region.Depth) * scale,
                    Stroke = new SolidColorBrush(region.Overlay ?
                        Color.FromRgb(237, 169, 64) : Color.FromRgb(46, 149, 211)),
                    StrokeThickness = selectedPart && selectedLayer ? 2.2 : 1.0,
                    Opacity = selectedPart && selectedLayer ? 0.97 : 0.24,
                    IsHitTestVisible = false
                };
                if (region.Overlay) outline.StrokeDashArray = new DoubleCollection { 3, 2 };
                Canvas.SetLeft(outline, region.U * scale);
                Canvas.SetTop(outline, region.V * scale);
                guideCanvas.Children.Add(outline);
            }
        }

        void RefreshImages()
        {
            var atlas = document.TextureImage();
            texture.Source = atlas;
            editor3D.SetSkin(atlas, document.SlimArms, showOuter.IsChecked != false);
            front.Source = document.AvatarImage(false, showOuter.IsChecked != false);
            back.Source = document.AvatarImage(true, showOuter.IsChecked != false);
            info.Text = (document.Dirty ? "● 未保存的修改" : "✓ 已保存") +
                "\n皮肤规格：64 × 64 PNG" +
                "\n手臂模型：" + (document.SlimArms ? "Alex · 3px" : "Steve · 4px") +
                "\n编辑方式：仅本地处理，不上传账号";
        }

        bool PixelAt(MouseEventArgs e, out int x, out int y)
        {
            var pos = e.GetPosition(paintingSurface);
            double unit = paintingSurface.ActualWidth / 64d;
            x = unit <= 0 ? -1 : (int)Math.Floor(pos.X / unit);
            y = unit <= 0 ? -1 : (int)Math.Floor(pos.Y / unit);
            return x >= 0 && y >= 0 && x < 64 && y < 64;
        }

        string ActivePart => Convert.ToString(part.SelectedItem) ?? "全部部位";
        string ActiveLayer => Convert.ToString(layer.SelectedItem) ?? "基础层";
        string ActiveTool => Convert.ToString(tool.SelectedItem) ?? "画笔";

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!PixelAt(e, out int x, out int y)) return;
            if (ActiveTool == "取色器")
            {
                SetColor(document.Pixel(x, y));
            }
            else if (ActiveTool == "油漆桶")
            {
                document.Fill(x, y, selected, ActivePart, ActiveLayer);
            }
            else
            {
                document.BeginChange();
                dragging = true;
                paintingSurface.CaptureMouse();
                document.Draw(x, y, selected, (int)Math.Round(brush.Value),
                    ActiveTool == "橡皮擦", ActivePart, ActiveLayer);
            }
            e.Handled = true;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!PixelAt(e, out int x, out int y))
            {
                if (dragging && e.LeftButton != MouseButtonState.Pressed) EndStroke();
                return;
            }
            var color = document.Pixel(x, y);
            pointInfo.Text = "像素 (" + x + ", " + y + ") · " +
                HexColor(color) + " · " +
                (document.CanPaint(x, y, ActivePart, ActiveLayer)
                    ? "可绘制" : "选中部位/图层外");
            if (dragging)
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                    EndStroke();
                else
                    document.Draw(x, y, selected, (int)Math.Round(brush.Value),
                        ActiveTool == "橡皮擦", ActivePart, ActiveLayer);
            }
        }

        void OnPickRightClick(object sender, MouseButtonEventArgs e)
        {
            if (PixelAt(e, out int x, out int y))
                SetColor(document.Pixel(x, y));
            e.Handled = true;
        }

        void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            EndStroke(); e.Handled = true;
        }

        void EndStroke()
        {
            if (!dragging) return;
            dragging = false;
            document.EndChange();
            if (paintingSurface.IsMouseCaptured)
                paintingSurface.ReleaseMouseCapture();
        }

        static string HexColor(Color c)
        {
            return c.A == 255 ?
                "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2") :
                "#" + c.A.ToString("X2") + c.R.ToString("X2") +
                c.G.ToString("X2") + c.B.ToString("X2");
        }

        void SetColor(Color color)
        {
            changingColor = true;
            try
            {
                selected = color;
                red.Value = color.R; green.Value = color.G; blue.Value = color.B;
                transparency.Value = color.A;
                hex.Text = HexColor(color);
                colorSwatch.Background = new SolidColorBrush(color);
            }
            finally { changingColor = false; }
        }

        void FromColorSliders()
        {
            if (changingColor) return;
            SetColor(Color.FromArgb((byte)transparency.Value,
                (byte)red.Value, (byte)green.Value, (byte)blue.Value));
        }

        void ApplyHexColor()
        {
            if (changingColor) return;
            string input = (hex.Text ?? "").Trim();
            if (input.Length != 7 && input.Length != 9)
            {
                hex.Text = HexColor(selected);
                return;
            }
            if (input[0] != '#' ||
                !input.Skip(1).All(Uri.IsHexDigit))
            {
                hex.Text = HexColor(selected);
                return;
            }
            byte Read(int n) => byte.Parse(input.Substring(n, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            SetColor(input.Length == 9 ?
                Color.FromArgb(Read(1), Read(3), Read(5), Read(7)) :
                Color.FromArgb((byte)transparency.Value, Read(1), Read(3), Read(5)));
        }

        void ClearSelection()
        {
            if (MessageBox.Show(Window.GetWindow(this),
                "清除所选的“" + ActivePart + " / " + ActiveLayer + "”像素？\n" +
                "清除基础层可能使人物皮肤透明；可使用 Ctrl+Z 撤销。",
                "清除皮肤像素", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
                document.ClearSelected(ActivePart, ActiveLayer);
        }

        bool ConfirmDiscard()
        {
            return !document.Dirty ||
                MessageBox.Show(Window.GetWindow(this),
                    "当前皮肤有未保存的修改。是否舍弃这些修改？",
                    "未保存的皮肤", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        void ResetTemplate()
        {
            if (!ConfirmDiscard()) return;
            editorSource = null;
            document.ResetTemplate(armModel.SelectedIndex == 1);
            info.Text = "已新建原创建议配色皮肤，可修改任意颜色与图层。";
        }

        void ImportPng()
        {
            if (!ConfirmDiscard()) return;
            var picker = new OpenFileDialog
            {
                Filter = "Minecraft 皮肤 PNG (*.png)|*.png",
                CheckFileExists = true,
                Title = "导入 Minecraft 64×64 或旧版 64×32 皮肤"
            };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            OpenFile(picker.FileName);
        }

        void OpenFile(string path)
        {
            try
            {
                document.LoadPng(path, armModel.SelectedIndex == 1);
                editorSource = null;
                ClearRecoveryDraft();
                skinName.Text = System.IO.Path.GetFileNameWithoutExtension(path);
                armModel.SelectedIndex = document.SlimArms ? 1 : 0;
                DrawGuides();
                RefreshImages();
                info.Text = "已导入：" + System.IO.Path.GetFileName(path) +
                    "\n可以继续绘制，再导出为标准 64×64 PNG。";
            }
            catch (Exception error)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "无法导入皮肤：" + error.GetBaseException().Message,
                    "皮肤文件无效", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        readonly bool enableDraftRecovery;

        void SaveRecoveryDraft()
        {
            if (!enableDraftRecovery || !document.Dirty) return;
            string destination = RecoveryFile;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(SkinEditorDocument.LocalLibrary);
                File.WriteAllBytes(temporary, document.ExportPngBytes());
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            catch (Exception ex)
            {
                info.Text = "恢复草稿写入失败，请手动保存皮肤：" + ex.GetBaseException().Message;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }

        static void ClearRecoveryDraft()
        {
            try { if (File.Exists(RecoveryFile)) File.Delete(RecoveryFile); }
            catch (IOException) { /* Recovery is best-effort. */ }
        }

        void OfferRecoveryOnce()
        {
            if (!enableDraftRecovery || recoveryChecked) return;
            recoveryChecked = true;
            try
            {
                if (!File.Exists(RecoveryFile)) return;
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(RecoveryFile);
                if (age < TimeSpan.Zero || age > TimeSpan.FromDays(14))
                {
                    ClearRecoveryDraft();
                    return;
                }
                if (MessageBox.Show(Window.GetWindow(this),
                    "发现上次未手动保存的 Minecraft 皮肤草稿。"
                    + "\n是否恢复上次的编辑内容？",
                    "恢复皮肤 DIY 草稿",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    document.LoadPng(RecoveryFile, document.SlimArms);
                    info.Text = "已恢复上次未保存的皮肤草稿。"
                        + "请点击“保存到皮肤库”或“导出 PNG”留存。";
                    // Recovered content is not an explicitly saved skin.
                    document.MarkUnsaved();
                }
                else ClearRecoveryDraft();
            }
            catch (Exception ex)
            {
                info.Text = "恢复草稿不可用：" + ex.GetBaseException().Message;
            }
        }

        void SaveSafely(string path)
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidDataException("皮肤保存路径无效");
            Directory.CreateDirectory(directory);
            string temporary = System.IO.Path.Combine(directory,
                ".blockcraft-skin-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                document.SavePng(temporary);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch
            {
                // The PNG was only written to staging, not committed to the
                // user's chosen location. Keep the document unsaved and
                // eligible for an automatic recovery draft.
                document.MarkUnsaved();
                throw;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
            ClearRecoveryDraft();
            RefreshImages();
        }

        void SaveToLibrary()
        {
            try
            {
                string path = SkinEditorDocument.SafeLibraryPath(skinName.Text);
                if (File.Exists(path) &&
                    MessageBox.Show(Window.GetWindow(this),
                        "皮肤库已有“" + skinName.Text + "”。是否覆盖旧文件？",
                        "确认覆盖皮肤", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                SaveSafely(path);
                if (editorSource != null)
                    File.WriteAllText(path + ".source.txt",
                        OnlineSkinSearchService.SkinSourceNote(editorSource),
                        System.Text.Encoding.UTF8);
                RefreshLibrary();
                info.Text = "已保存到 BlockCraft 本地皮肤库：\n" + path;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "保存失败：" + ex.GetBaseException().Message,
                    "皮肤库写入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void ExportPng()
        {
            var picker = new SaveFileDialog
            {
                Filter = "Minecraft Java 皮肤 PNG (*.png)|*.png",
                DefaultExt = ".png", AddExtension = true,
                FileName = (skinName.Text ?? "BlockCraft-Skin") + ".png",
                OverwritePrompt = true,
                Title = "导出标准 Minecraft 64×64 PNG"
            };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                SaveSafely(picker.FileName);
                if (editorSource != null)
                    File.WriteAllText(picker.FileName + ".source.txt",
                        OnlineSkinSearchService.SkinSourceNote(editorSource),
                        System.Text.Encoding.UTF8);
                info.Text = "已导出标准 64×64 PNG：\n" + picker.FileName +
                    "\n正版账号可在 Minecraft 官方启动器上传。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this),
                    "导出失败：" + ex.GetBaseException().Message,
                    "保存皮肤 PNG 失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        async System.Threading.Tasks.Task UploadToMicrosoftAsync()
        {
            if (uploading) return;
            if (uploadToMicrosoft == null ||
                string.IsNullOrWhiteSpace(accountName?.Invoke()))
            {
                MessageBox.Show(Window.GetWindow(this),
                    "请先在启动器的账户区域完成 Minecraft Java 微软正版登录。"
                    + "离线用户可继续使用皮肤库或导出 PNG，无法向微软账号同步皮肤。",
                    "上传正版皮肤需要登录", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            string username = accountName();
            bool slim = document.SlimArms;
            if (MessageBox.Show(Window.GetWindow(this),
                "确定将当前皮肤上传到 Minecraft Java 正版账号 “" +
                username + "” 吗？\n\n" +
                "上传成功后，官方服务会修改此账号正在使用的皮肤（" +
                (slim ? "Alex 纤细手臂" : "Steve 经典宽臂") + "）。\n" +
                "仅向 api.minecraftservices.com 上传当前 PNG，"
                + "不会同步给第三方，也不会保存你的登录令牌。",
                "确认修改微软正版皮肤", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            uploading = true;
            info.Text = "正在向 Minecraft 官方皮肤服务上传，请勿重复点击...";
            try
            {
                byte[] png = document.ExportPngBytes();
                await uploadToMicrosoft(png, slim);
                info.Text = "Minecraft 官方服务器已确认上传成功：\n" +
                    username + " · " + (slim ? "Alex" : "Steve") +
                    "\n如果游戏中的旧皮肤仍未刷新，可以退出世界后重新进入。";
            }
            catch (Exception ex)
            {
                info.Text = "正版皮肤上传失败，当前本地 PNG 和编辑内容仍保留。";
                MessageBox.Show(Window.GetWindow(this),
                    ex.GetBaseException().Message,
                    "无法上传 Minecraft 正版皮肤",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { uploading = false; }
        }

        void RefreshLibrary()
        {
            try
            {
                Directory.CreateDirectory(SkinEditorDocument.LocalLibrary);
                var items = Directory.GetFiles(SkinEditorDocument.LocalLibrary, "*.png",
                        SearchOption.TopDirectoryOnly)
                    .Where(path => !System.IO.Path.GetFileName(path).StartsWith(
                        ".blockcraft-", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(250)
                    .Select(path => new SavedSkin {
                        Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        Path = path }).ToList();
                savedSkins.ItemsSource = items;
                if (items.Count > 0) savedSkins.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                info.Text = "无法读取本地皮肤库：" + ex.GetBaseException().Message;
            }
        }

        void LoadFromLibrary()
        {
            if (!(savedSkins.SelectedItem is SavedSkin selectedSkin)) return;
            if (!ConfirmDiscard()) return;
            // Refuse symlinks/reparse points for libraries where users expect
            // ordinary local PNGs; the PNG decoder still validates contents.
            try
            {
                var attributes = File.GetAttributes(selectedSkin.Path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("皮肤库不支持通过符号链接打开皮肤");
                OpenFile(selectedSkin.Path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), ex.GetBaseException().Message,
                    "无法打开本地皮肤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void OnShortcut(object sender, KeyEventArgs e)
        {
            if (downloadMode || (Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
                e.OriginalSource is TextBox) return;
            if (e.Key == Key.Z) { document.Undo(); e.Handled = true; }
            else if (e.Key == Key.Y) { document.Redo(); e.Handled = true; }
            else if (e.Key == Key.S) { SaveToLibrary(); e.Handled = true; }
        }

        /// <summary>
        /// Render a real WPF 3D preview of the built-in offline cyan skin
        /// after the UI is laid out. Mesh-only tests cannot detect a model
        /// which is technically present but transparent on the screen.
        /// </summary>
        internal void Verify3DRenderedPixels()
        {
            if (downloadMode || editor3D.ActualWidth < 150 ||
                editor3D.ActualHeight < 150)
                throw new InvalidOperationException("皮肤 3D 视图未正确布局");
            int width = (int)Math.Round(editor3D.ActualWidth);
            int height = (int)Math.Round(editor3D.ActualHeight);
            var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, PixelFormats.Pbgra32);
            shot.Render(editor3D);
            var pixels = new byte[width * height * 4];
            shot.CopyPixels(pixels, width * 4, 0);
            int teal = 0;
            for (int y = height / 4; y < height * 3 / 4; y++)
                for (int x = width / 5; x < width * 4 / 5; x++)
                {
                    int i = (y * width + x) * 4;
                    int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    // Original locally generated default skin has a strong
                    // cyan sweatshirt, unlike the neutral preview background.
                    if (pixels[i + 3] > 180 && g > r + 24 && b > r + 28)
                        teal++;
                }
            if (teal < 160)
                throw new InvalidOperationException(
                    "3D 皮肤模型未正确显示（胸前衣服颜色像素过少：" + teal +
                    "）。请检查 WPF 材质与 UV，而非仅检查三角面数量。");
        }

        internal void VerifyOnlinePreviewLayout(Action capture)
        {
            var fixture = new OnlineSkinEntry
            {
                Name = "经典探险家", Source = "Mosaicary", Author = "测试画师", License = "CC0-1.0",
                TextureUrl = "https://mosaicary.com/api/skins/test"
            };
            var preview = new SkinEditorDocument();
            PresentOnlineSkinDetails(fixture);
            try
            {
                ShowOnlinePreview(fixture, preview, preview.ExportPngBytes());
                if (online3D.Visibility != Visibility.Visible || online3D.PartCount != 12 ||
                    !onlineSaveButton.IsEnabled || !onlineExpandPreview.IsEnabled)
                    throw new InvalidOperationException("皮肤下载预览与操作没有就绪");
                online3D.ShowBack();
                if (online3D.Yaw != 180 || online3D.Pitch != 0)
                    throw new InvalidOperationException("背面视角预设未生效");
                online3D.ShowFront();
                if (online3D.Yaw != 0)
                    throw new InvalidOperationException("正面视角预设未生效");
                onlinePreviewMode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (onlineFlatPanel.Visibility != Visibility.Visible ||
                    online3D.Visibility != Visibility.Collapsed ||
                    onlinePreview.Source == null || onlinePreviewBack.Source == null)
                    throw new InvalidOperationException("皮肤正反面预览未显示");
                onlineOuter.IsChecked = false;
                if (online3D.PartCount != 6)
                    throw new InvalidOperationException("预览的外层开关未更新 3D 模型");
                onlineOuter.IsChecked = true;
                onlinePreviewMode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                online3D.ResetView();
                capture();
            }
            finally { ClearOnlinePreview(); }
        }

        internal void VerifyOfflineLayout()
        {
            if (texture.Source == null || front.Source == null || back.Source == null ||
                guideCanvas == null || tool.SelectedIndex != 0 ||
                part.SelectedIndex != 0 || layer.SelectedIndex != 0 ||
                savedSkins == null || hex == null ||
                onlineResults == null || onlineSource.SelectedIndex != 1 ||
                onlineSearchButton == null || onlineSaveButton == null ||
                editorPage == null || downloadPage == null ||
                editorPage.Visibility != (downloadMode ? Visibility.Collapsed : Visibility.Visible) ||
                downloadPage.Visibility != (downloadMode ? Visibility.Visible : Visibility.Collapsed) ||
                document.TextureImage().PixelWidth != 64 ||
                editor3D.PartCount != 12 || online3D == null ||
                onlineOuter.IsChecked != true)
                throw new InvalidOperationException("皮肤 DIY 工具、贴图、图层或 360° 预览窗口未初始化");
            Skin3DPreview.VerifyGeometry();
            double originalYaw = editor3D.Yaw;
            editor3D.RotateBy(150, 1000);
            if (Math.Abs(editor3D.Yaw - originalYaw) < 1 ||
                editor3D.Pitch != 42)
                throw new InvalidOperationException("皮肤 360° 旋转或垂直角度限制失效");
            editor3D.ZoomByWheelDelta(12000);
            if (editor3D.CameraDistance != 43)
                throw new InvalidOperationException("皮肤 360° 视角缩放限制失效");
            editor3D.ResetView();
            if (Math.Abs(editor3D.Yaw + 28) > 0.01 ||
                editor3D.CameraDistance != 54)
                throw new InvalidOperationException("皮肤 360° 重置视角失效");
            // Outer transparency must never mask the underlying body.
            if (editor3D.VisibleOuterQuads <= 0)
                throw new InvalidOperationException("皮肤模板外层像素未参与 3D 模型");
            editor3D.SetSkin(document.TextureImage(), document.SlimArms, false);
            if (editor3D.PartCount != 6 || editor3D.VisibleOuterQuads != 0)
                throw new InvalidOperationException("关闭皮肤外层后仍然渲染透明面");
            editor3D.SetSkin(document.TextureImage(), document.SlimArms, true);
            if (!editor3D.ToggleAutoRotate() || !editor3D.IsAutoRotating)
                throw new InvalidOperationException("皮肤自动旋转无法开启");
            editor3D.ResetView();
            if (editor3D.IsAutoRotating)
                throw new InvalidOperationException("重置视角未停止自动旋转");
            int before = guideCanvas.Children.Count;
            layer.SelectedIndex = 1;
            if (guideCanvas.Children.Count < 12)
                throw new InvalidOperationException("皮肤第二层 UV 引导丢失");
            layer.SelectedIndex = 0;
            var prior = document.Pixel(10, 11);
            document.BeginChange();
            document.Draw(10, 11, Colors.Blue, 1, false, "头部", "基础层");
            document.EndChange();
            if (!document.Undo() || document.Pixel(10, 11) != prior ||
                front.Source == null || before < 12)
                throw new InvalidOperationException("皮肤 DIY WPF 画笔、撤销或预览绑定失效");
        }
    }
}
