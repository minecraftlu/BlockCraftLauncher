using System;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using Microsoft.Win32;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    // This view also runs in the single-file bootstrap, before WPF resources
    // have been extracted. Keep its visuals and fonts self-contained.
    internal sealed class StartupLoadingWindow : Window
    {
        internal const string ReadyVariable = "BLOCKCRAFT_STARTUP_READY_EVENT";
        internal const string EventPrefix = @"Local\BlockCraftLauncher.Startup.";
        const int DesignWidth = 500, DesignHeight = 196;

        internal StartupLoadingWindow()
        {
            Title = "BlockCraft Launcher · 正在启动";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            ShowActivated = false;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            var work = SystemParameters.WorkArea;
            double scale = Math.Min(1, Math.Min(work.Width / DesignWidth, work.Height / DesignHeight));
            Width = Math.Max(1, DesignWidth * scale);
            Height = Math.Max(1, DesignHeight * scale);
            AutomationProperties.SetAutomationId(this, "BlockCraftStartup");
            Content = new Viewbox { Stretch = Stretch.Uniform, Child = CreateView(UseDarkTheme()) };
        }

        static bool UseDarkTheme()
        {
            try
            {
                var preferred = LauncherSettingsFiles.ReadStartupTheme(LauncherSettingsFiles.DefaultPath);
                if (preferred.HasValue) return preferred.Value;
                return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", null) is int value && value == 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            { return false; }
        }

        static SolidColorBrush Paint(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        static FrameworkElement CreateView(bool dark)
        {
            bool contrast = SystemParameters.HighContrast;
            var panel = contrast ? SystemColors.WindowBrush : Paint(dark ? "#172B3C" : "#F3F8FC");
            var ink = contrast ? SystemColors.WindowTextBrush : Paint(dark ? "#F0F6FB" : "#192E46");
            var muted = contrast ? ink : Paint(dark ? "#BDD0DE" : "#496277");
            var accent = contrast ? SystemColors.HighlightBrush : Paint(dark ? "#81C4FF" : "#176DD2");
            var track = contrast ? SystemColors.ControlDarkBrush : Paint(dark ? "#345267" : "#D4E2EE");
            var grid = new Grid { Width = DesignWidth, Height = DesignHeight, Background = panel, ClipToBounds = true };
            if (!contrast)
                grid.Children.Add(new Image { Source = Landscape(dark), Width = 203, Height = 142,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 16, 19), IsHitTestVisible = false, Focusable = false });
            var name = new TextBlock { Text = "BlockCraft Launcher", FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold, FontSize = 25, Foreground = ink,
                Margin = new Thickness(28, 49, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top };
            grid.Children.Add(name);
            var status = new TextBlock { Text = "正在打开启动器…", FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 14, Foreground = muted, Margin = new Thickness(30, 95, 0, 0),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
            grid.Children.Add(status);
            var rail = new Canvas { Height = 4, Width = 440, Background = track, ClipToBounds = true,
                Margin = new Thickness(0, 0, 0, 26), VerticalAlignment = VerticalAlignment.Bottom };
            var slide = new TranslateTransform();
            var mark = new Rectangle { Width = 112, Height = 4, Fill = accent, RenderTransform = slide };
            rail.Children.Add(mark);
            if (!contrast && SystemParameters.ClientAreaAnimation)
            {
                mark.Loaded += (s, e) => slide.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(-112, 440, TimeSpan.FromSeconds(1.65)) {
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                mark.Unloaded += (s, e) => slide.BeginAnimation(TranslateTransform.XProperty, null);
            }
            grid.Children.Add(rail);
            return new Border { BorderThickness = new Thickness(1),
                BorderBrush = contrast ? ink : track, Child = grid, SnapsToDevicePixels = true };
        }

        // Isometric grass terraces, a stream and two firs, drawn in launcher colors.
        static DrawingImage Landscape(bool dark)
        {
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                Point P(double x, double y, double h) => new Point(75 + (x-y)*12, 53 + (x+y)*5.5 - h*8);
                void Face(string color, params Point[] pts)
                {
                    var poly = new StreamGeometry();
                    using (var ctx = poly.Open())
                    {
                        ctx.BeginFigure(pts[0], true, true);
                        for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i], true, false);
                    }
                    poly.Freeze();
                    dc.DrawGeometry(Paint(color), null, poly);
                }
                void Block(int x, int y, int h, bool water)
                {
                    string top = water ? (dark ? "#59BFD9" : "#82D2E4") :
                        ((x+y)%2==0 ? (dark ? "#72BEAB" : "#80CBB3") : (dark ? "#8BCDB5" : "#9BD5B9"));
                    Face(dark ? "#36587A" : "#6683A2", P(x,y+1,h), P(x+1,y+1,h), P(x+1,y+1,0), P(x,y+1,0));
                    Face(dark ? "#29465F" : "#4D6988", P(x+1,y+1,h), P(x+1,y,h), P(x+1,y,0), P(x+1,y+1,0));
                    Face(top, P(x,y,h), P(x+1,y,h), P(x+1,y+1,h), P(x,y+1,h));
                }
                void Fir(double x, double y, double h)
                {
                    var foot=P(x,y,h);
                    dc.DrawRectangle(Paint(dark ? "#8AA098" : "#526F6B"), null, new Rect(foot.X-1.5,foot.Y-6,3,6));
                    for (int tier=0;tier<3;tier++)
                    {
                        double cy=foot.Y-4-tier*4, w=8-tier*1.65;
                        var tip=new Point(foot.X,cy-11+tier);
                        var seam=new Point(foot.X,cy+2);
                        Face(dark ? "#4F9F96" : "#458D81", tip,new Point(foot.X-w,cy),seam);
                        Face(dark ? "#76C1A5" : "#67B096", tip,seam,new Point(foot.X+w,cy));
                    }
                }
                for (int sum=0;sum<=9;sum++)
                    for (int x=0;x<=5;x++)
                    {
                        int y=sum-x;
                        if (y<0 || y>4 || (x==0 && (y==0 || y==4)) || (x==5 && (y==0 || y==4))) continue;
                        bool water = y==(x<3?2:3);
                        int height=water?1:(y<2?3:2);
                        Block(x,y,height,water);
                        if ((x==1 && y==0) || (x==3 && y==1)) Fir(x+.5,y+.5,height);
                    }
            }
            group.Freeze();
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        internal static void CaptureLayoutEvidence()
        {
            var directory = Environment.GetEnvironmentVariable("BLOCKCRAFT_UI_CAPTURE_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            var root = CreateView(UseDarkTheme());
            var size = new Size(DesignWidth+2, DesignHeight+2);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(directory);
            using (var file = File.Create(Path.Combine(directory, "startup-loading.png"))) png.Save(file);
        }
    }
}
