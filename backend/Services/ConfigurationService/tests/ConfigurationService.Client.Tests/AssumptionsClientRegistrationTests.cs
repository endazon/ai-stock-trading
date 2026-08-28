using ConfigurationService.Client.Adapters;
using ConfigurationService.Client.Extensions;
using ConfigurationService.Client.Ports;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ConfigurationService.Client.Tests;

// FR-17, IADR-0063 決定 6: 消費側の 1 行配線（AddAiStockTradingAssumptions）の安全既定を検証する。
// BaseUrl 未設定なら HTTP を構築せず既定プロバイダ（外部接続なし）＝既定ビルド/CI が緑であること。
public class AssumptionsClientRegistrationTests
{
    private static ServiceProvider BuildWith(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAssumptions(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void BaseUrl未設定なら既定プロバイダ()
    {
        using var provider = BuildWith([]);

        provider.GetRequiredService<IAssumptionsProvider>().Should().BeOfType<DefaultAssumptionsProvider>();
    }

    [Fact]
    public void BaseUrlが不正なURIなら既定プロバイダ()
    {
        using var provider = BuildWith(new() { ["Configuration:BaseUrl"] = "not a uri" });

        provider.GetRequiredService<IAssumptionsProvider>().Should().BeOfType<DefaultAssumptionsProvider>();
    }

    [Fact]
    public async Task 既定プロバイダは前提条件の既定値を未解決として返す()
    {
        using var provider = BuildWith([]);

        var current = await provider.GetRequiredService<IAssumptionsProvider>().GetCurrentAsync();

        current.IsResolved.Should().BeFalse();
        current.Assumptions.Should().Be(TradingAssumptionsDefaults.Create());
    }

    [Fact]
    public void BaseUrl設定時はキャッシュ付きプロバイダ()
    {
        using var provider = BuildWith(new() { ["Configuration:BaseUrl"] = "http://configuration-service:8080" });

        provider.GetRequiredService<IAssumptionsProvider>().Should().BeOfType<CachedAssumptionsProvider>();
    }

    // 購読（AssumptionsChangedConsumer）は消費側 Program で静的に登録されるため、プロバイダの選択に関わらず
    // 無効化の受け口が解決でき、かつ provider と同一インスタンスであること。
    [Theory]
    [InlineData(null)]
    [InlineData("http://configuration-service:8080")]
    public void 無効化の受け口はプロバイダと同一インスタンス(string? baseUrl)
    {
        using var provider = BuildWith(new() { ["Configuration:BaseUrl"] = baseUrl });

        var invalidator = provider.GetRequiredService<IAssumptionsCacheInvalidator>();

        invalidator.Should().BeSameAs(provider.GetRequiredService<IAssumptionsProvider>());
    }
}
