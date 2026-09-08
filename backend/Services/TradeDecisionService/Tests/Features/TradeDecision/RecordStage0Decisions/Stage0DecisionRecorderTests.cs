extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
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
        LlmPriceTable.From([("claude-sonnet-5", "1", "1")], "1", "1");

    private static Stage0RecordingOptions Options(
        bool enabled = true,
        int voteCount = 1,
        int? approvedVotes = 1,
        decimal? approvedJpy = null,
        string from = "2026-06-01",
        string to = "2026-06-02",
        string? cutoff = "2026-03-31",
        string? outputPath = "records.json") => new()
        {
            Enabled = enabled,
            From = from,
            To = to,
            LlmTrainingCutoff = cutoff,
            Symbols = [new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates }],
            VoteCount = voteCount,
            DecisionsPerDay = 1,
            InputTokensPerDecision = 1_000,
            OutputTokensPerDecision = 1_000,
            ApprovedVoteCount = approvedVotes,
            // 見積り = 銘柄1 × 平日2 × 1 × votes × (1000/1000×1 + 1000/1000×1) = 4 × votes 円
            ApprovedEstimateJpy = approvedJpy ?? 4m * voteCount,
            OutputPath = outputPath,
            Model = "claude-sonnet-5",
        };

    private static (Stage0DecisionRecorder Recorder, FakeLlmClient Llm, CapturingSink Sink, RecordingReporter Reporter)
        Build(IReadOnlyList<string> responses, int tokensPerCall = 1_000)
    {
        var reporter = new RecordingReporter();
        var collector = new Stage0RecordingUsageCollector(reporter);
        var llm = new FakeLlmClient(responses, collector, tokensPerCall);
        var sink = new CapturingSink();
        var recorder = new Stage0DecisionRecorder(
            llm, new StubInputProvider(), sink, collector, Prices(),
            new FixedTimeProvider(Now), NullLogger<Stage0DecisionRecorder>.Instance);
        return (recorder, llm, sink, reporter);
    }

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
    [InlineData(null, 4.0)]      // 金額だけ承認
    [InlineData(2, 4.0)]         // 回数が食い違う（VoteCount=1）
    [InlineData(1, 3.99)]        // 金額が食い違う
    [InlineData(1, 400.0)]       // 桁を取り違えた承認も通さない
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

        estimate.CallCount.Should().Be(6); // 銘柄 1 × 平日 2 × 1 日 1 回 × 多数決 3 回
        estimate.TotalJpy.Should().Be(12m);
        llm.CallCount.Should().Be(0);
    }

    // 肯定形（承認ゲートの対）: 承認が一致すれば実行され、記録が保存される。
    [Fact]
    public async Task 承認が一致すれば記録して保存する()
    {
        var (recorder, llm, sink, _) = Build([Decision("Buy")]);

        var outcome = await recorder.RunAsync(Options(), CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.CallCount.Should().Be(2); // 平日 2 日 × 1 銘柄 × 多数決 1 回
        sink.Saved.Should().NotBeNull();
        sink.Saved!.Records.Should().HaveCount(2);
        sink.Saved.StrategyId.Should().StartWith($"{Stage0StrategyIdentity.Prefix}/claude-sonnet-5/");
        sink.Saved.LlmTrainingCutoff.Should().Be(new DateOnly(2026, 3, 31));
    }

    // ------------------------------------------------------------------------------------------------
    // 2. 見積り超過での停止（ADR-0033 決定5）
    // ------------------------------------------------------------------------------------------------

    // 🔴 **否定形**: 実績が見積りを超えたら停止し、**途中までの記録は保存する**（黙って消費しない）。
    [Fact]
    public async Task 見積り額を超えたら停止し途中までの記録を保存する()
    {
        // 1 呼び出しあたり入力・出力とも 10,000 トークン ＝ 20 円（見積りは 4 円）。1 判断目で超える。
        var (recorder, llm, sink, _) = Build([Decision("Buy")], tokensPerCall: 10_000);

        var outcome = await recorder.RunAsync(Options(), CancellationToken.None);

        outcome.Status.Should().Be(Stage0RecordingStatus.StoppedOverBudget);
        outcome.ActualCostJpy.Should().BeGreaterThan(outcome.Estimate.TotalJpy);
        // 超過は 1 判断ぶんに限られる（2 日目は呼ばれない）。
        llm.CallCount.Should().Be(1);
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

        llm.Purposes.Should().OnlyContain(p => p == LlmPurposes.TradeDecision);
        llm.Models.Should().OnlyContain(m => m == "claude-sonnet-5");
    }

    // 🔴 **否定形**: 記録中でなければ計上は素通しである（本番の計上区分を変えない）。
    [Fact]
    public async Task 記録中でなければ計上は素通しである()
    {
        var reporter = new RecordingReporter();
        var collector = new Stage0RecordingUsageCollector(reporter);

        await collector.ReportAsync(new LlmUsage(LlmPurposes.TradeDecision, 100, 20, "claude-sonnet-5"));

        reporter.Reported.Should().ContainSingle()
            .Which.Purpose.Should().Be(LlmPurposes.TradeDecision);
    }

    // ------------------------------------------------------------------------------------------------
    // テストダブル
    // ------------------------------------------------------------------------------------------------

    // LLM 客の偽装。**本番の egress と同じく、成功応答のトークンを計測へ渡す**
    // （HttpLlmCompletionClient と同じ位置で報告しないと、費用の付け替えを検証できない）。
    private sealed class FakeLlmClient(
        IReadOnlyList<string> responses, Stage0RecordingUsageCollector usage, int tokensPerCall)
        : ILlmCompletionClient
    {
        public int CallCount { get; private set; }

        public List<string?> Purposes { get; } = [];

        public List<string?> Models { get; } = [];

        public async Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null,
            CancellationToken cancellationToken = default)
        {
            var index = CallCount;
            CallCount++;
            Purposes.Add(purpose);
            Models.Add(model);

            await usage.ReportAsync(
                new LlmUsage(purpose ?? LlmPurposes.TradeDecision, tokensPerCall, tokensPerCall, model),
                cancellationToken);

            return responses.Count == 0 ? string.Empty : responses[index % responses.Count];
        }
    }

    // as-of 入力の偽装（AsOf 以前の情報だけを渡す）。
    private sealed class StubInputProvider : IAsOfDecisionInputProvider
    {
        public Task<AsOfDecisionInput?> GetAsync(
            string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default) =>
            Task.FromResult<AsOfDecisionInput?>(new AsOfDecisionInput(
                asOf,
                new DailyPolicy(asOf, "当日の方針"),
                new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m,
                    BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()),
                new DatedPrice(asOf, 100m)));
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
