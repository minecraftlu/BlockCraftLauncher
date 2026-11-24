using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BlockCraftLauncher.Wpf
{
    internal static class LauncherDialog
    {
        internal static (Window Dialog, ContentControl Body) Create(Window owner, string caption,
            double width, double? height = null, MessageBoxImage image = MessageBoxImage.None)
        {
            var area = SystemParameters.WorkArea;
            var window = new Window
            {
                Title = caption, Width = Math.Min(width, Math.Max(260, area.Width - 32)),
                MaxHeight = Math.Max(240, area.Height - 32),
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, UseLayoutRounding = true, SnapsToDevicePixels = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                SizeToContent = height.HasValue ? SizeToContent.Manual : SizeToContent.Height
            };
            if (height.HasValue) window.Height = Math.Min(height.Value, window.MaxHeight);
            if (owner?.IsVisible == true)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));

            string accent = image == MessageBoxImage.Error ? "DangerBrush" :
                image == MessageBoxImage.Warning ? "WarningBrush" : "AccentInkBrush";
            string symbol = image == MessageBoxImage.Warning ? "\uE7BA" :
                image == MessageBoxImage.Error ? "\uEA39" :
                image == MessageBoxImage.Question ? "\uE897" : "\uE946";
            var glyph = new TextBlock
            {
                Text = symbol, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, accent);
            var tile = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Child = glyph,
                Margin = new Thickness(0, 0, 11, 0)
            };
            tile.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            tile.SetResourceReference(Border.BorderBrushProperty, "SoftDividerBrush");
            tile.BorderThickness = new Thickness(1);
            var title = new TextBlock
            {
                Text = caption, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var close = new Button
            {
                Content = "\uE8BB", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                Width = 28, MinHeight = 27, Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(0), ToolTip = "关闭"
            };
            close.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
            AutomationProperties.SetName(close, "关闭");
            close.Click += (sender, args) => window.Close();
            var bar = new DockPanel { Background = Brushes.Transparent };
            DockPanel.SetDock(tile, Dock.Left);
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(tile);
            bar.Children.Add(close);
            bar.Children.Add(title);
            bar.MouseLeftButtonDown += (sender, args) =>
            {
                if (args.ButtonState == MouseButtonState.Pressed) window.DragMove();
            };
            var header = new Border
            {
                Child = bar, Padding = new Thickness(16, 11, 12, 11),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            header.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
            header.SetResourceReference(Border.BorderBrushProperty, "SoftDividerBrush");
            var body = new ContentControl
            {
                Margin = new Thickness(19, 15, 19, 15),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            var layout = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(body);
            var frame = new Border
            {
                Child = layout,
                BorderThickness = new Thickness(1)
            };
            frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            frame.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
            window.Content = frame;
            window.PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.Escape) return;
                args.Handled = true;
                window.Close();
            };
            return (window, body);
        }

        internal static MessageBoxResult Show(string text, string caption = "BlockCraft Launcher",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) =>
            Show(null, text, caption, buttons, image);

        internal static MessageBoxResult Show(Window owner, string text, string caption = "BlockCraft Launcher",
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None,
            string yesText = "是", string noText = "否")
        {
            var app = Application.Current;
            if (app?.Dispatcher.CheckAccess() != true)
                return System.Windows.MessageBox.Show(text, caption, buttons, image);
            owner = owner ?? app.Windows.OfType<Window>().FirstOrDefault(item => item.IsActive) ?? app.MainWindow;
            var (dialog, body) = Create(owner, caption, 482, image: image);
            var message = new TextBlock
            {
                Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                LineHeight = 20, Margin = new Thickness(1, 0, 4, 0)
            };
            var scroll = new ScrollViewer
            {
                Content = message, MaxHeight = Math.Min(330, dialog.MaxHeight - 164),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var actions = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -8, 0)
            };
            var result = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No :
                buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
            void Add(string label, MessageBoxResult value, bool primary = false)
            {
                var button = new Button { Content = label, MinWidth = 68, IsDefault = primary };
                if (primary) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
                button.Click += (sender, args) =>
                {
                    result = value;
                    dialog.DialogResult = primary;
                };
                actions.Children.Add(button);
            }
            if (buttons == MessageBoxButton.YesNo || buttons == MessageBoxButton.YesNoCancel)
            {
                Add(yesText, MessageBoxResult.Yes, true);
                Add(noText, MessageBoxResult.No);
            }
            else Add("确定", MessageBoxResult.OK, true);
            if (buttons == MessageBoxButton.OKCancel || buttons == MessageBoxButton.YesNoCancel)
                Add("取消", MessageBoxResult.Cancel);
            var footer = new Border { Child = actions, Margin = new Thickness(0, 16, 0, 0),
                Padding = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
            footer.SetResourceReference(Border.BorderBrushProperty, "SoftDividerBrush");
            body.Content = new StackPanel { Children = { scroll, footer } };
            dialog.ShowDialog();
            return result;
        }
    }
}
