using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Hosted;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-10, FR-03, ADR-0040 決定1（S1）, #902, IADR-0365 決定2・決定3: 損切り評価の生存要約と価格欠落の Warning。
// 時刻は引数で注入する（壁時計・実時間の待ちを使わない。#885/#900/#901 の教訓）。
public class StopLossLivenessReporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 0, 0, TimeSpan.Zero);

    private static readonly MonitorOptions Defaults = new(); // 要約 300 秒・欠落しきい値 300 秒

    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Informations => Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message);

        public IEnumerable<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (StopLossLivenessReporter Reporter, RecordingLogger<StopLossLivenessReporter> Log) Create(
        MonitorOptions? options = null)
    {
        var log = new RecordingLogger<StopLossLivenessReporter>();
        return (new StopLossLivenessReporter(Options.Create(options ?? Defaults), log), log);
    }

    private static StopLossEvaluation Aapl(decimal? price, DateTimeOffset at) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, 707, 338.51m, price, at);

    [Fact]
    public void T_10_622_保有を評価した最初の巡回で件数_銘柄_価格_ライン_評価時刻を含む要約を1行出す()
    {
        // T-10-622, FR-10, #902, IADR-0365 決定2
        var (reporter, log) = Create();

        reporter.Observe([Aapl(340.12m, T0)], T0);

        log.Informations.Should().ContainSingle();
        var line = log.Informations.Single();
        line.Should().Contain("保有 1 件");
        line.Should().Contain("AAPL/UnitedStates");
        line.Should().Contain("707株");
        line.Should().Contain("現在値=340.12");
        line.Should().Contain("ライン=338.51");
        line.Should().Contain("評価=2026-09-23T14:00:00.0000000+00:00");
        log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void T_10_623_間隔内は要約を重ねず_偽時計を間隔ぶん進めると再び出す()
    {
        // T-10-623, FR-10, #902, IADR-0365 決定2（毎巡回 60 秒 × 4 回は間隔 300 秒の内側）
        var (reporter, log) = Create();

        reporter.Observe([Aapl(340m, T0)], T0);
        for (var i = 1; i <= 4; i++)
            reporter.Observe([Aapl(340m + i, T0.AddSeconds(60 * i))], T0.AddSeconds(60 * i));
        reporter.Observe([Aapl(339m, T0.AddSeconds(299))], T0.AddSeconds(299));

        log.Informations.Should().HaveCount(1);

        reporter.Observe([Aapl(339.5m, T0.AddSeconds(300))], T0.AddSeconds(300));

        log.Informations.Should().HaveCount(2);
        log.Informations.Last().Should().Contain("現在値=339.5");
    }

    [Fact]
    public void T_10_624_価格欠落はしきい値以内なら警告せず_超えたら1回_以後は要約間隔に1回まで()
    {
        // T-10-624, FR-10, #902, IADR-0365 決定3（欠落は最後に価格が取れた時刻から数える）
        var (reporter, log) = Create();

        reporter.Observe([Aapl(340m, T0)], T0);
        reporter.Observe([Aapl(null, T0.AddSeconds(60))], T0.AddSeconds(60));
        reporter.Observe([Aapl(null, T0.AddSeconds(300))], T0.AddSeconds(300)); // ちょうどしきい値: まだ出さない

        log.Warnings.Should().BeEmpty();

        reporter.Observe([Aapl(null, T0.AddSeconds(301))], T0.AddSeconds(301)); // 超えた

        log.Warnings.Should().ContainSingle();
        var warning = log.Warnings.Single();
        warning.Should().Contain("AAPL/UnitedStates");
        warning.Should().Contain("ライン=338.51");
        warning.Should().Contain("最終価格 340");
        warning.Should().Contain("自動では決済しません");

        // 連続中は要約間隔（300 秒）に 1 回まで。
        reporter.Observe([Aapl(null, T0.AddSeconds(360))], T0.AddSeconds(360));
        reporter.Observe([Aapl(null, T0.AddSeconds(600))], T0.AddSeconds(600));
        log.Warnings.Should().HaveCount(1);

        reporter.Observe([Aapl(null, T0.AddSeconds(601))], T0.AddSeconds(601));
        log.Warnings.Should().HaveCount(2);
    }

    [Fact]
    public void T_10_624b_一度も価格が取れていない保有は最初の欠落から数え_要約は取得できずと示す()
    {
        // T-10-624, FR-10, #902, IADR-0365 決定3（再起動直後から欠落している場合）
        var (reporter, log) = Create();

        reporter.Observe([Aapl(null, T0)], T0);

        log.Informations.Should().ContainSingle().Which.Should().Contain("現在値=取得できず（最終 なし");
        log.Warnings.Should().BeEmpty();

        reporter.Observe([Aapl(null, T0.AddSeconds(301))], T0.AddSeconds(301));

        log.Warnings.Should().ContainSingle().Which.Should().Contain("最終取得 なし");
    }

    [Fact]
    public void T_10_625_価格が回復したらInformationを1回出し_欠落の連続を解く()
    {
        // T-10-625, FR-10, #902, IADR-0365 決定3
        var (reporter, log) = Create();

        reporter.Observe([Aapl(340m, T0)], T0);
        reporter.Observe([Aapl(null, T0.AddSeconds(400))], T0.AddSeconds(400));
        log.Warnings.Should().ContainSingle();

        reporter.Observe([Aapl(341m, T0.AddSeconds(460))], T0.AddSeconds(460));

        log.Informations.Should().Contain(m => m.Contains("価格取得が回復") && m.Contains("現在値=341"));

        // 連続が解けたので、次の欠落は新しい起点（最後の取得 460 秒）から数え直す。
        reporter.Observe([Aapl(null, T0.AddSeconds(700))], T0.AddSeconds(700));
        log.Warnings.Should().HaveCount(1);

        // 回復が 2 回目に出ることはない（警告していない取得は回復とみなさない）。
        reporter.Observe([Aapl(342m, T0.AddSeconds(720))], T0.AddSeconds(720));
        log.Informations.Count(m => m.Contains("価格取得が回復")).Should().Be(1);
    }

    [Fact]
    public void T_10_626_保有0件では何も出さず_次に保有が現れたら即時に要約する()
    {
        // T-10-626, FR-10, #902, IADR-0365 決定2
        var (reporter, log) = Create();

        reporter.Observe([], T0);
        log.Entries.Should().BeEmpty();

        reporter.Observe([Aapl(340m, T0.AddSeconds(60))], T0.AddSeconds(60));
        reporter.Observe([], T0.AddSeconds(120)); // 手仕舞い
        reporter.Observe([Aapl(339m, T0.AddSeconds(180))], T0.AddSeconds(180)); // 間隔内でも新しい保有は即時

        log.Informations.Should().HaveCount(2);
        log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void T_10_626b_評価から外れた銘柄は要約から消える()
    {
        // T-10-626, FR-10, #902, IADR-0365 決定2
        var (reporter, log) = Create();
        var msft = new StopLossEvaluation("MSFT", Market.UnitedStates, TradeSide.Buy, 10, 400m, 410m, T0);

        reporter.Observe([Aapl(340m, T0), msft], T0);
        log.Informations.Single().Should().Contain("保有 2 件").And.Contain("MSFT/UnitedStates");

        reporter.Observe([Aapl(340m, T0.AddSeconds(300))], T0.AddSeconds(300));

        log.Informations.Last().Should().Contain("保有 1 件").And.NotContain("MSFT");
    }

    [Fact]
    public void T_10_633_閉場をまたいだ最初の欠落は閉場前の最終取得から数えず_開場後の最初の欠落から数える()
    {
        // T-10-633, FR-10, #902, IADR-0365 決定3・決定4（#904 監査 N2）
        var (reporter, log) = Create();
        var monday = T0.AddDays(3);

        reporter.Observe([Aapl(340m, T0)], T0);
        reporter.OnMarketClosed(Market.UnitedStates, [], T0.AddHours(1), nextOpen: monday);
        reporter.Observe([Aapl(null, monday)], monday);
        reporter.Observe([Aapl(null, monday.AddSeconds(300))], monday.AddSeconds(300));

        // #909, IADR-0380 決定3: 閉場そのものの Warning（保護の空白）は別物なので、欠落の数えから除く。
        var missingWarnings = log.Warnings.Where(w => w.Contains("価格を取得できていません", StringComparison.Ordinal));
        missingWarnings.Should().BeEmpty("閉場のあいだは評価していないので欠落ではない");

        reporter.Observe([Aapl(null, monday.AddSeconds(301))], monday.AddSeconds(301));

        missingWarnings.Should().ContainSingle();
    }

    [Fact]
    public void T_10_697_閉場へ移った最初の巡回だけ保護の空白を出す()
    {
        // T-10-697, FR-03, FR-10, #909, IADR-0380 決定3: **黙って閉場しない。**
        var (reporter, log) = Create();
        var nextOpen = T0.AddHours(17);

        reporter.Observe([Aapl(340.12m, T0)], T0);
        reporter.OnMarketClosed(Market.UnitedStates, [Aapl(null, T0.AddHours(2))], T0.AddHours(2), nextOpen);

        var closed = log.Entries.Where(e => e.Message.Contains("市場が閉場しました", StringComparison.Ordinal)).ToList();
        closed.Should().ContainSingle();
        closed[0].Level.Should().Be(LogLevel.Warning, "最終観測値 340.12 はライン 338.51 を越えていない");
        closed[0].Message.Should().Contain("保有 1 件").And.Contain("AAPL/UnitedStates")
            .And.Contain("ライン=338.51").And.Contain("最終観測値=340.12")
            .And.Contain("無保護").And.Contain(nextOpen.ToString("O", CultureInfo.InvariantCulture));

        // 60 秒ごとの巡回で重ねない。
        reporter.OnMarketClosed(Market.UnitedStates, [Aapl(null, T0.AddHours(2).AddMinutes(1))], T0.AddHours(2).AddMinutes(1), nextOpen);
        log.Entries.Count(e => e.Message.Contains("市場が閉場しました", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void T_10_697_最終観測値がラインを越えたまま閉場したらCriticalで出す()
    {
        // T-10-697, FR-03, FR-10, #909, IADR-0380 決定3: 到達したまま閉場した建玉は翌寄りで滑り得る。
        var (reporter, log) = Create();

        reporter.Observe([Aapl(330m, T0)], T0); // ライン 338.51 を割ったまま
        reporter.OnMarketClosed(Market.UnitedStates, [Aapl(null, T0.AddHours(2))], T0.AddHours(2), T0.AddHours(17));

        var closed = log.Entries.Single(e => e.Message.Contains("市場が閉場しました", StringComparison.Ordinal));
        closed.Level.Should().Be(LogLevel.Critical);
        closed.Message.Should().Contain("ラインを越えたまま閉場").And.Contain("うち 1 件");
    }

    [Fact]
    public void T_10_697_保有を何も知らない閉場では保有の報告を出さず_知った時点で出せる()
    {
        // T-10-697, FR-03, #909, IADR-0380 決定3: 再起動直後は保有を照会していない。
        // 「保有 0 件」と「照会していない」を混同させないため**保有の報告（Warning / Critical）を出さず**、既出にも数えない。
        // （「閉場と判定している」Information は別に 1 行出る。IADR-0380［2026-09-24 追記 / PR #929 監査］F3 / T-10-726）
        var (reporter, log) = Create();

        reporter.OnMarketClosed(Market.UnitedStates, [], T0, nextOpen: null);
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);

        reporter.OnMarketClosed(Market.UnitedStates, [Aapl(null, T0)], T0.AddMinutes(1), nextOpen: null);
        log.Warnings.Should().ContainSingle()
            .Which.Should().Contain("最終観測値=なし").And.Contain("不明（カレンダーが次の開場を見通せません）");
    }

    [Fact]
    public void T_10_698_開場して最初に評価できた巡回で保護の再開を1回出す()
    {
        // T-10-698, FR-03, FR-10, #909, IADR-0380 決定3
        var (reporter, log) = Create();
        var monday = T0.AddDays(3);

        reporter.Observe([Aapl(340.12m, T0)], T0);
        reporter.OnMarketClosed(Market.UnitedStates, [Aapl(null, T0.AddHours(2))], T0.AddHours(2), monday);
        reporter.Observe([Aapl(341m, monday)], monday);

        log.Informations.Should().Contain(m => m.Contains("損切り評価を再開しました", StringComparison.Ordinal));

        // 再開は 1 回だけ（次の巡回では出さない）。
        reporter.Observe([Aapl(342m, monday.AddSeconds(60))], monday.AddSeconds(60));
        log.Informations.Count(m => m.Contains("損切り評価を再開しました", StringComparison.Ordinal)).Should().Be(1);

        // 次の閉場ではまた出せる（既出の印が開場で解けている）。
        reporter.OnMarketClosed(
            Market.UnitedStates, [Aapl(null, monday.AddHours(7))], monday.AddHours(7), monday.AddDays(1));
        log.Entries.Count(e => e.Message.Contains("市場が閉場しました", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public void T_10_698_閉場している市場の報告は開場している市場の要約を止めない()
    {
        // T-10-698（対の否定形）, FR-03, #909, IADR-0380 決定3: 市場ごとに数える。
        var (reporter, log) = Create();
        var toyota = new StopLossEvaluation("7203", Market.Japan, TradeSide.Buy, 100, 2_900m, 3_000m, T0);

        reporter.Observe([Aapl(340.12m, T0), toyota], T0);
        reporter.OnMarketClosed(Market.UnitedStates, [Aapl(null, T0)], T0, T0.AddHours(17));

        log.Entries.Count(e => e.Message.Contains("市場が閉場しました", StringComparison.Ordinal)).Should().Be(1);

        // 東証は開いたままなので、次の巡回でも米国の閉場を重ねない（＝日本の評価が印を解かない）。
        reporter.Observe([toyota], T0.AddSeconds(60));
        reporter.OnMarketClosed(Market.UnitedStates, [Aapl(null, T0.AddSeconds(60))], T0.AddSeconds(60), T0.AddHours(17));
        log.Entries.Count(e => e.Message.Contains("市場が閉場しました", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void T_10_726_何も知らない閉場でも閉場と判定したことを閉場期間ごとに1回だけ出す()
    {
        // T-10-726, FR-03, FR-10, #909, IADR-0380［2026-09-24 追記 / PR #929 監査］F3: 再起動直後・誤って閉場と読んだ日を
        // 無音にしない。保有は知らないので書かず、「閉場と判定している・次の開場はいつか」だけを Information で 1 行出す。
        var (reporter, log) = Create();
        var nextOpen = T0.AddHours(17);

        reporter.OnMarketClosed(Market.UnitedStates, [], T0, nextOpen);
        reporter.OnMarketClosed(Market.UnitedStates, [], T0.AddMinutes(1), nextOpen); // 60 秒後の巡回

        log.Entries.Should().ContainSingle();
        log.Entries[0].Level.Should().Be(LogLevel.Information);
        log.Entries[0].Message.Should().Contain("閉場と判定しています").And.Contain("UnitedStates")
            .And.Contain(nextOpen.ToString("O", CultureInfo.InvariantCulture));

        // 開場を挟めば、次の閉場期間でまた 1 回出す（保有が無く Observe に評価が来なくても解ける）。
        reporter.OnMarketOpen(Market.UnitedStates);
        reporter.OnMarketClosed(Market.UnitedStates, [], T0.AddDays(1), nextOpen.AddDays(1));
        log.Informations.Count(m => m.Contains("閉場と判定しています", StringComparison.Ordinal)).Should().Be(2);

        // 他の市場の開場は印を解かない（市場ごとに数える）。
        reporter.OnMarketOpen(Market.Japan);
        reporter.OnMarketClosed(Market.UnitedStates, [], T0.AddDays(1).AddMinutes(1), nextOpen.AddDays(1));
        log.Informations.Count(m => m.Contains("閉場と判定しています", StringComparison.Ordinal)).Should().Be(2);
    }

    // T-10-839, FR-03, FR-10, #957, IADR-0399 決定2: 近似のライン（応答にラインが無く平均取得単価から見積もった値）は、生存要約と
    // 閉場の報告で実値と並べて書かない（「近似」と付ける）。実値のラインには付けない。
    [Fact]
    public void T_10_839_近似のラインは生存要約と閉場の報告で近似と示し_実値のラインには付けない()
    {
        var (reporter, log) = Create();
        var approximated = Aapl(340.12m, T0) with { StopLossApproximated = true };
        var real = new StopLossEvaluation("MSFT", Market.UnitedStates, TradeSide.Buy, 5, 1_900m, 1_950m, T0);

        reporter.Observe([approximated, real], T0);

        var summary = log.Informations.Single(m => m.Contains("損切り評価は稼働中", StringComparison.Ordinal));
        summary.Should().Contain("ライン=338.51（近似");
        summary.Should().Contain("ライン=1900 評価=", "実値のラインには印を付けない");

        reporter.OnMarketClosed(
            Market.UnitedStates, [approximated with { Price = null }], T0.AddHours(2), T0.AddHours(17));
        log.Entries.Single(e => e.Message.Contains("市場が閉場しました", StringComparison.Ordinal))
            .Message.Should().Contain("ライン=338.51（近似");
    }
}
