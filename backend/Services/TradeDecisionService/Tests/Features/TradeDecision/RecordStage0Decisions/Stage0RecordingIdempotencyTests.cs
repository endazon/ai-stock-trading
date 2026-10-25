extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Xunit;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// FR-15, NFR（費用）, ADR-0033 決定5, #1342, IADR-0537: Stage 0 の記録器の冪等化と費用の累計の上限。
//
// 「再起動」は、同じ状態の置き場（InMemoryStage0RecordingState）を新しい記録器へ渡して表す（永続領域だけが残る）。
// 見るのは次のとおり（作業仕様書 20261014_1342 の受け入れ基準）。
//   1. 🔴 完了した後の 2 回目の起動では LLM を 1 回も呼ばない
//   2. 🔴 途中で落ちた後は、記録済みの判断時点を呼ばずに途中から再開して完了する
//   3. 🔴 LLM の応答の後・チェックポイントの前に落ちたら、その判断時点の費用も累計に入り、上限で止まる
//   4. 🔴 状態（印・チェックポイント・台帳）が読めない・書けない・ロックが取れないときは LLM を呼ばない
//   5. 🔴 印が無くても、台帳の累計が承認額に届いていれば呼ばない（両端で止める）
//   6. 🔴 呼ぶ前に「累計＋1 判断時点の見積り」が承認額を超える見込みなら止める
//   7. 上限に届かない通常の完走は従来どおり
public class Stage0RecordingIdempotencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    // 単価: 入力 1 円 / 1k・出力 1 円 / 1k。1 呼び出し 1,000 / 1,000 トークン ＝ 2 円。1 判断時点（一次 1 ＋ 本判断 1）＝ 4 円。
    // #1305, IADR-0553: 表に無いモデル（下の claude-opus-5-5 への差し替え）も同じ 1/1 で引く（未知モデルの計上単価を明示する）。
    private static LlmPriceTable Prices() =>
        LlmPriceTable.From(
            [("claude-sonnet-5-5", "1", "1"), ("claude-haiku-5-5", "1", "1")], "1", "1",
            unknownModelInputPer1kTokens: "1", unknownModelOutputPer1kTokens: "1");

    // 2026-06-01（月）〜 to の平日 × AAPL。見積り = 平日数 × 4 円。
    private static Stage0RecordingOptions Options(string to = "2026-06-03", decimal? approvedJpy = null) => new()
    {
        Enabled = true,
        From = "2026-06-01",
        To = to,
        LlmTrainingCutoff = "2026-03-31",
        Symbols = [new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates }],
        VoteCount = 1,
        DecisionsPerDay = 1,
        InputTokensPerDecision = 1_000,
        OutputTokensPerDecision = 1_000,
        ScreeningInputTokensPerDecision = 1_000,
        ScreeningOutputTokensPerDecision = 1_000,
        ApprovedVoteCount = 1,
        ApprovedEstimateJpy = approvedJpy ?? 4m * Stage0RecordingBudget.WeekdayCount(new DateOnly(2026, 6, 1), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture)),
        OutputPath = "records.json",
        StateDirectory = "state",
        Model = "claude-sonnet-5-5",
    };

    private static (Stage0DecisionRecorder Recorder, CountingLlm Llm, CapturingSink Sink) Build(
        InMemoryStage0RecordingState state, int tokensPerCall = 1_000)
    {
        var collector = new Stage0RecordingUsageCollector(new NullReporter());
        var llm = new CountingLlm(collector, tokensPerCall);
        var sink = new CapturingSink();
        var recorder = new Stage0DecisionRecorder(
            llm, DecisionOrchestrationOptions.Default, new StubInputs(), sink, state, state, collector, Prices(),
            new FixedTimeProvider(Now), NullLogger<Stage0DecisionRecorder>.Instance);
        return (recorder, llm, sink);
    }

    // ------------------------------------------------------------------------------------------------
    // 1. 完了の印（窓の後の端）
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task 完了した後の2回目の起動ではLLMを1回も呼ばない()
    {
        var state = new InMemoryStage0RecordingState();
        var (first, firstLlm, _) = Build(state);
        (await first.RunAsync(Options(), TestContext.Current.CancellationToken)).Status
            .Should().Be(Stage0RecordingStatus.Completed);
        firstLlm.Calls.Should().Be(6);

        var (second, secondLlm, secondSink) = Build(state);
        var outcome = await second.RunAsync(Options(), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.AlreadyFinished);
        outcome.CalledLlm.Should().BeFalse();
        secondLlm.Calls.Should().Be(0);
        secondSink.Saved.Should().BeNull("記録は上書きしない");
    }

    // 書き出し先は印の鍵に入らない（置き場を変えただけで同じ承認値の記録がもう一度走らない）。
    [Fact]
    public async Task 書き出し先を変えても完了済みの記録はもう一度走らない()
    {
        var state = new InMemoryStage0RecordingState();
        await Build(state).Recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        var moved = Options();
        moved.OutputPath = "elsewhere/records.json";
        var (second, llm, _) = Build(state);

        (await second.RunAsync(moved, TestContext.Current.CancellationToken)).Status
            .Should().Be(Stage0RecordingStatus.AlreadyFinished);
        llm.Calls.Should().Be(0);
    }

    // ------------------------------------------------------------------------------------------------
    // 2. 途中で落ちた後（開始の印だけがある）
    // ------------------------------------------------------------------------------------------------

    // 🔴 規則 11 のプローブ A・B（増える側・減る側）: 1 判断時点を書いたところで落ちる → 再起動で残り 2 判断時点だけを呼び、完了する。
    [Fact]
    public async Task 途中で落ちた後は記録済みの判断時点を呼ばずに途中から再開する()
    {
        var state = new InMemoryStage0RecordingState { CrashAfterCheckpoint = 1 };
        var (first, firstLlm, firstSink) = Build(state);
        var crash = () => first.RunAsync(Options(), TestContext.Current.CancellationToken);
        await crash.Should().ThrowAsync<SimulatedCrashException>();
        firstLlm.Calls.Should().Be(2, "6/1 の 1 判断時点（一次 1 ＋ 本判断 1）");
        firstSink.Saved.Should().BeNull();
        state.ReleaseLock(); // 落ちたプロセスのロックは OS が外す

        var (second, secondLlm, secondSink) = Build(state);
        var outcome = await second.RunAsync(Options(), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        secondLlm.Calls.Should().Be(4, "6/2・6/3 の 2 判断時点だけ（6/1 は呼ばない）");
        secondSink.Saved!.Records.Select(r => r.AsOf).Should().Equal(
            new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 2), new DateOnly(2026, 6, 3));
        outcome.CumulativeCostJpy.Should().Be(12m, "2 回の実行の合計（二重の課金が無い）");

        // 3 回目の起動は完了の印で止まる。
        var (third, thirdLlm, _) = Build(state);
        (await third.RunAsync(Options(), TestContext.Current.CancellationToken)).Status
            .Should().Be(Stage0RecordingStatus.AlreadyFinished);
        thirdLlm.Calls.Should().Be(0);
    }

    // 再開した記録の戦略 ID は、途中で落ちなかった実行と同じ（記録の中身が同じ）。
    [Fact]
    public async Task 途中から再開した記録は落ちなかった記録と同じ戦略IDになる()
    {
        var (clean, _, cleanSink) = Build(new InMemoryStage0RecordingState());
        await clean.RunAsync(Options(), TestContext.Current.CancellationToken);

        var state = new InMemoryStage0RecordingState { CrashAfterCheckpoint = 2 };
        var crash = () => Build(state).Recorder.RunAsync(Options(), TestContext.Current.CancellationToken);
        await crash.Should().ThrowAsync<SimulatedCrashException>();
        state.ReleaseLock();
        var (resumed, _, resumedSink) = Build(state);
        await resumed.RunAsync(Options(), TestContext.Current.CancellationToken);

        resumedSink.Saved!.StrategyId.Should().Be(cleanSink.Saved!.StrategyId);
    }

    // 🔴 最悪の瞬間（LLM の応答の後・チェックポイントの前）に落ちたら、その判断時点は再開時にもう一度呼ぶことになる。
    // その費用は台帳に入っており、累計が承認額を超える見込みになれば**呼ぶ前に**止まる（二重の課金は最大 1 判断時点）。
    [Fact]
    public async Task 応答の後チェックポイントの前に落ちたらその費用も累計に入り上限で止まる()
    {
        // 6/1〜6/2（見積り＝承認額 8 円）。6/2 を呼んだ直後に落ちる（累計 8 円・チェックポイントは 6/1 だけ）。
        var state = new InMemoryStage0RecordingState { CrashBeforeCheckpoint = 2 };
        var crash = () => Build(state).Recorder.RunAsync(Options(to: "2026-06-02"), TestContext.Current.CancellationToken);
        await crash.Should().ThrowAsync<SimulatedCrashException>();
        state.ReleaseLock();

        var (second, llm, sink) = Build(state);
        var outcome = await second.RunAsync(Options(to: "2026-06-02"), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.StoppedOverBudget);
        llm.Calls.Should().Be(0, "累計 8 円 ＋ 次の判断時点 4 円 ＞ 承認額 8 円");
        outcome.CumulativeCostJpy.Should().Be(8m);
        sink.Saved!.Records.Should().ContainSingle("6/1 の記録は書き出す");
        state.Runs.Values.Should().ContainSingle(r => r.Phase == Stage0RunPhase.Stopped, "上限で止めた印が付く");
    }

    // 呼ぶ前に予約を積み、呼んだ後に実績へ置き換える（呼んだ直後に落ちても累計に入っている）。
    [Fact]
    public async Task 台帳は呼ぶ前に見込みを積み呼んだ後に実績へ置き換える()
    {
        var state = new InMemoryStage0RecordingState();
        // 1 呼び出し 500 / 500 トークン ＝ 1 円。1 判断時点の実績 2 円（見積り 4 円）。
        await Build(state, tokensPerCall: 500).Recorder.RunAsync(Options(to: "2026-06-02"), TestContext.Current.CancellationToken);

        state.LedgerWrites.Should().Equal(4m, 2m, 6m, 4m);
    }

    // ------------------------------------------------------------------------------------------------
    // 3. 状態が読めない・書けない・ロック
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("mark")]       // 印が読めない
    [InlineData("ledger")]     // 台帳が読めない
    [InlineData("start")]      // 開始の印が書けない
    [InlineData("locked")]     // ほかの実行がロックを持つ
    public async Task 状態が読めず書けずロックも取れなければLLMを1回も呼ばない(string broken)
    {
        var state = new InMemoryStage0RecordingState();
        var options = Options();
        switch (broken)
        {
            case "mark":
                state.Runs[Stage0RecordingIdentity.RunId(options)] = new Stage0RunState(Stage0RunPhase.Unreadable, Detail: "壊れた JSON");
                break;
            case "ledger":
                state.Ledger[Stage0RecordingIdentity.LedgerId(options)] = null;
                break;
            case "start":
                state.FailMarkStarted = true;
                break;
            default:
                state.Locked = true;
                break;
        }

        var (recorder, llm, sink) = Build(state);
        var outcome = await recorder.RunAsync(options, TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(broken == "locked" ? Stage0RecordingStatus.Locked : Stage0RecordingStatus.StateUnavailable);
        llm.Calls.Should().Be(0);
        sink.Saved.Should().BeNull();
    }

    // 🔴 状態の置き場（永続ボリューム）が構成されていなければ走らない（揮発領域の状態で走らせない）。
    [Fact]
    public async Task 状態の置き場が未構成ならLLMを1回も呼ばない()
    {
        var options = Options();
        options.StateDirectory = " ";
        var (recorder, llm, _) = Build(new InMemoryStage0RecordingState());

        (await recorder.RunAsync(options, TestContext.Current.CancellationToken)).Status
            .Should().Be(Stage0RecordingStatus.NotConfigured);
        llm.Calls.Should().Be(0);
    }

    // ------------------------------------------------------------------------------------------------
    // 4. 費用の累計の上限（印とは独立）
    // ------------------------------------------------------------------------------------------------

    // 🔴 両端で止める: 印が消えても（壊れても）、台帳の累計が承認額に届いていれば呼ばない。
    [Fact]
    public async Task 印が無くても累計が承認額に届いていればLLMを呼ばない()
    {
        var state = new InMemoryStage0RecordingState();
        await Build(state).Recorder.RunAsync(Options(), TestContext.Current.CancellationToken);
        state.Runs.Clear(); // 印とチェックポイントが消えた

        var (recorder, llm, sink) = Build(state);
        var outcome = await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.StoppedOverBudget);
        llm.Calls.Should().Be(0);
        outcome.CumulativeCostJpy.Should().Be(12m);
        // 🔴 0 件で止まった実行は、既存の記録（課金済み）を空の集合で上書きせず、終わりの印でも固めない。
        sink.Saved.Should().BeNull("0 件の集合で既存の記録を上書きしない");
        outcome.RecordSet.Should().BeNull();
        state.Runs.Values.Should().NotContain(r => r.Phase == Stage0RunPhase.Stopped, "0 件で止めた実行に終わりの印を付けない");
    }

    // 🔴 記録の中身の欄（モデル）を変えると印の鍵は変わるが台帳の鍵は同じ。1 件目より前で上限に当たり、
    // 既存の記録を 0 件で上書きしない（LLM も呼ばない）。
    [Fact]
    public async Task 印の鍵だけが変わって上限で止まっても既存の記録を上書きしない()
    {
        var state = new InMemoryStage0RecordingState();
        await Build(state).Recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        var changed = Options();
        changed.Model = "claude-opus-5-5"; // 表に無いモデル＝未知モデルの計上単価（1/1）で見積りは同じ＝承認値は一致する
        var (recorder, llm, sink) = Build(state);
        var outcome = await recorder.RunAsync(changed, TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.StoppedOverBudget);
        llm.Calls.Should().Be(0);
        sink.Saved.Should().BeNull();
        state.Runs[Stage0RecordingIdentity.RunId(changed)].Phase
            .Should().Be(Stage0RunPhase.InProgress, "開始の印は付くが、0 件で止めた実行に終わりの印は付けない");
    }

    // 🔴 呼ぶ前の判定: 累計＋1 判断時点の見積り（4 円）が承認額を超える見込みなら、その判断時点を呼ばない。
    [Fact]
    public async Task 累計と次の判断時点の見込みが承認額を超えるなら呼ぶ前に止める()
    {
        var state = new InMemoryStage0RecordingState();
        var options = Options();
        state.Ledger[Stage0RecordingIdentity.LedgerId(options)] = 5m; // 承認額 12 円・残り 7 円 → 1 判断時点（4 円）だけ呼べる

        var (recorder, llm, sink) = Build(state);
        var outcome = await recorder.RunAsync(options, TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.StoppedOverBudget);
        llm.Calls.Should().Be(2, "5 ＋ 4 ＝ 9 ≦ 12 で 6/1 を呼び、9 ＋ 4 ＝ 13 ＞ 12 で 6/2 は呼ばない");
        outcome.CumulativeCostJpy.Should().Be(9m);
        sink.Saved!.Records.Should().ContainSingle();
    }

    // 承認額は見積りを円未満 2 桁で写した値である。写した端数（切り捨て側）で最後の判断時点を落とさない（丸めは承認ゲートと同じ）。
    [Fact]
    public async Task 承認額が見積りを2桁へ丸めた値でも最後の判断時点まで呼ぶ()
    {
        var options = Options(to: "2026-06-02");
        // 本判断の入力を 1,002 トークンにする: 1 判断時点の見積り 4.002 円・合計 8.004 円 → 承認額 8.00 円（切り捨て側）。
        // 実績も 4.002 円 / 判断時点（応答のトークン量を見積りと同じにする）。最後の判断時点の前の累計 ＋ 見込み ＝ 8.004 円。
        options.InputTokensPerDecision = 1_002;
        options.ApprovedEstimateJpy = 8.00m;
        var collector = new Stage0RecordingUsageCollector(new NullReporter());
        var llm = new CountingLlm(collector, 1_000, decisionInputTokens: 1_002);
        var recorder = new Stage0DecisionRecorder(
            llm, DecisionOrchestrationOptions.Default, new StubInputs(), new CapturingSink(),
            new InMemoryStage0RecordingState(), new InMemoryStage0RecordingState(), collector, Prices(),
            new FixedTimeProvider(Now), NullLogger<Stage0DecisionRecorder>.Instance);

        var outcome = await recorder.RunAsync(options, TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.Calls.Should().Be(4);
        outcome.CumulativeCostJpy.Should().Be(8.004m);
    }

    // 上限に届かない通常の完走は従来どおり（呼び出し回数・費用・記録が変わらない）。終わりの印が付く。
    [Fact]
    public async Task 上限に届かない通常の完走は全件を記録して完了の印を付ける()
    {
        var state = new InMemoryStage0RecordingState();
        var (recorder, llm, sink) = Build(state);

        var outcome = await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.Calls.Should().Be(6);
        outcome.ActualCostJpy.Should().Be(12m);
        outcome.CumulativeCostJpy.Should().Be(12m);
        sink.Saved!.Records.Should().HaveCount(3);
        state.Runs[Stage0RecordingIdentity.RunId(Options())].Phase.Should().Be(Stage0RunPhase.Completed);
        state.Locked.Should().BeFalse("実行の後はロックを外す");
    }

    // 書き出しに失敗したら終わりの印は付けない。次の起動は LLM を呼ばずにチェックポイントから書き出し直して完了する。
    [Fact]
    public async Task 書き出しに失敗したら次の起動はLLMを呼ばずに書き出し直す()
    {
        var state = new InMemoryStage0RecordingState();
        var collector = new Stage0RecordingUsageCollector(new NullReporter());
        var failing = new Stage0DecisionRecorder(
            new CountingLlm(collector, 1_000), DecisionOrchestrationOptions.Default, new StubInputs(),
            new FailingSink(), state, state, collector, Prices(), new FixedTimeProvider(Now),
            NullLogger<Stage0DecisionRecorder>.Instance);
        (await failing.RunAsync(Options(), TestContext.Current.CancellationToken)).Status
            .Should().Be(Stage0RecordingStatus.SaveFailed);

        var (recorder, llm, sink) = Build(state);
        var outcome = await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        llm.Calls.Should().Be(0);
        sink.Saved!.Records.Should().HaveCount(3);
    }

    // ------------------------------------------------------------------------------------------------

    private sealed class CountingLlm(Stage0RecordingUsageCollector usage, int tokensPerCall, int? decisionInputTokens = null)
        : ILlmCompletionClient
    {
        public int Calls { get; private set; }

        public async Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var input = purpose == LlmPurposes.TradeDecision ? decisionInputTokens ?? tokensPerCall : tokensPerCall;
            await usage.ReportAsync(
                new LlmUsage(purpose ?? LlmPurposes.TradeDecision, input, tokensPerCall, LlmAssignments.For(purpose)?.PrimaryModel),
                cancellationToken);
            return purpose == LlmPurposes.TradeDecisionScreening
                ? """{"action":"Buy","rationale":"関心あり"}"""
                : """{"action":"Buy","rationale":"根拠","referencePrice":100,"stopLossDistancePerShare":2}""";
        }
    }

    private sealed class StubInputs : IAsOfDecisionInputProvider
    {
        public Task<AsOfDecisionInput?> GetAsync(
            string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default) =>
            Task.FromResult<AsOfDecisionInput?>(new AsOfDecisionInput(
                asOf,
                new DailyPolicy(asOf, "当日の方針"),
                new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()),
                new DatedPrice(asOf, 100m)));
    }

    private sealed class CapturingSink : IStage0DecisionRecordSink
    {
        public Stage0DecisionRecordSet? Saved { get; private set; }

        public Task<bool> SaveAsync(Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default)
        {
            Saved = recordSet;
            return Task.FromResult(true);
        }
    }

    private sealed class FailingSink : IStage0DecisionRecordSink
    {
        public Task<bool> SaveAsync(Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class NullReporter : ILlmUsageReporter
    {
        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
