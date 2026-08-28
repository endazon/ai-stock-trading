using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Infrastructure.Persistence;

// #10 Slice B, IADR-0012 踏襲: 永続化の行モデル。設定は単一行 JSON＋Version（楽観排他）、
// 基準値・クールダウンは (Symbol, Market) キーの行。ADR-0001 の専有 DB に配置する。
internal static class SingletonKeys
{
    public const int Id = 1;
}

// FR-03, FR-13, IADR-0012: 監視設定を JSON 直列化で保持し、Version で楽観的排他制御する。
internal sealed class MonitorSettingsRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    public string Json { get; set; } = string.Empty;

    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// FR-03: 銘柄別の基準値（前回判断時点価格）。
internal sealed class PriceBaselineRow
{
    public string Symbol { get; set; } = string.Empty;

    public Market Market { get; set; }

    public decimal Price { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// FR-03: 銘柄別の最終トリガー時刻（クールダウン）。
internal sealed class CooldownRow
{
    public string Symbol { get; set; } = string.Empty;

    public Market Market { get; set; }

    public DateTimeOffset LastTriggeredAt { get; set; }
}

// FR-11, FR-13: 監視設定（監視銘柄）変更履歴の追記専用行。Risk の SettingsChangeRow をミラーする。
internal sealed class MonitorSettingsChangeRow
{
    public Guid Id { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; set; }

    public string? Before { get; set; }

    public string? After { get; set; }
}
