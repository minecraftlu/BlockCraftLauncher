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
            var title = new TextBlock
            {
                Text = caption, FontSize = 21, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, accent);
            var close = new Button
            {
                Content = "\uE8BB", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                Width = 28, MinHeight = 27, Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(0), ToolTip = "关闭"
            };
            close.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
            AutomationProperties.SetName(close, "关闭");
            close.Click += (sender, args) => window.Close();
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 9), Background = Brushes.Transparent };
            DockPanel.SetDock(close, Dock.Right);
            bar.Children.Add(close);
            bar.Children.Add(title);
            bar.MouseLeftButtonDown += (sender, args) =>
            {
                if (args.ButtonState == MouseButtonState.Pressed) window.DragMove();
            };
            var divider = new Border { Height = 2, Margin = new Thickness(0, 0, 0, 13) };
            divider.SetResourceReference(Border.BackgroundProperty, accent);
            var body = new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            var layout = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(divider, Dock.Top);
            layout.Children.Add(bar);
            layout.Children.Add(divider);
            layout.Children.Add(body);
            var frame = new Border
            {
                Child = layout, Padding = new Thickness(19, 14, 19, 15),
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
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
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
                Content = message, MaxHeight = Math.Min(330, dialog.MaxHeight - 128),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var actions = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, -8, 0)
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
                Add("是", MessageBoxResult.Yes, true);
                Add("否", MessageBoxResult.No);
            }
            else Add("确定", MessageBoxResult.OK, true);
            if (buttons == MessageBoxButton.OKCancel || buttons == MessageBoxButton.YesNoCancel)
                Add("取消", MessageBoxResult.Cancel);
            body.Content = new StackPanel { Children = { scroll, actions } };
            dialog.ShowDialog();
            return result;
        }
    }
}
