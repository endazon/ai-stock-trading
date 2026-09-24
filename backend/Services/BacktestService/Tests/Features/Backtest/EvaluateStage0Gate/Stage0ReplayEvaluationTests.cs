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

    // FR-15, ADR-0036 決定1, #749, IADR-0387: as-of 入力 3 種すべてを「再構成できた」と申告した記録。
    // 🔴 **申告の無い記録は判定を組ませない**ため、既存の肯定形はここを通る形でしか成立しない。
    private static IReadOnlyList<Stage0AsOfInputStatus> AllReconstructed =>
    [
        new(Stage0AsOfInputKind.NewsAndDisclosures, Stage0AsOfInputAvailability.Reconstructed),
        new(Stage0AsOfInputKind.DailyPolicy, Stage0AsOfInputAvailability.Reconstructed),
        new(Stage0AsOfInputKind.FxRateToBase, Stage0AsOfInputAvailability.Reconstructed),
    ];

    private static Stage0DecisionRecord Record(
        DateOnly asOf,
        string symbol,
        int signedQuantity,
        IReadOnlyList<Stage0AsOfInputStatus>? asOfInputs = null) =>
        new(symbol, Market.UnitedStates, asOf, "fp", "claude-sonnet-5", VoteCount: 3,
            RawDecisions: [new Stage0RawDecision(1, Stage0DecisionAction.Buy, "根拠", 100m, 2m, 100, 20, false)],
            MajorityAction: signedQuantity > 0 ? Stage0DecisionAction.Buy : Stage0DecisionAction.Hold,
            MajorityRationale: "根拠", SignedQuantity: signedQuantity, CostJpy: 1m,
            InputTokens: 300, OutputTokens: 60, AsOfInputs: asOfInputs ?? AllReconstructed);

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
        // 🔴 ADR-0039 決定1, #777, IADR-0337: 記録再生戦略はパラメータ探索を持たないため試行は 1 本である。
        // **試行数の記録は免除されない** —— 記録しなければ「探索が無い」と「探索を隠した」が区別できない。
        preparation.GateContext.Trials.Count.Should().Be(1);
        preparation.GateContext.OverfittingPartitions.Should().Be(Stage0ReplayEvaluation.OverfittingPartitions);

        var decision = new Stage0GateService().Evaluate(preparation.GateContext);

        // 🔴 **合否は判定器が決める。**
        // 🔴 **［2026-09-11 変更 / #777・ADR-0039 決定1・決定2・IADR-0337］** 旧アサーション
        // 「試行数条件（`TrialCount`）で必ず落ちる」は**もはや成立しない** —— 探索を持たない試行 1 本では
        // PBO が `評価不能` であり、試行数の下限 20 は適用されない。**合否は残る条件で決まる。**
        decision.Pbo.Should().BeOfType<PboVerdict.NotEvaluable>()
            .Which.Reason.Should().Be(PboNotEvaluableReason.NoSearchSingleTrial);
        decision.Gate.FailedChecks.Should().NotContain(Stage0GateCheck.TrialCount);
        decision.Gate.FailedChecks.Should().NotContain(Stage0GateCheck.Overfitting);
        // この記録（買い 1 回のみ・雑音のバー）では DSR とウォークフォワードが満たせず、なお不合格である。
        decision.Gate.Passed.Should().BeFalse();
        decision.Gate.FailedChecks.Should().Contain(Stage0GateCheck.DeflatedSharpe)
            .And.Contain(Stage0GateCheck.WalkForward);
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

    // ------------------------------------------------------------------------------------------------
    // FR-15, ADR-0036 決定1, #749, IADR-0387: **再構成できなかった as-of 入力の扱い**（フォローアップ 2 の履行）
    // ------------------------------------------------------------------------------------------------

    private static IReadOnlyList<Stage0AsOfInputStatus> Thin(Stage0AsOfInputKind kind) =>
    [
        .. Stage0AsOfInputs.RequiredKinds.Select(k => new Stage0AsOfInputStatus(
            k,
            k == kind
                ? Stage0AsOfInputAvailability.NotReconstructable
                : Stage0AsOfInputAvailability.Reconstructed)),
    ];

    // 申告の欄そのものを持たない記録（旧記録・手書きの JSON が復元される形）。
    private static Stage0DecisionRecord Undeclared(DateOnly asOf, string symbol, int signedQuantity) =>
        Record(asOf, symbol, signedQuantity) with { AsOfInputs = null };

    // 記録を評価期間いっぱいに敷き詰める（日次リターンの標本不足で先に落ちないようにする）。
    private static Stage0DecisionRecord[] Daily(
        int count, IReadOnlyList<Stage0AsOfInputStatus>? asOfInputs = null, int signedQuantity = 10) =>
        [.. Enumerable.Range(1, count).Select(i => Record(From.AddDays(i), "AAPL", signedQuantity, asOfInputs))];

    // 🔴 T-15-109 **陰性対照（最重要）**: 全入力が再構成できていれば**除外は 0 件**で、
    // 判定は従来どおり本物の判定器へ到達する（本変更が既存の合否経路を塞いでいないことを固定する）。
    [Fact]
    public void すべて再構成できていれば除外0件で判定器へ到達する()
    {
        var preparation = Stage0ReplayEvaluation.Prepare(Request(SetOf(records: Daily(3))));

        preparation.IsReady.Should().BeTrue();
        preparation.GateContext!.Exclusions.Should().BeOfType<Stage0ExclusionSummary.Counted>()
            .Which.Should().BeEquivalentTo(new { Excluded = 0, Evaluated = 3 });

        // 🔴 **「除外 0 件」は実測であり、「数えていない」ではない**（verdict まで区別して運ばれる）。
        var decision = new Stage0GateService().Evaluate(preparation.GateContext!);
        decision.Exclusions.IsCounted.Should().BeTrue();
        decision.Exclusions.Format().Should().Contain("除外なし");
    }

    // 🔴 T-15-108 **陽性**: 一部が再構成できなければ、その判断だけが母集団から外れ、件数が verdict へ載る。
    // 残りの判断では判定器へ到達する（**全部を止めるのではなく、外した範囲を明示して進む**）。
    // ［2026-09-24 追記 / PR #931 監査］外してなお進めるのは**見送り（数量 0）**だけである（IADR-0387 決定3 追記）。
    // 数量を持つ判断を外すと残した判断の再生経路が歪むため、T-15-113 が遮断を固定する。
    [Fact]
    public void 再構成できない判断だけが母集団から外れ件数が載る()
    {
        var records = Daily(3).Concat(
            [Record(From.AddDays(4), "AAPL", 0, Thin(Stage0AsOfInputKind.FxRateToBase))]).ToArray();

        var preparation = Stage0ReplayEvaluation.Prepare(Request(SetOf(records: records)));

        preparation.IsReady.Should().BeTrue();
        var counted = preparation.GateContext!.Exclusions.Should()
            .BeOfType<Stage0ExclusionSummary.Counted>().Subject;
        counted.Excluded.Should().Be(1);
        counted.Evaluated.Should().Be(3);
        counted.Kinds.Should().Equal(Stage0AsOfInputKind.FxRateToBase);
    }

    // 🔴 T-15-110 **否定形（最重要・0 件と未供給の区別）**: 記録が再構成可否を申告していなければ
    // 判定を組まない。**「申告が無い＝痩せていない」と読む口を作らない** ——
    // 読めば、痩せた入力での結果がそのまま Stage 0 の合格根拠になり得る（ADR-0036 決定1 が禁じたこと）。
    //
    // 🔴 **`null`（欄そのものが無い旧 JSON）と空の申告を対で見る。** 記録はファイルで持ち込まれる資材であり、
    // 実際に来るのは前者である —— 片方だけ固定すると、`null` を充足へ倒す変異が緑のまま通る。
    [Theory]
    [InlineData(true)]   // 欄が無い（旧記録・手書き）
    [InlineData(false)]  // 欄はあるが空
    public void 再構成可否が未申告の記録では判定を組まない_failclosed(bool absentField)
    {
        // 1 件でも未申告があれば止める（残りが申告済みでも通さない）。
        var undeclared = absentField
            ? Undeclared(From.AddDays(4), "AAPL", 10)
            : Record(From.AddDays(4), "AAPL", 10, asOfInputs: []);
        var records = Daily(3).Concat([undeclared]).ToArray();

        var preparation = Stage0ReplayEvaluation.Prepare(Request(SetOf(records: records)));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Contain(Stage0GateCheck.InputCompletenessNotDeclared);
        preparation.GateContext.Should().BeNull();

        // 🔴 **verdict は「除外 0 件」を名乗らない。** 数えていないことが理由つきで読める。
        var decision = Stage0DriverVerdict.RecordingUnusable(preparation.BlockingChecks);
        decision.Gate.Passed.Should().BeFalse();
        decision.Exclusions.IsCounted.Should().BeFalse();
        decision.Exclusions.Should().BeOfType<Stage0ExclusionSummary.Unknown>()
            .Which.Reason.Should().Be(Stage0ExclusionUnknownReason.CompletenessNotDeclared);
        decision.Exclusions.Format().Should().NotContain("0");
    }

    // 🔴 T-15-110 **否定形**: 3 種を覆わない部分申告も未申告として止める
    // （抜けた種別が黙って「再構成できた」側へ倒れる口を塞ぐ）。
    [Fact]
    public void 部分申告の記録でも判定を組まない_failclosed()
    {
        IReadOnlyList<Stage0AsOfInputStatus> partial =
        [
            new(Stage0AsOfInputKind.NewsAndDisclosures, Stage0AsOfInputAvailability.Reconstructed),
            new(Stage0AsOfInputKind.DailyPolicy, Stage0AsOfInputAvailability.Reconstructed),
        ];

        Stage0ReplayEvaluation.Prepare(Request(SetOf(records: Daily(3, partial))))
            .BlockingChecks.Should().Contain(Stage0GateCheck.InputCompletenessNotDeclared);
    }

    // 🔴 T-15-111 **否定形（最重要）**: 外した結果、母集団が 1 件も残らなければ判定を組まない。
    // 計画 ADR-0036 決定1「**外した結果 Stage 0 の対象が実質的に成立しなくなった場合は、合格としない。
    // 範囲を狭めて通すのではなく、通らないことを報告する**」。全件を外した走行は 1 件も発注しないため
    // 成績が動かず、判定器へ通すと「損失が無い」ように見え得る。
    //
    // ［2026-09-24 追記 / PR #931 監査］数量を持つ判断の除外は `ExcludedDecisionAltersReplayPath` でも止まるため、
    // **全件が見送りの除外**を対で置く —— こちらは本遮断だけが止める（片方だけだと本遮断を外す変異が緑で通る）。
    [Theory]
    [InlineData(10)] // 数量を持つ判断の全件除外（全件除外を先に判定する）
    [InlineData(0)]  // 見送りの全件除外（経路の歪みは無く、本遮断だけが止める）
    public void 全件が除外されたら判定を組まない_failclosed(int signedQuantity)
    {
        var preparation = Stage0ReplayEvaluation.Prepare(
            Request(SetOf(records: Daily(3, Thin(Stage0AsOfInputKind.DailyPolicy), signedQuantity))));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Equal(Stage0GateCheck.AllDecisionsExcluded);
        preparation.GateContext.Should().BeNull();

        var decision = Stage0DriverVerdict.RecordingUnusable(preparation.BlockingChecks);
        decision.Gate.Passed.Should().BeFalse();
        decision.Gate.FormatFailedChecks().Should().Contain(nameof(Stage0GateCheck.AllDecisionsExcluded));
    }

    // 🔴 T-15-111 **否定形**: 除外の遮断は**合格を作らない**（本変更で新たに通る経路が生まれていない）。
    // 記録が痩せている限り、どの入口からも Passed=true は出ない。
    [Theory]
    [InlineData(true)]   // 未申告
    [InlineData(false)]  // 全件除外
    public void 痩せた記録から合格verdictは出ない(bool undeclared)
    {
        var records = undeclared
            ? Daily(3, [])
            : Daily(3, Thin(Stage0AsOfInputKind.NewsAndDisclosures));

        var preparation = Stage0ReplayEvaluation.Prepare(Request(SetOf(records: records)));

        preparation.IsReady.Should().BeFalse();
        Stage0DriverVerdict.RecordingUnusable(preparation.BlockingChecks).Gate.Passed.Should().BeFalse();
    }

    // ------------------------------------------------------------------------------------------------
    // FR-15, ADR-0036 決定1, #749, IADR-0387 決定3［2026-09-24 追記 / PR #931 監査］:
    // **除外は残した判断の再生経路を歪めてはならない。**
    //
    // 再生の注文は目標建玉ではなく**差分**であり、`SignedInventory` で積み上がる（`BacktestSimulator`）。
    // したがって数量を持つ判断を 1 件でも外すと、残した判断が「AI が実際には取らなかった経路」を走る
    // —— 入口を外せば残した出口が裸の空売りを建て、出口を外せば買い建てが開いたまま残る。
    // DSR・最大 DD はその架空の経路を測ることになり、それでも Passed=true が出得た。
    // ------------------------------------------------------------------------------------------------

    private static BacktestContext FlatContext(DateOnly asOf) =>
        new(asOf, [], new Dictionary<(string Symbol, Market Market), InventoryLot>(), 0m);

    // 🔴 T-15-113 **否定形（最重要・監査のプローブの再現）**: 数量を持つ判断を外すと、残した判断の経路が
    // 歪む。**判定を組まず、名前つきの理由で止める**（歪んだ経路の成績を合格根拠にしない）。
    //
    // 監査の実測: 6/2 の Buy +10 を外し 6/3 の Sell −10 を残すと、再生はフラットから −10 を出した。
    [Theory]
    [InlineData(true)]   // 入口を外し出口を残す（裸の空売りが建つ）
    [InlineData(false)]  // 入口を残し出口を外す（買い建てが開いたまま残る）
    public void 数量を持つ判断を外すと残した判断の経路が歪むので判定を組まない_failclosed(bool excludeEntry)
    {
        var entryDay = new DateOnly(2026, 6, 2);
        var exitDay = new DateOnly(2026, 6, 3);
        var thin = Thin(Stage0AsOfInputKind.NewsAndDisclosures);
        var entry = Record(entryDay, "AAPL", 10, excludeEntry ? thin : null);
        var exit = Record(exitDay, "AAPL", -10, excludeEntry ? null : thin);
        var recordSet = SetOf(records: [entry, exit]);

        if (excludeEntry)
        {
            // 前提（歪みの実在）: 再生は入口を外したまま、残した出口をフラットから −10 として出す。
            new RecordedDecisionReplayStrategy(recordSet).DecideOrders(FlatContext(exitDay))
                .Should().ContainSingle().Which.SignedQuantity.Should().Be(-10);
        }

        var preparation = Stage0ReplayEvaluation.Prepare(Request(recordSet));

        preparation.IsReady.Should().BeFalse();
        preparation.BlockingChecks.Should().Equal(Stage0GateCheck.ExcludedDecisionAltersReplayPath);
        preparation.GateContext.Should().BeNull();
        preparation.BaselineRun.Should().BeNull();

        var decision = Stage0DriverVerdict.RecordingUnusable(preparation.BlockingChecks);
        decision.Gate.Passed.Should().BeFalse();
        decision.Gate.FormatFailedChecks()
            .Should().Contain(nameof(Stage0GateCheck.ExcludedDecisionAltersReplayPath));
    }

    // 🔴 T-15-114 **陰性対照**: 外したのが見送り（数量 0）だけなら経路は変わらない。遮断せず判定器へ到達し、
    // 見送りの除外は件数に載る（決定3「見送りも除外として数える」は維持される）。
    // **再生の注文列は、見送りの記録を最初から持たない記録集合と一致する**（経路が変わらないことの直接の確認）。
    [Theory]
    [InlineData(Stage0AsOfInputKind.NewsAndDisclosures)]
    [InlineData(Stage0AsOfInputKind.DailyPolicy)]
    [InlineData(Stage0AsOfInputKind.FxRateToBase)]
    public void 外したのが見送りだけなら経路は変わらず判定器へ到達する(Stage0AsOfInputKind kind)
    {
        var kept = Daily(3);
        var excludedHold = Record(From.AddDays(4), "AAPL", 0, Thin(kind));
        var withHold = SetOf(records: [.. kept, excludedHold]);
        var withoutHold = SetOf(records: kept);

        var preparation = Stage0ReplayEvaluation.Prepare(Request(withHold));

        preparation.IsReady.Should().BeTrue();
        preparation.BlockingChecks.Should().BeEmpty();
        var counted = preparation.GateContext!.Exclusions.Should()
            .BeOfType<Stage0ExclusionSummary.Counted>().Subject;
        counted.Excluded.Should().Be(1);
        counted.Evaluated.Should().Be(3);
        counted.Kinds.Should().Equal(kind);

        var replayed = new RecordedDecisionReplayStrategy(withHold);
        var reference = new RecordedDecisionReplayStrategy(withoutHold);
        for (var day = From; day <= To; day = day.AddDays(1))
        {
            replayed.DecideOrders(FlatContext(day))
                .Should().Equal(reference.DecideOrders(FlatContext(day)));
        }
    }
}
