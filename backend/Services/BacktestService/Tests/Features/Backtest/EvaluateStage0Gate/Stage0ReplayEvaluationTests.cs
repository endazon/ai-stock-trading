using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using BacktestService.Domain;
using BacktestService.Features.Backtest.EvaluateStage0Gate;
using BacktestService.Infrastructure.ExternalServices;
using Xunit;

namespace BacktestService.Tests.Features.Backtest.EvaluateStage0Gate;

// FR-04, FR-15, FR-20, ADR-0008, ADR-0033 決定2/決定3, #632, IADR-0318:
// 記録再生の評価文脈の組み立て（整合検査 ＋ fail-closed ＋ 本物の判定器へ到達すること）。
public class Stage0ReplayEvaluationTests
{
    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly To = new(2026, 6, 30);
    private static readonly DateOnly Cutoff = new(2026, 3, 31);

    private static SecurityUniverse Universe(params string[] symbols) =>
        new([.. symbols.Select(s => new UniverseMembership(s, Market.UnitedStates, DateOnly.MinValue, null))]);

    private static IReadOnlyList<PriceBar> Bars(string symbol, int days) =>
        [.. Enumerable.Range(0, days).Select(i =>
        {
            var close = 100m + (i % 5) - 2m;
            return new PriceBar(symbol, Market.UnitedStates, From.AddDays(i), close, close + 1m, close - 1m, close, 1_000);
        })];

    private static Stage0DecisionRecord Record(DateOnly asOf, string symbol, int signedQuantity) =>
        new(symbol, Market.UnitedStates, asOf, "fp", "claude-sonnet-5", VoteCount: 3,
            RawDecisions: [new Stage0RawDecision(1, Stage0DecisionAction.Buy, "根拠", 100m, 2m, 100, 20, false)],
            MajorityAction: signedQuantity > 0 ? Stage0DecisionAction.Buy : Stage0DecisionAction.Hold,
            MajorityRationale: "根拠", SignedQuantity: signedQuantity, CostJpy: 1m,
            InputTokens: 300, OutputTokens: 60);

    private static Stage0DecisionRecordSet SetOf(
        DateOnly? from = null,
        DateOnly? to = null,
        DateOnly? cutoff = null,
        string symbol = "AAPL",
        params Stage0DecisionRecord[] records) =>
        new(from ?? From, to ?? To, [new Stage0RecordedSymbol(symbol, Market.UnitedStates)],
            cutoff ?? Cutoff, DateTimeOffset.UnixEpoch, "claude-sonnet-5", "ai-decision-replay/m/h",
            records.Length == 0 ? [Record(From.AddDays(1), symbol, 10)] : records);

    // configuredCutoff は「構成の LLM 学習カットオフ日」。null は**未構成**を表す（既定値へ倒さない）。
    private static Stage0ReplayEvaluationRequest Request(
        Stage0DecisionRecordSet? recordSet,
        SecurityUniverse? universe = null,
        int barDays = 30) =>
        RequestWithCutoff(recordSet, Cutoff, universe, barDays);

    private static Stage0ReplayEvaluationRequest RequestWithCutoff(
        Stage0DecisionRecordSet? recordSet,
        DateOnly? configuredCutoff,
        SecurityUniverse? universe = null,
        int barDays = 30) =>
        new(recordSet,
            MaterializedBarDataSource.FromBars(Bars("AAPL", barDays)),
            universe ?? Universe("AAPL"),
            From, To,
            configuredCutoff,
            InitialCapital: 1_000_000m,
            CostModel: new BacktestCostModel(TradingAssumptionsDefaults.Create(), SlippageRatio: 0m),
            Criteria: Stage0GateCriteria.Default);

    // 🔴 **否定形（最重要）**: 記録が無ければ評価文脈を組まない（＝判定器を呼ばせない）。
    // 既定の供給ポート（NoStage0DecisionRecordSource）では常にこの経路である。
    [Fact]
    public void 記録が無ければ評価文脈を組まない_failclosed()
    {
        var preparation = Stage0ReplayEvaluation.Prepare(Request(recordSet: null));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Contain(Stage0GateCheck.NoDecisionRecords);
        preparation.GateContext.Should().BeNull();
    }

    // 🔴 **否定形**: 記録集合はあるが判断が 0 件（＝評価対象が存在しない）。
    [Fact]
    public void 判断が0件の記録集合は評価文脈を組まない_failclosed()
    {
        var empty = new Stage0DecisionRecordSet(
            From, To, [new Stage0RecordedSymbol("AAPL", Market.UnitedStates)], Cutoff,
            DateTimeOffset.UnixEpoch, "claude-sonnet-5", "sid", []);

        var preparation = Stage0ReplayEvaluation.Prepare(Request(empty));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Contain(Stage0GateCheck.NoDecisionRecords);
    }

    // 🔴 **否定形**（ADR-0033 決定3）: カットオフ日が未構成なら評価しない。
    // 未構成を「充足」へ倒すと、確認していない検証条件①を満たしたように読める。
    [Fact]
    public void カットオフ日が未構成なら評価文脈を組まない_failclosed()
    {
        var preparation = Stage0ReplayEvaluation.Prepare(RequestWithCutoff(SetOf(), configuredCutoff: null));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Contain(Stage0GateCheck.DataCutoff);
    }

    // 🔴 **否定形**（ADR-0033 決定3）: 記録のカットオフ日が構成と違えば評価しない。
    // 別の汚染対策前提で採った記録を、いまの構成の検証結果として使わせない。
    [Fact]
    public void 記録のカットオフ日が構成と違えば評価文脈を組まない_failclosed()
    {
        var preparation = Stage0ReplayEvaluation.Prepare(
            RequestWithCutoff(SetOf(cutoff: new DateOnly(2026, 4, 30)), configuredCutoff: Cutoff));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Contain(Stage0GateCheck.RecordingMismatch);
    }

    // 🔴 **否定形**: 記録が評価期間を覆っていなければ評価しない。
    // 覆っていない区間は「判断していない」＝無発注になり、何もしなかった成績を AI 判断の成績として読むことになる。
    [Theory]
    [InlineData("2026-06-05", "2026-06-30")] // 始端が足りない
    [InlineData("2026-06-01", "2026-06-20")] // 終端が足りない
    public void 記録が評価期間を覆っていなければ評価文脈を組まない_failclosed(string recordFrom, string recordTo)
    {
        var preparation = Stage0ReplayEvaluation.Prepare(
            Request(SetOf(from: DateOnly.Parse(recordFrom), to: DateOnly.Parse(recordTo))));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Contain(Stage0GateCheck.RecordingMismatch);
    }

    // 🔴 **否定形**: 銘柄集合が構成と違えば評価しない（生存者バイアス排除の前提が崩れる）。
    [Fact]
    public void 銘柄集合が構成と違えば評価文脈を組まない_failclosed()
    {
        var preparation = Stage0ReplayEvaluation.Prepare(
            Request(SetOf(symbol: "MSFT"), universe: Universe("AAPL")));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Contain(Stage0GateCheck.RecordingMismatch);
    }

    // 🔴 **否定形**: 過剰適合補正（PBO）の標本が足りなければ評価しない。
    // 足りないまま組んだ PBO は「過剰適合が無い」ように見えるだけである。
    [Fact]
    public void 標本が足りなければ評価文脈を組まない_failclosed()
    {
        // 日次リターンはバー数 − 1 本。分割数 4 に満たないバー数（3 本 → 2 本）を与える。
        var preparation = Stage0ReplayEvaluation.Prepare(Request(SetOf(), barDays: 3));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Contain(Stage0GateCheck.InsufficientEvaluationSample);
        // 走行そのものはできているため、最大 DD と空売り観測の材料は残す（verdict へ渡す）。
        preparation.BaselineRun.Should().NotBeNull();
    }

    // 肯定形: 記録が整合すれば評価文脈が揃い、**本物の判定器**（Stage0GateService）が走る。
    [Fact]
    public void 記録が整合すれば本物の判定器へ到達する()
    {
        var preparation = Stage0ReplayEvaluation.Prepare(Request(SetOf()));

        preparation.IsReady.Should().BeTrue();
        preparation.GateContext.Should().NotBeNull();
        preparation.GateContext!.LlmTrainingCutoff.Should().Be(Cutoff);
        // ADR-0033 決定3: 匿名化は合否判定の根拠に用いない（true にできる口を作らない）。
        preparation.GateContext.DataAnonymized.Should().BeFalse();
        // 記録再生戦略はパラメータ探索を持たないため試行は 1 本である（＝MinTrials を満たさない）。
        preparation.GateContext.Trials.Count.Should().Be(1);
        preparation.GateContext.OverfittingPartitions.Should().Be(Stage0ReplayEvaluation.OverfittingPartitions);

        var decision = new Stage0GateService().Evaluate(preparation.GateContext);

        // 🔴 **合否は判定器が決める。** 探索が無い現状は試行数条件を満たさないため必ず不合格である。
        decision.Gate.Passed.Should().BeFalse();
        decision.Gate.FailedChecks.Should().Contain(Stage0GateCheck.TrialCount);
        // 駆動側の事前条件（プレースホルダ・記録なし）は判定器からは決して出ない。
        decision.Gate.FailedChecks.Should().NotContain(Stage0GateCheck.PlaceholderStrategy);
        decision.Gate.FailedChecks.Should().NotContain(Stage0GateCheck.NoDecisionRecords);
    }

    // 全バーがカットオフ後なら、検証条件①は未達理由に載らない（理由の読み違えを作らない）。
    [Fact]
    public void 全バーがカットオフ後なら検証条件1は未達にならない()
    {
        var preparation = Stage0ReplayEvaluation.Prepare(Request(SetOf()));

        var decision = new Stage0GateService().Evaluate(preparation.GateContext!);

        decision.DataCutoffSatisfied.Should().BeTrue();
        decision.Gate.FailedChecks.Should().NotContain(Stage0GateCheck.DataCutoff);
    }

    // 🔴 **否定形（陽性対照）**: カットオフ以前のバーが混ざれば、判定器が検証条件①を未達にする。
    // 記録と構成のカットオフ日は一致させ（fail-closed を先に効かせない）、**バーの側だけ**汚染させる。
    [Fact]
    public void カットオフ以前のバーが混ざれば検証条件1が未達になる()
    {
        var midPeriodCutoff = new DateOnly(2026, 6, 10);
        var preparation = Stage0ReplayEvaluation.Prepare(
            RequestWithCutoff(SetOf(cutoff: midPeriodCutoff), configuredCutoff: midPeriodCutoff));

        preparation.IsReady.Should().BeTrue();

        var decision = new Stage0GateService().Evaluate(preparation.GateContext!);

        decision.DataCutoffSatisfied.Should().BeFalse();
        decision.Gate.FailedChecks.Should().Contain(Stage0GateCheck.DataCutoff);
    }

    // 整合検査は純関数として単体で呼べる（駆動の外でも同じ判断ができる）。
    [Fact]
    public void 整合する記録に対して阻害理由は空である() =>
        Stage0ReplayEvaluation.Validate(SetOf(), Request(SetOf())).Should().BeEmpty();

    // ------------------------------------------------------------------------------------------------
    // FR-15, ADR-0008, ADR-0036 決定2, #632, IADR-0329: **検証の分割の固定**（フォローアップ 3 の履行）
    // ------------------------------------------------------------------------------------------------

    // 計画 ADR-0036 決定2 は分割（IS:OOS 比・PBO 分割数）を実装の裁量として追認したうえで、
    // 🔴 **「決めたら固定する。後から動かすことを禁じる」**と定めた。**同決定は「機械検査は無い」と明記している**ため、
    // せめて回帰で動かないよう、値そのものをここで固定する（根拠は IADR-0329）。
    [Fact]
    public void 検証の分割はIS_OOS2対1とPBO分割4に固定される()
    {
        // FR-15, ADR-0008, ADR-0036 決定2: 動かすには新しい IADR が要る（本テストを書き換えるだけで通してはならない）。
        Stage0ReplayEvaluation.InSampleNumerator.Should().Be(2);
        Stage0ReplayEvaluation.InSampleDenominator.Should().Be(3);
        Stage0ReplayEvaluation.OverfittingPartitions.Should().Be(4);
    }

    // 🔴 **値だけ合わせて使っていないこと**を見る（定数を宣言しても使われていなければ固定の意味が無い）。
    // 評価文脈が名乗る分割数は上の定数そのものであり、標本不足の境界も同じ定数から動く。
    [Fact]
    public void PBO分割数は評価文脈にそのまま載る()
    {
        var preparation = Stage0ReplayEvaluation.Prepare(Request(SetOf()));

        preparation.GateContext!.OverfittingPartitions.Should().Be(Stage0ReplayEvaluation.OverfittingPartitions);
    }

    // 境界の対（つい）: 日次リターンは「バー数 − 1」本である。分割数に**1 本足りない**と組まず、**ちょうど足りる**と組む。
    // 期待値を直書きせず定数から導く（規則 10: 導出値は走査ではなく計算し直す）。
    [Theory]
    [InlineData(0, false)]  // バー数 = 分割数 → 日次リターンは分割数 − 1 本 → 足りない
    [InlineData(1, true)]   // バー数 = 分割数 + 1 → 日次リターンは分割数 ちょうど → 足りる
    public void 標本不足の境界はPBO分割数から動く(int extraBars, bool expectedReady)
    {
        var barDays = Stage0ReplayEvaluation.OverfittingPartitions + extraBars;

        var preparation = Stage0ReplayEvaluation.Prepare(Request(SetOf(), barDays: barDays));

        preparation.IsReady.Should().Be(expectedReady);
        preparation.BlockingChecks.Contains(Stage0GateCheck.InsufficientEvaluationSample)
            .Should().Be(!expectedReady);
    }
}
