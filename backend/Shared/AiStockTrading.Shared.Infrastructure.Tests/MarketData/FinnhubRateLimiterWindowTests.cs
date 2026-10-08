using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// FR-01, FR-03, FR-10, ADR-0043 決定2 (a), #1247, IADR-0513: Finnhub の鍵は 60 回 / 60 秒の固定窓（IADR-0275）で、
// 同じ鍵を 5 プロセスが協調せずに使う（IADR-0068 決定4）。構成の合計 Σr ≤ 60（IADR-0512）が「どの 60 秒の窓でも合計 ≤ 60」に
// なるには、1 プロセスがどの窓でも r 回以下しか送らないことが要る。
//
// 模擬: プロセスごとに模擬時計を持ち（全員が同じ時刻 T0 から始まる＝同じ窓に並ぶ）、限流器（DelayingRateLimiter）の待ちは
// 待った分だけその時計を進める。要求の往復は 0 秒（送れるだけ送る最悪の形）。送出時刻を全プロセスで合わせ、各送出時刻を
// 始点とする半開区間 [s, s+60) の件数の最大を数える（最大の窓は必ずある送出時刻から始まる形へずらせる）。
public class FinnhubRateLimiterWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 13, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private const int FinnhubLimitPerWindow = 60;

    // values-local（経路 B）の同じ鍵の 5 プロセス: 情報収集 30・市場監視 12・リスク管理 / 報告書 / 取引判断 各 5（合計 57。IADR-0434）。
    private static readonly int[] ValuesLocalRates = [30, 12, 5, 5, 5];

    // T-10-2429
    [Fact]
    public async Task 全プロセスが同時に起動して自制レートいっぱいまで要求してもどの60秒の窓でも合計は60回以下()
    {
        var perProcess = new List<List<DateTimeOffset>>();
        foreach (var rate in ValuesLocalRates)
            perProcess.Add(await Greedy(FinnhubRateLimiter.CreateBucket(rate), T0, TimeSpan.FromMinutes(10)));

        for (var i = 0; i < ValuesLocalRates.Length; i++)
            MaxInAnyWindow(perProcess[i]).Should().Be(ValuesLocalRates[i], $"自制 {ValuesLocalRates[i]} 回/分のプロセスは窓あたり r 回まで");

        var all = MaxInAnyWindow(perProcess.SelectMany(x => x));
        all.Should().Be(ValuesLocalRates.Sum()).And.BeLessThanOrEqualTo(FinnhubLimitPerWindow);
    }

    // T-10-2430
    [Theory]
    [MemberData(nameof(AllRates))]
    public async Task 自制レートが1から60のどれでも1プロセスはどの60秒の窓でもr回以下(int rate)
    {
        // 起動直後（満杯）から送れるだけ送る。割り切れない r（7・11・13 …）は間隔をティックで切り上げる側で守る。
        var sent = await Greedy(FinnhubRateLimiter.CreateBucket(rate), T0, TimeSpan.FromMinutes(5));

        MaxInAnyWindow(sent).Should().Be(rate);
    }

    // T-10-2431
    [Fact]
    public async Task 巡回の間の休止で満ちた後に全プロセスが一斉に要求してもどの60秒の窓でも合計は60回以下()
    {
        // 情報収集は巡回間隔（300 秒）の間にバケットが満ちる。是正前は毎巡回の頭で 30 回を一気に送っていた（#1247 の例の 59 回/窓）。
        var perProcess = new List<List<DateTimeOffset>>();
        foreach (var rate in ValuesLocalRates)
        {
            var time = new FakeTimeProvider(T0);
            var limiter = Limiter(FinnhubRateLimiter.CreateBucket(rate), time);
            await limiter.WaitAsync(TestContext.Current.CancellationToken); // 起動時の 1 回
            time.Advance(TimeSpan.FromSeconds(300));                       // 休止
            perProcess.Add(await Greedy(limiter, time, T0 + TimeSpan.FromSeconds(300) + TimeSpan.FromMinutes(5)));
        }

        MaxInAnyWindow(perProcess.SelectMany(x => x)).Should().BeLessThanOrEqualTo(FinnhubLimitPerWindow);
    }

    // T-10-2432（陰性対照）
    [Fact]
    public async Task 是正前の形の満杯で起動するバケットは同じ模擬で60回を超える()
    {
        // 是正前: new TokenBucket(r, 1 分)＝容量 r・満杯で起動。窓の頭の r 回と窓の中の補充 r − 1 回で 1 プロセス 2r − 1 回。
        var perProcess = new List<List<DateTimeOffset>>();
        foreach (var rate in ValuesLocalRates)
            perProcess.Add(await Greedy(new TokenBucket(rate, TimeSpan.FromMinutes(1)), T0, TimeSpan.FromMinutes(10)));

        for (var i = 0; i < ValuesLocalRates.Length; i++)
            MaxInAnyWindow(perProcess[i]).Should().BeGreaterThanOrEqualTo((2 * ValuesLocalRates[i]) - 1);

        // 2 × 57 − 5 ＝ 109。模擬（数え方）が壊れて少なく数えるなら、ここが赤になる（上の正例の緑を空振りにしない）。
        MaxInAnyWindow(perProcess.SelectMany(x => x))
            .Should().BeGreaterThanOrEqualTo((2 * ValuesLocalRates.Sum()) - ValuesLocalRates.Length)
            .And.BeGreaterThan(FinnhubLimitPerWindow);
    }

    // T-10-2433
    [Fact]
    public async Task 市場監視の12回毎分で1巡回12要求は55秒で出し終わり次の巡回の頭で待たずに送れる()
    {
        // (b) 12 × 60 ≤ 12 × 60 秒（IADR-0434・check-finnhub-key-budget.js）。等間隔では最後の要求が 55 秒に出て、
        // 往復に 5 秒（/quote の打ち切りと同じ。IADR-0469）を残して巡回間隔 60 秒に収まる。
        var time = new FakeTimeProvider(T0);
        var limiter = Limiter(FinnhubRateLimiter.CreateBucket(12), time);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var tick = T0 + (Window * cycle);
            if (time.GetUtcNow() < tick)
                time.Advance(tick - time.GetUtcNow()); // PeriodicTimer の次の刻み

            var sent = new List<DateTimeOffset>();
            for (var i = 0; i < 12; i++)
            {
                await limiter.WaitAsync(TestContext.Current.CancellationToken);
                sent.Add(time.GetUtcNow());
            }

            sent[0].Should().Be(tick, "前の巡回の最後の要求から 5 秒経っており、巡回の頭は待たない");
            sent[^1].Should().Be(tick + TimeSpan.FromSeconds(55));
            sent.Zip(sent.Skip(1), (a, b) => b - a).Should().AllBeEquivalentTo(TimeSpan.FromSeconds(5));
        }
    }

    // T-10-2434
    [Fact]
    public void Finnhubへ送る限流器の生成箇所はどちらもFinnhubRateLimiterを使う()
    {
        // 市況 4 サービス（共有の MarketDataSourceFactory）と情報収集の Finnhub 系（finnhub・finnhub-news が共有）の 2 か所。
        // どちらかが容量 ＝ r の new TokenBucket へ戻ると、上の試験は緑のまま本番だけがバーストへ戻る。
        var root = RepoRoot();
        var marketData = File.ReadAllText(Path.Combine(root,
            "backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/Adapters/MarketData/MarketDataSourceFactory.cs"));
        var collection = File.ReadAllText(Path.Combine(root,
            "backend/Services/InformationCollectionService/Infrastructure/ExternalServices/InformationSourceFactory.cs"));

        marketData.Should().Contain("FinnhubRateLimiter.Create(requestsPerMinute, timeProvider)").And.NotContain("new TokenBucket(");
        collection.Should().Contain("FinnhubRateLimiter.Create(options.Finnhub.RateLimitPerMinute, timeProvider)");
        collection.Should().NotContain("Limiter(options.Finnhub.RateLimitPerMinute");

        var bucket = FinnhubRateLimiter.CreateBucket(30);
        bucket.TryConsume(T0, out _).Should().BeTrue();
        bucket.TryConsume(T0, out var retryAfter).Should().BeFalse("容量 1: 起動直後でも 2 回目は待つ");
        retryAfter.Should().Be(TimeSpan.FromSeconds(2));
    }

    public static TheoryData<int> AllRates()
    {
        var data = new TheoryData<int>();
        for (var r = 1; r <= 60; r++) data.Add(r);
        return data;
    }

    private static Task<List<DateTimeOffset>> Greedy(TokenBucket bucket, DateTimeOffset start, TimeSpan duration)
    {
        var time = new FakeTimeProvider(start);
        return Greedy(Limiter(bucket, time), time, start + duration);
    }

    // 往復 0 秒で送れるだけ送る（限流器の待ちだけが時計を進める）。
    private static async Task<List<DateTimeOffset>> Greedy(IRateLimiter limiter, FakeTimeProvider time, DateTimeOffset until)
    {
        var sent = new List<DateTimeOffset>();
        while (true)
        {
            await limiter.WaitAsync(TestContext.Current.CancellationToken);
            var now = time.GetUtcNow();
            if (now >= until) return sent;
            sent.Add(now);
        }
    }

    // 待った分だけ時計を進める。0 ティックへ丸まった待ち（割り切れない間隔の端数）は 1 ティック進める
    // （実時間は必ず進む。進めないと模擬時計の上で同じ時刻を回り続ける）。送出の可否はバケットが時刻で決めるので、
    // 待ちを長めに取っても早く送る側へは倒れない。
    private static DelayingRateLimiter Limiter(TokenBucket bucket, FakeTimeProvider time) =>
        new(bucket, time, (delay, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            time.Advance(delay > TimeSpan.Zero ? delay : TimeSpan.FromTicks(1));
            return Task.CompletedTask;
        });

    // 各送出時刻を始点とする [s, s+60) の件数の最大。
    private static int MaxInAnyWindow(IEnumerable<DateTimeOffset> sent)
    {
        var sorted = sent.OrderBy(t => t).ToArray();
        var max = 0;
        var end = 0;
        for (var start = 0; start < sorted.Length; start++)
        {
            if (end < start) end = start;
            while (end < sorted.Length && sorted[end] < sorted[start] + Window) end++;
            max = Math.Max(max, end - start);
        }
        return max;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    // Microsoft.Extensions.Time.Testing は中央パッケージ管理に未登録のため、最小の偽装で足す（DelayingRateLimiterTests と同じ方針）。
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
