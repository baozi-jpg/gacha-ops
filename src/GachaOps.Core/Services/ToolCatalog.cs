using GachaOps.Core.Abstractions;
using GachaOps.Core.Adapters;
using GachaOps.Core.Models;

namespace GachaOps.Core.Services;

public static class ToolCatalog
{
    private static readonly IReadOnlyList<ToolDefinition> Definitions =
    [
        new(ToolId.BetterGi, "BetterGI", "原神", "原神 · BetterGI", "原", "#FFF1E5", "#E76F00",
            () => new BetterGiAdapter()),
        new(ToolId.Maa, "MAA", "明日方舟", "明日方舟 · MAA", "舟", "#EBF2FF", "#1967D2",
            () => new MaaAdapter()),
        new(ToolId.MaaEnd, "MaaEnd", "终末地", "终末地 · MaaEnd", "末", "#EEEAFB", "#7E57C2",
            () => new MaaEndAdapter())
    ];

    private static readonly IReadOnlyDictionary<ToolId, ToolDefinition> DefinitionsById =
        Definitions.ToDictionary(definition => definition.Id);

    public static IReadOnlyList<ToolDefinition> All => Definitions;

    public static ToolDefinition Get(ToolId id) => DefinitionsById.TryGetValue(id, out var definition)
        ? definition
        : throw new ArgumentOutOfRangeException(nameof(id), id, "未知的自动化工具。");

    public static IReadOnlyList<IAutomationAdapter> CreateAdapters() =>
        Definitions.Select(definition => definition.CreateAdapter()).ToArray();
}
