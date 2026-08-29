using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using InformationCollectionService.Domain;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, ADR-0004, ADR-0020 決定3, IADR-0064: 案A+ の複数情報源の合成。
// 1 ソースの障害が他ソースと巡回を巻き込まないこと（欠測検知・フォールバック）と、
// **ソース単位の成否が判定へ渡ること**を検証する（旧 CompositeInformationSourceTests の後継）。
public class SourceFetchRunnerTests
{
    [Fact]
    public async Task 全ソースの取得結果を連結して返す()
    {
        var runner = Create(Stub("sec-edgar"), Stub("fred"));

        var result = await runner.FetchAllAsync();

        result.Items.Select(i => i.Source).Should().Equal("sec-edgar", "fred");
        result.Outcomes.Should().OnlyContain(o => o.Succeeded);
    }

    [Fact]
    public async Task 一部のソースが例外を投げても他ソースの結果を返す()
    {
        var runner = Create(Throwing("finnhub-news"), Stub("fred"));

        var result = await runner.FetchAllAsync();

        // 障害ソースは欠測として記録し、巡回自体は継続する（案A+ の冗長化の狙いを保つ）。
        result.Items.Select(i => i.Source).Should().Equal("fred");
        result.Outcomes.Should().Equal(SourceOutcome.Failed("finnhub-news"), SourceOutcome.Ok("fred"));
    }

    // 🔴 **失敗をログして捨てない。** 捨てると「どの区分が落ちたか」が欠測判定へ届かない（ADR-0020 決定3）。
    [Fact]
    public async Task 全ソースが失敗しても例外を投げずソース単位の欠測を返す()
    {
        var runner = Create(Throwing("finnhub-news"), Throwing("google-news"));

        var result = await runner.FetchAllAsync();

        result.Items.Should().BeEmpty();
        result.Outcomes.Should().OnlyContain(o => !o.Succeeded);

        // 判定へ渡すと「ニュース系の全滅」になる。
        DegradationEvaluator.Evaluate(InformationSourceCatalog.Default, result.Outcomes)
            .NewsOutage.Should().BeTrue();
    }

    // 取得が 0 件でも「成功」である（新着が無い日と、取れなかった日を混同しない）。
    [Fact]
    public async Task 取得0件は欠測ではなく成功として記録する()
    {
        var runner = Create(new NamedInformationSource("google-news", new EmptySource()));

        var result = await runner.FetchAllAsync();

        result.Outcomes.Should().Equal(SourceOutcome.Ok("google-news"));
        DegradationEvaluator.Evaluate(InformationSourceCatalog.Default, result.Outcomes)
            .NewsOutage.Should().BeFalse();
    }

    [Fact]
    public async Task キャンセルは握りつぶさず伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var runner = Create(new NamedInformationSource("fred", new CancelingSource()));

        var act = () => runner.FetchAllAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void 有効化された情報源の名前を申告する()
    {
        Create(Stub("fred"), Stub("boj")).SourceNames.Should().Equal("fred", "boj");
    }

    private static SourceFetchRunner Create(params NamedInformationSource[] sources) =>
        new(sources, NullLogger<SourceFetchRunner>.Instance);

    private static NamedInformationSource Stub(string source) =>
        new(source, new StubSource(source));

    private static NamedInformationSource Throwing(string source) =>
        new(source, new ThrowingSource());

    private sealed class StubSource(string source) : IInformationSource
    {
        public Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RawInformationItem>>(
            [
                new RawInformationItem(
                    InformationKind.News, source, null, "title", "content", DateTimeOffset.UnixEpoch)
            ]);
    }

    private sealed class EmptySource : IInformationSource
    {
        public Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RawInformationItem>>([]);
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
