using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RiskManagementService.Tests;

// FR-06, FR-16, FR-10, #611, ADR-0022, IADR-0285 決定1: 承認記録時の認識時レートの源が
// **composition root で実際に結線されている**ことを固定する（判断サービスの FxWiringTests と同型）。
// 配線が外れると、構成で有効化したつもりのレート源が黙って no-op のままになり、承認の認識時レートが恒久的に未記録になる
// （＝症状が「報告書の為替差損益が出ない」なので気づきにくい）。
public class RecognitionFxRateWiringTests
{
    private static RiskWorkerWebApplicationFactory Factory(IDictionary<string, string?>? settings = null) =>
        settings is null
            ? new RiskWorkerWebApplicationFactory()
            : new RiskWorkerWebApplicationFactory { HostSettings = settings };

    // 🔴 **否定形（安全既定）**: Fx:Provider が無い構成では no-op（外部へ接続しない）。承認は未記録のまま記録される。
    [Fact]
    public void 既定は実接続しないno_opのレート源()
    {
        using var factory = Factory();
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IFxRateSource>().Should().BeOfType<NoOpFxRateSource>();
        factory.Services.GetRequiredService<IRecognitionFxRateResolver>()
            .Should().BeOfType<FxSourceRecognitionFxRateResolver>();
    }

    // **対の肯定形**: 判断サービスと同じ構成キーで実レート源（鮮度装飾つき）が組み上がる。
    [Fact]
    public void fred指定かつAPIキーありで実レート源になる()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Fx:Provider"] = "fred",
            ["Fx:Fred:ApiKey"] = "key",
        });
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IFxRateSource>().Should().BeOfType<CachingFxRateSource>();
    }

    // レート源は singleton（TTL キャッシュを承認をまたいで共有し、承認ごとに源を叩かない）。
    [Fact]
    public void レート源はsingletonでキャッシュが共有される()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["Fx:Provider"] = "fred",
            ["Fx:Fred:ApiKey"] = "key",
        });
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<IFxRateSource>()
            .Should().BeSameAs(factory.Services.GetRequiredService<IFxRateSource>());
    }
}
