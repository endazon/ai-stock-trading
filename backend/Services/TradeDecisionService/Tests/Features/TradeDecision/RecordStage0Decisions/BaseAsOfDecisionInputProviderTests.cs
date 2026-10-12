extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Xunit;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-15, FR-07, ADR-0033 決定2, ADR-0036 決定1, #1308, IADR-0528: Stage 0 の as-of 入力の基底の供給。
//
// 見るのは 4 点である。
//   1. 判断時点は判断日の通常取引の終値の時刻（夏時間の切り替えをまたいでも正しい）。
//   2. 🔴 参考情報の窓を両端で切る（未来・古い記事を落とし、境界ちょうどは残す）。新しい順・上位 K 件・銘柄と市場を別々に数える。
//   3. 🔴 3 源がそろわなければ (b) を再構成不可と申告し、欠けた源は理由欄に残す（利用者裁定 2026-10-13）。
//   4. 🔴 「当時無かった」（休場・確定日報なし）は null、「取れなかった」（照会の失敗）は例外。両者を混ぜない。
public class BaseAsOfDecisionInputProviderTests
{
    // 2026-09-15（火）。米国は夏時間（EDT・UTC−4）なので終値の時刻は 20:00Z。
    private static readonly DateOnly AsOf = new(2026, 9, 15);
    private static readonly DateTimeOffset At = new(2026, 9, 15, 20, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------------------------------------
    // 1. 判断時点
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2026, 9, 15, 20)] // 夏時間（EDT）: 16:00 ET ＝ 20:00Z
    [InlineData(2026, 10, 30, 20)] // 夏時間の最後の金曜
    [InlineData(2026, 11, 2, 21)] // 11/1 に標準時へ戻った翌営業日（EST）: 16:00 ET ＝ 21:00Z
    [InlineData(2026, 3, 6, 21)] // 標準時の最後の金曜
    [InlineData(2026, 3, 9, 20)] // 3/8 に夏時間へ入った翌営業日
    public void 判断時点は判断日の通常取引の終値の時刻である(int y, int m, int d, int utcHour)
    {
        Stage0DecisionInstant.Of(Market.UnitedStates, new DateOnly(y, m, d))
            .Should().Be(new DateTimeOffset(y, m, d, utcHour, 0, 0, TimeSpan.Zero));
    }

    // ------------------------------------------------------------------------------------------------
    // 2. 参考情報の窓（仕様書「母集合」の窓の表のプローブ）
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task 参考情報は窓の両端で切り_境界ちょうどは残す()
    {
        var feed = Feed("finnhub-news", AsOfReferenceCoverage.Symbol,
            Doc("未来の記事", At.AddSeconds(1), "finnhub-news"),
            Doc("古い記事", At.AddHours(-168).AddSeconds(-1), "finnhub-news"),
            Doc("判断時点ちょうど", At, "finnhub-news"),
            Doc("窓の始まりちょうど", At.AddHours(-168), "finnhub-news"));

        var set = await Assembler(5, feed).AssembleAsync("AAPL", Market.UnitedStates, At, TestContext.Current.CancellationToken);

        set.Contexts.Select(c => c.Title).Should().Equal("判断時点ちょうど", "窓の始まりちょうど");
        feed.Requests.Should().Equal((At.AddHours(-168), At));
    }

    [Fact]
    public async Task 参考情報は新しい順に上位K件を銘柄と市場で別々に数える()
    {
        var news = Feed("finnhub-news", AsOfReferenceCoverage.Symbol,
            Doc("n1", At.AddHours(-1), "finnhub-news"),
            Doc("n3", At.AddHours(-3), "finnhub-news"),
            Doc("n2", At.AddHours(-2), "finnhub-news"));
        var edgar = Feed("sec-edgar", AsOfReferenceCoverage.Symbol, Doc("e1", At.AddMinutes(-90), "sec-edgar"));
        var fred = Feed("fred", AsOfReferenceCoverage.Market,
            Doc("f1", At.AddHours(-10), "fred"),
            Doc("f2", At.AddHours(-20), "fred"),
            Doc("f3", At.AddHours(-30), "fred"));

        var set = await Assembler(2, news, edgar, fred).AssembleAsync("AAPL", Market.UnitedStates, At, TestContext.Current.CancellationToken);

        set.Contexts.Select(c => c.Title).Should().Equal("n1", "e1", "f1", "f2");
        set.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task 出所タグの無い文書は渡さない_発行時刻の無い文書は落とさない()
    {
        var feed = Feed("finnhub-news", AsOfReferenceCoverage.Symbol,
            Doc("出所なし", At.AddHours(-1)),
            Doc("時刻なし", null, "finnhub-news"));

        var set = await Assembler(5, feed).AssembleAsync("AAPL", Market.UnitedStates, At, TestContext.Current.CancellationToken);

        set.Contexts.Select(c => c.Title).Should().Equal("時刻なし");
    }

    // 🔴 #1323 の AI レビュー: 時刻つきの文書が K 件を超えても、時刻なしの文書は枠の外で必ず渡す。
    // 押し出されると「時点に置けない資料が在った」ことが消え、(b) が再構成できたと誤って申告される。
    [Fact]
    public async Task 時刻つきの文書がK件を超えても時刻なしの文書は枠の外で渡り再構成不可と申告される()
    {
        var provider = Provider(feeds:
        [
            Feed("finnhub-news", AsOfReferenceCoverage.Symbol,
                Doc("n1", At.AddHours(-1), "finnhub-news"),
                Doc("n2", At.AddHours(-2), "finnhub-news"),
                Doc("n3", At.AddHours(-3), "finnhub-news"),
                Doc("時刻なし", null, "finnhub-news")),
            Feed("sec-edgar", AsOfReferenceCoverage.Symbol),
            Feed("fred", AsOfReferenceCoverage.Market),
        ], topK: 2);

        var input = await provider.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        input!.DroppedUndatedReferenceCount.Should().Be(1);
        input.References.Select(r => r.Title).Should().Equal("n1", "n2");
        input.AsOfInputs.Single(s => s.Kind == Stage0AsOfInputKind.NewsAndDisclosures).Availability
            .Should().Be(Stage0AsOfInputAvailability.NotReconstructable);
    }

    // ------------------------------------------------------------------------------------------------
    // 3. (b) の申告と欠けた源
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task 三つの源がそろえばニュース開示は再構成済みで理由欄に欠けた源が残る()
    {
        var provider = Provider(feeds: AllFeeds(Doc("記事", At.AddHours(-1), "finnhub-news")));

        var input = await provider.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        var news = input!.AsOfInputs.Single(s => s.Kind == Stage0AsOfInputKind.NewsAndDisclosures);
        news.Availability.Should().Be(Stage0AsOfInputAvailability.Reconstructed);
        news.Reason.Should().Contain("欠けた源: google-news").And.Contain("report（確定報告書の RAG）");
        news.Reason.Should().NotContain("取れなかった源");
        input.NotReconstructableKinds.Should().NotContain(Stage0AsOfInputKind.NewsAndDisclosures);
    }

    [Fact]
    public async Task 三つの源がそろって記事が無ければ当時無しと申告する()
    {
        var input = await Provider(feeds: AllFeeds()).GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        var news = input!.AsOfInputs.Single(s => s.Kind == Stage0AsOfInputKind.NewsAndDisclosures);
        news.Availability.Should().Be(Stage0AsOfInputAvailability.AbsentAtAsOf);
        news.Reason.Should().Contain("欠けた源");
    }

    [Theory]
    [InlineData("fail", "取れなかった源: sec-edgar")]
    [InlineData("throw", "取れなかった源: sec-edgar")]
    [InlineData("unconfigured", "取れなかった源: sec-edgar（未構成）")]
    public async Task 必須の源が一つでも取れなければニュース開示は再構成不可と申告する(string mode, string expected)
    {
        var feeds = new List<IAsOfReferenceFeed>
        {
            Feed("finnhub-news", AsOfReferenceCoverage.Symbol, Doc("記事", At.AddHours(-1), "finnhub-news")),
            Feed("fred", AsOfReferenceCoverage.Market),
        };
        if (mode != "unconfigured")
            feeds.Add(new FakeFeed("sec-edgar", AsOfReferenceCoverage.Symbol, [], fail: mode == "fail", @throw: mode == "throw"));

        var input = await Provider(feeds: [.. feeds]).GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        var news = input!.AsOfInputs.Single(s => s.Kind == Stage0AsOfInputKind.NewsAndDisclosures);
        news.Availability.Should().Be(Stage0AsOfInputAvailability.NotReconstructable);
        news.Reason.Should().Contain(expected).And.Contain("欠けた源");
        input.NotReconstructableKinds.Should().Contain(Stage0AsOfInputKind.NewsAndDisclosures);
    }

    // ------------------------------------------------------------------------------------------------
    // 4. 当時無かった（null）と取れなかった（例外）
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task 価格と方針と参考情報を判断時点で組みサイジングは固定値である()
    {
        var policies = new FakePolicies(new DailyPolicy(AsOf.AddDays(-1), "前日に確定した方針"));
        var prices = new FakePrices(new AsOfPriceReading(new DatedPrice(AsOf, 101m), new DatedPrice(AsOf.AddDays(-1), 100m)));
        var provider = Provider(prices, policies, AllFeeds(Doc("記事", At.AddHours(-1), "finnhub-news")));

        var input = await provider.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        policies.Requests.Should().Equal(At);
        prices.Requests.Should().Equal(("AAPL", Market.UnitedStates, AsOf));
        input!.AsOf.Should().Be(AsOf);
        input.ReferencePrice.Should().Be(101m);
        input.Intraday.PreviousClose.Should().Be(100m);
        input.Policy.Summary.Should().Be("前日に確定した方針");
        input.RateToBase.Should().Be(1m);
        input.References.Select(r => r.Title).Should().Equal("記事");

        var limits = TradingDefaults.CreateRiskLimits();
        input.Sizing.Should().BeEquivalentTo(new SizingContext(
            1_000_000m, 1_000_000m, limits.MaxDailyOrderAmountFor(1_000_000m), 0, 0m, BrokerProvider.MoomooSimulate, limits));
        // 監視銘柄（(e)）は外側のデコレータが埋める。基底だけでは再構成不可のまま（記録の対象銘柄で代えない）。
        input.Watchlist.Should().BeNull();
    }

    [Fact]
    public async Task 判断日の足が無い日は判断しない_休場()
    {
        var policies = new FakePolicies(new DailyPolicy(AsOf.AddDays(-1), "方針"));
        var provider = Provider(new FakePrices(new AsOfPriceReading(null, new DatedPrice(AsOf.AddDays(-1), 100m))), policies, AllFeeds());

        (await provider.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken)).Should().BeNull();
        policies.Requests.Should().BeEmpty("休場の日は方針も参考情報も引かない");
    }

    [Fact]
    public async Task 判断時点までに確定した日報が無ければ判断しない()
    {
        var provider = Provider(policies: new FakePolicies((DailyPolicy?)null), feeds: AllFeeds());

        (await provider.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task 方針の日付が判断日より後なら判断しない()
    {
        var provider = Provider(policies: new FakePolicies(new DailyPolicy(AsOf.AddDays(1), "未来の方針")), feeds: AllFeeds());

        (await provider.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task 基準通貨でない市場は判断しない()
    {
        var prices = new FakePrices(new AsOfPriceReading(new DatedPrice(AsOf, 1_000m), null));
        var provider = Provider(prices: prices, feeds: AllFeeds());

        (await provider.GetAsync("7203", Market.Japan, AsOf, TestContext.Current.CancellationToken)).Should().BeNull();
        prices.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 価格の口が判断日と違う日の足を返したら記録を止める()
    {
        var provider = Provider(new FakePrices(new AsOfPriceReading(new DatedPrice(AsOf.AddDays(-1), 100m), null)), feeds: AllFeeds());

        var act = () => provider.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<AsOfInputUnavailableException>();
    }

    // 🔴 否定形: 照会の失敗を「判断しない（null）」に倒さない（記録から判断が黙って消え、再生の経路が変わる）。
    [Fact]
    public async Task 価格と方針の照会の失敗は例外のまま伝わる()
    {
        var failingPrices = Provider(new FakePrices((AsOfPriceReading?)null), feeds: AllFeeds());
        var failingPolicies = Provider(policies: new FakePolicies((DailyPolicy?)null, fail: true), feeds: AllFeeds());

        await ((Func<Task>)(() => failingPrices.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken)))
            .Should().ThrowAsync<AsOfInputUnavailableException>();
        await ((Func<Task>)(() => failingPolicies.GetAsync("AAPL", Market.UnitedStates, AsOf, TestContext.Current.CancellationToken)))
            .Should().ThrowAsync<AsOfInputUnavailableException>();
    }

    // ------------------------------------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------------------------------------

    internal static BaseAsOfDecisionInputProvider Provider(
        FakePrices? prices = null, FakePolicies? policies = null, IAsOfReferenceFeed[]? feeds = null, int topK = 5) =>
        new(
            prices ?? new FakePrices(new AsOfPriceReading(new DatedPrice(AsOf, 101m), new DatedPrice(AsOf.AddDays(-1), 100m))),
            policies ?? new FakePolicies(new DailyPolicy(AsOf.AddDays(-1), "方針")),
            Assembler(topK, feeds ?? AllFeeds()),
            NullLogger<BaseAsOfDecisionInputProvider>.Instance);

    private static AsOfReferenceAssembler Assembler(int topK, params IAsOfReferenceFeed[] feeds) =>
        new(feeds, topK, TimeSpan.FromHours(168));

    internal static IAsOfReferenceFeed[] AllFeeds(params RetrievedContext[] newsDocs) =>
    [
        Feed("finnhub-news", AsOfReferenceCoverage.Symbol, newsDocs),
        Feed("sec-edgar", AsOfReferenceCoverage.Symbol),
        Feed("fred", AsOfReferenceCoverage.Market),
    ];

    private static FakeFeed Feed(string name, AsOfReferenceCoverage coverage, params RetrievedContext[] docs) =>
        new(name, coverage, docs);

    private static RetrievedContext Doc(string title, DateTimeOffset? publishedAt, params string[] tags) =>
        new(title, $"{title} の本文", Tags: tags, PublishedAt: publishedAt);

    // reading が null なら照会の失敗（例外）。
    internal sealed class FakePrices(Func<DateOnly, AsOfPriceReading?> reading) : IAsOfPriceSource
    {
        public FakePrices(AsOfPriceReading? fixedReading)
            : this(_ => fixedReading)
        {
        }

        // 判断日ごとに、その日の終値と前日の終値を返す（記録器に通す試験用）。
        public static FakePrices FollowingAsOf(decimal close, decimal previousClose) =>
            new(asOf => new AsOfPriceReading(new DatedPrice(asOf, close), new DatedPrice(asOf.AddDays(-1), previousClose)));

        public List<(string, Market, DateOnly)> Requests { get; } = [];

        public Task<AsOfPriceReading> GetAsync(string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default)
        {
            Requests.Add((symbol, market, asOf));
            return reading(asOf) is { } r ? Task.FromResult(r) : throw new AsOfInputUnavailableException("日足の照会に失敗");
        }
    }

    internal sealed class FakePolicies(Func<DateTimeOffset, DailyPolicy?> policy, bool fail = false) : IAsOfDailyPolicySource
    {
        public FakePolicies(DailyPolicy? fixedPolicy, bool fail = false)
            : this(_ => fixedPolicy, fail)
        {
        }

        // 判断時点の前日に確定した方針を返す（記録器に通す試験用）。
        public static FakePolicies PreviousDay(string summary) =>
            new(at => new DailyPolicy(DateOnly.FromDateTime(at.UtcDateTime).AddDays(-1), summary));

        public List<DateTimeOffset> Requests { get; } = [];

        public Task<DailyPolicy?> GetAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            Requests.Add(at);
            return fail ? throw new AsOfInputUnavailableException("方針の照会に失敗") : Task.FromResult(policy(at));
        }
    }

    internal sealed class FakeFeed(
        string name, AsOfReferenceCoverage coverage, IReadOnlyList<RetrievedContext> docs, bool fail = false, bool @throw = false)
        : IAsOfReferenceFeed
    {
        public string SourceName => name;

        public AsOfReferenceCoverage Coverage => coverage;

        public List<(DateTimeOffset From, DateTimeOffset At)> Requests { get; } = [];

        public Task<IReadOnlyList<RetrievedContext>?> FetchAsync(
            string symbol, Market market, DateTimeOffset from, DateTimeOffset at, CancellationToken cancellationToken = default)
        {
            Requests.Add((from, at));
            if (@throw)
                throw new HttpRequestException("接続できない");
            return Task.FromResult(fail ? null : docs);
        }
    }
}
