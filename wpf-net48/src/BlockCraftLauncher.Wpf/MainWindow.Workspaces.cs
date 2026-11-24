using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        internal static UIElement WorkspaceHeader(string heading, string active,
            params (string Label, Action Open)[] tabs)
        {
            var headingControl = (FrameworkElement)PageHeading(heading, "");
            headingControl.Margin = new Thickness(0, 0, 14, 0);
            var options = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(options, heading);
            string idPrefix = "WorkspaceTab." + heading + ".";
            bool restoreFocus = Keyboard.FocusedElement is RadioButton previous &&
                AutomationProperties.GetAutomationId(previous).StartsWith(idPrefix, StringComparison.Ordinal);
            foreach (var tab in tabs)
            {
                bool selected = tab.Label == active;
                var option = new RadioButton
                {
                    Content = tab.Label, IsChecked = selected, MinHeight = 32,
                    Padding = new Thickness(12, 4, 12, 4)
                };
                option.SetResourceReference(StyleProperty, "AccountMode");
                AutomationProperties.SetName(option, tab.Label);
                AutomationProperties.SetAutomationId(option, idPrefix + tab.Label);
                option.Checked += (sender, args) => tab.Open();
                if (restoreFocus && selected)
                    option.Loaded += (sender, args) => option.Focus();
                options.Children.Add(option);
            }
            var header = Row(headingControl, options);
            header.Margin = new Thickness(0, 0, 0, 12);
            return header;
        }

        StackPanel serverWorkspace;
        readonly ContentControl serverWorkspaceHeading = new ContentControl();
        readonly ContentControl serverWorkspaceBody = new ContentControl();
        UIElement serverFavoritesView, serverDirectoryView, serverLanView;

        UIElement ShowServerTab(string active, UIElement body)
        {
            if (serverWorkspace == null)
                serverWorkspace = Col(serverWorkspaceHeading, serverWorkspaceBody);
            serverWorkspaceHeading.Content = WorkspaceHeader("服务器", active,
                ("收藏", () => Navigate("多人联机")),
                ("发现", () => Navigate("查找服务器")),
                ("局域网", () => Navigate("局域网")));
            serverWorkspaceBody.Content = body;
            return serverWorkspace;
        }
    }
}
