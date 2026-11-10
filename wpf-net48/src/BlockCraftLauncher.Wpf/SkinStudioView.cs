using System;
using System.Collections.Generic;
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
        bool uploading;
        readonly Image texture = new Image();
        readonly Image front = new Image();
        readonly Image back = new Image();
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
        readonly TextBox onlineQuery = new TextBox();
        readonly ListBox onlineResults = new ListBox();
        readonly Image onlinePreview = new Image();
        readonly TextBlock onlineStatus = new TextBlock();
        readonly TextBlock onlineAttribution = new TextBlock();
        Button onlineSearchButton;
        Button onlineSaveButton;
        Button onlineEditButton;
        OnlineSkinEntry selectedOnlineSkin;
        byte[] selectedOnlinePng;
        int onlineRequestNumber;
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
            var label = new TextBlock { Text = caption };
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
            Func<string> accountName = null)
        {
            this.uploadToMicrosoft = uploadToMicrosoft;
            this.accountName = accountName;
            document.Changed += () =>
            {
                RefreshImages();
                if (document.Dirty)
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
                Keyboard.Focus(paintingSurface);
                OfferRecoveryOnce();
            };
            Unloaded += (sender, args) =>
            {
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
                    ActionButton("新建皮肤", ResetTemplate)))));
            page.Children.Add(BuildOnlineSearch());

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(249) });

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
            columns.Children.Add(editor);

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
            Grid.SetColumn(rightPanel, 2);
            columns.Children.Add(rightPanel);
            page.Children.Add(columns);
            Content = page;
            ResizeEditor();
        }


        // PCL-style information flow: filter bar -> image + metadata results ->
        // selected preview/detail -> download/apply, similar to mod search.
        UIElement BuildOnlineSearch()
        {
            var root = Column(Label("皮肤下载中心", 20, true),
                Label("浏览开放许可皮肤图库、筛选作者与分类、下载 PNG 或进入 DIY 编辑器。" +
                    "公开可下载不代表开源，许可证信息以作者实际声明为准。"));

            onlineSource.ItemsSource = new[] {
                "自由授权图库（Mosaicary）", "全部图库（多来源）",
                "社区图库（DP-Plugins）", "玩家皮肤（Mojang）" };
            onlineSource.SelectedIndex = 0;
            onlineSource.Width = 186;
            StyleSearch(onlineSource);
            onlineQuery.MaxLength = 60;
            onlineQuery.Width = 242;
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
            onlineOpenOnly.Content = "仅显示开放许可（CC0 / CC BY / CC BY-SA 等）";
            onlineOpenOnly.IsChecked = true;
            onlineOpenOnly.ToolTip = "勾选后隐藏未声明授权、保留所有权和限制商业使用的作品";
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
                onlineSource.SelectedIndex = 0;
                onlineCategory.SelectedIndex = 0;
                onlineArms.SelectedIndex = 0;
                onlineSort.SelectedIndex = 0;
                onlineQuery.Clear();
                onlineOpenOnly.IsChecked = true;
                await SearchOnlineAsync(1);
            });
            var search = Column(Label("搜索皮肤", 16, true), filters, onlineOpenOnly,
                Row(onlineSearchButton, reset),
                Label("开放许可图库可逐页浏览；社区和玩家来源需遵守原作者的授权范围。"));
            root.Children.Add(Card(search, true));

            onlineResults.MinHeight = 300;
            onlineResults.Height = 430;
            onlineResults.ItemTemplate =
                (DataTemplate)Application.Current.FindResource("OnlineSkinResultTemplate");
            onlineResults.SelectionChanged += async (sender, args) =>
                await PreviewOnlineSelectionAsync();
            onlineResults.MouseDoubleClick += async (sender, args) =>
                await PreviewOnlineSelectionAsync();

            onlinePageStatus.Text = "请选择来源并搜索。";
            onlinePageStatus.TextWrapping = TextWrapping.Wrap;
            onlinePageStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            onlinePrevButton = ActionButton("上一页", async () =>
                await SearchOnlineAsync(onlinePage - 1));
            onlineNextButton = ActionButton("下一页", async () =>
                await SearchOnlineAsync(onlinePage + 1));
            onlinePrevButton.IsEnabled = onlineNextButton.IsEnabled = false;
            var results = Column(Label("搜索结果", 16, true), onlinePageStatus,
                onlineResults, Row(onlinePrevButton, onlineNextButton));

            onlinePreview.Width = 106;
            onlinePreview.Height = 205;
            onlinePreview.Stretch = Stretch.Uniform;
            onlinePreview.HorizontalAlignment = HorizontalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(onlinePreview, BitmapScalingMode.NearestNeighbor);
            onlineAttribution.Text = "选择一个皮肤，可以在这里查看授权、作者和模型。";
            onlineAttribution.TextWrapping = TextWrapping.Wrap;
            onlineAttribution.Margin = new Thickness(0, 10, 0, 10);
            onlineAttribution.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            onlineSaveButton = ActionButton("下载到皮肤库", async () =>
                await SaveOnlineSkinAsync(), true);
            onlineEditButton = ActionButton("使用并编辑", async () =>
                await UseOnlineSkinAsync());
            onlineSaveButton.IsEnabled = onlineEditButton.IsEnabled = false;
            onlineDetailButton = ActionButton("查看原作者与许可证", OpenOnlineSource);
            onlineDetailButton.IsEnabled = false;
            var selected = Column(Label("皮肤详情", 16, true),
                onlinePreview, onlineAttribution, onlineSaveButton,
                onlineEditButton, onlineDetailButton);
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(247) });
            layout.Children.Add(Card(results));
            var details = Card(selected);
            Grid.SetColumn(details, 2);
            layout.Children.Add(details);
            root.Children.Add(layout);

            onlineStatus.Text = "图片缩略图按需获取，不会自动批量下载皮肤；联网失败不影响本地 DIY。";
            onlineStatus.TextWrapping = TextWrapping.Wrap;
            onlineStatus.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            root.Children.Add(onlineStatus);
            root.Children.Add(Label("图库来源：Mosaicary（mosaicary.com）、DP-Plugins。二者独立运营；" +
                "部分皮肤可能带有署名或相同方式共享要求。"));
            UpdateOnlineFilters();
            Loaded += async (sender, args) =>
            {
                if (onlineAutoLoaded) return;
                onlineAutoLoaded = true;
                await SearchOnlineAsync(1);
            };
            return root;
        }

        void UpdateOnlineFilters()
        {
            bool catalog = onlineSource.SelectedIndex == 0 || onlineSource.SelectedIndex == 1;
            bool player = onlineSource.SelectedIndex == 3;
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
            ++onlineRequestNumber;
            onlinePreviewCancellation?.Cancel();
            onlineThumbnailCancellation?.Cancel();
            onlineThumbnailCancellation?.Dispose();
            onlineThumbnailCancellation = new CancellationTokenSource();
            selectedOnlineSkin = null;
            selectedOnlinePng = null;
            onlineSaveButton.IsEnabled = onlineEditButton.IsEnabled = false;
            onlineDetailButton.IsEnabled = false;
            onlinePreview.Source = null;
            onlineResults.ItemsSource = null;
            onlineAttribution.Text = "";
            searchingOnline = true;
            onlineSearchButton.IsEnabled = false;
            onlineSource.IsEnabled = false;
            onlineQuery.IsEnabled = false;
            onlinePrevButton.IsEnabled = onlineNextButton.IsEnabled = false;
            onlineStatus.Text = "正在搜索在线皮肤（会保留原作者许可信息）...";
            try
            {
                if (source == 3 && query.Length == 0)
                    throw new ArgumentException("玩家搜索需要输入 Minecraft Java 玩家名或 UUID");
                var entries = new List<OnlineSkinEntry>();
                int maxPage = 1;
                int total = 0;
                var errors = new List<string>();
                if (source == 0 || source == 1)
                {
                    try
                    {
                        var gallery = await OnlineSkinSearchService.BrowseMosaicaryAsync(
                            query, page, MosaicaryCategory(), MosaicaryModel(),
                            MosaicarySort(), onlyFree, CancellationToken.None);
                        entries.AddRange(gallery.Entries);
                        maxPage = gallery.TotalPages;
                        total = gallery.Total;
                    }
                    catch (Exception ex)
                    {
                        if (source == 0) throw;
                        errors.Add("Mosaicary：" + ex.GetBaseException().Message);
                    }
                }
                if (source == 2 || (source == 1 && page == 1))
                {
                    try
                    {
                        var community = await OnlineSkinSearchService.SearchCommunityAsync(
                            query, CancellationToken.None);
                        entries.AddRange(community.Where(x => !onlyFree || x.OpenLicense));
                    }
                    catch (Exception ex)
                    {
                        if (source == 2) throw;
                        errors.Add("DP-Plugins：" + ex.GetBaseException().Message);
                    }
                }
                if (source == 3)
                    entries.AddRange(await OnlineSkinSearchService.FindPlayerAsync(
                        query, CancellationToken.None));

                // No duplicate PNGs within a mixed-source results page.
                entries = entries.GroupBy(x => x.TextureUrl, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First()).ToList();
                if (entries.Count == 0 && errors.Count > 0 && total == 0)
                    throw new InvalidOperationException(string.Join("；", errors));
                onlinePage = page;
                onlinePages = maxPage;
                onlineResults.ItemsSource = entries;
                onlinePrevButton.IsEnabled = page > 1 && (source == 0 || source == 1);
                onlineNextButton.IsEnabled = page < maxPage && (source == 0 || source == 1);
                onlinePageStatus.Text = "第 " + page + " / " + maxPage +
                    " 页 · 本页展示 " + entries.Count + " 项" +
                    (total > 0 ? " · 图库共有 " + total + " 项（筛选前）" : "") +
                    (errors.Count > 0 ? " · 部分来源失败：" + string.Join("；", errors) : "");
                onlineStatus.Text = entries.Count == 0 ?
                    "当前页没有符合授权条件的皮肤。可切换来源或搜索词，或翻到其他页。" :
                    "选择皮肤查看人物预览与作者许可证；开放授权作品可下载到本地皮肤库。";
                if (entries.Count > 0)
                {
                    onlineResults.SelectedIndex = 0;
                    _ = LoadOnlineThumbnailsAsync(entries, onlineThumbnailCancellation.Token);
                }
            }
            catch (Exception ex)
            {
                onlinePageStatus.Text = "搜索失败";
                onlineStatus.Text = "在线搜索失败：" + ex.GetBaseException().Message +
                    "。可稍后重试，或继续使用离线 DIY。";
            }
            finally
            {
                searchingOnline = false;
                onlineSearchButton.IsEnabled = true;
                onlineSource.IsEnabled = true;
                onlineQuery.IsEnabled = true;
            }
        }

        async Task LoadOnlineThumbnailsAsync(IEnumerable<OnlineSkinEntry> results,
            CancellationToken token)
        {
            using (var limit = new SemaphoreSlim(3))
            {
                var tasks = results.Where(x => x.Source == "Mosaicary")
                    .Take(24).Select(async entry =>
                {
                    bool acquired = false;
                    try
                    {
                        await limit.WaitAsync(token);
                        acquired = true;
                        var image = await OnlineSkinSearchService.DownloadThumbnailAsync(entry, token);
                        if (!token.IsCancellationRequested) entry.Thumbnail = image;
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception) { /* A broken thumbnail must not break results. */ }
                    finally { if (acquired) limit.Release(); }
                });
                await Task.WhenAll(tasks);
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

        async Task PreviewOnlineSelectionAsync()
        {
            int request = ++onlineRequestNumber;
            var item = onlineResults.SelectedItem as OnlineSkinEntry;
            selectedOnlineSkin = null;
            selectedOnlinePng = null;
            onlineSaveButton.IsEnabled = onlineEditButton.IsEnabled = false;
            onlinePreview.Source = null;
            if (item == null) return;
            onlineAttribution.Text = item.Name + "\n" + item.Attribution;
            onlineStatus.Text = "正在下载皮肤预览（只下载公开 PNG）...";
            try
            {
                byte[] png = await OnlineSkinSearchService.DownloadPngAsync(
                    item, CancellationToken.None);
                if (request != onlineRequestNumber ||
                    !ReferenceEquals(onlineResults.SelectedItem, item)) return;
                var preview = DecodeOnlineSkin(png, item.SlimArms);
                onlinePreview.Source = preview.AvatarImage(false, true);
                selectedOnlineSkin = item;
                selectedOnlinePng = png;
                onlineSaveButton.IsEnabled = onlineEditButton.IsEnabled = true;
                onlineStatus.Text = "预览成功。可以下载到本地皮肤库，也可以加载到现有 DIY 编辑器。";
            }
            catch (Exception ex)
            {
                if (request == onlineRequestNumber)
                    onlineStatus.Text = "无法预览该皮肤：" +
                        ex.GetBaseException().Message + "。请选择其他皮肤。";
            }
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

        async Task SaveOnlineSkinAsync()
        {
            var item = selectedOnlineSkin;
            var png = selectedOnlinePng;
            if (item == null || png == null) return;
            onlineSaveButton.IsEnabled = false;
            try
            {
                var preview = DecodeOnlineSkin(png, item.SlimArms);
                Directory.CreateDirectory(SkinEditorDocument.LocalLibrary);
                string name = SafeOnlineName(item);
                string path = SkinEditorDocument.SafeLibraryPath(name);
                if (File.Exists(path))
                {
                    name = name.Substring(0, Math.Min(name.Length, 38)) + "-" +
                        Guid.NewGuid().ToString("N").Substring(0, 6);
                    path = SkinEditorDocument.SafeLibraryPath(name);
                }
                preview.SavePng(path); // strips remote metadata; produces normalized PNG
                RefreshLibrary();
                onlineStatus.Text = "皮肤已下载到本地：" + path +
                    "\n来源：" + item.Attribution;
            }
            catch (Exception ex)
            {
                onlineStatus.Text = "无法保存皮肤：" + ex.GetBaseException().Message;
            }
            finally { onlineSaveButton.IsEnabled = selectedOnlinePng != null; }
            await Task.CompletedTask;
        }

        async Task UseOnlineSkinAsync()
        {
            var item = selectedOnlineSkin;
            var png = selectedOnlinePng;
            if (item == null || png == null || !ConfirmDiscard()) return;
            onlineEditButton.IsEnabled = false;
            string temporary = null;
            try
            {
                temporary = TemporarySkinFile(png);
                document.LoadPng(temporary, item.SlimArms);
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
                onlineEditButton.IsEnabled = selectedOnlinePng != null;
            }
            await Task.CompletedTask;
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
            return Row(leftPanel, rightPanel);
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
            texture.Source = document.TextureImage();
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

        void SaveRecoveryDraft()
        {
            if (!document.Dirty) return;
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
            if (recoveryChecked) return;
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
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
                e.OriginalSource is TextBox) return;
            if (e.Key == Key.Z) { document.Undo(); e.Handled = true; }
            else if (e.Key == Key.Y) { document.Redo(); e.Handled = true; }
            else if (e.Key == Key.S) { SaveToLibrary(); e.Handled = true; }
        }

        internal void VerifyOfflineLayout()
        {
            if (texture.Source == null || front.Source == null || back.Source == null ||
                guideCanvas == null || tool.SelectedIndex != 0 ||
                part.SelectedIndex != 0 || layer.SelectedIndex != 0 ||
                savedSkins == null || hex == null ||
                onlineResults == null || onlineSource.SelectedIndex != 0 ||
                onlineSearchButton == null || onlineSaveButton == null ||
                document.TextureImage().PixelWidth != 64)
                throw new InvalidOperationException("皮肤 DIY 工具、贴图、图层或预览窗口未初始化");
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
