using System.Windows;
using System.Windows.Controls;
using GachaOps.Core.Models;
using GachaOps.Core.Services;
using System.Windows.Input;
using System.Windows.Media;

namespace GachaOps.App;

internal enum AppDialogKind
{
    Success,
    Information,
    Warning,
    Error
}

public partial class AppDialog : Window
{
    private AppDialog(string title, string? message, AppDialogKind kind)
    {
        InitializeComponent();
        Title = title;
        DialogTitleText.Text = title;
        DialogMessageText.Text = message ?? string.Empty;
        MessageScrollViewer.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ApplyKind(kind);
        Loaded += (_, _) => ConfirmButton.Focus();
        PreviewKeyDown += AppDialog_PreviewKeyDown;
    }

    internal static bool? ShowModal(Window? owner, string title, string? message, AppDialogKind kind,
        string? confirmAction = null)
    {
        var dialog = new AppDialog(title, message, kind);
        if (confirmAction is not null)
        {
            dialog.ConfirmButton.Content = confirmAction;
            dialog.CancelButton.Content = "关闭";
            dialog.CancelButton.Visibility = Visibility.Visible;
            dialog.ConfirmButton.IsDefault = false;
            dialog.Loaded += (_, _) => dialog.CancelButton.Focus();
        }
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.ShowInTaskbar = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog();
    }

    internal static IReadOnlyList<WorkflowTaskSetting> SelectRerunTasks(Window owner,
        IReadOnlyList<WorkflowTaskSetting> tasks)
    {
        var dialog = new AppDialog("临时补做", "使用原轮次配置重新运行所选工具，已完成的部分也可能再次执行。",
            AppDialogKind.Information) { Owner = owner };
        dialog.RerunChoicesPanel.Visibility = Visibility.Visible;
        dialog.CancelButton.Visibility = Visibility.Visible;
        dialog.ConfirmButton.Content = "开始补做";
        var choices = tasks.Select(task => new CheckBox
        {
            Content = $"{ToolCatalog.Get(task.ToolId).Name} · 通道 {task.Channel}",
            IsChecked = true, Tag = task, Margin = new Thickness(0, 6, 0, 6)
        }).ToArray();
        foreach (var choice in choices)
        {
            dialog.RerunChoicesPanel.Children.Add(choice);
            choice.Checked += (_, _) => dialog.ConfirmButton.IsEnabled = choices.Any(item => item.IsChecked == true);
            choice.Unchecked += (_, _) => dialog.ConfirmButton.IsEnabled = choices.Any(item => item.IsChecked == true);
        }
        return dialog.ShowDialog() == true
            ? choices.Where(choice => choice.IsChecked == true).Select(choice => (WorkflowTaskSetting)choice.Tag).ToArray()
            : [];
    }

    private void ApplyKind(AppDialogKind kind)
    {
        switch (kind)
        {
            case AppDialogKind.Success:
                IconBadge.Background = (Brush)FindResource("PrimaryBrush");
                IconGlyph.Text = "✓";
                break;
            case AppDialogKind.Information:
                IconBadge.Background = (Brush)FindResource("PrimaryBrush");
                IconGlyph.Text = "i";
                break;
            case AppDialogKind.Warning:
                IconBadge.Background = new SolidColorBrush(Color.FromRgb(255, 159, 10));
                IconGlyph.Text = "!";
                break;
            case AppDialogKind.Error:
                IconBadge.Background = (Brush)FindResource("DangerBrush");
                IconGlyph.Text = "×";
                break;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void AppDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        DialogResult = false;
        e.Handled = true;
    }
}
