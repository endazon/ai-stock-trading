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

// 取引の一時停止（pause）の単一行状態（FR-10, ADR-0009）。kill switch と同型。日次損失ロックアウトとは別テーブル。
internal sealed class PauseRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    public bool Paused { get; set; }

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

    /// <summary>取引判断が決めた損切り価格（IADR-0035・nullable＝機械執行 Close 等は null）。</summary>
    public decimal? StopLossPrice { get; set; }

    /// <summary>
    /// 基準通貨（円）への換算レート（IADR-0107・約定時レートの近似）。nullable＝本列の追加前に記録された行で、
    /// 読み出し時はレート 1（基準通貨建て＝当時の暗黙の前提）として扱う。
    /// </summary>
    public decimal? FxRateToBase { get; set; }

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

// FR-20, UC-06, IADR-0041/0070: 段階ゲートの遷移履歴（追記専用・監査対象）。Sequence を主キーとし、
// 現在段階・次シーケンスは履歴の畳み込み（StageGateLedger）で導出する（可変の「現在段階」列は持たない）。
// Sequence の一意制約により並行する二重追記を弾く（楽観的整合）。
internal sealed class StageTransitionRow
{
    public int Sequence { get; set; }

    public AiStockTrading.RiskManagement.Domain.TradingStage FromStage { get; set; }

    public AiStockTrading.RiskManagement.Domain.TradingStage ToStage { get; set; }

    public AiStockTrading.RiskManagement.Domain.StageTransitionKind Kind { get; set; }

    public string ApprovedBy { get; set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; set; }

    public string Reason { get; set; } = string.Empty;
}

// FR-20, FR-15, IADR-0070: 段階ゲートの合格・撤退基準の入力＝段階別実績の単一行。未記録時は fail-safe 既定
// （BacktestPassed=false ほか全 false/0）を返す＝既定で昇格を許可しない安全側。実供給（バックテスト verdict・
// 実DD・統制違反・スリッページ実績）は後続（BacktestService からの s2s・#82 系）で upsert する。
internal sealed class StagePerformanceRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    public bool BacktestPassed { get; set; }

    public decimal BacktestMaxDrawdownRatio { get; set; }

    public decimal ObservedMaxDrawdownRatio { get; set; }

    public bool PaperDeviationExplained { get; set; }

    public int ControlViolationCount { get; set; }

    public bool SlippageAndCostWithinExpected { get; set; }

    public bool DailyLossLimitRespected { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// FR-20, FR-09, IADR-0085, #189: 撤退の非停止（Stage 1 ペーパー乖離）降格提案の「最後に通知したシグネチャ」を保持する
// 単一行。停止経路（Stage 2/3）は kill switch 状態を冪等鍵にするが、非停止経路は kill switch を起動しないため別の
// durable な通知済み状態が要る（IADR-0085）。行が無い／Signature が null＝未通知＝fail-safe。
internal sealed class WithdrawalNotificationRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    /// <summary>最後に通知した撤退提案のシグネチャ（"{Reason}:{(int)ProposedStage}"）。未通知なら null。</summary>
    public string? Signature { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// FR-05, FR-10, #305, IADR-0121: 建玉乖離の報告可否を決める追跡状態の単一行。IADR-0118 はこれをプロセス内に
// 持っていたが、replicas>1 では観測が Pod へ分散して「連続 N 回」条件が満たされず、乖離が例外もログも出さずに
// 恒久未報告になり得た。Version を並行トークンにして、レプリカ間の read-modify-write を明示的に守る。
internal sealed class PositionDriftStateRow
{
    public int Id { get; set; } = SingletonKeys.Id;

    /// <summary>観測中の乖離の正準シグネチャ。空文字＝乖離なし。</summary>
    public string ObservedSignature { get; set; } = string.Empty;

    /// <summary>同一シグネチャを連続で観測した回数。</summary>
    public int ConsecutiveCount { get; set; }

    /// <summary>最後に報告した乖離のシグネチャ。空文字＝未報告（解消時にも空へ戻す）。</summary>
    public string ReportedSignature { get; set; } = string.Empty;

    /// <summary>楽観的排他制御用の版番号（IADR-0012 と同型）。並行更新に負けた側は何も書かない。</summary>
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// FR-19, #154, IADR-0067: 相場操縦検知の入力＝1 注文のライフサイクル要約を DecisionId で保持する行。
// 承認で作成し、約定・訂正・取消で更新する（可変・射影）。取引台帳（ApprovedOrderRow/TradeFillRow）とは別テーブル。
// 取引台帳は Filled のみを載せる設計で、本用途の母集団である「約定ゼロで取り消された注文」を構造的に捨てるため、
// 関心・寿命の異なる別ストアに射影する（IADR-0067）。
internal sealed class OrderActivityRow
{
    public Guid DecisionId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public AiStockTrading.Shared.Contracts.Trading.Market Market { get; set; }

    public AiStockTrading.Shared.Contracts.Trading.TradeSide Side { get; set; }

    /// <summary>発注時刻（承認時刻で近似・IADR-0067）。窓判定・生存時間の起点。</summary>
    public DateTimeOffset PlacedAt { get; set; }

    public int Quantity { get; set; }

    public int FilledQuantity { get; set; }

    public AiStockTrading.Shared.Contracts.Trading.OrderStatus Status { get; set; }

    public int AmendmentCount { get; set; }

    /// <summary>終端時刻（取消/失効/約定などで確定した時刻）。未確定なら null。生存時間の終点。</summary>
    public DateTimeOffset? TerminalAt { get; set; }
}
