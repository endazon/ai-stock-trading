extern alias MarketMonitorWorker;
extern alias ReportWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Features.Notifications;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;
using MonitorApply = MarketMonitorWorker::MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;
using MonitorDomain = MarketMonitorWorker::MarketMonitorService.Domain;
using ReportRevise = ReportWorker::ReportService.Features.Reports.RevisePolicy;
using ReportWatchlist = ReportWorker::ReportService.Features.Reports.WatchlistProposal;

namespace NotificationService.Tests;

// FR-13, FR-14, ADR-0042 決定 1, #1025, IADR-0433, IADR-0420: `/policy` の入れ替え案の越境の契約（T-10-1388〜T-10-1395）。
// 送り手（市場監視・報告書）の**本物の型を送り手の JSON 設定で**直列化・逆直列化し、受け手のアダプタに読ませる。
// 市場監視は web 既定（列挙は数値）、報告書は web 既定＋文字列列挙。原則 A（失敗・不明・無し）も併せて固定する。
public class WatchlistApplyContractTests
{
    private static readonly JsonSerializerOptions MonitorWire = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static HttpMarketMonitorWatchlistController Monitor(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://market-monitor") }, NullLogger<HttpMarketMonitorWatchlistController>.Instance);

    private static HttpPolicyRevisionController Report(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://report") }, NullLogger<HttpPolicyRevisionController>.Instance);

    private static readonly IReadOnlyList<WatchlistSnapshotItemView> Snapshot =
        [new("AAPL", "UnitedStates"), new("7203", "Japan")];

    private static readonly IReadOnlyList<WatchlistChangeSuggestionView> Changes =
        [new("add", "NVDA", "AI 需要"), new("remove", "AAPL", "値動きが小さい")];

    // T-10-1388: 監視銘柄の照会は、市場監視の本物の型（MonitoredSymbol・列挙は数値）の一覧を市場の名前へ写して読む。
    [Fact]
    public async Task 監視銘柄の照会は送り手の本物の型から読める()
    {
        MonitorDomain.MonitoredSymbol[] sent = [new("AAPL", Market.UnitedStates), new("7203", Market.Japan)];
        var handler = new FakeHandler(HttpStatusCode.OK, JsonSerializer.Serialize(sent, MonitorWire));

        var result = await Monitor(handler).GetWatchlistAsync();

        result.Succeeded.Should().BeTrue();
        result.Items.Should().Equal(Snapshot);
        handler.RequestUri.Should().Be("http://market-monitor/monitor/watchlist");
    }

    // T-10-1389: 照会の失敗・解釈できない応答は「空」ではなく失敗（Succeeded=false）。
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "[]")]
    [InlineData(HttpStatusCode.OK, "null")]
    [InlineData(HttpStatusCode.OK, """[{"symbol":null,"market":1}]""")]
    public async Task 照会の失敗は空と区別する(HttpStatusCode status, string body)
    {
        var result = await Monitor(new FakeHandler(status, body)).GetWatchlistAsync();

        result.Succeeded.Should().BeFalse();
    }

    // T-10-1390: 適用の要求本文を市場監視の本物の要求型が読める（期待値の市場・入れ替え・出所・代理）。応答は本物の応答型から読める。
    [Fact]
    public async Task 適用の要求と応答は送り手の本物の型で読める()
    {
        MonitorApply.WatchlistProposalApplyResponse sent = new(
            [new("add", "NVDA", true, null), new("remove", "AAPL", false, "銘柄 AAPL は監視対象にありません")],
            "developer",
            new MonitorApply.FinnhubDailyVolumeEstimateView(4320, 300, true));
        var handler = new FakeHandler(HttpStatusCode.OK, JsonSerializer.Serialize(sent, MonitorWire));

        var outcome = await Monitor(handler).ApplyProposalAsync(Snapshot, Changes, "daily-2026-09-28-v3", "developer");

        handler.RequestUri.Should().Be("http://market-monitor/monitor/watchlist/proposal-apply");
        var request = JsonSerializer.Deserialize<MonitorApply.WatchlistProposalApplyRequest>(handler.Body!, MonitorWire)!;
        request.ExpectedWatchlist!.Select(e => (e.Symbol, e.Market)).Should().Equal(("AAPL", Market.UnitedStates), ("7203", Market.Japan));
        request.Changes!.Select(c => (c.Action, c.Symbol, c.Reason)).Should().Equal(("add", "NVDA", "AI 需要"), ("remove", "AAPL", "値動きが小さい"));
        (request.ProposalRef, request.OnBehalfOf).Should().Be(("daily-2026-09-28-v3", "developer"));

        outcome.Status.Should().Be(WatchlistApplyStatus.Applied);
        outcome.Items.Should().Equal(
            new WatchlistApplyItemView("add", "NVDA", true, null),
            new WatchlistApplyItemView("remove", "AAPL", false, "銘柄 AAPL は監視対象にありません"));
        outcome.Estimate.Should().Be(new FinnhubEstimateView(4320, 300, true));
    }

    // T-10-1391: 409＝変わっていた（適用なし）、400＝受理されず（適用なし）、タイムアウト・例外・解釈できない 2xx＝不明。
    [Fact]
    public async Task 適用の失敗と不明を区別する()
    {
        (await Monitor(new FakeHandler(HttpStatusCode.Conflict, """{"error":"変わりました"}""")).ApplyProposalAsync(Snapshot, Changes, "r", "developer"))
            .Should().Match<WatchlistApplyOutcome>(o => o.Status == WatchlistApplyStatus.Stale && o.Message == "変わりました");
        (await Monitor(new FakeHandler(HttpStatusCode.BadRequest, """{"error":"形式"}""")).ApplyProposalAsync(Snapshot, Changes, "r", "developer"))
            .Status.Should().Be(WatchlistApplyStatus.Rejected);
        (await Monitor(new FakeHandler(HttpStatusCode.OK, "null")).ApplyProposalAsync(Snapshot, Changes, "r", "developer"))
            .Status.Should().Be(WatchlistApplyStatus.Indeterminate);
        var timeout = await Monitor(new ThrowingHandler(new TaskCanceledException())).ApplyProposalAsync(Snapshot, Changes, "r", "developer");
        timeout.Status.Should().Be(WatchlistApplyStatus.Indeterminate);
        timeout.Message.Should().Contain("分かりません");
    }

    // T-10-1392: 改訂の要求が運ぶ現在の監視銘柄を、報告書の本物の要求型が読める（null＝照会できなかった も運ぶ）。
    [Fact]
    public async Task 改訂の要求の監視銘柄は報告書の本物の型で読める()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, JsonSerializer.Serialize(
            new ReportRevise.PolicyRevisionResponse("daily-2026-09-28", 2, false, true, "m", "p", [], null), ReportWire));

        await Report(handler).ReviseAsync("daily-2026-09-28", "指示", "developer", Snapshot);
        var withSnapshot = JsonSerializer.Deserialize<ReportRevise.RevisePolicyRequest>(handler.Body!, ReportWire)!;
        withSnapshot.CurrentWatchlist!.Select(w => (w.Symbol, w.Market)).Should().Equal(("AAPL", "UnitedStates"), ("7203", "Japan"));

        await Report(handler).ReviseAsync("daily-2026-09-28", "指示", "developer", null);
        JsonSerializer.Deserialize<ReportRevise.RevisePolicyRequest>(handler.Body!, ReportWire)!.CurrentWatchlist.Should().BeNull();
    }

    // T-10-1393: 確定した版の入れ替え案は、報告書の本物の応答型（WatchlistProposalView）から読める。404 は「案ではない」、他の失敗は照会の失敗。
    [Fact]
    public async Task 確定した版の案は報告書の本物の型から読める()
    {
        var attemptId = Guid.NewGuid();
        ReportWatchlist.WatchlistProposalView sent = new(
            attemptId, "daily-2026-09-28", 3,
            [new ReportRevise.WatchlistChangeView("add", "NVDA", "AI 需要")],
            [new ReportWatchlist.WatchlistSnapshotEntryView("AAPL", "UnitedStates")],
            ApplyRecorded: false);
        var handler = new FakeHandler(HttpStatusCode.OK, JsonSerializer.Serialize(sent, ReportWire));

        var lookup = await Report(handler).GetWatchlistProposalAsync("daily-2026-09-28", 3);

        handler.RequestUri.Should().Be("http://report/reports/policy-revisions/watchlist-proposal?periodKey=daily-2026-09-28&version=3");
        (lookup.Succeeded, lookup.Found).Should().Be((true, true));
        lookup.Proposal!.AttemptId.Should().Be(attemptId);
        lookup.Proposal.Changes.Should().Equal(new WatchlistChangeSuggestionView("add", "NVDA", "AI 需要"));
        lookup.Proposal.Snapshot.Should().Equal(new WatchlistSnapshotItemView("AAPL", "UnitedStates"));

        var unknownSnapshot = await Report(new FakeHandler(HttpStatusCode.OK, JsonSerializer.Serialize(sent with { Snapshot = null }, ReportWire)))
            .GetWatchlistProposalAsync("daily-2026-09-28", 3);
        unknownSnapshot.Proposal!.Snapshot.Should().BeNull("照会できなかった監視銘柄を空の一覧に化けさせない");

        (await Report(new FakeHandler(HttpStatusCode.NotFound, "{}")).GetWatchlistProposalAsync("k", 1))
            .Should().Match<WatchlistProposalLookup>(l => l.Succeeded && !l.Found);
        (await Report(new FakeHandler(HttpStatusCode.InternalServerError, "{}")).GetWatchlistProposalAsync("k", 1))
            .Succeeded.Should().BeFalse();
    }

    // T-10-1394: 内訳の記録の本文を、報告書の本物の要求型（WatchlistApplyResultRequest）が読める。
    [Fact]
    public async Task 内訳の記録は報告書の本物の型で読める()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var attemptId = Guid.NewGuid();

        var ok = await Report(handler).RecordWatchlistApplyAsync(
            attemptId, "applied", [new WatchlistApplyItemView("add", "NVDA", true, null)], "内訳", "developer");

        ok.Should().BeTrue();
        handler.RequestUri.Should().Be($"http://report/reports/policy-revisions/{attemptId}/watchlist-apply-result");
        var request = JsonSerializer.Deserialize<ReportWatchlist.WatchlistApplyResultRequest>(handler.Body!, ReportWire)!;
        (request.Outcome, request.Message, request.OnBehalfOf).Should().Be(("applied", "内訳", "developer"));
        request.Items!.Single().Should().Be(new ReportWatchlist.WatchlistApplyItemRecord("add", "NVDA", true, null));

        (await Report(new FakeHandler(HttpStatusCode.Conflict, "{}")).RecordWatchlistApplyAsync(attemptId, "applied", [], "m", "developer"))
            .Should().BeFalse();
    }

    // T-10-1395: 市場監視の本物の要求型は、Bot が送る市場の名前が未知なら市場を欠いたまま読む（送り手が 400 で止める側）。
    [Fact]
    public async Task 未知の市場は欠けたまま送られ送り手が止める()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest, """{"error":"形式"}""");

        var outcome = await Monitor(handler).ApplyProposalAsync([new("AAPL", "Moon")], Changes, "r", "developer");

        JsonSerializer.Deserialize<MonitorApply.WatchlistProposalApplyRequest>(handler.Body!, MonitorWire)!
            .ExpectedWatchlist!.Single().Market.Should().BeNull();
        outcome.Status.Should().Be(WatchlistApplyStatus.Rejected);
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }
}
