using AiStockTrading.Shared.Contracts.Events;
using CostControlService.Application.Ports;
using CostControlService.Infrastructure.Adapters;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Xunit;

namespace CostControlService.Api.Tests;

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
    //
    // ADR-0013, IADR-0129, #354: MassTransit は consumer を DI へ登録したため型の解決可否で購読を見られたが、
    // Wolverine のハンドラは DI に型登録されず、アセンブリ走査で発見される。よって「発見されたか」を直接見る
    // （AssumptionsChanged を扱う実行器が解決でき、未処理型を表す NoHandlerExecutor でないこと）。
    // ハンドラのあるアセンブリを Program.cs が発見範囲から外すと、この検査が落ちる。
    [Fact]
    public void 費用統制は前提条件の変更を購読する()
    {
        using var factory = new CostControlWorkerWebApplicationFactory();
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();

        var invoker = runtime.FindInvoker(typeof(AssumptionsChanged));

        invoker.Should().NotBeNull();
        invoker.GetType().Name.Should().NotBe("NoHandlerExecutor");
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
