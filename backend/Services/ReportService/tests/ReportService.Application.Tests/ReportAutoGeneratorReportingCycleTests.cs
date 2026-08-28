using AiStockTrading.Report.Application.Adapters;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Report.Application.Tests;

// FR-06, FR-16, #338, #282, ADR-0016 決定15, ADR-0027 決定4, ADR-0017 決定2・決定4, IADR-0254:
// 報告サイクルの新しい供給（LLM 利用実績・借株料）が自動生成へ結線されていることと、
// **その失敗の向き**（未供給へ倒す）を固定する。
//
// 🔴 **既定（未注入）が空ではなく未供給であること**が要点である。
// 空へ倒すと「LLM を 1 度も使わず費用 0 円」「借株コスト 0 USD」を報告書が主張する。
public class ReportAutoGeneratorReportingCycleTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 3, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class StubLlmUsageSource(LlmUsageRecord? record) : ILlmUsageRecordSource
    {
        public List<(DateOnly From, DateOnly To)> Requested { get; } = [];

        public Task<LlmUsageRecord?> GetUsageAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requested.Add((from, to));
            return Task.FromResult(record);
        }
    }

    private sealed class ThrowingLlmUsageSource : ILlmUsageRecordSource
    {
        public Task<LlmUsageRecord?> GetUsageAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("監査台帳へ到達できません");
    }

    private sealed class StubBorrowFeeSource(BorrowFeeRecord? record) : IBorrowFeeRecordSource
    {
        public Task<BorrowFeeRecord?> GetBorrowFeesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult(record);
    }

    private sealed class ThrowingBorrowFeeSource : IBorrowFeeRecordSource
    {
        public Task<BorrowFeeRecord?> GetBorrowFeesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("監査台帳へ到達できません");
    }

    private static ReportAutoGenerator NewGenerator(
        IReportStore store,
        ILlmUsageRecordSource? llmUsageSource = null,
        IBorrowFeeRecordSource? borrowFeeSource = null) =>
        new(store,
            new ReportDraftService(new StubDrafter()),
            new NoOpPeriodFillSource(),
            new FixedClock(WedAfterClose),
            new ReportAutoGenerationSettings(),
            notifier: null,
            reductionSource: null,
            buyInSource: null,
            fxSourceStatusSource: null,
            llmUsageSource: llmUsageSource,
            borrowFeeSource: borrowFeeSource);

    private static string BodyOf(IReportStore store) =>
        store.List().Single(r => r.Kind == ReportKind.Daily).Body;

    // 🔴 **未注入の既定は「未供給」である**（空・0 ではない）。
    [Fact]
    public async Task 供給元が未注入なら未供給として描く()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store).RunOnceAsync();

        var body = BodyOf(store);
        body.Should().Contain("モデル利用不能による取引判断スキップ | **供給されていません**（0 件ではありません）");
        body.Should().Contain("**借株コストを照会できませんでした（供給元がありません）**");
    }

    // 🔴 **照会が失敗しても未供給へ倒す**（空へ倒さない）。
    [Fact]
    public async Task 照会が失敗しても未供給へ倒す()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, new ThrowingLlmUsageSource(), new ThrowingBorrowFeeSource()).RunOnceAsync();

        var body = BodyOf(store);
        body.Should().Contain("モデル利用不能による取引判断スキップ | **供給されていません**（0 件ではありません）");
        body.Should().Contain("**借株コストを照会できませんでした（供給元がありません）**");
    }

    // **対の肯定形**: 供給されたら報告書へ確かに載る（未供給の表明だけでは、結線が切れていても緑になる）。
    [Fact]
    public async Task 供給された利用実績と借株料を報告書へ載せる()
    {
        var store = new InMemoryReportStore();
        var usage = new LlmUsageRecord(
            [new LlmCostIncurred(3_000m, T0, LlmPurposes.TradeDecision, "claude-sonnet-5")],
            [],
            [new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "a", null, T0)]);
        var fees = new BorrowFeeRecord(
            [new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 7, 8), 0.06m, 10_000m, 1.64m, T0)],
            []);

        await NewGenerator(store, new StubLlmUsageSource(usage), new StubBorrowFeeSource(fees)).RunOnceAsync();

        var body = BodyOf(store);
        body.Should().Contain($"モデル利用不能による取引判断スキップ | 1 件（{TradeDecisionSkipReasons.ModelUnavailable}: 1 件）");
        body.Should().Contain("**借株コスト（経費区分 BorrowFee）合計: +1.64 USD**（計上 1 件）");
    }

    // 供給の照会は**当該報告書の期間**で行う（別の期間の実績を載せない）。
    [Fact]
    public async Task 供給の照会は当該報告書の期間で行う()
    {
        var source = new StubLlmUsageSource(new LlmUsageRecord([], [], []));

        await NewGenerator(new InMemoryReportStore(), source).RunOnceAsync();

        // 2026-07-08（水）の日報＝当日 1 日ぶん。
        source.Requested.Should().Contain((new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 8)));
    }

    // 🔴 供給が null（未供給）を返した場合も、**空の記録が返った場合と描き分ける**。
    [Fact]
    public async Task 未供給と事象なしを描き分ける()
    {
        var unsupplied = new InMemoryReportStore();
        await NewGenerator(unsupplied, new StubLlmUsageSource(null), new StubBorrowFeeSource(null)).RunOnceAsync();

        var empty = new InMemoryReportStore();
        await NewGenerator(empty,
            new StubLlmUsageSource(new LlmUsageRecord([], [], [])),
            new StubBorrowFeeSource(new BorrowFeeRecord([], []))).RunOnceAsync();

        BodyOf(unsupplied).Should().Contain("**借株コストを照会できませんでした（供給元がありません）**");
        BodyOf(empty).Should().Contain("**空売り建玉: 0 件**");
        BodyOf(empty).Should().NotContain("**借株コストを照会できませんでした（供給元がありません）**");
    }
}
