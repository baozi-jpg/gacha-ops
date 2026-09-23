namespace GachaOps.Core.Models;

public sealed record BarkNotificationSettings
{
    public bool IsEnabled { get; init; } = true;
    public string ServerAddress { get; init; } = "https://api.day.app";
    public string DeviceKey { get; init; } = string.Empty;

    public override string ToString() => "Bark notification settings";
}

public sealed record NtfyNotificationSettings
{
    public bool IsEnabled { get; init; } = true;
    public string ServerAddress { get; init; } = "https://ntfy.sh";
    public string Topic { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;

    public override string ToString() => "ntfy notification settings";
}
