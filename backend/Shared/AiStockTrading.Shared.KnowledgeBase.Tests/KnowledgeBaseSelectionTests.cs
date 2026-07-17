using AiStockTrading.Shared.KnowledgeBase.Adapters;
using AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.Shared.KnowledgeBase.Tests;

// FR-08, IADR-0069 決定 2: KnowledgeBase:Documents/Search:BaseUrl の有無で保存・取得ポートが
// 安全既定（NoOp）／実 HTTP に切り替わることを検証する。選択は解決時に構成を読む。
public class KnowledgeBaseSelectionTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingKnowledgeBase(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void BaseUrl未設定は保存も取得も安全既定のNoOp()
    {
        using var sp = Build();

        sp.GetRequiredService<IKnowledgeBaseWriter>().Should().BeOfType<NoOpKnowledgeBaseWriter>();
        sp.GetRequiredService<IKnowledgeBaseSearch>().Should().BeOfType<NoOpKnowledgeBaseSearch>();
    }

    [Fact]
    public void DocumentsBaseUrl設定時は保存が実HTTPになる()
    {
        using var sp = Build(("KnowledgeBase:Documents:BaseUrl", "http://documents"));

        sp.GetRequiredService<IKnowledgeBaseWriter>().Should().BeOfType<HttpKnowledgeBaseWriter>();
        // 取得側は未設定のため NoOp のまま（独立に opt-in）。
        sp.GetRequiredService<IKnowledgeBaseSearch>().Should().BeOfType<NoOpKnowledgeBaseSearch>();
    }

    [Fact]
    public void SearchBaseUrl設定時は取得が実HTTPになる()
    {
        using var sp = Build(("KnowledgeBase:Search:BaseUrl", "http://retrieval"));

        sp.GetRequiredService<IKnowledgeBaseSearch>().Should().BeOfType<HttpKnowledgeBaseSearch>();
        sp.GetRequiredService<IKnowledgeBaseWriter>().Should().BeOfType<NoOpKnowledgeBaseWriter>();
    }

    [Fact]
    public void 不正なBaseUrlは安全既定のNoOp()
    {
        using var sp = Build(
            ("KnowledgeBase:Documents:BaseUrl", "not-a-url"),
            ("KnowledgeBase:Search:BaseUrl", "not-a-url"));

        sp.GetRequiredService<IKnowledgeBaseWriter>().Should().BeOfType<NoOpKnowledgeBaseWriter>();
        sp.GetRequiredService<IKnowledgeBaseSearch>().Should().BeOfType<NoOpKnowledgeBaseSearch>();
    }
}
