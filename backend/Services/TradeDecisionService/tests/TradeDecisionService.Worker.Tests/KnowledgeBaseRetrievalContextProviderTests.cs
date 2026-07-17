using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// FR-08, IADR-0069/0072: RAG 取得アダプタが trigger+policy から検索クエリを組み、KnowledgeHit を RetrievedContext へ写像することを検証する。
public class KnowledgeBaseRetrievalContextProviderTests
{
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "米国株の押し目買い方針");

    private sealed class FakeSearch(IReadOnlyList<KnowledgeHit> hits) : IKnowledgeBaseSearch
    {
        public KnowledgeQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(KnowledgeQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(hits);
        }
    }

    private static KnowledgeBaseRetrievalContextProvider Create(FakeSearch search, int topK = 5) =>
        new(search, topK, NullLogger<KnowledgeBaseRetrievalContextProvider>.Instance);

    [Fact]
    public async Task 検索クエリは銘柄と市場と方針要約から組み立てられTopKを渡す()
    {
        var search = new FakeSearch([]);
        var provider = Create(search, topK: 7);

        await provider.GetContextAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates), Policy);

        search.LastQuery.Should().NotBeNull();
        search.LastQuery!.Query.Should().Contain("AAPL");
        search.LastQuery.Query.Should().Contain(Policy.Summary);
        search.LastQuery.TopK.Should().Be(7);
    }

    [Fact]
    public async Task 検索ヒットはRetrievedContextへ写像される()
    {
        var docId = Guid.NewGuid();
        var search = new FakeSearch(new[]
        {
            new KnowledgeHit(docId, "決算メモ", "増収増益。", 0.91d, "kb://doc/1", ["earnings"]),
        });
        var provider = Create(search);

        var result = await provider.GetContextAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates), Policy);

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("決算メモ");
        result[0].Text.Should().Be("増収増益。");
        result[0].SourceUri.Should().Be("kb://doc/1");
        result[0].Score.Should().Be(0.91d);
    }

    [Fact]
    public async Task 検索ヒットが空なら空の文脈を返す()
    {
        var provider = Create(new FakeSearch([]));

        var result = await provider.GetContextAsync(DecisionTrigger.Scheduled("7203", Market.Japan), Policy);

        result.Should().BeEmpty();
    }
}
