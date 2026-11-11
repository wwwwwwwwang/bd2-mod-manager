using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BD2ModManager.Gui;

internal static class RiskConfirmation
{
    public static async Task<bool> ConfirmAsync(Window owner, IReadOnlyList<string> names)
    {
        var dialog = new Window
        {
            Title = "高风险功能确认",
            Width = 560,
            Height = 300,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#F4F8FF"),
        };

        var body = new TextBlock
        {
            Text = "你即将启用：\n\n" + string.Join("\n", names.Select(n => "· " + n))
                + "\n\n这些功能只修改本机显示，服务器不会认可，可能导致界面异常、数据不同步或账号风险。确定要启用吗？",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Foreground = Brush.Parse("#1E293B"),
        };

        var cancel = new Button { Content = "取消", Width = 96, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "我了解风险，仍然启用", Width = 180, Height = 34 };
        ok.Classes.Add("primary");
        cancel.Click += (_, _) => dialog.Close(false);
        ok.Click += (_, _) => dialog.Close(true);

        var panel = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        panel.Children.Add(body);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        };
        Grid.SetRow(buttons, 1);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        return await dialog.ShowDialog<bool>(owner);
    }
}
