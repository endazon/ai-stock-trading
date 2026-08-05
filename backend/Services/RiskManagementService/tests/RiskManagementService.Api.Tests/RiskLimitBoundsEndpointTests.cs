using System.Net;
using System.Net.Http.Json;
using AiStockTrading.RiskManagement.Api.Foundation.Endpoints;
using AiStockTrading.RiskManagement.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Api.Tests;

// FR-10, SC-02, UC-06, #362, IADR-0151 決定2: `PUT /risk-controls/settings/limits` の値域の関門。
//
// **本テスト群の主眼は否定形かつ迂回不能性である。** 画面（SC-02）にも同じ値域があるが、画面だけの統制は
// API を直接叩けば消える（IADR-0141 決定1 と同じ判断）。ここで確かめるのは「画面を通さない経路でも
// 統制を無効化する値が保存できないこと」であり、**加えて拒否時に設定も履歴も変わっていないこと**である
// （拒否された要求を履歴に積むと、実際には起きていない変更が監査上の事実になる）。
//
// 各テストは専用のファクトリ（＝独立した設定ストア）を立てる。共有すると「変わっていない」の確認が
// 他テストの保存に汚染される。
public class RiskLimitBoundsEndpointTests
{
    private const string Owner = "trading-owner";

    private static HttpClient OwnerClient(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, Owner);
        return client;
    }

    // 否定形（#362 が名指しした事故）: 利用者が旧来の感覚で入れた「35000」が equity 比として届いても、
    // **equity の 35,000 倍**は 1 注文の上限にならない。
    [Fact]
    public async Task equityの35000倍を送っても400で設定は変わらない()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        var before = await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings");

        var res = await client.PutAsJsonAsync("/risk-controls/settings/limits",
            new LimitsUpdateRequest(
                TradingDefaults.CreateRiskLimits() with { MaxOrderAmountRatio = 35_000m },
                "統制を無効化する値"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var after = await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings");
        after!.Limits.MaxOrderAmountRatio.Should().Be(before!.Limits.MaxOrderAmountRatio,
            "拒否された要求で設定が変わってはならない");
    }

    // 否定形: 拒否された変更は**履歴にも残らない**（起きていない変更を監査上の事実にしない）。
    [Fact]
    public async Task 値域外の要求は変更履歴に残らない()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PutAsJsonAsync("/risk-controls/settings/limits",
            new LimitsUpdateRequest(
                TradingDefaults.CreateRiskLimits() with { LosingStreakSizeFactor = 1.0m },
                "連敗時の縮小をやめる"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var history = await client.GetFromJsonAsync<List<SettingsChangeDto>>("/risk-controls/settings/history");
        history.Should().NotBeNull();
        history!.Should().NotContain(e => e.Reason == "連敗時の縮小をやめる");
    }

    // 400 の本文には**違反の全件**が入る（画面がそのまま利用者へ提示する）。
    [Fact]
    public async Task 拒否応答の本文に違反の詳細が全件入る()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PutAsJsonAsync("/risk-controls/settings/limits",
            new LimitsUpdateRequest(
                TradingDefaults.CreateRiskLimits() with { MaxOrderAmountRatio = 35_000m, MaxOpenPositions = 0 },
                "複数の値域違反"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<BoundsErrorDto>();
        body!.Details.Should().HaveCount(2);
        body.Details.Should().Contain(d => d.Contains("1注文あたりの発注金額上限"));
        body.Details.Should().Contain(d => d.Contains("保有建玉数上限"));
    }

    // 肯定形（否定形の対照）: 値域内なら保存される。これが無いと「常に 400 を返す」実装でも
    // 上の否定形が通ってしまう。
    [Fact]
    public async Task 値域内の上限は保存される()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var res = await client.PutAsJsonAsync("/risk-controls/settings/limits",
            new LimitsUpdateRequest(
                TradingDefaults.CreateRiskLimits() with { MaxOrderAmountRatio = 0.35m },
                "1 注文上限を 35% へ引き上げる"));

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<SettingsDto>("/risk-controls/settings");
        after!.Limits.MaxOrderAmountRatio.Should().Be(0.35m);
    }

    // 応答の読み取りは関心のあるキーだけの DTO で行う（`RiskManagementSettings` は
    // `IReadOnlySet` を含み System.Text.Json で逆直列化できない）。
    private sealed record SettingsDto(LimitsDto Limits);

    private sealed record LimitsDto(decimal MaxOrderAmountRatio);

    private sealed record BoundsErrorDto(string Error, string[] Details);

    private sealed record SettingsChangeDto(string Actor, string Reason);
}
