using System.Net;
using System.Net.Http.Json;
using CostControlService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CostControlService.Tests;

// FR-17, IADR-0063 決定 1/5: GET /assumptions の同期照会。失敗（401/500・不正応答・タイムアウト・不達）は例外を出さず
// null（＝取得不可）に倒し、縮退の判断は CachedAssumptionsProvider に委ねることを検証する。
//
// NFR, #526, IADR-0264 決定 1: 旧 ConfigurationService.Client.Tests から移した（共有クライアントの廃止に伴い、
// 実装が呼び出し元ごとに複製されたためテストも呼び出し元ごとに持つ）。**移送時にテスト本文は変えていない**
// （namespace 宣言と using の書き換えのみ）。
public class HttpAssumptionsClientTests
{
    // 応答を差し替える偽の送信ハンドラ。
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? RequestedPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpAssumptionsClient ClientOver(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://configuration-service:8080") },
            NullLogger<HttpAssumptionsClient>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body) };

    [Fact]
    public async Task 現在値と版を取得する()
    {
        var payload = new VersionedAssumptions(
            TradingAssumptionsDefaults.Create() with { FxSpreadRatio = 0.003m }, Version: 7);
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, payload));

        var fetched = await ClientOver(handler).FetchAsync();

        fetched.Should().NotBeNull();
        fetched!.Version.Should().Be(7);
        fetched.Assumptions.FxSpreadRatio.Should().Be(0.003m);
        handler.RequestedPath.Should().Be("/assumptions");
    }

    // s2s トークン未設定（ServiceAuth 未構成）で 401 になっても例外を出さない。
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task 非2xxは取得不可に倒す(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        (await ClientOver(handler).FetchAsync()).Should().BeNull();
    }

    [Fact]
    public async Task 不正な応答本文は取得不可に倒す()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("これはJSONではない"),
        });

        (await ClientOver(handler).FetchAsync()).Should().BeNull();
    }

    // 版なし（0）の応答は未解決の番兵と衝突するため取得不可として扱う（誤って「解決済み v0」を掴まない）。
    [Fact]
    public async Task 版なしの応答は取得不可に倒す()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            new VersionedAssumptions(TradingAssumptionsDefaults.Create(), Version: 0)));

        (await ClientOver(handler).FetchAsync()).Should().BeNull();
    }

    [Fact]
    public async Task 不達は取得不可に倒す()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        (await ClientOver(handler).FetchAsync()).Should().BeNull();
    }

    // HttpClient のタイムアウトは OperationCanceledException として現れる（呼び出し側のキャンセルではない）。
    [Fact]
    public async Task タイムアウトは取得不可に倒す()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));

        (await ClientOver(handler).FetchAsync()).Should().BeNull();
    }

    // 呼び出し側がキャンセルした場合は握り潰さない（停止要求はそのまま伝える）。
    [Fact]
    public async Task 呼び出し側のキャンセルは伝播する()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("cancelled"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await ClientOver(handler).FetchAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
