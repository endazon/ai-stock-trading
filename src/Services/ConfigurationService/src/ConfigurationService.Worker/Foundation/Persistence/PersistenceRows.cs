namespace AiStockTrading.Configuration.Worker.Foundation.Persistence;

// FR-17, IADR-0012/0021: 永続化の行モデル。前提条件は単一行（Json＋Version 楽観排他）、変更履歴は追記専用。
internal static class SingletonKeys
{
    public const int Id = 1;
}

// 全体前提条件（TradingAssumptions を JSON 直列化）。Version 列で楽観的排他制御する（IADR-0012 踏襲）。
internal sealed class AssumptionsRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    public string Json { get; set; } = string.Empty;

    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// 前提条件の変更履歴（FR-17, ADR-0007）。追記専用。
internal sealed class AssumptionsChangeRow
{
    public Guid Id { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; set; }

    public int Version { get; set; }

    public string? Before { get; set; }

    public string? After { get; set; }
}
