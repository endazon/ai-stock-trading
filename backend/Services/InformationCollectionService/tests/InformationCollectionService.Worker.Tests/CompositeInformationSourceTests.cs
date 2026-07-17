using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01, ADR-0004, IADR-0064: 案A+ の複数情報源の合成。1 ソースの障害が他ソースと巡回を巻き込まないこと（欠測検知・
// フォールバック）を検証する。
public class CompositeInformationSourceTests
{
    [Fact]
    public async Task 全ソースの取得結果を連結して返す()
    {
        var composite = new CompositeInformationSource(
            [Stub("sec-edgar"), Stub("fred")], NullLogger<CompositeInformationSource>.Instance);

        var items = await composite.FetchAsync();

        items.Select(i => i.Source).Should().Equal("sec-edgar", "fred");
    }

    [Fact]
    public async Task 一部のソースが例外を投げても他ソースの結果を返す()
    {
        var composite = new CompositeInformationSource(
            [Throwing(), Stub("fred")], NullLogger<CompositeInformationSource>.Instance);

        var items = await composite.FetchAsync();

        // 障害ソースは欠測としてログし、巡回自体は継続する（案A+ の冗長化の狙いを保つ）。
        items.Select(i => i.Source).Should().Equal("fred");
    }

    [Fact]
    public async Task 全ソースが失敗しても空を返し例外を投げない()
    {
        var composite = new CompositeInformationSource(
            [Throwing(), Throwing()], NullLogger<CompositeInformationSource>.Instance);

        var items = await composite.FetchAsync();

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task キャンセルは握りつぶさず伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var composite = new CompositeInformationSource(
            [new CancelingSource()], NullLogger<CompositeInformationSource>.Instance);

        var act = () => composite.FetchAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static IInformationSource Stub(string source) => new StubSource(source);

    private static IInformationSource Throwing() => new ThrowingSource();

    private sealed class StubSource(string source) : IInformationSource
    {
        public Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RawInformationItem>>(
            [
                new RawInformationItem(
                    InformationKind.News, source, null, "title", "content", DateTimeOffset.UnixEpoch)
            ]);
    }

    private sealed class ThrowingSource : IInformationSource
    {
        public Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("情報源が応答しない");
    }

    private sealed class CancelingSource : IInformationSource
    {
        public Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<RawInformationItem>>([]);
        }
    }
}
