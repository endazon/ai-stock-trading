using BacktestService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BacktestService.Tests;

// FR-15, ADR-0023 決定5, IADR-0157, #382: moomoo 履歴 K 線からの米国株日足 OHLCV 取得。
//
// 「壊れると黙って結果が歪む」3 点を固定する。
//   1. **ページング**（NextReqKey が返る限り続ける）— 1 リクエスト 1,000 件の上限で打ち切ると
//      長期間の履歴が黙って切り詰められ、Stage 0 の判定（最大 DD・ウォークフォワード）が歪む。
//   2. **前復権（RehabType_Forward）の指定** — 外すと株式分割を跨いで価格が不連続になる。
//   3. **未確認 2 点の警告**（ADR-0023 決定5 の「確認するまで本番のバックテストへ流さない」）。
public class MoomooHistoricalBarSourceTests
{
    private static readonly (string Symbol, Market Market)[] Aapl = [("AAPL", Market.UnitedStates)];
    private static readonly DateOnly From = new(2024, 1, 1);
    private static readonly DateOnly To = new(2024, 12, 31);

    // ADR-0023 決定5: 1 リクエスト 1,000 件・NextReqKey でページングする。
    [Fact]
    public async Task NextReqKeyが返る限りページングして全期間のバーを取得する()
    {
        var client = new FakeClient(
            new MoomooHistoryKLinePage([KLine(2024, 1, 4), KLine(2024, 1, 5)], [1]),
            new MoomooHistoryKLinePage([KLine(2024, 1, 8), KLine(2024, 1, 9)], [2]),
            new MoomooHistoryKLinePage([KLine(2024, 1, 10)], null));

        var load = await Create(client).LoadBarsAsync(Aapl, From, To);

        client.Requests.Should().HaveCount(3);
        client.Requests[0].NextRequestKey.Should().BeNull();          // 先頭ページは鍵なし
        client.Requests[1].NextRequestKey.Should().Equal((byte)1);    // 前ページの鍵を渡して継続する
        client.Requests[2].NextRequestKey.Should().Equal((byte)2);
        load.Bars.Should().HaveCount(5);
        load.Gaps.Should().BeEmpty();
    }

    // ADR-0023 決定5: OpenD の 1 リクエスト上限は 1,000 件。
    [Fact]
    public async Task 一度に要求する件数は1000件_ADR0023決定5()
    {
        var client = new FakeClient(new MoomooHistoryKLinePage([KLine(2024, 1, 4)], null));

        await Create(client).LoadBarsAsync(Aapl, From, To);

        MoomooHistoricalBarSource.MaxKLinesPerRequest.Should().Be(1000);
        client.Requests[0].MaxCount.Should().Be(1000);
    }

    // ADR-0023 決定5: 前復権（RehabType_Forward）で取得する。無指定は分割を跨いで価格が不連続になる。
    [Fact]
    public async Task 前復権を指定して取得する_ADR0023決定5()
    {
        var client = new FakeClient(new MoomooHistoryKLinePage([KLine(2024, 1, 4)], null));

        await Create(client).LoadBarsAsync(Aapl, From, To);

        MoomooHistoricalBarSource.Adjustment.Should().Be(MoomooKLineAdjustment.ForwardAdjusted);
        client.Requests.Should().OnlyContain(r => r.Adjustment == MoomooKLineAdjustment.ForwardAdjusted);
    }

    [Fact]
    public async Task 銘柄と期間をそのまま要求へ載せる()
    {
        var client = new FakeClient(new MoomooHistoryKLinePage([KLine(2024, 1, 4)], null));

        await Create(client).LoadBarsAsync(Aapl, From, To);

        client.Requests[0].Symbol.Should().Be("AAPL");
        client.Requests[0].From.Should().Be(From);
        client.Requests[0].To.Should().Be(To);
    }

    [Fact]
    public async Task 取得したOHLCVをバーへ写す()
    {
        var client = new FakeClient(new MoomooHistoryKLinePage(
            [new MoomooDailyKLine(new DateOnly(2024, 3, 1), 1.1m, 2.2m, 0.9m, 2.0m, 12345)], null));

        var load = await Create(client).LoadBarsAsync(Aapl, From, To);

        load.Bars.Should().ContainSingle().Which.Should().Be(
            new BacktestService.Domain.PriceBar(
                "AAPL", Market.UnitedStates, new DateOnly(2024, 3, 1), 1.1m, 2.2m, 0.9m, 2.0m, 12345));
    }

    // 期間指定を無視した応答（データ源の揺れ）を素通しさせない（Stooq 実装と同じ方針）。
    [Fact]
    public async Task 期間外のバーは捨てる()
    {
        var client = new FakeClient(new MoomooHistoryKLinePage(
            [KLine(2023, 12, 29), KLine(2024, 6, 3), KLine(2025, 1, 2)], null));

        var load = await Create(client).LoadBarsAsync(Aapl, From, To);

        load.Bars.Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2024, 6, 3));
    }

    // IHistoricalBarSource の契約: 欠測は無音破棄せず銘柄と理由で残し、他銘柄の取得は続ける。
    [Fact]
    public async Task 取得に失敗した銘柄は欠測として残し他の銘柄は続行する()
    {
        var client = new FakeClient(new MoomooHistoryKLinePage([KLine(2024, 1, 4)], null))
        {
            FailFor = "MSFT",
        };

        var load = await Create(client).LoadBarsAsync(
            [("MSFT", Market.UnitedStates), ("AAPL", Market.UnitedStates)], From, To);

        load.Gaps.Should().ContainSingle().Which.Symbol.Should().Be("MSFT");
        load.Bars.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
    }

    // 半端に取れた履歴で Stage 0 を合格させない（失敗した銘柄のバーは 1 本も採らない）。
    [Fact]
    public async Task ページングの途中で失敗したら_その銘柄のバーを一切採らない()
    {
        var client = new FakeClient(new MoomooHistoryKLinePage([KLine(2024, 1, 4)], [1]))
        {
            FailAfterRequests = 1,
        };

        var load = await Create(client).LoadBarsAsync(Aapl, From, To);

        load.Bars.Should().BeEmpty();
        load.Gaps.Should().ContainSingle().Which.Reason.Should().Contain("失敗");
    }

    // ADR-0023 決定5 が定めたのは**米国株**の履歴源である。日本株を勝手に写像せず欠測として残す。
    [Theory]
    [InlineData("7203", Market.Japan)]
    [InlineData("   ", Market.UnitedStates)]
    public async Task 米国株以外と空の銘柄は外部へ要求せず欠測として残す(string symbol, Market market)
    {
        var client = new FakeClient(new MoomooHistoryKLinePage([KLine(2024, 1, 4)], null));

        var load = await Create(client).LoadBarsAsync([(symbol, market)], From, To);

        client.Requests.Should().BeEmpty();
        load.Bars.Should().BeEmpty();
        load.Gaps.Should().ContainSingle();
    }

    [Fact]
    public async Task 期間内のデータが1件も無ければ欠測として残す()
    {
        var client = new FakeClient(new MoomooHistoryKLinePage([], null));

        var load = await Create(client).LoadBarsAsync(Aapl, From, To);

        load.Bars.Should().BeEmpty();
        load.Gaps.Should().ContainSingle().Which.Reason.Should().Contain("0 件");
    }

    // 続きの鍵が返っても 1 件も返らないページで打ち切る（進捗しないページングで回り続けない）。
    [Fact]
    public async Task 空のページが返れば続きの鍵があっても打ち切る()
    {
        var client = new FakeClient(
            new MoomooHistoryKLinePage([KLine(2024, 1, 4)], [1]),
            new MoomooHistoryKLinePage([], [2]));

        var load = await Create(client).LoadBarsAsync(Aapl, From, To);

        client.Requests.Should().HaveCount(2);
        load.Bars.Should().ContainSingle();
    }

    // ADR-0023 決定5 の「実装側で確認を要する 2 点」は**構成した人が実行時に必ず目にする**必要がある
    // （コメント・仕様書だけでは、構成した人が読まなければ効かない）。IADR-0157 決定3。
    [Fact]
    public async Task 未確認2点を取得のたびに警告する_本番のバックテストへ流さない()
    {
        var warnings = new List<string>();
        var client = new FakeClient(new MoomooHistoryKLinePage([KLine(2024, 1, 4)], null));
        var source = new MoomooHistoricalBarSource(client, NoWait, new CapturingLogger<MoomooHistoricalBarSource>(warnings));

        await source.LoadBarsAsync(Aapl, From, To);

        var warning = warnings.Should().ContainSingle(w => w.Contains("本番のバックテスト")).Subject;
        warning.Should().Contain("取得枠");     // 確認事項 1（remainQuota の単位・回復周期）
        warning.Should().Contain("前復権");     // 確認事項 2（費用モデルとの整合）
        warning.Should().Contain("ADR-0016");   // 決定14 の費用モデルが相手方であること
    }

    private static MoomooHistoricalBarSource Create(IMoomooHistoryKLineClient client) =>
        new(client, NoWait, NullLogger<MoomooHistoricalBarSource>.Instance);

    // 実時間を待たないレート制御（自制の有無ではなく取得ロジックを見るため）。
    private static IRateLimiter NoWait => new NoWaitRateLimiter();

    private static MoomooDailyKLine KLine(int year, int month, int day) =>
        new(new DateOnly(year, month, day), 10m, 11m, 9m, 10.5m, 1000);

    private sealed class NoWaitRateLimiter : IRateLimiter
    {
        public Task WaitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeClient(params MoomooHistoryKLinePage[] pages) : IMoomooHistoryKLineClient
    {
        private readonly List<MoomooHistoryKLineRequest> _requests = [];

        public IReadOnlyList<MoomooHistoryKLineRequest> Requests => _requests;

        /// <summary>この銘柄の要求を OpenD の非成功応答として扱う。</summary>
        public string? FailFor { get; init; }

        /// <summary>指定回数の要求のあと失敗させる（ページングの途中で失敗する状況）。</summary>
        public int? FailAfterRequests { get; init; }

        public Task<MoomooHistoryKLinePage> RequestUsDailyKLinesAsync(
            MoomooHistoryKLineRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Symbol == FailFor || _requests.Count == FailAfterRequests)
                throw new MoomooHistoryKLineException("retType=1: テスト用の非成功応答");

            var index = _requests.Count(r => r.Symbol == request.Symbol);
            _requests.Add(request);
            return Task.FromResult(index < pages.Length ? pages[index] : new MoomooHistoryKLinePage([], null));
        }
    }

    private sealed class CapturingLogger<T>(List<string> warnings) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                warnings.Add(formatter(state, exception));
        }
    }
}
