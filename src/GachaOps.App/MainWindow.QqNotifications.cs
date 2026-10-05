using System.Windows;
using System.Windows.Automation;
using GachaOps.Core.Services;

namespace GachaOps.App;

public partial class MainWindow
{
    private CancellationTokenSource? _qqBindingCancellation;

    private async void BindQqButton_Click(object sender, RoutedEventArgs e)
    {
        if (_qqBindingCancellation is { } active)
        {
            active.Cancel();
            return;
        }
        if (!CanAutoSaveSettings()) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_appCancellation.Token);
        _qqBindingCancellation = cancellation;
        SetQqBindingBusy(true);
        QqBindingStatus.Text = "正在连接 QQ…";
        try
        {
            var settings = await SaveSettingsFromControlsAsync();
            if (settings?.Qq is not { } qq)
            {
                QqBindingStatus.Text = "设置未保存，请重试";
                return;
            }
            var result = await new QqNotificationService().BindAsync(qq, code =>
            {
                if (_isClosing || Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isClosing && !cancellation.IsCancellationRequested && _qqBindingCancellation == cancellation)
                        QqBindingStatus.Text = $"请在手机 QQ 私聊机器人，发送验证码 {code}";
                }));
            }, cancellation.Token);
            if (_isClosing) return;
            if (cancellation.IsCancellationRequested)
            {
                QqBindingStatus.Text = "已取消绑定";
                return;
            }
            if (result.OpenId is { } openId)
            {
                var previousOpenId = QqUserOpenIdTextBox.Text;
                QqUserOpenIdTextBox.Text = openId;
                if (await SaveSettingsFromControlsAsync() is null)
                {
                    QqUserOpenIdTextBox.Text = previousOpenId;
                    QqBindingStatus.Text = "OpenID 未保存，请重新绑定";
                }
                else QqBindingStatus.Text = "绑定成功";
            }
            else QqBindingStatus.Text = result.Error ?? "绑定失败，请重试";
        }
        catch (OperationCanceledException) when (_isClosing || _appCancellation.IsCancellationRequested)
        {
            // Closing cancels settings persistence and binding together.
        }
        finally
        {
            _qqBindingCancellation = null;
            if (!_isClosing) SetQqBindingBusy(false);
        }
    }

    private void SetQqBindingBusy(bool busy)
    {
        QqConfigurationGrid.IsEnabled = !busy;
        QqEnabledCheckBox.IsEnabled = !busy;
        QqTestNotificationButton.IsEnabled = !busy;
        QqRemoveChannelButton.IsEnabled = !busy;
        BindQqButton.Content = busy ? "取消" : "绑定";
        AutomationProperties.SetName(BindQqButton, busy ? "取消 QQ 绑定" : "绑定 QQ 用户 OpenID");
    }
}
