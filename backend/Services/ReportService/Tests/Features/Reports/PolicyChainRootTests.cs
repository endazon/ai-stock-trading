using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, UC-03, #839, ADR-0003, IADR-0071, IADR-0115, IADR-0352, IADR-0382:
// **方針の連鎖（月報 → 週報 → 日報）が最上位で切れていることを、確定の前に見せる**ことと、
// **初回月報を作る導線**（保存＋提示）を検証する。
//
// 🔴 是正前の実測（issue 本文・PoC 運用中）: 確定した週報 `weekly-2026-W38` の方針文が空で、
// 報告書テーブルに月報の行は 1 件も無かった。方針連鎖の欠落は `ReportInput` の語彙に無く、
// 記録も警告もされないため、**中身が無いことが確定の妨げにならなかった**。
//
// テスト ID は **T-06-001〜014**（本作業で新設した帯。走査の結果、本リポジトリに `T-06` 帯は
// 1 件も存在しなかった。作業仕様書 `20260923_839_policy-chain-root-and-monthly-bootstrap` 参照）。
public class PolicyChainRootTests
{
    // 2026-07-31（金）17:00 JST ＝ 08:00 UTC。7 月の最終営業日かつ週の最終営業日＝日報・週報・月報が揃う時刻。
    private static readonly DateTimeOffset MonthEndAfterClose = new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(
            ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("自動生成の散文");
    }

    private sealed class RecordingNotifier : IReportDraftPresentedNotifier
    {
        public List<PresentedReportNotice> Notices { get; } = [];

        public Task NotifyAsync(PresentedReportNotice notice, CancellationToken cancellationToken = default)
        {
            Notices.Add(notice);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotifier : IReportDraftPresentedNotifier
    {
        public Task NotifyAsync(PresentedReportNotice notice, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("バスへ到達できません");
    }

    private static ReportAutoGenerator NewGenerator(
        IReportStore store, DateTimeOffset now, IReportDraftPresentedNotifier? notifier = null) =>
        new(store,
            new ReportDraftService(new StubDrafter()),
            new NoOpPeriodFillSource(),
            new FixedClock(now),
            new ReportAutoGenerationSettings(),
            notifier);

    private static void SeedConfirmed(IReportStore store, string periodKey, ReportKind kind, DateOnly start)
    {
        var version = store.UpsertDraft(
            new TradingReport { PeriodKey = periodKey, Kind = kind, PeriodStart = start, PolicySummary = "実質のある方針" },
            expectedVersion: 0);
        store.Confirm(periodKey, version, DateTimeOffset.UnixEpoch);
    }

    private static IReadOnlyList<ReportInput> UnsuppliedOf(IReportStore store, string periodKey) =>
        store.Get(periodKey)!.Report.UnsuppliedInputs;

    // --- T-06-001 / T-06-002: 連鎖の欠落を未供給として記録する ---

    [Fact]
    public async Task T06_001_上位方針が無いまま生成した週報は上位方針を未供給として記録する()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        // 週報の上位は月報。確定済み月報が 1 件も無い＝連鎖が最上位で切れている（#839 の実測と同じ状態）。
        UnsuppliedOf(store, "weekly-2026-W31").Should().Contain(ReportInput.ParentPolicy);
    }

    [Fact]
    public async Task T06_002_前期方針が無いときは前期の確定済み方針も記録する()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        // 日報の上位は週報・前期は前日の日報。どちらも確定済みが無い。
        UnsuppliedOf(store, "daily-2026-07-31").Should()
            .Contain(ReportInput.ParentPolicy).And.Contain(ReportInput.PreviousPolicy);
    }

    // --- T-06-003: 月報は上位＝前期のため二重に数えない ---

    [Fact]
    public async Task T06_003_月報は上位と前期が同一なので前期方針を二重に数えない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        var unsupplied = UnsuppliedOf(store, "monthly-2026-07");
        unsupplied.Should().Contain(ReportInput.ParentPolicy);
        // 🔴 同じ事実（前月の月報が無い）を 2 つの表示名で 2 回警告しない。
        unsupplied.Should().NotContain(ReportInput.PreviousPolicy);
    }

    // --- T-06-004: 揃っていれば記録しない（否定形） ---

    [Fact]
    public async Task T06_004_上位と前期が揃っていれば方針連鎖の未供給は記録しない()
    {
        var store = new InMemoryReportStore();
        // 週報の上位（月報）と前期（前週の週報）をどちらも確定済みにする。
        SeedConfirmed(store, "monthly-2026-06", ReportKind.Monthly, new DateOnly(2026, 6, 1));
        SeedConfirmed(store, "weekly-2026-W30", ReportKind.Weekly, new DateOnly(2026, 7, 20));

        await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        UnsuppliedOf(store, "weekly-2026-W31").Should()
            .NotContain(ReportInput.ParentPolicy).And.NotContain(ReportInput.PreviousPolicy);
    }

    // --- T-06-005: 見送りの対象にしない ---

    [Fact]
    public async Task T06_005_方針連鎖の欠落では生成を見送らない()
    {
        var store = new InMemoryReportStore();

        var result = await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        // 🔴 供給元は自リポジトリのストアであり HTTP を出さない＝一過性の失敗を観測しない。
        // **待っても確定済みの上位は増えない**（確定は利用者の行為である）ため、見送ってはならない。
        result.Deferred.Should().BeEmpty();
        result.Generated.Select(r => r.PeriodKey).Should()
            .BeEquivalentTo(["daily-2026-07-31", "weekly-2026-W31", "monthly-2026-07"]);
    }

    // --- T-06-006: 提示通知の要約に警告が出る ---

    [Fact]
    public async Task T06_006_提示通知の要約に方針連鎖の欠落が警告として出る()
    {
        var store = new InMemoryReportStore();
        var notifier = new RecordingNotifier();

        await NewGenerator(store, MonthEndAfterClose, notifier).RunOnceAsync();

        var weekly = notifier.Notices.Single(n => n.PeriodKey == "weekly-2026-W31");
        weekly.Summary.Should().Contain(ReportSummary.UnsuppliedWarningPrefix);
        weekly.Summary.Should().Contain("上位方針（親の確定済み報告書）");
    }

    // --- T-06-007: `/report show` の応答 ---

    [Fact]
    public async Task T06_007_レビュー照会に方針連鎖の欠落が表示名で出る()
    {
        var store = new InMemoryReportStore();
        await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        var view = new ReportAppService(store, new FixedClock(MonthEndAfterClose))
            .GetReviewView("weekly-2026-W31");

        view!.UnsuppliedInputs.Should().Contain("上位方針（親の確定済み報告書）");
    }

    // --- T-06-008 / T-06-011: 初回月報ブートストラップの保存＋提示 ---

    [Fact]
    public async Task T06_008_初回月報ブートストラップを保存し承認待ちへ並べる()
    {
        var store = new InMemoryReportStore();
        var notifier = new RecordingNotifier();
        var svc = new ReportAppService(store, new FixedClock(MonthEndAfterClose), notifier);

        var result = await svc.StartMonthlyBootstrapAsync(["AAPL", "MSFT"], 1, "owner");

        result.Outcome.Should().Be(MonthlyBootstrapOutcome.Started);
        result.Presented.Should().BeTrue();
        result.NotificationFailed.Should().BeFalse();

        // 🔴 確定は**しない**（ADR-0003）。承認待ち（PendingApproval）で止まる。
        var saved = store.Get("monthly-2026-07")!;
        saved.Report.State.Should().Be(ReportState.Draft);
        store.GetReview("monthly-2026-07")!.State.Should().Be(ReviewState.PendingApproval);

        // 版番号は確定要求に添える expectedVersion である。
        result.Version.Should().Be(saved.Version);
        notifier.Notices.Should().ContainSingle().Which.Version.Should().Be(saved.Version);
    }

    [Fact]
    public async Task T06_011_ブートストラップの提示要約は数値を騙らない()
    {
        var store = new InMemoryReportStore();
        var notifier = new RecordingNotifier();
        var svc = new ReportAppService(store, new FixedClock(MonthEndAfterClose), notifier);

        await svc.StartMonthlyBootstrapAsync(["AAPL"], 1, "owner");

        var summary = notifier.Notices.Should().ContainSingle().Subject.Summary;
        // 🔴 ブートストラップは集計を 1 つも持たない。「実現損益 0」「取引 0 件」と書けば騙りになる。
        summary.Should().NotContain("実現損益");
        summary.Should().NotContain("取引: 0 件");
        summary.Should().Contain("数値の集計・散文はありません");
        summary.Should().Contain("AAPL");
    }

    // T-06-014 (#839, IADR-0382): 通知の失敗を成功に見せない（保存・提示は巻き戻さない）。
    [Fact]
    public async Task T06_014_通知の失敗は成功に見せないが保存と提示は巻き戻さない()
    {
        var store = new InMemoryReportStore();
        var svc = new ReportAppService(store, new FixedClock(MonthEndAfterClose), new ThrowingNotifier());

        var result = await svc.StartMonthlyBootstrapAsync(["AAPL"], 1, "owner");

        result.Outcome.Should().Be(MonthlyBootstrapOutcome.Started);
        result.Presented.Should().BeTrue();
        result.NotificationFailed.Should().BeTrue();
        store.GetReview("monthly-2026-07")!.State.Should().Be(ReviewState.PendingApproval);
    }

    // --- T-06-009 / T-06-010: 既存の行を踏まない ---

    [Fact]
    public async Task T06_009_確定済み月報があればブートストラップは不要として何もしない()
    {
        var store = new InMemoryReportStore();
        SeedConfirmed(store, "monthly-2026-06", ReportKind.Monthly, new DateOnly(2026, 6, 1));
        var svc = new ReportAppService(store, new FixedClock(MonthEndAfterClose));

        var result = await svc.StartMonthlyBootstrapAsync(["AAPL"], 1, "owner");

        result.Outcome.Should().Be(MonthlyBootstrapOutcome.NotNeeded);
        store.Get("monthly-2026-07").Should().BeNull();
    }

    [Fact]
    public async Task T06_010_当月の月報の行が既にあれば上書きしない()
    {
        var store = new InMemoryReportStore();
        // 利用者が手で作った（あるいは差し戻し中の）ドラフト。
        store.UpsertDraft(
            new TradingReport
            {
                PeriodKey = "monthly-2026-07",
                Kind = ReportKind.Monthly,
                PeriodStart = new DateOnly(2026, 7, 1),
                PolicySummary = "利用者が書いた方針",
            },
            expectedVersion: 0);
        var svc = new ReportAppService(store, new FixedClock(MonthEndAfterClose));

        var result = await svc.StartMonthlyBootstrapAsync(["AAPL"], 1, "owner");

        result.Outcome.Should().Be(MonthlyBootstrapOutcome.PeriodOccupied);
        store.Get("monthly-2026-07")!.Report.PolicySummary.Should().Be("利用者が書いた方針");
    }

    // --- T-06-012: 既知の帰結（受容した副作用を黙って起こさない） ---

    // 🔴 ブートストラップは**当月の月報の枠**（`monthly-YYYY-MM`）を使う（IADR-0071 決定4 が当月を採る）。
    // 自動生成の冪等は「行があれば作らない」（IADR-0115 決定3）ため、**その月の月末に、データに基づく
    // 月報は別途生成されない。** 運用開始の月にだけ起きる一度きりの事象であり受容するが、
    // **テストで固定して黙って起きないようにする**（IADR-0382 の残余リスク）。
    [Fact]
    public async Task T06_012_ブートストラップが当月の枠を使うと月末の自動生成はその期間を作らない()
    {
        var store = new InMemoryReportStore();
        var svc = new ReportAppService(store, new FixedClock(MonthEndAfterClose));
        await svc.StartMonthlyBootstrapAsync(["AAPL"], 1, "owner");

        var result = await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        result.Generated.Select(r => r.PeriodKey).Should()
            .BeEquivalentTo(["daily-2026-07-31", "weekly-2026-W31"]);
        // 枠を使っているのはブートストラップのドラフトである（自動生成が上書きしていない）。
        store.Get("monthly-2026-07")!.Report.PolicySummary.Should().StartWith("初回月報ブートストラップ");
    }
}
