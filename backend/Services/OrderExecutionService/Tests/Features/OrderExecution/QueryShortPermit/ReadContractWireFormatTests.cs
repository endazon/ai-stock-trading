using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1033, FR-10, #967, IADR-0420 決定2, IADR-0425 決定1: **本番の Program.cs が `GET /order-execution/short-permit`
// に出す JSON は、応答型 `ShortPermitView` を web 既定（camelCase・列挙は数値）で直列化したものと一字一句同じである**ことを固定する。
// 受け手（リスク管理の HttpShortSellBorrowSource・T-10-1025）はこの設定で直列化した本文を読ませており、その前提は受け手の側からは
// 見えない —— 例えば本サービスの Program.cs に `JsonStringEnumConverter` が足されると、受け手のテストは緑のまま実行時は状態の
// 読み取りで失敗する（＝借株可否は分からない＝空売りは拒否され続ける。安全側だが、上限が一度も評価されない状態に黙って戻る）。
// あわせて口の守り（匿名は 401・値域外は 400 でブローカーの枠を使わない）を固定する。
public class ReadContractWireFormatTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> Wire(ExecutionWorkerWebApplicationFactory factory, IShortPermitSource source) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            // 内蔵 paper 構成は照会ポートを登録しない。照会の答え（ブローカーの境界）だけを足し、口・サービスの組み立て
            // （Program.cs の構築式が GetService で拾う）・直列化は本物のまま組む。
            services.RemoveAll<IShortPermitSource>();
            services.AddSingleton(source);
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        }));

    [Fact]
    public async Task 借株可否の本文は応答型を_web_既定で直列化したものと同じ()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        using var wired = Wire(factory, new Permit(true));
        var client = wired.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");

        var res = await client.GetAsync("/order-execution/short-permit?symbol=AAPL&market=1", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        // 同じ照会はキャッシュから同じ値を返す（照会した時刻も同じ）＝本文と突き合わせる期待値。
        var expected = await wired.Services.GetRequiredService<ShortPermitQueryService>()
            .QueryAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);
        expected.Status.Should().Be(ShortPermitStatus.Permitted, "空の値どうしの一致は何も証明しない");

        JsonNode.DeepEquals(body, JsonSerializer.SerializeToNode(expected, Web))
            .Should().BeTrue($"借株可否の本文が web 既定と異なる: {body.ToJsonString()}");
        body["status"]!.GetValue<int>().Should().Be((int)ShortPermitStatus.Permitted);
        body["market"]!.GetValue<int>().Should().Be((int)Market.UnitedStates);
        body["symbol"]!.GetValue<string>().Should().Be("AAPL");
    }

    [Theory]
    [InlineData("/order-execution/short-permit?symbol=AAPL&market=1", null, HttpStatusCode.Unauthorized)]
    [InlineData("/order-execution/short-permit?market=1", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/short-permit?symbol=AAPL", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/short-permit?symbol=AAPL&market=7", "trading-service", HttpStatusCode.BadRequest)]
    [InlineData("/order-execution/short-permit?symbol=AAPL&market=1", "trading-owner", HttpStatusCode.OK)]
    public async Task 匿名は401で値域外は400になりブローカーへ照会しない(string path, string? roles, HttpStatusCode expected)
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        var source = new Permit(true);
        using var wired = Wire(factory, source);
        var client = wired.CreateClient();
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);

        var res = await client.GetAsync(path, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(expected);
        source.Calls.Should().Be(expected == HttpStatusCode.OK ? 1 : 0);
    }

    private sealed class Permit(bool? answer) : IShortPermitSource
    {
        public int Calls { get; private set; }

        public Task<bool?> GetShortPermitAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }
}
