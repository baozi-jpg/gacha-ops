using System.Windows;
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

    internal static bool? ShowModal(Window? owner, string title, string? message, AppDialogKind kind)
    {
        var dialog = new AppDialog(title, message, kind);
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
