extern alias OrderExecutionWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using OrderExecutionWorker::OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, UC-06, ADR-0016 決定3, #967, IADR-0420, IADR-0425 決定4（T-10-1025 / T-10-1026）: 借株可否の受け手（HttpShortSellBorrowSource）。
//
// 🔴 T-10-1025 は**送り手（発注執行）の本物の型** `ShortPermitView` を送り手の web 既定（camelCase・列挙は数値）で直列化した本文を読ませる
// （手書きの JSON だけだと、送り手の項目名を変えても両サービスの試験が緑のまま、実行時は既定値で読まれる＝#940 / #943）。
// 送り手がその設定で出していることは発注執行側の T-10-1033（ReadContractWireFormatTests）が本物の Program.cs で固定する。
public class HttpShortSellBorrowSourceTests
{
    // 発注執行の Minimal API（Results.Ok）と同じ web 既定。
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 25, 14, 0, 0, TimeSpan.Zero);

    private static HttpShortSellBorrowSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://order-execution") },
            NullLogger<HttpShortSellBorrowSource>.Instance);

    /// <summary>T-10-1025: 送り手の本物の型の 3 つの状態を、許可／不許可／分からないとして読む。要求は銘柄と市場の数値を運ぶ。</summary>
    [Theory]
    [InlineData(ShortPermitStatus.Permitted, true)]
    [InlineData(ShortPermitStatus.NotPermitted, false)]
    [InlineData(ShortPermitStatus.Unknown, null)]
    public async Task 送り手の本物の型を直列化した応答から借株可否を読む(ShortPermitStatus status, bool? expected)
    {
        var view = new ShortPermitView(
            "AAPL", Market.UnitedStates, status,
            status == ShortPermitStatus.Unknown ? ShortPermitUnknownReasons.QueryFailed : null,
            status == ShortPermitStatus.Unknown ? null : ObservedAt);
        var handler = new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(view, Web));

        var observation = await Source(handler).GetAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        observation.ShortPermit.Should().Be(expected);
        if (expected is null)
            observation.UnknownReason.Should().Be(ShortPermitUnknownReasons.QueryFailed, "送り手の分からない理由をそのまま運ぶ");
        handler.Requests.Should().ContainSingle()
            .Which.Should().Be("/order-execution/short-permit?symbol=AAPL&market=1");
    }

    /// <summary>T-10-1026: 送り手の型の項目が 1 つ欠けても（改名の窓）、既定値で読まず「分からない」にする。</summary>
    [Theory]
    [InlineData("symbol")]
    [InlineData("market")]
    [InlineData("status")]
    public async Task 送り手の項目が欠ければ分からないとして読む(string missing)
    {
        var body = JsonSerializer.SerializeToNode(
            new ShortPermitView("AAPL", Market.UnitedStates, ShortPermitStatus.Permitted, null, ObservedAt), Web)!.AsObject();
        body.Remove(missing).Should().BeTrue($"送り手の web 既定の項目名に {missing} がある");

        var observation = await Source(new Stub(HttpStatusCode.OK, body.ToJsonString()))
            .GetAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        observation.ShortPermit.Should().BeNull("状態の欠落を列挙の既定値（0）で読むと、読み違いが許可へ倒れる余地を作る");
    }

    /// <summary>T-10-1026: 非 2xx・未定義の状態・別の銘柄／市場の答え・壊れた本文・例外・タイムアウトはすべて「分からない」。</summary>
    [Theory]
    [InlineData("500")]
    [InlineData("401")]
    [InlineData("未定義の状態")]
    [InlineData("別の銘柄")]
    [InlineData("別の市場")]
    [InlineData("壊れた本文")]
    [InlineData("例外")]
    [InlineData("タイムアウト")]
    public async Task 照会の失敗と契約の食い違いは分からないとして読む(string kind)
    {
        var permitted = new ShortPermitView("AAPL", Market.UnitedStates, ShortPermitStatus.Permitted, null, ObservedAt);
        HttpMessageHandler handler = kind switch
        {
            "500" => new Stub(HttpStatusCode.InternalServerError, "{}"),
            "401" => new Stub(HttpStatusCode.Unauthorized, ""),
            "未定義の状態" => new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(permitted with { Status = (ShortPermitStatus)9 }, Web)),
            "別の銘柄" => new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(permitted with { Symbol = "MSFT" }, Web)),
            "別の市場" => new Stub(HttpStatusCode.OK, JsonSerializer.Serialize(permitted with { Market = Market.Japan }, Web)),
            "壊れた本文" => new Stub(HttpStatusCode.OK, "{not json"),
            "例外" => new Throwing(new HttpRequestException("connection refused")),
            "タイムアウト" => new Throwing(new TaskCanceledException("timeout")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var observation = await Source(handler).GetAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        observation.ShortPermit.Should().BeNull();
        observation.UnknownReason.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>T-10-1026: 照会先が構成されていないときの供給は「分からない」（借りられない、とは返さない）。</summary>
    [Fact]
    public async Task 照会先が無いときは分からないを返す()
    {
        var observation = await new UnavailableShortSellBorrowSource()
            .GetAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        observation.ShortPermit.Should().BeNull();
        observation.UnknownReason.Should().Be(UnavailableShortSellBorrowSource.Reason);
    }

    // ---- 補助 ----

    internal sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class Throwing(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
