namespace BacktestService.Domain;

// FR-15, FR-20, ADR-0008: Stage 0 合格判定の各条件。
//
// 先頭 7 つが判定器（Stage0GateEvaluator）の 7 条件である。末尾 5 つは**駆動側（Hosted の定時評価）の事前条件**であり、
// 判定器は決して出さない（#688, IADR-0310 決定3・#632, IADR-0318）。同じ enum に同居させるのは、未達理由の文字列表現を
// Stage0GateResult.FormatFailedChecks() の単一情報源に保つためである（2 系統に分けると区切り文字がドリフトする）。
public enum Stage0GateCheck
{
    DeflatedSharpe,
    Overfitting,
    MaxDrawdown,
    CostRobustness,
    WalkForward,
    TrialCount,
    DataCutoff,

    /// <summary>
    /// FR-15, #688, IADR-0310 決定2: 過去データが 1 本も無い（**判定そのものを走らせていない**）。
    /// DataCutoff は空バーを違反と見なさない（`bars.All(...)` は空に対して真）ため、駆動側が明示的に載せる。
    /// </summary>
    NoHistoricalBars,

    /// <summary>
    /// FR-15, FR-20, ADR-0033, #688, IADR-0310 決定3: 評価対象がプレースホルダ戦略である
    /// （**本番の合否ではない**）。#632 / IADR-0318 で本番戦略（AI 判断の記録・再生）が載ったため、
    /// 本理由が載るのは **`Backtest:Stage0:Strategy` が既定（`placeholder`）のまま走ったとき**に限る。
    /// </summary>
    PlaceholderStrategy,

    /// <summary>
    /// FR-04, FR-15, ADR-0033 決定2, #632, IADR-0318: 記録再生戦略を選んだが、**AI 判断の記録が 1 件も無い**
    /// （供給ポートが既定の「記録なし」・ファイルが無い・空の記録集合）。評価対象が存在しないため判定を走らせない。
    /// </summary>
    NoDecisionRecords,

    /// <summary>
    /// FR-04, FR-15, ADR-0033 決定2/決定3, #632, IADR-0318: 記録はあるが**評価の構成と整合しない**
    /// （期間が評価期間を覆っていない・銘柄集合が違う・記録のカットオフ日が構成と違う）。
    /// 別の前提で採った記録を、いまの構成の検証結果として使わせない。
    /// </summary>
    RecordingMismatch,

    /// <summary>
    /// FR-15, ADR-0008, #632, IADR-0318: 記録は整合するが、**過剰適合補正（PBO）の標本が足りない**
    /// （評価期間の日次リターンが分割数に満たない）。判定を走らせず不合格に倒す
    /// —— 標本不足のまま算出した PBO は「過剰適合が無い」ように見えるだけである。
    /// </summary>
    InsufficientEvaluationSample,
}

// FR-15, ADR-0008, 06_daytrading-review §4: Stage 0 合格基準の閾値。
public sealed record Stage0GateCriteria(
    double MinDeflatedSharpe,
    double MaxProbabilityOfOverfitting,
    decimal MaxDrawdownTolerance,
    int MinTrials)
{
    /// <summary>
    /// FR-15, FR-20, ADR-0018 決定2, #333, IADR-0138: Stage 0 合格判定の**最大ドローダウン許容値 10%**。
    /// <para>
    /// **運用の DD 停止ライン（<c>TradingDefaults.CreateRiskLimits().MaxDrawdownRatio</c> ＝ 10%）と同値である。**
    /// 検証段階だからといって意図的に緩めない——運用で停止する水準の戦略を検証で合格させれば、
    /// ゲートは合格の意味を失う（ADR-0018 決定2）。
    /// </para>
    /// <para>
    /// 旧値 <c>0.15</c> は ADR-0008 の旧レンジ「10〜15%」の上限側からの逆算であり、**Stage 0 が運用停止ラインより
    /// 5 ポイント緩い戦略を合格させ得る**状態にあった（検証を通った戦略が運用開始と同時に停止条件へ抵触し得る）。
    /// 2026-08-01 の計画裁定（ADR-0018）で 10% が確定したため、これを是正した（旧 issue #306 を吸収）。
    /// **0.15 への退行は <c>Stage0GateCriteriaTests</c> が検知する。**
    /// </para>
    /// </summary>
    public const decimal MaxDrawdownToleranceDefault = 0.10m;

    // 既定閾値（DSR 0.95・PBO 0.5・最大DD 0.10・最小試行数 20）。
    //
    // #208, IADR-0110: MinTrials を暫定値 1 から 20 へ較正した。1 では ExpectedMaxSharpe が 0 を返し
    // （trials<2）多重検定補正が恒等的に消えるため、探索を過少申告した Stage 0 判定を素通しさせていた。
    // 実測（決定論モンテカルロ・真のエッジ 0）: 200 候補を探索して 1 件だけ記録すると偽陽性率 100%、
    // 2 件で 57.20%、20 件で 0.62%。SR0 の推定変動係数も N=2 の 75.9% から N=20 で 16.3% へ収束する。
    // 他の 2 閾値は据え置き（DSR 0.95 は名目 5% 水準と実測整合・PBO 0.5 の厳格化は既知エッジも同程度に
    // 落とすため見送り）。
    // 実市場データによる水準確認は #382（ADR-0023・米国株の日足 OHLC 履歴源が未確定）に残置する。
    public static Stage0GateCriteria Default => new(
        MinDeflatedSharpe: 0.95,
        MaxProbabilityOfOverfitting: 0.50,
        MaxDrawdownTolerance: MaxDrawdownToleranceDefault,
        MinTrials: 20);
}

// FR-15, ADR-0008: Stage 0 合格判定の入力（Slice A/B の集計・補正結果）。
public sealed record Stage0GateEvaluation(
    double DeflatedSharpe,
    double ProbabilityOfBacktestOverfitting,
    decimal MaxDrawdown,
    decimal DoubledCostTotalReturn,
    decimal WalkForwardOutOfSampleReturn,
    int TrialCount,
    bool DataCutoffSatisfied);

// FR-15: Stage 0 合格判定の結果。FailedChecks が空なら合格。
public sealed record Stage0GateResult(bool Passed, IReadOnlyList<Stage0GateCheck> FailedChecks)
{
    // 未達条件を名称の連結で表現する。昇格推奨の根拠（Stage0Promotion）と Risk へ供給する契約イベントの診断
    // （BacktestEvaluated・IADR-0089）で同一表現を共有し、区切り文字のドリフトを防ぐ単一情報源。
    public string FormatFailedChecks() => string.Join(", ", FailedChecks);
}

// FR-15, FR-20, ADR-0008, 06_daytrading-review §4, IADR-0045: Stage 0 合格判定（純関数）。
// DSR 補正後のエッジ・過剰適合・最大DD・コスト2倍頑健性・ウォークフォワードOOS・試行数・データカットオフの 7 条件を合成する。
public static class Stage0GateEvaluator
{
    public static Stage0GateResult Evaluate(Stage0GateEvaluation evaluation, Stage0GateCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(criteria);

        var failed = new List<Stage0GateCheck>();

        // エッジ有意: DSR 補正後もエッジが正（閾値以上）。
        if (evaluation.DeflatedSharpe < criteria.MinDeflatedSharpe)
            failed.Add(Stage0GateCheck.DeflatedSharpe);
        // 過剰適合: PBO が閾値以下。
        if (evaluation.ProbabilityOfBacktestOverfitting > criteria.MaxProbabilityOfOverfitting)
            failed.Add(Stage0GateCheck.Overfitting);
        // 最大 DD: 許容内。
        if (evaluation.MaxDrawdown > criteria.MaxDrawdownTolerance)
            failed.Add(Stage0GateCheck.MaxDrawdown);
        // コスト頑健性: コストを 2 倍にしても期待値が正（06_daytrading-review §3.2）。
        if (evaluation.DoubledCostTotalReturn <= 0m)
            failed.Add(Stage0GateCheck.CostRobustness);
        // ウォークフォワード: OOS リターンが正。
        if (evaluation.WalkForwardOutOfSampleReturn <= 0m)
            failed.Add(Stage0GateCheck.WalkForward);
        // 試行数: 最小以上（過剰適合補正の前提）。
        if (evaluation.TrialCount < criteria.MinTrials)
            failed.Add(Stage0GateCheck.TrialCount);
        // データ健全性: 全バーが LLM 学習カットオフ後（または匿名化）＝汚染なし（FR-15 検証条件①）。
        if (!evaluation.DataCutoffSatisfied)
            failed.Add(Stage0GateCheck.DataCutoff);

        return new Stage0GateResult(failed.Count == 0, failed);
    }
}
