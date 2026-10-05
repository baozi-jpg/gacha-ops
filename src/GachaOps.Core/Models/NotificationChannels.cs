namespace GachaOps.Core.Models;

public sealed record QqNotificationSettings
{
    public bool IsEnabled { get; init; } = true;
    public string AppId { get; init; } = string.Empty;
    public string UserOpenId { get; init; } = string.Empty;
    public string ProtectedAppSecret { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string AppSecret { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CredentialUnavailable { get; init; }

    public override string ToString() => "QQ notification settings";
}

public sealed record QqEmailNotificationSettings
{
    public bool IsEnabled { get; init; } = true;
    public string SenderAddress { get; init; } = string.Empty;
    public string RecipientAddress { get; init; } = string.Empty;
    public string ProtectedAuthorizationCode { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string AuthorizationCode { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool CredentialUnavailable { get; init; }

    public override string ToString() => "QQ email notification settings";
}

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
