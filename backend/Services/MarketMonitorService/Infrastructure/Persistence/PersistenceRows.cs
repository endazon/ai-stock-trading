using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Infrastructure.Persistence;

// #10 Slice B, IADR-0012 踏襲: 永続化の行モデル。設定は単一行 JSON＋Version（楽観排他）、
// 基準値・クールダウンは (Symbol, Market) キーの行。ADR-0001 の専有 DB に配置する。
public static class SingletonKeys
{
    public const int Id = 1;
}

// FR-03, FR-13, IADR-0012: 監視設定を JSON 直列化で保持し、Version で楽観的排他制御する。
public sealed class MonitorSettingsRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    public string Json { get; set; } = string.Empty;

    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // #286, IADR-0282: 構成（Monitor:SeedSymbols）シードを最後に適用した時刻。未適用（旧行含む）は null。
    // ドメイン型 MarketMonitorSettings には持たせない（API 契約で直接動かせる値ではないため。
    // IADR-0164 決定1 と同型の理由）。
    public DateTimeOffset? SeededAt { get; set; }

    // #286, IADR-0282: 利用者が監視銘柄を明示的に全削除した時刻。null なら未削除（＝構成シードの対象）。
    // 一度でも設定されれば、監視銘柄が再び追加されるまで構成シードによる再投入を止める
    // （IADR-0095「空の watchlist は利用者の正当な選択」を尊重する）。
    public DateTimeOffset? ClearedByUserAt { get; set; }
}

// FR-03: 銘柄別の基準値（前回判断時点価格）。
public sealed class PriceBaselineRow
{
    public string Symbol { get; set; } = string.Empty;

    public Market Market { get; set; }

    public decimal Price { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// FR-03: 銘柄別の最終トリガー時刻（クールダウン）。
public sealed class CooldownRow
{
    public string Symbol { get; set; } = string.Empty;

    public Market Market { get; set; }

    public DateTimeOffset LastTriggeredAt { get; set; }
}

// FR-11, FR-13: 監視設定（監視銘柄）変更履歴の追記専用行。Risk の SettingsChangeRow をミラーする。
public sealed class MonitorSettingsChangeRow
{
    public Guid Id { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; set; }

    public string? Before { get; set; }

    public string? After { get; set; }
}
