namespace GachaOps.Core.Models;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Issues)
{
    public static ValidationResult Success() => new(true, Array.Empty<string>());

    public static ValidationResult Failure(params string[] issues) => new(false, issues);
}
