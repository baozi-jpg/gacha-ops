using GachaOps.Core.Abstractions;

namespace GachaOps.Core.Models;

public sealed record ToolDefinition(
    ToolId Id,
    string Name,
    string GameName,
    string DisplayName,
    string FallbackGlyph,
    string FallbackBackground,
    string FallbackForeground,
    Func<IAutomationAdapter> CreateAdapter);
