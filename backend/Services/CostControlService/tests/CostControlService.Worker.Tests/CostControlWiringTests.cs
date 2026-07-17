using AiStockTrading.Configuration.Client.Composable.Steps;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.CostControl.Worker.Tests;

// NFR（費用）, FR-17, #139, IADR-0065: 費用統制ホストが上限をバージョン付き前提条件から取るよう配線されていることを固定する。
// 挙動（追随・fail-safe）は VersionedCostLimitsTests が見る。ここは「Program.cs の配線が外れていないこと」だけを見る
// （配線が外れると全テストが既定値で緑のまま通ってしまい、上限変更が効かない現象に気付けないため）。
public class CostControlWiringTests
{
    [Fact]
    public void 月次上限はバージョン付き前提条件から供給される()
    {
        using var factory = new CostControlWorkerWebApplicationFactory();

        factory.Services.GetRequiredService<ICostLimitsProvider>()
            .Should().BeOfType<AssumptionsCostLimitsProvider>();
    }

    // 版の追随はイベント購読による無効化に依る（IADR-0065 決定 4）。購読の登録が外れると、
    // 利用者が上限を変更しても TTL（既定 5 分）が切れるまで追随しなくなる。
    [Fact]
    public void 費用統制は前提条件の変更を購読する()
    {
        using var factory = new CostControlWorkerWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetService<AssumptionsChangedConsumer>().Should().NotBeNull();
    }

    // 安全既定（IADR-0063 決定 6 / IADR-0065 決定 5）: Configuration:BaseUrl 未設定なら HTTP を構築せず既定値へ倒れる。
    // テスト構成では BaseUrl を与えていないため、外部接続なしで既定の上限が供給される（現行挙動の保持）。
    [Fact]
    public async Task BaseUrl未設定なら外部接続せず既定の上限へ倒れる()
    {
        using var factory = new CostControlWorkerWebApplicationFactory();

        var limits = await factory.Services.GetRequiredService<ICostLimitsProvider>().GetLimitsAsync();

        limits.Should().Be(TradingAssumptionsDefaults.Create().CostLimits);
    }
}
