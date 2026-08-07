namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

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

// 設定・kill switch の変更履歴（FR-11）。追記専用。
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

    public AiStockTrading.Shared.Contracts.Trading.BrokerProvider Mode { get; set; }

    public int Quantity { get; set; }

    public decimal Price { get; set; }

    /// <summary>取引判断が決めた損切り価格（IADR-0035・nullable＝機械執行 Close 等は null）。</summary>
    public decimal? StopLossPrice { get; set; }

    /// <summary>
    /// 基準通貨（USD）への換算レート（IADR-0107 / IADR-0152・約定時レートの近似）。nullable＝本列の追加前に記録された行で、
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

    // FR-20, #386, IADR-0149 決定3: 旧 Stage1TradeCount 列は**削除した**。件数の供給元は本行ではなく
    // 約定の観測ログ（stage1_fill_observations）であり、そこが計上単位（1 注文 1 行・DecisionId 主キー）を
    // 担保する。件数を別途この列にも持つと供給元が 2 つになり、必ず食い違う。
    // 死んだ列を残すと「まだ使う値」に見え、次の実装者が判定へ結線し直す余地が残る
    // （IADR-0137 決定2 / IADR-0148 決定2 と同じ規律）。
    //
    // FR-20, #385, IADR-0150 決定4: 旧 Stage1QualifiedTradingDays / Stage1ExcludedInternalPaperDays 列も
    // **同じ理由で削除した**。営業日数の供給元は稼働の観測ログ（stage1_session_uptime）であり、
    // そこだけが「1 取引日 1 行」と算入規則（両仮説で 50%）を担保する。

    // FR-20, #387, IADR-0148: 旧 ControlViolationCount 列は**削除した**。件数の供給元は本行ではなく
    // 発注審査の観測ログ（order_screening_observations）であり、供給の有無（未供給 / 0 件）を
    // 非 nullable の int 列では表現できない。死んだ列を残すと「まだ使う値」に見え、次の実装者が
    // 判定へ結線し直す余地が残る（IADR-0137 決定2 と同じ規律）。

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

// FR-05, FR-10, #305, IADR-0124: 建玉乖離の報告可否を決める追跡状態の単一行。IADR-0118 はこれをプロセス内に
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

// FR-20, FR-11, #387, 06_daytrading-review §4.1 条件1, IADR-0148: 発注審査 1 回ぶんの観測。
// 段階ゲートの「統制違反 0 件」（**クラス C 限定**）を数える供給元であり、**承認された審査も 1 行として残す**
// （記録が違反だけだと「違反 0 件」を主張する根拠が無く、未供給と区別できない）。
//
// DecisionId が主キーであることが**計上単位（1 回の発注拒否につき 1 件）そのもの**を担保する。
// 1 回の拒否に複数のクラス C 理由が返っても行は 1 つであり、再送（同一 DecisionId）でも増えない。
//
// 算入可否・違反該当は**記録時に純関数（ControlViolationAggregation / RejectionReasonClassification）が
// 決めた結果**を保持する。クラス分けの規則を SQL 側へ写すと単一情報源が壊れる。
internal sealed class OrderScreeningObservationRow
{
    public Guid DecisionId { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }

    /// <summary>その審査が向いていた発注先（監査のため生値も残す）。</summary>
    public AiStockTrading.Shared.Contracts.Trading.BrokerProvider Provider { get; set; }

    /// <summary>Stage 1 の合格判定へ算入してよい発注先か（moomoo SIMULATE の許可制・IADR-0142 決定2）。</summary>
    public bool CountsTowardStage1 { get; set; }

    /// <summary>クラス C の理由を含む拒否か（＝統制違反 1 件として数えるか）。</summary>
    public bool IsControlViolation { get; set; }
}

// FR-20, FR-12, #386, 06_daytrading-review §4.1 条件3, IADR-0149: 約定が成立した注文 1 件ぶんの観測。
// 段階ゲートの「最小取引件数 100 件」を数える供給元である。
//
// DecisionId が主キーであることが**計上単位（約定した注文 1 件）そのもの**を担保する。
// 分割約定は同一注文について累積約定数を運ぶ複数の OrderExecuted として現れる（IADR-0113）が、行は 1 つである。
//
// Provider は**実際に発注したアダプタの発注先**（OrderExecuted.Provider）であって、取引判断が運ぶ
// intent.Mode（＝段階が定める既定の発注先・IADR-0140 決定3）ではない。算入可否は**記録時に純関数
// Stage1Aggregation.CountsAsTrade が決めた結果**を持ち、SQL 側へ算入規則を写さない。
internal sealed class Stage1FillObservationRow
{
    public Guid DecisionId { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }

    /// <summary>米国東部時間での取引日（日次の突合・監査のため。件数の判定には用いない）。</summary>
    public DateOnly SessionDateEasternTime { get; set; }

    /// <summary>実際に発注したアダプタの発注先（監査のため生値も残す）。</summary>
    public AiStockTrading.Shared.Contracts.Trading.BrokerProvider Provider { get; set; }

    /// <summary>その注文の建玉効果（新規建てだけを数えるため・IADR-0149 決定2）。</summary>
    public AiStockTrading.Shared.Contracts.Trading.PositionEffect PositionEffect { get; set; }

    /// <summary>Stage 1 の取引件数へ算入するか（SIMULATE の許可制 ∧ 新規建て）。</summary>
    public bool CountsTowardStage1 { get; set; }
}

// FR-20, FR-12, #385, 06_daytrading-review §4.2, IADR-0150: 1 取引日 × 1 発注先ぶんの稼働観測。
// 段階ゲートの「60 営業日」（期間カウント）を数える供給元である。
//
// (SessionDateEasternTime, Provider) が主キーであることが**1 取引日 1 発注先 1 行**を担保し、
// probe の巡回が何度届いても行は増えない。積み方（初回は遡らない・落とした区間は積まない・逆行は無視）は
// 純関数 Stage1SessionUptime.Credit が単一情報源であり、SQL 側へ規則を写さない。
//
// **稼働分数を 2 つ持つのは、その日の実際の通常取引時間を実装が知らないためである**（IADR-0150 決定3）。
// 半日取引日（9:30〜13:00）と通常日（9:30〜16:00）の両方の窓で分数を持ち、両仮説で 50% を満たす日だけを算入する。
internal sealed class Stage1SessionUptimeRow
{
    /// <summary>米国東部時間での取引日（§4.2 の判定基準時刻）。</summary>
    public DateOnly SessionDateEasternTime { get; set; }

    /// <summary>その稼働の発注先（実際に接続していたアダプタの自己申告。監査のため生値も残す）。</summary>
    public AiStockTrading.Shared.Contracts.Trading.BrokerProvider Provider { get; set; }

    /// <summary>直前に成功した観測の分（米国東部時間の 0 時から）。未観測は -1。次の区間の起点になる。</summary>
    public int LastObservedMinuteOfDayEasternTime { get; set; } = -1;

    /// <summary>9:30〜13:00 ET（半日取引日の窓）のうち稼働していた分数。</summary>
    public int OperationalMinutesBeforeEarlyClose { get; set; }

    /// <summary>9:30〜16:00 ET（通常日の窓）のうち稼働していた分数。</summary>
    public int OperationalMinutesBeforeRegularClose { get; set; }

    /// <summary>稼働率の条件（**どの仮説でも 50% 以上**）を満たすか。記録時に純関数が決めた結果。</summary>
    public bool MeetsUptimeThreshold { get; set; }

    /// <summary>Stage 1 の営業日へ算入するか（稼働率の条件 ∧ SIMULATE の許可制）。記録時に純関数が決めた結果。</summary>
    public bool QualifiesTowardStage1 { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

// FR-10, FR-11, FR-06, UC-06, ADR-0016 決定4（2026-08-06 改訂）・決定15, #419, IADR-0159:
// 強制買戻しの事後推定 1 件ぶんの**追記専用**の行。
//
// 行は 2 種類ある。NewlyInferredQuantity > 0 が**推定行**（＝ADR-0016 決定15 の「発生回数」の集計元）、
// 0 が**リセット行**（乖離が解消し帰属数量を戻した記録。禁止期限は戻らないため BanUntil は null）。
//
// **`RejectionReason.BuyInBanned` の拒否件数を発生回数の集計元にしてはならない**——1 回の強制買戻しに対して
// 禁止期間 30 日のあいだ何度でも拒否は起こり得るため、実際より大きな数字が月報に載る（決定15 の明文）。
internal sealed class BuyInInferenceRow
{
    public Guid Id { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public AiStockTrading.Shared.Contracts.Trading.Market Market { get; set; }

    /// <summary>台帳（＝自らの約定履歴の射影）が示す空売り建玉の数量。</summary>
    public int LedgerShortQuantity { get; set; }

    /// <summary>ブローカが示す空売り建玉の数量（応答に現れない銘柄は 0＝全量消失）。</summary>
    public int BrokerShortQuantity { get; set; }

    /// <summary>承認済みだが約定が台帳へ届いていない決済数量（＝処理中の自らの決済指示）。</summary>
    public int InFlightCloseQuantity { get; set; }

    /// <summary>自らの決済指示で説明できない消失の累計（＝強制買戻しへ帰属させている数量）。</summary>
    public int UnexplainedQuantity { get; set; }

    /// <summary>今回新たに推定した数量（0 はリセット行）。</summary>
    public int NewlyInferredQuantity { get; set; }

    /// <summary>30 日の空売り禁止の解除日（リセット行は null）。</summary>
    public DateOnly? BanUntil { get; set; }

    /// <summary>推定した取引日（期間集計の単位）。</summary>
    public DateOnly InferredOn { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }

    public DateTimeOffset InferredAtUtc { get; set; }
}

// FR-19, FR-10, FR-11, #425, ADR-0025 決定2, IADR-0165:
// GFV 発生 1 件（自前計数）の**追記専用**の行。
//
// **主キーは OrderId である**（1 注文 1 件が計上単位）。部分約定の進行・メッセージ再送で二重計上しない。
//
// ★ 本行が数えているのは「**自らのガードをすり抜けた買付**」であり、**ブローカーが GFV と判定した件数ではない**
//   （ADR-0025 §理由。両者が一致する保証はない）。**ガードが正しく働けば 1 行も増えない。**
//
// **`RejectionReason.CashAccountSettlementHold` の拒否件数を集計元にしてはならない**——同理由は
// ガードが**働いた**記録（買付を止めた回数）であり、本行はガードが**すり抜けられた**記録である。向きが逆である。
internal sealed class GoodFaithViolationRow
{
    /// <summary>ブローカの注文 ID（主キー＝計上単位）。</summary>
    public string OrderId { get; set; } = string.Empty;

    public Guid Id { get; set; }

    /// <summary>相関する取引判断（発注審査の記録と突き合わせるための鍵）。</summary>
    public Guid DecisionId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public AiStockTrading.Shared.Contracts.Trading.Market Market { get; set; }

    /// <summary>その約定の基準通貨（USD）建て金額。</summary>
    public decimal PurchaseAmountInBase { get; set; }

    /// <summary>判定に用いた決済済み資金。**null は「供給されていなかった」**（0 ではない）。</summary>
    public decimal? SettledCashInBase { get; set; }

    /// <summary>計上した取引日（米国東部時間・期間集計の単位）。</summary>
    public DateOnly OccurredOn { get; set; }

    public DateTimeOffset ExecutedAtUtc { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }
}
