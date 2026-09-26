using System.Net;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AwesomeAssertions;
using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, ADR-0043（計画）決定 1, #1044 項目 3, IADR-0437 決定 7（T-10-1574）: 情報収集の本番の組み立て（InformationSourceFactory）で、
// 企業ニュース（/company-news）と現在値（/quote）は直前の要求の時刻を共有する。企業ニュースの要求の直後に現在値が
// 「残りがあるのに 429」を受けても、秒次（30 回/秒）の余地として日次上限の手がかり（4301）にしない。
// 実 Finnhub は叩かない（偽のハンドラ）。
public class FinnhubSharedLastRequestTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    // T-10-1574: 企業ニュースの直後（同じ時刻）の現在値の 429（残り 5）は 4301 にならない。企業ニュースを挟まずに受けた最初の
    // 429（残り 5）は従来どおり 4301（同じ応答が手がかりになる状況であることの陽性対照）。
    [Fact]
    public async Task 企業ニュースの直後の現在値の429は日次の手がかりにしない()
    {
        var afterNews = new CapturingLoggerFactory();
        var sources = Create(afterNews);
        await Source(sources, InformationSourceFactory.FinnhubNews).FetchAsync();
        await Source(sources, InformationSourceFactory.Finnhub).FetchAsync();

        afterNews.Entries.Should().Contain(e => e.Message.Contains("PossibleBurst"), "現在値の 429 は秒次の余地として記録する");
        afterNews.Entries.Should().NotContain(e => e.EventId == FinnhubQuoteClient.DailyLimitClueEvent);

        var alone = new CapturingLoggerFactory();
        await Source(Create(alone), InformationSourceFactory.Finnhub).FetchAsync();
        alone.Entries.Should().ContainSingle(e => e.EventId == FinnhubQuoteClient.DailyLimitClueEvent);
    }

    private static IReadOnlyList<NamedInformationSource> Create(ILoggerFactory logs) =>
        InformationSourceFactory.Create(
            new CollectionSourceOptions
            {
                Provider = "finnhub,finnhub-news",
                Finnhub = new FinnhubOptions { ApiKey = "key", Symbols = ["AAPL"] },
            },
            new HttpClient(new FinnhubRouteHandler()),
            new FixedClock(),
            new FixedTimeProvider(),
            logs);

    private static IInformationSource Source(IReadOnlyList<NamedInformationSource> sources, string name) =>
        sources.Single(s => s.Name == name).Source;

    // /company-news は成功（記事 0 件）、/quote は「残り 5・リセットは未来」の 429。
    private sealed class FinnhubRouteHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/company-news", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
            response.Headers.Add(FinnhubRateLimitClassifier.RemainingHeader, "5");
            response.Headers.Add(
                FinnhubRateLimitClassifier.ResetHeader,
                Now.AddSeconds(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<(EventId EventId, string Message)> _entries = [];

        public IReadOnlyList<(EventId EventId, string Message)> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                    owner._entries.Add((eventId, formatter(state, exception)));
            }
        }
    }
}
