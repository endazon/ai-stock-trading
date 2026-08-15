using System.Globalization;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-10, #257, IADR-0107 決定5: 取得回数の抑制（TTL）と、古すぎる観測を使わない鮮度上限。
// いずれも「そのレートを使ってよいか」を決める判断であり、同じ装飾に置く。
public class CachingFxRateSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TTL内は取得済みレートを返し外部へ再要求しない()
    {
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-1)));
        var (source, _) = Create(inner);

        var first = await source.GetRateToBaseAsync(Currency.Jpy);
        var second = await source.GetRateToBaseAsync(Currency.Jpy);

        first!.Rate.Should().Be(second!.Rate);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task TTL経過後は再取得する()
    {
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-1)));
        var (source, time) = Create(inner, ttl: TimeSpan.FromHours(6));

        await source.GetRateToBaseAsync(Currency.Jpy);
        time.Advance(TimeSpan.FromHours(6.5));
        await source.GetRateToBaseAsync(Currency.Jpy);

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task 鮮度上限を超えた観測は採らない()
    {
        // DEXJPUS は営業日次のため週末・祝日で数日空くが、上限を超えたレートで発注はしない（レート無し＝見送り）。
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-8)));
        var (source, _) = Create(inner, maxAge: TimeSpan.FromDays(7));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    [Fact]
    public async Task 鮮度上限内の観測は採る()
    {
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-3)));
        var (source, _) = Create(inner, maxAge: TimeSpan.FromDays(7));

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().NotBeNull();
    }

    // FR-10, #271, IADR-0112 決定1: 既定の鮮度上限は FRED DEXJPUS の公表周期を賄う。系列は営業日次だが公表は
    // H.10 週次リリース（月曜 16:15 ET ≒ 20:15 UTC・前週金曜まで一括収載／月曜が祝日なら火曜）であり、最新観測の齢は
    // 「公表間隔 7 日 ＋ 公表ラグ（金→月）3 日 ＋ 祝日ずれ 最大 2 日 ＋ 公表時刻」として積み上がる。
    // 既定 7 日では予定どおりの公表でも毎週必ず超過し、非基準通貨建て銘柄が週明け・連休明けに全件見送りになっていた。
    [Theory]
    [InlineData("2026-07-17", "2026-07-27T20:00:00Z", true, "通常＝次の月曜公表の直前（10.84 日・#271 の実測）")]
    [InlineData("2026-07-17", "2026-07-28T20:00:00Z", true, "月曜が祝日で火曜公表（11.84 日）")]
    [InlineData("2026-07-16", "2026-07-28T20:00:00Z", true, "対象週の金曜が休場で直近観測が木曜＋翌月曜が祝日（12.84 日）")]
    // **【⚠️ 2026-08-07・#381 / IADR-0174 で判定が変わった】** 旧既定 14 日ではこの 17.84 日を**棄却**していたが、
    // 計画が絶対上限を 30 日と確定したため**採用（警告つき続行）**になる。**これは計画が選んだ緩和である**——
    // 計画 §5 は「5 日超〜30 日以下＝直近レートで続行し警告」と定めており、週次リリース 1 回の欠落は
    // その範囲に入る。**14 日で止まっていた保護は失われた**（IADR-0174 §悪い影響に記録）。
    [InlineData("2026-07-17", "2026-08-03T20:00:00Z", true, "週次リリースが 1 回丸ごと欠落（17.84 日）＝計画の警告域（30 日以下）")]
    public async Task 既定の鮮度上限は公表周期の遅延を吸収し系列の異常は見送る(
        string observedOn, string evaluatedAt, bool accepted, string scenario)
    {
        // 既定値そのものを回帰対象にするため、上限は構成の実効解決（ResolveMaxRateAge）から採る。
        var (source, _) = Create(
            new CountingSource(Rate(asOf: Instant($"{observedOn}T00:00:00Z"))),
            maxAge: FxRateSourceFactory.ResolveMaxRateAge(new FxOptions()),
            now: Instant(evaluatedAt));

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        (rate is not null).Should().Be(accepted, scenario);
    }

    [Fact]
    public async Task 取得できなかった結果はキャッシュせず次回に再取得する()
    {
        // 一時障害を TTL のあいだ引きずると、回復後もレート無し＝見送りが続く。
        var inner = new CountingSource(null);
        var (source, _) = Create(inner);

        await source.GetRateToBaseAsync(Currency.Jpy);
        await source.GetRateToBaseAsync(Currency.Jpy);

        inner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task 鮮度切れで棄却したレートもキャッシュしない()
    {
        var inner = new CountingSource(Rate(asOf: Now.AddDays(-30)));
        var (source, _) = Create(inner, maxAge: TimeSpan.FromDays(7));

        await source.GetRateToBaseAsync(Currency.Jpy);
        await source.GetRateToBaseAsync(Currency.Jpy);

        inner.Calls.Should().Be(2);
    }

    // --- FR-10, FR-17, ADR-0022 決定4・5, #381, IADR-0174: 警告と停止の分離 ---
    //
    // **このガードは「緩む方向」に壊れても静かである** —— 古いレートで発注が通るだけで何も赤くならない。
    // したがって境界を**両側**で固定する。

    // 警告域（警告しきい値超〜上限以下）: **値は返す**（新規建ては止めない）。気づくために警告だけ出す。
    [Fact]
    public async Task 警告域では値を返しつつ警告を出す()
    {
        var log = new RecordingFxLogger();
        var inner = new CountingSource(Rate(Now.AddDays(-7)));   // 7 日前 = 警告 5 日超・上限 30 日以下
        var source = new CachingFxRateSource(
            inner, TimeSpan.FromHours(6), TimeSpan.FromDays(30), TimeSpan.FromDays(5),
            new TestTimeProvider(Now), log);

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate.Should().NotBeNull("警告域では新規建てを止めない（ADR-0022 決定4）");
        log.Warnings.Should().ContainSingle().Which.Should().Contain("古くなっています");
    }

    // 境界の直下: 警告しきい値ちょうどは**警告を出さない**（`>` 比較）。
    [Fact]
    public async Task 警告しきい値ちょうどでは警告を出さない()
    {
        var log = new RecordingFxLogger();
        var inner = new CountingSource(Rate(Now.AddDays(-5)));
        var source = new CachingFxRateSource(
            inner, TimeSpan.FromHours(6), TimeSpan.FromDays(30), TimeSpan.FromDays(5),
            new TestTimeProvider(Now), log);

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().NotBeNull();
        log.Warnings.Should().BeEmpty();
    }

    // 上限超: **値を返さない**（従来どおり）。警告域との違いを固定する。
    [Fact]
    public async Task 上限を超えると値を返さない()
    {
        var log = new RecordingFxLogger();
        var inner = new CountingSource(Rate(Now.AddDays(-31)));
        var source = new CachingFxRateSource(
            inner, TimeSpan.FromHours(6), TimeSpan.FromDays(30), TimeSpan.FromDays(5),
            new TestTimeProvider(Now), log);

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    // --- #381 停止側 / IADR-0198 決定1: 統制が発動したことを外へ出す -----------------------------
    //
    // 🔴 **従来、上限超の分岐はログを出して return するだけだった。**
    // 「新規建てを止めた」という**統制の発動が、7 年保持のどこにも残っていなかった**——
    // ADR-0022 決定2 の「黙って劣化させない」は警告域だけの話ではない。

    [Fact]
    public async Task 上限を超えたら停止したことを可視化経路へ報告する()
    {
        var notifier = new RecordingNotifier();
        var source = new CachingFxRateSource(
            new CountingSource(Rate(Now.AddDays(-31))), TimeSpan.FromHours(6),
            TimeSpan.FromDays(30), TimeSpan.FromDays(5),
            new TestTimeProvider(Now), NullLogger<CachingFxRateSource>.Instance, notifier);

        await source.GetRateToBaseAsync(Currency.Jpy);

        notifier.Stales.Should().ContainSingle().Which.EntryBlocked.Should().BeTrue(
            "新規建てを止めたなら、止めたことを外へ出す");
    }

    // 🔴 **否定形: 警告域を「止まった」と報告しない。** 同じ経路で報告するため、ここが逆転すると
    // **止まっていないのに止まったと通知が飛ぶ**（ADR-0022 決定5 の読み違いを招く）。
    [Fact]
    public async Task 警告域では止まっていないものとして報告する()
    {
        var notifier = new RecordingNotifier();
        var source = new CachingFxRateSource(
            new CountingSource(Rate(Now.AddDays(-7))), TimeSpan.FromHours(6),
            TimeSpan.FromDays(30), TimeSpan.FromDays(5),
            new TestTimeProvider(Now), NullLogger<CachingFxRateSource>.Instance, notifier);

        await source.GetRateToBaseAsync(Currency.Jpy);

        notifier.Stales.Should().ContainSingle().Which.EntryBlocked.Should().BeFalse();
    }

    // #364, IADR-0152 決定1/2: 基準通貨は USD であり、解決対象は JPY（レートは USD per JPY）。
    private static FxRate Rate(DateTimeOffset asOf) => new(Currency.Jpy, Currency.Usd, 1m / 152.35m, asOf);

    private static DateTimeOffset Instant(string iso8601) =>
        DateTimeOffset.Parse(iso8601, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    // #381, IADR-0174: 警告しきい値は既定で上限と同値にする（既存テストの挙動を変えないため）。
    // 警告域そのものを検査するテストは warning を明示的に渡す。
    private static (CachingFxRateSource Source, TestTimeProvider Time) Create(
        IFxRateSource inner, TimeSpan? ttl = null, TimeSpan? maxAge = null, DateTimeOffset? now = null,
        TimeSpan? warning = null)
    {
        var time = new TestTimeProvider(now ?? Now);
        var effectiveMaxAge = maxAge ?? TimeSpan.FromDays(7);
        var source = new CachingFxRateSource(
            inner,
            ttl ?? TimeSpan.FromHours(6),
            effectiveMaxAge,
            warning ?? effectiveMaxAge,
            time,
            NullLogger<CachingFxRateSource>.Instance);
        return (source, time);
    }

    private sealed class RecordingFxLogger : ILogger<CachingFxRateSource>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    // IADR-0064/0066 と同じ理由（FakeTimeProvider は中央パッケージ管理に未登録）で最小の偽装を置く。
    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    // #381 停止側: 「何を報告したか」だけを記録する最小の偽装（抑止の判定は本物の実装が持つ）。
    private sealed class RecordingNotifier : IFxSourceStatusNotifier
    {
        public List<(string Quote, TimeSpan Age, bool EntryBlocked)> Stales { get; } = [];

        public Task ReportSourceUsedAsync(
            string quote, string sourceName, int rank, int totalSources,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportStaleAsync(
            string quote, DateTimeOffset asOf, TimeSpan age, TimeSpan warnThreshold, TimeSpan maxAge,
            CancellationToken cancellationToken = default, bool entryBlocked = false)
        {
            Stales.Add((quote, age, entryBlocked));
            return Task.CompletedTask;
        }

        public Task ReportClosedWithStaleRateAsync(
            string symbol, Market market, string quote, int quantity, decimal fxRateToBase,
            DateTimeOffset rateAsOf, TimeSpan age, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CountingSource(FxRate? rate) : IFxRateSource
    {
        public int Calls { get; private set; }

        public Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(rate);
        }
    }
}
