namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// #12 Slice B, IADR-0012: 永続化の行モデル（EF Core エンティティ）。設定・kill switch・ロックアウトは
// 単一行のシングルトン（SingletonId 固定）。変更履歴は追記専用。ADR-0001 の専有 DB に配置する。
internal static class SingletonKeys
{
    // 単一行テーブルの固定主キー（設定・kill switch・ロックアウトは常に 1 行）。
    public const int Id = 1;
}

// IADR-0012: リスク管理設定を JSON 直列化で保持し、Version 列で楽観的排他制御する。
internal sealed class RiskSettingsRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    /// <summary>RiskManagementSettings を System.Text.Json で直列化した JSON（jsonb）。</summary>
    public string Json { get; set; } = string.Empty;

    /// <summary>楽観的排他制御用の版番号。保存のたびに +1 し、読み込み版と一致する行のみ更新する。</summary>
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// kill switch の単一行状態（FR-10, ADR-0003）。
internal sealed class KillSwitchRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    public bool Engaged { get; set; }

    public string? Actor { get; set; }

    public string? Reason { get; set; }

    public DateTimeOffset? ChangedAt { get; set; }
}

// 日次損失ロックアウトの単一行状態（IADR-0008）。行が存在する＝ロックアウト情報を保持している。
internal sealed class LockoutRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    public DateOnly ReleaseOn { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset EngagedAt { get; set; }
}

// 設定・kill switch の変更履歴（FR-11, ADR-0007）。追記専用。
internal sealed class SettingsChangeRow
{
    public Guid Id { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string ChangeType { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset ChangedAt { get; set; }

    public string? Before { get; set; }

    public string? After { get; set; }
}

// FR-10, FR-05, IADR-0018: 承認済み注文の Intent を DecisionId で保持する追記専用行（取引台帳の一部）。
// OrderExecuted は銘柄・方向を持たないため、これを DecisionId で相関して約定を補完する。
internal sealed class ApprovedOrderRow
{
    public Guid DecisionId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public AiStockTrading.Shared.Contracts.Trading.Market Market { get; set; }

    public AiStockTrading.Shared.Contracts.Trading.TradeSide Side { get; set; }

    public AiStockTrading.Shared.Contracts.Trading.ProductType ProductType { get; set; }

    public AiStockTrading.Shared.Contracts.Trading.PositionEffect PositionEffect { get; set; }

    public AiStockTrading.Shared.Contracts.Trading.TradeMode Mode { get; set; }

    public int Quantity { get; set; }

    public decimal Price { get; set; }

    public DateTimeOffset ApprovedAt { get; set; }
}

// FR-10, FR-05, IADR-0018: 約定（OrderExecuted）を OrderId で保持する追記専用行（取引台帳の一部）。
// DecisionId で ApprovedOrderRow を相関して銘柄・方向・建玉効果を補完する。
internal sealed class TradeFillRow
{
    public string OrderId { get; set; } = string.Empty;

    public Guid DecisionId { get; set; }

    public int FilledQuantity { get; set; }

    public decimal AveragePrice { get; set; }

    public DateTimeOffset ExecutedAt { get; set; }
}
