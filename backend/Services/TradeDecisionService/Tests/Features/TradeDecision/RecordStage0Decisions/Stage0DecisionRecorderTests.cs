extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using TradeDecisionService.Domain;
using Xunit;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-11, FR-15, NFR（費用）, ADR-0003, ADR-0011, ADR-0033 決定4/決定5, #632, IADR-0318:
// Stage 0 記録器の検証。
//
// 見るのは 4 点である。
//   1. 🔴 **否定形（最重要）**: 未承認では LLM が 1 回も呼ばれない（ADR-0033 決定5）
//   2. 🔴 **否定形**: 見積り額を超えたら停止し、途中までの記録が残る
//   3. 多数決の境界（同数→Hold・N=1・N=3）が本番と同一規則であること
//   4. 🔴 **否定形**: 費用が月次上限の区分（取引判断サイクル）へ混ざらないこと
public class Stage0DecisionRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    // 単価: 入力 1 円 / 1k・出力 1 円 / 1k（計算を読みやすくするための任意値。計画の確定値ではない）。
    private static LlmPriceTable Prices() =>
        LlmPriceTable.From([("claude-sonnet-5-5", "1", "1"), ("claude-haiku-5-5", "1", "1")], "1", "1");

    private static Stage0RecordingOptions Options(
        bool enabled = true,
        int voteCount = 1,
        int? approvedVotes = 1,
        decimal? approvedJpy = null,
        string from = "2026-06-01",
        string to = "2026-06-02",
        string? cutoff = "2026-03-31",
        string? outputPath = "records.json",
        IReadOnlyList<Stage0RecordingOptions.SymbolEntry>? symbols = null) => new()
        {
            Enabled = enabled,
            From = from,
            To = to,
            LlmTrainingCutoff = cutoff,
            Symbols = symbols ?? [new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates }],
            VoteCount = voteCount,
            DecisionsPerDay = 1,
            InputTokensPerDecision = 1_000,
            OutputTokensPerDecision = 1_000,
            // #1196, IADR-0498: 一次スクリーニングのトークン量（見積りの一次の項）。
            ScreeningInputTokensPerDecision = 1_000,
            ScreeningOutputTokensPerDecision = 1_000,
            ApprovedVoteCount = approvedVotes,
            // 見積り = 銘柄1 × 平日2 × 1 ×（一次 1 ＋ votes）× (1000/1000×1 + 1000/1000×1) = 4 ×（1 ＋ votes）円
            ApprovedEstimateJpy = approvedJpy ?? 4m * (voteCount + 1),
            OutputPath = outputPath,
            Model = "claude-sonnet-5-5",
        };

    private static (Stage0DecisionRecorder Recorder, FakeLlmClient Llm, CapturingSink Sink, RecordingReporter Reporter)
        Build(
            IReadOnlyList<string> responses,
            int tokensPerCall = 1_000,
            IReadOnlyList<Stage0AsOfInputKind>? notReconstructable = null,
            IReadOnlyList<WatchedSymbol>? asOfWatchlist = null,
            Func<IAsOfDecisionInputProvider, IAsOfDecisionInputProvider>? wrapInputs = null,
            IReadOnlyList<string>? screening = null,
            Func<string?, string?>? effectiveModel = null,
            DecisionOrchestrationOptions? production = null,
            LlmPriceTable? prices = null)
    {
        var reporter = new RecordingReporter();
        var collector = new Stage0RecordingUsageCollector(reporter);
        var llm = new FakeLlmClient(responses, collector, tokensPerCall, screening, effectiveModel);
        var sink = new CapturingSink();
        var recorder = new Stage0DecisionRecorder(
            llm,
            production ?? DecisionOrchestrationOptions.Default,
            wrapInputs is null
                ? new StubInputProvider(notReconstructable, asOfWatchlist)
                : wrapInputs(new StubInputProvider(notReconstructable, asOfWatchlist)),
            sink, collector, prices ?? Prices(),
            new FixedTimeProvider(Now), NullLogger<Stage0DecisionRecorder>.Instance);
        return (recorder, llm, sink, reporter);
    }

    // #1295, IADR-0524: プロンプト長の 2 段を持つ単価表。閾値は claude-haiku-5-5 の実際の境界（入力 100,000 トークン
    // **超**で第 2 段）。単価は計算しやすい任意値（第 1 段 1 円・第 2 段 10 円 / 1k）。
    private static LlmPriceTable TieredPrices() =>
        LlmPriceTable.FromRows(
        [
            new LlmPriceRow("claude-sonnet-5-5", "1", "1", "100000", "10", "10"),
            new LlmPriceRow("claude-haiku-5-5", "1", "1", "100000", "10", "10"),
        ]);

    private static string Decision(string action) =>
        $$"""{"action":"{{action}}","rationale":"根拠","referencePrice":100,"stopLossDistancePerShare":2}""";

    // ------------------------------------------------------------------------------------------------
    // 1. 承認ゲート（ADR-0033 決定5）
    // ------------------------------------------------------------------------------------------------

    // 🔴 **否定形（最重要）**: 既定（無効）では LLM を 1 回も呼ばない。
    [Fact]
    public async Task 既定は無効でLLMを1回も呼ばない()
    {
        var (recorder, llm, sink, _) = Build([Decision("Buy")]);

        var outcome = await recorder.RunAsync(Options(enabled: false), CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.Disabled);
        outcome.CalledLlm.Should().BeFalse();
        llm.CallCount.Should().Be(0);
        sink.Saved.Should().BeNull();
    }

    // 🔴 **否定形（最重要）**: 承認値が無い・食い違うときは LLM を 1 回も呼ばない。
    [Theory]
    [InlineData(null, null)]     // 何も承認していない
    [InlineData(1, null)]        // 回数だけ承認
    [InlineData(null, 8.0)]      // 金額だけ承認
    [InlineData(2, 8.0)]         // 回数が食い違う（VoteCount=1）
    [InlineData(1, 7.99)]        // 金額が食い違う
    [InlineData(1, 800.0)]       // 桁を取り違えた承認も通さない
    [InlineData(1, 4.0)]         // #1196: 一次を数えない旧式の見積り額での承認も通さない
    public async Task 未承認ならLLMを1回も呼ばない(int? approvedVotes, double? approvedJpy)
    {
        var (recorder, llm, sink, _) = Build([Decision("Buy")]);
        var options = Options(approvedVotes: approvedVotes, approvedJpy: (decimal?)approvedJpy);
        options.ApprovedEstimateJpy = (decimal?)approvedJpy;

        var outcome = await recorder.RunAsync(options, CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.NotApproved);
        llm.CallCount.Should().Be(0);
        sink.Saved.Should().BeNull();
    }

    // 🔴 **否定形**: 期間・カットオフ日・銘柄・出力先が未構成なら LLM を 1 回も呼ばない。
    // とくに**出力先が未設定なら実行しない** —— 費用だけ消費して記録が残らない実行を作らない。
    [Theory]
    [InlineData("period")]
    [InlineData("cutoff")]
    [InlineData("output")]
    public async Task 未構成ならLLMを1回も呼ばない(string missing)
    {
        var (recorder, llm, _, _) = Build([Decision("Buy")]);
        var options = missing switch
        {
            "period" => Options(from: "not-a-date"),
            "cutoff" => Options(cutoff: null),
            _ => Options(outputPath: null),
        };

        var outcome = await recorder.RunAsync(options, CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.NotConfigured);
        llm.CallCount.Should().Be(0);
    }

    // 見積りは**実行せずに**取得できる（ADR-0033 決定5 の「提示」）。
    [Fact]
    public void 見積りは実行せずに取得できる()
    {
        var (recorder, llm, _, _) = Build([]);

        var estimate = recorder.Estimate(Options(voteCount: 3));

        // T-15-121, #1196: 銘柄 1 × 平日 2 × 1 日 1 回 ×（一次 1 ＋ 多数決 3 回）
        estimate.CallCount.Should().Be(8);
        estimate.TotalJpy.Should().Be(16m);
        llm.CallCount.Should().Be(0);
    }

    // #1295, IADR-0524: 見積りの単価は層ごとの **1 回あたりの入力トークン量**で段を引く（閾値を超えれば第 2 段）。
    // 本判断と一次（claude-haiku-5-5）の両方の層で、入力トークン量が段の判定へ渡っていることを固定する
    // （片方を落とすと片方の行が赤）。境界ちょうど 100,000 は第 1 段（「100,000 超」で第 2 段）。
    // 銘柄 1 × 平日 2 × 1 日 1 回 → 本判断 2 回（多数決 1）・一次 2 回。出力は 1,000 トークン。
    [Theory]
    // 本判断の入力 / 一次の入力 / 期待額
    [InlineData(100_000, 100_000, 404.0)]     // 両層とも第 1 段（境界ちょうど）: 2×(100+1) + 2×(100+1)
    [InlineData(100_001, 100_000, 2222.02)]   // 本判断だけ第 2 段: 2×(100.001×10 + 1×10) + 2×(100+1)
    [InlineData(100_000, 100_001, 2222.02)]   // 一次だけ第 2 段:   2×(100+1) + 2×(100.001×10 + 1×10)
    public void 見積りは層ごとの入力トークン量でプロンプト長の段を引く(int decisionInput, int screeningInput, double expected)
    {
        var (recorder, _, _, _) = Build([], prices: TieredPrices());
        var options = Options();
        options.InputTokensPerDecision = decisionInput;
        options.ScreeningInputTokensPerDecision = screeningInput;

        recorder.Estimate(options).TotalJpy.Should().Be((decimal)expected);
    }

    // #1295, IADR-0524: 実費（`CostOf`）は**計測ごとの入力トークン数**で段を引く（要求ごとに決まる）。
    [Fact]
    public void 実費は計測ごとの入力トークン数でプロンプト長の段を引く()
    {
        LlmUsage[] usages =
        [
            new(LlmPurposes.TradeDecisionScreening, 100_000, 1_000, "claude-haiku-5-5"), // 境界ちょうど＝第 1 段: 100 + 1
            new(LlmPurposes.TradeDecisionScreening, 100_001, 1_000, "claude-haiku-5-5"), // 100,000 超＝第 2 段: 1000.01 + 10
        ];

        Stage0RecordingUsageCollector.CostOf(usages, TieredPrices()).Should().Be(1111.01m);
    }

    // 肯定形（承認ゲートの対）: 承認が一致すれば実行され、記録が保存される。
    [Fact]
    public async Task 承認が一致すれば記録して保存する()
    {
        var (recorder, llm, sink, _) = Build([Decision("Buy")]);

        var outcome = await recorder.RunAsync(Options(), CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.CallCount.Should().Be(4); // 平日 2 日 × 1 銘柄 ×（一次 1 ＋ 多数決 1 回）
        sink.Saved.Should().NotBeNull();
        sink.Saved!.Records.Should().HaveCount(2);
        sink.Saved.StrategyId.Should().StartWith($"{Stage0StrategyIdentity.Prefix}/claude-sonnet-5-5/");
        sink.Saved.LlmTrainingCutoff.Should().Be(new DateOnly(2026, 3, 31));
    }

    // ------------------------------------------------------------------------------------------------
    // 2. 見積り超過での停止（ADR-0033 決定5）
    // ------------------------------------------------------------------------------------------------

    // 🔴 **否定形**: 実績が見積りを超えたら停止し、**途中までの記録は保存する**（黙って消費しない）。
    [Fact]
    public async Task 見積り額を超えたら停止し途中までの記録を保存する()
    {
        // 1 呼び出しあたり入力・出力とも 10,000 トークン ＝ 20 円（見積りは 8 円）。1 判断目で超える。
        var (recorder, llm, sink, _) = Build([Decision("Buy")], tokensPerCall: 10_000);

        var outcome = await recorder.RunAsync(Options(), CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.StoppedOverBudget);
        outcome.ActualCostJpy.Should().BeGreaterThan(outcome.Estimate.TotalJpy);
        // 超過は 1 判断ぶん（一次 1 ＋ 本判断 1）に限られる（2 日目は呼ばれない）。
        llm.CallCount.Should().Be(2);
        sink.Saved.Should().NotBeNull();
        sink.Saved!.Records.Should().ContainSingle();
    }

    // ------------------------------------------------------------------------------------------------
    // 3. 多数決（ADR-0033 決定4・本番と同一規則）
    // ------------------------------------------------------------------------------------------------

    // 境界値テーブル: N=1（単発）・N=3 の多数決・**同数は安全側 Hold**。
    [Theory]
    [InlineData(1, "Buy", "Buy")]
    [InlineData(3, "Buy,Buy,Hold", "Buy")]
    [InlineData(3, "Sell,Sell,Buy", "Sell")]
    [InlineData(3, "Buy,Sell,Hold", "Hold")]   // 3 すくみ（首位タイ）→ Hold
    [InlineData(2, "Buy,Sell", "Hold")]        // 同数 → Hold（安全側）
    [InlineData(2, "Buy,Buy", "Buy")]
    public async Task 多数決は本番と同一規則である(int voteCount, string responses, string expected)
    {
        var (recorder, _, sink, _) = Build([.. responses.Split(',').Select(Decision)]);
        var options = Options(voteCount: voteCount, approvedVotes: voteCount);

        await recorder.RunAsync(options, CancellationToken.None);

        var record = sink.Saved!.Records[0];
        record.MajorityAction.Should().Be(Enum.Parse<Stage0DecisionAction>(expected));
        // ADR-0033 決定4 / ADR-0003: 生の判断は全回分残る（多数決結果だけにしない）。
        record.RawDecisions.Should().HaveCount(voteCount);
        record.VoteCount.Should().Be(voteCount);
    }

    // 🔴 **否定形**（#290 / IADR-0248）: 解析不能は Hold へ倒すが、**見送りとは区別して記録される**。
    [Fact]
    public async Task 解析不能な出力は見送りと区別して記録される()
    {
        var (recorder, _, sink, _) = Build(["これは JSON ではない"]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        var raw = sink.Saved!.Records[0].RawDecisions.Should().ContainSingle().Which;
        raw.Unparseable.Should().BeTrue();
        raw.Action.Should().Be(Stage0DecisionAction.Hold);
    }

    // 見送りは数量 0（再生側は無発注になる）。
    [Fact]
    public async Task 見送りの判断は数量0で記録される()
    {
        var (recorder, _, sink, _) = Build([Decision("Hold")]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        sink.Saved!.Records[0].SignedQuantity.Should().Be(0);
    }

    // ---- FR-15, ADR-0036 決定1, #749, IADR-0387: 再構成可否を記録へ残す ----

    // T-15-106 **陰性対照**: すべて再構成できたなら記録は 4 種（ADR-0044 決定 3 の (e) を含む）の申告を持ち、除外対象にならない。
    [Fact]
    public async Task 記録はas_of入力の再構成可否を4種そろえて持つ()
    {
        var (recorder, _, sink, _) = Build([Decision("Buy")], asOfWatchlist: [new("AAPL", Market.UnitedStates)]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        var record = sink.Saved!.Records[0];
        Stage0AsOfInputs.IsDeclared(record.AsOfInputs).Should().BeTrue();
        Stage0AsOfInputs.IsExcluded(record.AsOfInputs).Should().BeFalse();
    }

    // 🔴 T-15-106 **陽性（最重要）**: 再構成できなかった項目があっても**記録は止まらない**
    // （計画 ADR-0036 決定1「『外す』は『走らせない』ではない。痩せた入力での実行はしてよい。
    // その結果を合格根拠として引かないことだけを定める」）。外れるのは合否の集計からである。
    [Fact]
    public async Task 再構成できない入力があっても記録は残り除外対象として印がつく()
    {
        var (recorder, llm, sink, _) = Build(
            [Decision("Buy")], notReconstructable: [Stage0AsOfInputKind.FxRateToBase],
            asOfWatchlist: [new("AAPL", Market.UnitedStates)]);

        var outcome = await recorder.RunAsync(Options(), CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.CallCount.Should().BeGreaterThan(0); // 走らせないのではない
        var record = sink.Saved!.Records[0];
        Stage0AsOfInputs.IsDeclared(record.AsOfInputs).Should().BeTrue();
        Stage0AsOfInputs.NotReconstructableKinds(record.AsOfInputs)
            .Should().ContainSingle().Which.Should().Be(Stage0AsOfInputKind.FxRateToBase);
    }

    // 🔴 T-15-107: **戦略 ID は申告を含む。** 判断列が同じでも「何を合否から外すか」が違えば
    // 評価する母集団が違う —— 戦略 ID が同じままだと、別の母集団で採った合格が生き残る（IADR-0281 決定3）。
    [Fact]
    public async Task 戦略IDは再構成可否の申告を含む()
    {
        var (complete, _, completeSink, _) = Build([Decision("Buy")]);
        await complete.RunAsync(Options(), CancellationToken.None);

        var (thin, _, thinSink, _) = Build(
            [Decision("Buy")], notReconstructable: [Stage0AsOfInputKind.DailyPolicy]);
        await thin.RunAsync(Options(), CancellationToken.None);

        // 判断（行動・数量・票）は同一である —— 違うのは申告だけ。
        thinSink.Saved!.Records[0].SignedQuantity
            .Should().Be(completeSink.Saved!.Records[0].SignedQuantity);
        thinSink.Saved!.StrategyId.Should().NotBe(completeSink.Saved!.StrategyId);
    }

    // FR-04, FR-11, ADR-0040 決定5, #822, IADR-0343: 記録の多数決根拠も記録した数量と突合する。
    [Fact]
    public async Task 多数決根拠の株数が記録数量と異なれば注記を追記する()
    {
        var (recorder, _, sink, _) = Build(
            ["""{"action":"Buy","rationale":"1株単位の新規買いが可能","referencePrice":100,"stopLossDistancePerShare":2}"""]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        var record = sink.Saved!.Records[0];
        record.SignedQuantity.Should().BeGreaterThan(1);
        record.MajorityRationale.Should().StartWith("1株単位の新規買いが可能");
        record.MajorityRationale.Should().Contain(RationaleQuantityReconciler.NotePrefix);
        record.MajorityRationale.Should().Contain($"{record.SignedQuantity} 株");
        // 生の判断は各票の出力そのもの（数量を持たない）であり注記しない。
        record.RawDecisions[0].Rationale.Should().Be("1株単位の新規買いが可能");
    }

    // T-10-1809, FR-10, ADR-0049 決定2・決定3, #1120, IADR-0465 決定5: Stage 0 の記録も本番と同じ下限（参照価格の 2%）を掛けてから
    // サイジングする。縮小係数 0.25（5 連敗 × DD 5% 以上）で 1 取引リスク側が効く構成にし、幅の違いを株数で読む:
    // 予算 100,000 × 1% × 0.25 ＝ 250。AI の幅 0.5 → 下限 2 → 125 株（下限が無ければ 500 株 → 残枠で 200 株）。
    // 幅 3（下限以上）→ 83 株（そのまま）。各票の生の幅は AI の値のまま残る。
    [Theory]
    [InlineData(0.5, 125)]
    [InlineData(3, 83)]
    public async Task Stage0の記録も損切り幅に下限を掛けてサイジングする(double width, int expected)
    {
        var shrunk = new SizingContext(100_000m, 50_000m, 20_000m, 5, 0.06m,
            BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());
        var (recorder, _, sink, _) = Build(
            [$$"""{"action":"Buy","rationale":"根拠","referencePrice":100,"stopLossDistancePerShare":{{width}}}"""],
            wrapInputs: _ => new StubInputProvider(sizing: shrunk));

        await recorder.RunAsync(Options(), CancellationToken.None);

        var record = sink.Saved!.Records[0];
        record.SignedQuantity.Should().Be(expected);
        record.RawDecisions[0].StopLossDistancePerShare.Should().Be((decimal)width, "生の判断は AI の幅のまま");
    }

    // 売り判断は負の数量（再生側の空売り観測 IADR-0304 が働く形になる）。
    [Fact]
    public async Task 売り判断は負の数量で記録される()
    {
        var (recorder, _, sink, _) = Build([Decision("Sell")]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        sink.Saved!.Records[0].SignedQuantity.Should().BeNegative();
    }

    // 🔴 プロンプト本文は記録に載せない（保有ポジション・資金残枠等の機微を配布物へ入れない）。指紋だけ残す。
    [Fact]
    public async Task 記録は入力の指紋だけを持ちプロンプト本文を持たない()
    {
        var (recorder, _, sink, _) = Build([Decision("Buy")]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        var json = Stage0DecisionRecordJson.Serialize(sink.Saved!);
        sink.Saved!.Records[0].InputFingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        json.Should().NotContain("リスク制約");   // プロンプトの節見出し
        json.Should().NotContain("出力形式");
    }

    // ------------------------------------------------------------------------------------------------
    // 4. 費用の計上区分（ADR-0033 決定5 / IADR-0318 決定4）
    // ------------------------------------------------------------------------------------------------

    // 🔴 **否定形（最重要）**: 記録の費用は月次上限（取引判断サイクル対象）の区分へ混ざらない。
    // 混ざると、検証の実行が本番取引の抑制（80% で間隔延長・100% で停止）を引き起こす。
    [Fact]
    public async Task 記録の費用は月次上限の区分へ混ざらない()
    {
        var (recorder, _, _, reporter) = Build([Decision("Buy")]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        reporter.Reported.Should().NotBeEmpty();
        reporter.Reported.Should().OnlyContain(u => u.Purpose == LlmPurposes.Stage0Recording);
        reporter.Reported.Should().OnlyContain(u => !LlmCostScope.IsGoverned(u.Purpose));
    }

    // 🔴 呼び出し自体の用途は**本番と同じ** trade-decision である（ADR-0011 のモデル一致・ADR-0017 のフォールバック禁止）。
    [Fact]
    public async Task LLM呼び出しの用途は本番と同じである()
    {
        var (recorder, llm, _, _) = Build([Decision("Buy")]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        // #1196, IADR-0498: 層ごとに本番と同じ用途を名乗る（一次 → 本判断の順）。
        llm.Purposes.Should().Equal(
            LlmPurposes.TradeDecisionScreening, LlmPurposes.TradeDecision,
            LlmPurposes.TradeDecisionScreening, LlmPurposes.TradeDecision);
        llm.Models.Where((_, i) => llm.Purposes[i] == LlmPurposes.TradeDecision)
            .Should().OnlyContain(m => m == "claude-sonnet-5-5");
    }

    // 🔴 #854, IADR-0351 決定7: 記録器は**保有なしを明示して**プロンプトを組む。記録は銘柄 × 判断時点で独立であり、
    // 保有は再生側のシミュレーションでしか決まらない。既定（不明）のままだとプロンプトが「不明なら Hold」と述べ、
    // 全件が Hold へ倒れて Stage 0 が成立しない。
    [Fact]
    public async Task 記録器のプロンプトは保有なしであり不明へ倒れない()
    {
        var (recorder, llm, _, _) = Build([Decision("Buy")]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        llm.Prompts.Should().NotBeEmpty();
        llm.Prompts.Should().OnlyContain(p => p.Contains(TradeDecisionPromptBuilder.HeldNoneLine));
        llm.Prompts.Should().OnlyContain(p => !p.Contains(TradeDecisionPromptBuilder.HeldUnknownLine));
        // T-10-722, #934, IADR-0390 決定6: 未約定の新規建ても「無い」を明示する（不明＝保有不明へ倒さない）。
        llm.Prompts.Should().OnlyContain(p => !p.Contains(TradeDecisionPromptBuilder.WorkingUnknownNoFillsLine));
    }

    // 🔴 FR-04, ADR-0020 決定2, #1081, IADR-0455: 記録器は as-of 時点のニュースの状態を再構成しないため、ニュースの行は
    // 「不明」と書く。本番の最新値（取得済み等）を持ち込むと、当時は知り得なかった状態で判断させることになる。
    [Fact]
    public async Task 記録器のプロンプトはニュースの状態を不明と書く()
    {
        var (recorder, llm, _, _) = Build([Decision("Buy")]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        llm.Prompts.Should().NotBeEmpty();
        llm.Prompts.Should().OnlyContain(p => p.Contains(TradeDecisionPromptBuilder.NewsUnknownLine));
        llm.Prompts.Should().OnlyContain(p =>
            !p.Contains(TradeDecisionPromptBuilder.NewsFetchedLine)
            && !p.Contains(TradeDecisionPromptBuilder.NewsOutageLine)
            && !p.Contains(TradeDecisionPromptBuilder.NewsNotConfiguredLine));
    }

    // 🔴 T-10-1547 **否定形（最重要）**, FR-04, ADR-0044 決定 3・4, #1034, IADR-0440 決定 7（2026-09-27 改訂）:
    // 記録の対象銘柄を監視銘柄の代わりに渡さない。当時の監視銘柄が無い（再構成の供給口が無い）あいだ、監視銘柄の節は
    // 「不明」であり、記録は (e) を再構成不可と申告して Stage 0 の合否から外れる（記録そのものは残す）。
    [Fact]
    public async Task 記録の対象銘柄は監視銘柄の節に流れ込まず不明と書き記録は合否から外れる()
    {
        var (recorder, llm, sink, _) = Build([Decision("Buy")]);

        var outcome = await recorder.RunAsync(
            Options(
                approvedJpy: 16m,
                symbols:
                [
                    new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates },
                    new Stage0RecordingOptions.SymbolEntry { Symbol = "MSFT", Market = Market.UnitedStates },
                ]),
            CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.Prompts.Should().NotBeEmpty();
        llm.Prompts.Should().OnlyContain(p => p.Contains(TradeDecisionPromptBuilder.WatchlistSectionTitle));
        llm.Prompts.Should().OnlyContain(p => p.Contains(TradeDecisionPromptBuilder.WatchlistUnknownLine));
        // 記録の対象銘柄（AAPL・MSFT）が一覧の行・件数・所属の行として出ない。
        llm.Prompts.Should().OnlyContain(p => !p.Contains("""{"symbol":"""));
        llm.Prompts.Should().OnlyContain(p => !p.Contains("- ウォッチリスト: 2 件") && !p.Contains("- ウォッチリスト: 1 件"));
        llm.Prompts.Should().OnlyContain(p => !p.Contains(TradeDecisionPromptBuilder.WatchlistContainsSuffix));
        llm.Prompts.Should().OnlyContain(p => !p.Contains(TradeDecisionPromptBuilder.WatchlistNotContainsSuffix));

        sink.Saved!.Records.Should().HaveCount(4).And.OnlyContain(r =>
            Stage0AsOfInputs.IsDeclared(r.AsOfInputs)
            && Stage0AsOfInputs.NotReconstructableKinds(r.AsOfInputs).SequenceEqual(new[] { Stage0AsOfInputKind.Watchlist }));
    }

    // T-10-1547 肯定形, ADR-0044 決定 3: 当時の監視銘柄が供給されたら、記録器はそれ（だけ）を節へ載せる。
    // 記録の対象（AAPL）と違う一覧（META・NVDA）を渡し、節が記録の対象ではなく当時の一覧に従うことを確かめる。
    [Fact]
    public async Task 当時の監視銘柄が供給されればそれを監視銘柄の節に載せる()
    {
        var (recorder, llm, sink, _) = Build(
            [Decision("Buy")],
            asOfWatchlist: [new("META", Market.UnitedStates), new("NVDA", Market.UnitedStates)]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        llm.Prompts.Should().NotBeEmpty();
        llm.Prompts.Should().OnlyContain(p => p.Contains("""{"symbol":"META","market":"UnitedStates"}"""));
        llm.Prompts.Should().OnlyContain(p => p.Contains("""{"symbol":"NVDA","market":"UnitedStates"}"""));
        llm.Prompts.Should().OnlyContain(p => !p.Contains("""{"symbol":"AAPL","""));
        llm.Prompts.Should().OnlyContain(p => p.Contains("- ウォッチリスト: 2 件"));
        llm.Prompts.Should().OnlyContain(p =>
            p.Contains($"判断対象の AAPL（市場: UnitedStates）{TradeDecisionPromptBuilder.WatchlistNotContainsSuffix}"));
        llm.Prompts.Should().OnlyContain(p => !p.Contains(TradeDecisionPromptBuilder.WatchlistUnknownLine));
        Stage0AsOfInputs.IsExcluded(sink.Saved!.Records[0].AsOfInputs).Should().BeFalse();
    }

    // 🔴 T-10-1630, FR-04, ADR-0044 決定 3, ADR-0046 決定 1, #1049, IADR-0442 決定 4: 本番の組み立てと同じく供給をデコレータで包むと、
    // 当時の監視銘柄（META・NVDA）が節に載り、記録の対象銘柄（AAPL・MSFT）は一覧の行として載らない。照会は判断時点ごとに 1 回で、
    // 時刻は AsOf の UTC の日の終わりである。再構成できたので (e) は除外の理由にならない。
    [Fact]
    public async Task 当時の監視銘柄の供給口が再構成した一覧を節に載せ記録の対象銘柄は載らない()
    {
        var source = new RecordingAsOfWatchlistSource(
            AsOfWatchlist.Reconstructed([new("META", Market.UnitedStates), new("NVDA", Market.UnitedStates)]));
        var (recorder, llm, sink, _) = Build(
            [Decision("Buy")], wrapInputs: inner => new WatchlistAsOfDecisionInputProvider(inner, source));

        await recorder.RunAsync(
            Options(
                approvedJpy: 16m,
                symbols:
                [
                    new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates },
                    new Stage0RecordingOptions.SymbolEntry { Symbol = "MSFT", Market = Market.UnitedStates },
                ]),
            CancellationToken.None);

        llm.Prompts.Should().HaveCount(4);
        llm.Prompts.Should().OnlyContain(p => p.Contains("""{"symbol":"META","market":"UnitedStates"}"""));
        llm.Prompts.Should().OnlyContain(p => p.Contains("""{"symbol":"NVDA","market":"UnitedStates"}"""));
        llm.Prompts.Should().OnlyContain(p => !p.Contains("""{"symbol":"AAPL",""") && !p.Contains("""{"symbol":"MSFT","""));
        llm.Prompts.Should().OnlyContain(p => p.Contains(TradeDecisionPromptBuilder.WatchlistNotContainsSuffix));
        llm.Prompts.Should().OnlyContain(p => !p.Contains(TradeDecisionPromptBuilder.WatchlistUnknownLine));
        source.Queried.Should().Equal(
            new DateTimeOffset(2026, 6, 1, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
            new DateTimeOffset(2026, 6, 2, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999));
        sink.Saved!.Records.Should().OnlyContain(r => !Stage0AsOfInputs.IsExcluded(r.AsOfInputs));
    }

    // 🔴 T-10-1630: 再構成できない時点（例: SeededAt より前）は、節を「不明」と書き、供給口の理由を (e) の申告へ載せて合否から外す。
    // 記録そのものは残る（ADR-0036 決定 1）。記録の対象銘柄で代えない。
    [Fact]
    public async Task 再構成できない時点は不明と書き理由つきで合否から外れる()
    {
        var source = new RecordingAsOfWatchlistSource(
            AsOfWatchlist.NotReconstructable("SeededAt より前の時点です（その時点の監視銘柄は無かったか空でした）。"));
        var (recorder, llm, sink, _) = Build(
            [Decision("Buy")], wrapInputs: inner => new WatchlistAsOfDecisionInputProvider(inner, source));

        var outcome = await recorder.RunAsync(Options(), CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.Prompts.Should().OnlyContain(p => p.Contains(TradeDecisionPromptBuilder.WatchlistUnknownLine));
        llm.Prompts.Should().OnlyContain(p => !p.Contains("""{"symbol":"""));
        sink.Saved!.Records.Should().NotBeEmpty().And.OnlyContain(r =>
            Stage0AsOfInputs.NotReconstructableKinds(r.AsOfInputs).SequenceEqual(new[] { Stage0AsOfInputKind.Watchlist })
            && r.AsOfInputs!.Single(s => s.Kind == Stage0AsOfInputKind.Watchlist).Reason.Contains("SeededAt より前"));
    }

    private sealed class RecordingAsOfWatchlistSource(AsOfWatchlist result) : IAsOfWatchlistSource
    {
        public List<DateTimeOffset> Queried { get; } = [];

        public Task<AsOfWatchlist> GetWatchlistAtAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            Queried.Add(at);
            return Task.FromResult(result);
        }
    }

    // 🔴 **否定形**: 記録中でなければ計上は素通しである（本番の計上区分を変えない）。
    [Fact]
    public async Task 記録中でなければ計上は素通しである()
    {
        var reporter = new RecordingReporter();
        var collector = new Stage0RecordingUsageCollector(reporter);

        await collector.ReportAsync(new LlmUsage(LlmPurposes.TradeDecision, 100, 20, "claude-sonnet-5-5"));

        reporter.Reported.Should().ContainSingle()
            .Which.Purpose.Should().Be(LlmPurposes.TradeDecision);
    }

    // ------------------------------------------------------------------------------------------------
    // 5. 本番と同じ二段（FR-15, ADR-0054 決定3・4, #1196, IADR-0498）
    // ------------------------------------------------------------------------------------------------

    private const string ScreeningHold = """{"action":"Hold","rationale":"関心なし"}""";

    // 🔴 T-15-115（受け入れ基準 1）: 一次（trade-decision-screening）→ 本判断（trade-decision）の順に呼び、
    // **一次が Hold なら本判断を呼ばない**。記録には一次の見送りが残り、本判断の票は 0（数量 0・Hold）。
    [Fact]
    public async Task 一次がHoldなら本判断を呼ばず一次の見送りを記録する()
    {
        var (recorder, llm, sink, _) = Build([Decision("Buy")], screening: [ScreeningHold]);

        var outcome = await recorder.RunAsync(Options(), CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.Purposes.Should().Equal(LlmPurposes.TradeDecisionScreening, LlmPurposes.TradeDecisionScreening);
        llm.Prompts.Should().BeEmpty("一次で見送れば本判断のプロンプトは送られない");
        sink.Saved!.Records.Should().HaveCount(2).And.OnlyContain(r =>
            r.MajorityAction == Stage0DecisionAction.Hold
            && r.SignedQuantity == 0
            && r.RawDecisions.Count == 0
            && r.Screening != null
            && r.Screening.Action == Stage0DecisionAction.Hold
            && !r.Screening.Interested
            && r.MajorityRationale == "関心なし");
    }

    // 🔴 T-10-2518（#1290, IADR-0525 決定 3）: 一次の根拠文に化けの疑いがあれば、Stage 0 の記録（一次の根拠・見送りの多数決の根拠）に
    // 目印が付く。見送り（Hold）・本判断を呼ばないことは変えない。化けの無い根拠文（上の試験の「関心なし」）には付かない。
    [Fact]
    public async Task T_10_2518_一次の根拠文の化けはStage0の記録に目印を付ける()
    {
        const string garbled = "監視銘româ内の銘柄だが方向感が無い";
        var (recorder, llm, sink, _) = Build(
            [Decision("Buy")], screening: [$$"""{"action":"Hold","rationale":"{{garbled}}"}"""]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        llm.Prompts.Should().BeEmpty("見送りは変えない（本判断を呼ばない）");
        var expected = $"{RationaleGarbleDetector.Marker}: {garbled}";
        sink.Saved!.Records.Should().OnlyContain(r =>
            r.MajorityAction == Stage0DecisionAction.Hold
            && r.Screening!.Rationale == expected
            && r.MajorityRationale == expected);
    }

    // T-10-2523（#1290, IADR-0525 決定 3/4）: 一次が関心あり（本判断へ進む）で一次の根拠文が化けたら、一次の根拠にだけ目印が付き、
    // 多数決の根拠（本判断の根拠文。化けていない）には付かない。本判断の根拠文が化けたら多数決の根拠に付き、各票の生の根拠には付けない。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_2523_Stage0の記録は化けた層の根拠にだけ目印を付ける(bool screeningGarbled)
    {
        const string garbled = "監視銘HeaderItemの押し目";
        var screening = screeningGarbled
            ? $$"""{"action":"Buy","rationale":"{{garbled}}"}"""
            : """{"action":"Buy","rationale":"関心あり"}""";
        var main = screeningGarbled
            ? Decision("Buy")
            : $$"""{"action":"Buy","rationale":"{{garbled}}","referencePrice":100,"stopLossDistancePerShare":2}""";
        var (recorder, _, sink, _) = Build([main], screening: [screening]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        var marked = $"{RationaleGarbleDetector.Marker}: {garbled}";
        var record = sink.Saved!.Records[0];
        record.MajorityAction.Should().Be(Stage0DecisionAction.Buy, "化けで action を変えない");
        record.Screening!.Rationale.Should().Be(screeningGarbled ? marked : "関心あり");
        record.MajorityRationale.Should().Be(screeningGarbled ? "根拠" : marked);
        record.RawDecisions.Should().OnlyContain(r => !r.Rationale.Contains(RationaleGarbleDetector.Marker));
    }

    // 🔴 T-15-115: 一次の**解析不能も打ち切る**（本番の `ParseScreening` と同じ。見送りとは区別して記録する・IADR-0248）。
    [Fact]
    public async Task 一次が解析不能なら本判断を呼ばず解析不能として記録する()
    {
        var (recorder, llm, sink, _) = Build([Decision("Buy")], screening: ["これは JSON ではない"]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        llm.Purposes.Should().OnlyContain(p => p == LlmPurposes.TradeDecisionScreening);
        var screening = sink.Saved!.Records[0].Screening!;
        screening.Unparseable.Should().BeTrue();
        screening.Interested.Should().BeFalse();
        sink.Saved.Records[0].RawDecisions.Should().BeEmpty();
    }

    // T-15-115: 判断時点ごとに一次 → 本判断の順。一次が関心ありの日だけ本判断が多数決回数ぶん走る（1 日目 Hold・2 日目 Buy）。
    [Fact]
    public async Task 判断時点ごとに一次の結果で本判断の有無が決まる()
    {
        var (recorder, llm, sink, _) = Build(
            [Decision("Buy"), Decision("Buy"), Decision("Hold")],
            screening: [ScreeningHold, FakeLlmClient.Interested]);

        await recorder.RunAsync(Options(voteCount: 3, approvedVotes: 3), CancellationToken.None);

        llm.Purposes.Should().Equal(
            LlmPurposes.TradeDecisionScreening,
            LlmPurposes.TradeDecisionScreening, LlmPurposes.TradeDecision, LlmPurposes.TradeDecision, LlmPurposes.TradeDecision);
        var records = sink.Saved!.Records;
        records[0].RawDecisions.Should().BeEmpty();
        records[0].MajorityAction.Should().Be(Stage0DecisionAction.Hold);
        records[1].Screening!.Interested.Should().BeTrue();
        records[1].RawDecisions.Select(r => r.Attempt).Should().Equal(1, 2, 3);
        records[1].MajorityAction.Should().Be(Stage0DecisionAction.Buy);
    }

    // T-15-116: 一次のプロンプトは本番と同じ組み立て（`TradeDecisionPromptBuilder.BuildScreening`。保有なし・未約定なし・
    // ニュースは不明）で、本判断のプロンプトとは別物である。本番の構成に予算があれば縮退の枝（参考情報を載せる形）を通る。
    // 費用は両層の合計（1 呼び出し 2 円 × 2）。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 一次のプロンプトは本番と同じ組み立てで費用は両層の合計である(bool budgeted)
    {
        var production = budgeted
            ? DecisionOrchestrationOptions.Default with
            {
                ScreeningContextBudgetChars = DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars,
            }
            : DecisionOrchestrationOptions.Default;
        var (recorder, llm, sink, _) = Build([Decision("Buy")], production: production);

        await recorder.RunAsync(Options(), CancellationToken.None);

        var input = (await new StubInputProvider().GetAsync(
            "AAPL", Market.UnitedStates, new DateOnly(2026, 6, 1), CancellationToken.None))!;
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);
        var expected = budgeted
            ? TradeDecisionPromptBuilder.BuildScreening(
                trigger, input.Policy, input.Sizing, input.ReferencePrice,
                ScreeningContextAssembler.Assemble(
                    trigger, input.Policy, input.References, input.ReferencePrice,
                    DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars, input.Watchlist).RetainedReferences,
                HeldPosition.None, WorkingEntryOrders.None, input.Watchlist, input.Intraday, news: null, input.Volume)
            : TradeDecisionPromptBuilder.BuildScreening(
                trigger, input.Policy, input.Sizing, input.ReferencePrice, held: HeldPosition.None,
                working: WorkingEntryOrders.None, watchlist: input.Watchlist, intraday: input.Intraday, volume: input.Volume);

        llm.ScreeningPrompts.Should().HaveCount(2);
        llm.ScreeningPrompts[0].Should().Be(expected);
        llm.ScreeningPrompts.Should().OnlyContain(p =>
            p.Contains(TradeDecisionPromptBuilder.HeldNoneLine) && p.Contains(TradeDecisionPromptBuilder.NewsUnknownLine));
        llm.ScreeningPrompts.Should().NotIntersectWith(llm.Prompts);

        var record = sink.Saved!.Records[0];
        record.CostJpy.Should().Be(4m);
        record.InputTokens.Should().Be(2_000);
        record.Screening!.InputTokens.Should().Be(1_000);
        record.RawDecisions[0].InputTokens.Should().Be(1_000);
    }

    // 🔴 T-15-117（受け入れ基準 2）: 記録には**両層の実効モデル（応答が名乗った値）が別々に**残る。構成の希望値ではない。
    [Fact]
    public async Task 記録は両層の実効モデルを別々に持つ()
    {
        var (recorder, _, sink, _) = Build([Decision("Buy"), Decision("Buy")]);
        var options = Options(voteCount: 2, approvedVotes: 2);

        await recorder.RunAsync(options, CancellationToken.None);

        var record = sink.Saved!.Records[0];
        record.Screening!.EffectiveModelId.Should().Be("claude-haiku-5-5");
        record.RawDecisions.Should().HaveCount(2).And.OnlyContain(r => r.EffectiveModelId == "claude-sonnet-5-5");
        Stage0TwoTierModels.MatchesPinnedAssignments(record).Should().BeTrue();
    }

    // 🔴 T-15-117: 希望値と違うモデルが答えたら、記録に残るのは**答えたモデル**である（希望値で上書きしない）。
    // 名乗らなかった応答（計測にモデルが無い）は null ＝不明。どちらもピンとの照合で一致にならない。
    [Theory]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5")]  // 一次がピン（haiku）以外
    [InlineData(null, null)]                            // 一次が名乗らない
    public async Task 記録の実効モデルは応答が名乗った値で希望値ではない(string? screeningEffective, string? expected)
    {
        var (recorder, _, sink, _) = Build(
            [Decision("Buy")],
            effectiveModel: p => p == LlmPurposes.TradeDecisionScreening ? screeningEffective : "claude-sonnet-5-5");
        var options = Options();
        options.ScreeningModel = "claude-haiku-5-5"; // 希望値

        await recorder.RunAsync(options, CancellationToken.None);

        var record = sink.Saved!.Records[0];
        record.Screening!.EffectiveModelId.Should().Be(expected);
        record.RawDecisions[0].EffectiveModelId.Should().Be("claude-sonnet-5-5");
        Stage0TwoTierModels.MatchesPinnedAssignments(record).Should().BeFalse();
    }

    // T-15-117: 一次の希望値は一次の呼び出しへ、本判断の希望値は本判断の呼び出しへ渡る（層を取り違えない）。
    [Fact]
    public async Task モデルの希望値は層ごとに渡る()
    {
        var (recorder, llm, _, _) = Build([Decision("Buy")]);
        var options = Options();
        options.ScreeningModel = "claude-haiku-5-5";

        await recorder.RunAsync(options, CancellationToken.None);

        llm.Models.Should().Equal("claude-haiku-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-sonnet-5-5");
    }

    // ------------------------------------------------------------------------------------------------
    // テストダブル
    // ------------------------------------------------------------------------------------------------

    // LLM 客の偽装。**本番の egress と同じく、成功応答のトークンを計測へ渡す**
    // （HttpLlmCompletionClient と同じ位置で報告しないと、費用の付け替えを検証できない）。
    // FR-15, ADR-0054 決定3, #1196, IADR-0498: 二段の偽装。一次（trade-decision-screening）は screening を順に返し
    // （既定は関心あり＝本判断へ進める）、本判断（trade-decision）は responses を順に返す。計測の実効モデルは既定で用途のピン
    // （LlmAssignments）であり、effectiveModel で用途ごとに差し替えられる（null を返せば「名乗らなかった」）。
    // **Prompts は本判断のプロンプトだけ**を集める（既存の試験は本判断のプロンプトを検証している）。一次は ScreeningPrompts。
    private sealed class FakeLlmClient(
        IReadOnlyList<string> responses,
        Stage0RecordingUsageCollector usage,
        int tokensPerCall,
        IReadOnlyList<string>? screening = null,
        Func<string?, string?>? effectiveModel = null)
        : ILlmCompletionClient
    {
        public const string Interested = """{"action":"Buy","rationale":"関心あり"}""";

        private int _decisionCalls;
        private int _screeningCalls;

        public int CallCount { get; private set; }

        public List<string?> Purposes { get; } = [];

        public List<string?> Models { get; } = [];

        public List<string> Prompts { get; } = [];

        public List<string> ScreeningPrompts { get; } = [];

        public async Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Purposes.Add(purpose);
            Models.Add(model);

            var effective = effectiveModel is null
                ? LlmAssignments.For(purpose)?.PrimaryModel
                : effectiveModel(purpose);
            await usage.ReportAsync(
                new LlmUsage(purpose ?? LlmPurposes.TradeDecision, tokensPerCall, tokensPerCall, effective),
                cancellationToken);

            if (purpose == LlmPurposes.TradeDecisionScreening)
            {
                ScreeningPrompts.Add(prompt);
                var index = _screeningCalls++;
                return screening is null || screening.Count == 0 ? Interested : screening[index % screening.Count];
            }

            Prompts.Add(prompt);
            var decision = _decisionCalls++;
            return responses.Count == 0 ? string.Empty : responses[decision % responses.Count];
        }
    }

    // as-of 入力の偽装（AsOf 以前の情報だけを渡す）。
    // FR-15, ADR-0036 決定1, #749, IADR-0387: 再構成できなかった種別を申告する経路も張る。
    // FR-04, ADR-0044 決定 3, #1034: 当時の監視銘柄（(e)）を供給する経路も張る（null＝再構成できなかった）。
    // #1035, IADR-0451: previousCloseValue を与えると、判断時点の前日の日付で前日終値（日足）を渡す。
    private sealed class StubInputProvider(
        IReadOnlyList<Stage0AsOfInputKind>? notReconstructable = null,
        IReadOnlyList<WatchedSymbol>? asOfWatchlist = null,
        decimal? previousCloseValue = null,
        SizingContext? sizing = null)
        : IAsOfDecisionInputProvider
    {
        public Task<AsOfDecisionInput?> GetAsync(
            string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default) =>
            Task.FromResult<AsOfDecisionInput?>(new AsOfDecisionInput(
                asOf,
                new DailyPolicy(asOf, "当日の方針"),
                sizing ?? new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m,
                    BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()),
                new DatedPrice(asOf, 100m),
                notReconstructable: notReconstructable,
                watchlist: asOfWatchlist,
                previousClose: previousCloseValue is { } pc ? new DatedPrice(asOf.AddDays(-1), pc) : null));
    }

    // ------------------------------------------------------------------------------------------------
    // FR-02, FR-04, ADR-0033 決定2, #1035, IADR-0451: 値動きの行（前日比は日足の前日終値から・当日の変化率は不明）
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task 前日終値があれば前日比を出し当日の変化率と日中高安は不明とする()
    {
        var (recorder, llm, _, _) = Build(
            [Decision("Hold"), Decision("Hold")],
            wrapInputs: _ => new StubInputProvider(previousCloseValue: 80m));

        await recorder.RunAsync(Options(), CancellationToken.None);

        llm.Prompts.Should().NotBeEmpty();
        llm.Prompts.Should().OnlyContain(p =>
            p.Contains("- 前日終値: 80 / 前日比: +25.00%")
            && p.Contains("- 当日始値: 不明 / 当日始値比: 不明")
            && p.Contains("- 日中高値: 不明 / 日中安値: 不明")
            && p.Contains(TradeDecisionPromptBuilder.VolumeNotProvidedLine));
    }

    [Fact]
    public async Task 前日終値が無ければ前日比も不明とする_否定形()
    {
        var (recorder, llm, _, _) = Build([Decision("Hold"), Decision("Hold")]);

        await recorder.RunAsync(Options(), CancellationToken.None);

        llm.Prompts.Should().NotBeEmpty();
        llm.Prompts.Should().OnlyContain(p => p.Contains("- 前日終値: 不明 / 前日比: 不明"));
    }

    // 🔴 否定形: 判断時点以降の終値を前日終値として渡せない（未来の値で判断させない・ADR-0033 決定2）。
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 判断時点以降の終値を前日終値に渡すと例外(int daysAfterAsOf)
    {
        var asOf = new DateOnly(2026, 6, 1);
        var act = () => new AsOfDecisionInput(
            asOf,
            new DailyPolicy(asOf, "当日の方針"),
            new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()),
            previousClose: new DatedPrice(asOf.AddDays(daysAfterAsOf), 99m));

        act.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("previousClose");
    }

    [Fact]
    public void 監視銘柄を差し替えても前日終値は保たれる()
    {
        var asOf = new DateOnly(2026, 6, 1);
        var input = new AsOfDecisionInput(
            asOf,
            new DailyPolicy(asOf, "当日の方針"),
            new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()),
            previousClose: new DatedPrice(asOf.AddDays(-3), 99m));

        input.Intraday.Should().Be(new IntradayPriceContext(99m, null, null, null));
        input.WithWatchlist([], unavailableReason: null).Intraday.Should().Be(input.Intraday);
    }

    private sealed class CapturingSink : IStage0DecisionRecordSink
    {
        public Stage0DecisionRecordSet? Saved { get; private set; }

        public Task<bool> SaveAsync(
            Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default)
        {
            Saved = recordSet;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingReporter : ILlmUsageReporter
    {
        public List<LlmUsage> Reported { get; } = [];

        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default)
        {
            Reported.Add(usage);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
