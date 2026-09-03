using AuditService.Features.AuditEvents;
using AuditService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace AuditService.Tests;

// FR-01, FR-11, #336, ADR-0020 決定2〜4: 情報収集の縮退・回復・一般 Web 発動の台帳写像と、
// **月次の期間集計へ届くこと**（#336 受け入れ基準③後段）。
//
// 🔴 **記録が残ることと、集計から引けることは別である。** 台帳へ書けても種別 × 期間で引けなければ
// 月報には載らない（IADR-0199 決定2 が日報の為替欄のために開けた経路と同じ形で確かめる）。
public class InformationCollectionAuditEntryTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTimeOffset RecordedAt = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    // 🔴 台帳を読む人が「取引が全部止まっていた」と誤読すると、事後の検証が事実とずれる。
    [Fact]
    public void 情報源の縮退は_止まっていないものを要約に明記する()
    {
        var entry = AuditEntryFactory.From(
            new InformationSourceDegraded(
                "news", "LimitedDegradation", ["finnhub-news", "google-news"], BlocksNewEntries: true, T0),
            Id, RecordedAt);

        entry.EventType.Should().Be(nameof(InformationSourceDegraded));
        entry.Summary.Should().Contain("finnhub-news").And.Contain("google-news");
        entry.Summary.Should().Contain("新規建ての停止=あり");
        entry.Summary.Should().Contain("手仕舞い・損切りは止まっていない");
        entry.OccurredAt.Should().Be(T0);
    }

    // ADR-0020 決定2-3: 発生時刻・継続時間・該当サイクル数の 3 点が 1 行で読めること。
    [Fact]
    public void 回復の要約には継続時間と該当サイクル数が載る()
    {
        var entry = AuditEntryFactory.From(
            new InformationSourceRecovered("news", T0, AffectedCycles: 4, T0.AddHours(6)), Id, RecordedAt);

        entry.Summary.Should().Contain("6 時間").And.Contain("該当サイクル 4 回");
    }

    // 🔴 **欠測と回復を同じ相関に置く**（期間を 1 本の相関で辿れるようにする）。
    // カテゴリが違えば相関も違う（ニュース系と開示系は独立に劣化する）。
    [Fact]
    public void 欠測と回復は同じ相関で_カテゴリが違えば別の相関になる()
    {
        var degraded = AuditEntryFactory.From(
            new InformationSourceDegraded("news", "LimitedDegradation", ["google-news"], true, T0), Id, RecordedAt);
        var recovered = AuditEntryFactory.From(
            new InformationSourceRecovered("news", T0, 2, T0.AddHours(1)), Id, RecordedAt);
        var other = AuditEntryFactory.From(
            new InformationSourceDegraded("sec-edgar", "RecordAndNotifyOnly", ["sec-edgar"], false, T0), Id, RecordedAt);

        recovered.CorrelationId.Should().Be(degraded.CorrelationId);
        other.CorrelationId.Should().NotBe(degraded.CorrelationId);
    }

    // ADR-0020 決定4: 発動理由・対象カテゴリ・暫定期限が要約から読めること（恒久化しないことの検証手段）。
    [Fact]
    public void 一般_Web_収集の発動は理由と暫定期限を要約に残す()
    {
        var entry = AuditEntryFactory.From(
            new GeneralWebCollectionStateChanged(
                "news", Engaged: true, Reason: "4 条件を充足（裏取りあり）",
                ProvisionalUntil: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), T0),
            Id, RecordedAt);

        entry.EventType.Should().Be(nameof(GeneralWebCollectionStateChanged));
        entry.Summary.Should().Contain("発動").And.Contain("裏取りあり").And.Contain("2026-09-01");
        entry.Summary.Should().Contain("恒久化しない");
    }

    [Fact]
    public void 一般_Web_収集の解除も同じ相関へ残る()
    {
        var engaged = AuditEntryFactory.From(
            new GeneralWebCollectionStateChanged("news", true, "発動", T0.AddDays(14), T0), Id, RecordedAt);
        var disengaged = AuditEntryFactory.From(
            new GeneralWebCollectionStateChanged("news", false, "公式ソースへ切替", null, T0.AddDays(10)),
            Id, RecordedAt);

        disengaged.CorrelationId.Should().Be(engaged.CorrelationId);
        disengaged.Summary.Should().Contain("解除");
    }

    // 🔴 **月次の期間集計へ届くこと**（#336 受け入れ基準③後段）。
    // 月報は種別 × 期間（半開区間）で台帳を引く。当月の記録が引けて、前月・翌月のものが混ざらないこと。
    [Fact]
    public void 発動記録は月次の期間集計から引ける()
    {
        var store = new InMemoryAuditEventStore();
        var monthStart = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var nextMonth = monthStart.AddMonths(1);

        store.Append(AuditEntryFactory.From(
            new GeneralWebCollectionStateChanged("news", true, "発動", nextMonth, T0), Guid.NewGuid(), RecordedAt));
        store.Append(AuditEntryFactory.From(
            new InformationSourceRecovered("news", T0, 3, T0.AddHours(2)), Guid.NewGuid(), RecordedAt));
        // 前月の記録は当月の集計へ混ぜない。
        store.Append(AuditEntryFactory.From(
            new GeneralWebCollectionStateChanged("news", false, "解除", null, monthStart.AddDays(-1)),
            Guid.NewGuid(), RecordedAt));

        var monthly = store.GetByTypesInPeriod(
            [nameof(GeneralWebCollectionStateChanged), nameof(InformationSourceRecovered)], monthStart, nextMonth);

        monthly.Should().HaveCount(2);
        monthly.Select(e => e.EventType).Should().BeEquivalentTo(
            [nameof(GeneralWebCollectionStateChanged), nameof(InformationSourceRecovered)]);
        monthly.Should().OnlyContain(e => e.OccurredAt >= monthStart && e.OccurredAt < nextMonth);
    }
}
