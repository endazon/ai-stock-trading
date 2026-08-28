using ReportService.Application.Adapters;
using ReportService.Application.Ports;
using ReportService.Application.Services;
using ReportService.Application.State;
using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Application.Tests;

// FR-06/07/16, UC-03〜05, ADR-0003, IADR-0115, #280: 報告書の自動生成（生成→提示まで）を検証する。
// 最重要の不変条件は「自動生成は確定（Confirmed）しない」こと。確定は OwnerOnly の対話経路のみ（ADR-0003）。
public class ReportAutoGeneratorTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);

    // 2026-07-31（金）17:00 JST ＝ 08:00 UTC。7 月の最終営業日かつ週の最終営業日＝日報・週報・月報が揃う時刻。
    private static readonly DateTimeOffset MonthEndAfterClose = new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubDrafter(string narrative = "自動生成の散文") : IReportNarrativeDrafter
    {
        public int Calls { get; private set; }

        // IADR-0120 決定3, #293: 上位方針が散文ドラフトの文脈として届いているかを検証するため、
        // 受け取った文脈を記録する（生成の副作用ではなく「何を渡したか」が本作業の検証対象）。
        public List<ReportNarrativeContext> Contexts { get; } = [];

        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            Contexts.Add(context);
            return Task.FromResult(narrative);
        }
    }

    private sealed class StubFillSource(params PeriodTradeFill[] fills) : IPeriodFillSource
    {
        public List<(DateOnly From, DateOnly To)> Requested { get; } = [];

        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Requested.Add((from, to));
            return Task.FromResult<IReadOnlyList<PeriodTradeFill>>(fills);
        }
    }

    private sealed class ThrowingFillSource : IPeriodFillSource
    {
        public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("台帳へ到達できません");
    }

    // upsert だけが失敗するストア（競合・確定済み・想定外の失敗を作り分ける）。他の操作は素通しする。
    private sealed class ThrowingUpsertStore(Exception error) : IReportStore
    {
        private readonly InMemoryReportStore _inner = new();

        public int UpsertDraft(TradingReport report, int expectedVersion) => throw error;

        public VersionedReport? Get(string periodKey) => _inner.Get(periodKey);
        public IReadOnlyList<TradingReport> List() => _inner.List();
        public ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt) =>
            _inner.Confirm(periodKey, expectedVersion, confirmedAt);
        public VersionedReport? GetLatestConfirmed(ReportKind kind) => _inner.GetLatestConfirmed(kind);
        public ReportReview? GetReview(string periodKey) => _inner.GetReview(periodKey);
        public ReviewDecision? ApplyReview(string periodKey, ReviewCommand command) => _inner.ApplyReview(periodKey, command);
    }

    // 提示（Present）だけが受理されないストア。生成は成功するが承認待ちへ進まない状況を作る。
    private sealed class RejectingPresentStore : IReportStore
    {
        private readonly InMemoryReportStore _inner = new();

        public ReviewDecision? ApplyReview(string periodKey, ReviewCommand command) =>
            new(new ReportReview(periodKey, ReviewState.Drafting, 1), Transitioned: false, ReviewRejectionReason.InvalidTransition);

        public VersionedReport? Get(string periodKey) => _inner.Get(periodKey);
        public IReadOnlyList<TradingReport> List() => _inner.List();
        public int UpsertDraft(TradingReport report, int expectedVersion) => _inner.UpsertDraft(report, expectedVersion);
        public ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt) =>
            _inner.Confirm(periodKey, expectedVersion, confirmedAt);
        public VersionedReport? GetLatestConfirmed(ReportKind kind) => _inner.GetLatestConfirmed(kind);
        public ReportReview? GetReview(string periodKey) => _inner.GetReview(periodKey);
    }

    // IADR-0116: 提示通知の記録用。既定（notifier 未指定）は通知しない＝生成の検証に通知が混ざらない。
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
        IReportStore store,
        DateTimeOffset now,
        IPeriodFillSource? fills = null,
        IReportNarrativeDrafter? drafter = null,
        ReportAutoGenerationSettings? settings = null,
        IReportDraftPresentedNotifier? notifier = null,
        IBuyInInferenceRecordSource? buyInSource = null,
        IFxSourceStatusSource? fxSourceStatusSource = null) =>
        new(store,
            new ReportDraftService(drafter ?? new StubDrafter()),
            fills ?? new NoOpPeriodFillSource(),
            new FixedClock(now),
            settings ?? new ReportAutoGenerationSettings(),
            notifier,
            reductionSource: null,
            buyInSource: buyInSource,
            fxSourceStatusSource: fxSourceStatusSource);

    // #419: 照会そのものが失敗する記録源（未供給と区別せず null へ倒すことを確かめる）。
    private sealed class ThrowingBuyInSource : IBuyInInferenceRecordSource
    {
        public Task<IReadOnlyList<BuyInInferred>?> GetInferencesAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("推定台帳へ到達できません");
    }

    private static void SeedConfirmed(IReportStore store, string periodKey, ReportKind kind, DateOnly start, string policy)
    {
        var version = store.UpsertDraft(
            new TradingReport { PeriodKey = periodKey, Kind = kind, PeriodStart = start, PolicySummary = policy },
            expectedVersion: 0);
        store.Confirm(periodKey, version, DateTimeOffset.UnixEpoch);
    }

    // ---- 生成の基本 ----

    [Fact]
    public async Task 閉場境界を越えた日報のドラフトを生成する()
    {
        var store = new InMemoryReportStore();

        var result = await NewGenerator(store, WedAfterClose).RunOnceAsync();

        result.Generated.Should().ContainSingle().Which.PeriodKey.Should().Be("daily-2026-07-08");
        store.Get("daily-2026-07-08").Should().NotBeNull();
    }

    [Fact]
    public async Task 月末最終営業日には日報_週報_月報がすべて生成される()
    {
        var store = new InMemoryReportStore();

        var result = await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        result.Generated.Select(r => r.PeriodKey).Should()
            .BeEquivalentTo(["daily-2026-07-31", "weekly-2026-W31", "monthly-2026-07"]);
    }

    [Fact]
    public async Task 生成した本文_Markdown_を保存する()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose, drafter: new StubDrafter("水曜の振り返り")).RunOnceAsync();

        var saved = store.Get("daily-2026-07-08")!.Report;
        saved.Body.Should().Contain("report_type: daily");
        saved.Body.Should().Contain("水曜の振り返り");
    }

    // ---- 確定ゲート（最重要・ADR-0003） ----

    [Fact]
    public async Task 自動生成は提示までで止まり確定しない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        var saved = store.Get("daily-2026-07-08")!.Report;
        saved.State.Should().Be(ReportState.Draft);
        saved.ConfirmedAt.Should().BeNull();
        store.GetReview("daily-2026-07-08")!.State.Should().Be(ReviewState.PendingApproval);
    }

    [Fact]
    public async Task 自動生成しても確定済み日報方針は変化しない()
    {
        var store = new InMemoryReportStore();
        SeedConfirmed(store, "daily-2026-07-07", ReportKind.Daily, new DateOnly(2026, 7, 7), "確定済みの方針");

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        // 取引が参照するのは確定済みの最新日報。自動生成は Draft のままなので、方針は 07-07 のまま動かない。
        var latest = store.GetLatestConfirmed(ReportKind.Daily)!;
        latest.Report.PeriodKey.Should().Be("daily-2026-07-07");
        latest.Report.PolicySummary.Should().Be("確定済みの方針");
    }

    // ---- 冪等・再起動耐性 ----

    [Fact]
    public async Task 同一巡回を繰り返しても二重生成しない()
    {
        var store = new InMemoryReportStore();
        var generator = NewGenerator(store, WedAfterClose);

        var first = await generator.RunOnceAsync();
        var second = await generator.RunOnceAsync();

        first.Generated.Should().ContainSingle();
        second.Generated.Should().BeEmpty();
        store.List().Should().ContainSingle();
    }

    [Fact]
    public async Task 既存の報告書がある期間は生成も提示もしない()
    {
        var store = new InMemoryReportStore();
        // 利用者が手で作って差し戻し中のドラフトがあるところへスケジューラが走っても、局面を触らない。
        var version = store.UpsertDraft(
            new TradingReport { PeriodKey = "daily-2026-07-08", Kind = ReportKind.Daily, PeriodStart = new DateOnly(2026, 7, 8), PolicySummary = "手書き" },
            expectedVersion: 0);
        store.ApplyReview("daily-2026-07-08", new ReviewCommand(ReviewAction.Present, "owner", version));
        store.ApplyReview("daily-2026-07-08", new ReviewCommand(ReviewAction.RequestChanges, "owner", version));

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        store.Get("daily-2026-07-08")!.Report.PolicySummary.Should().Be("手書き");
        store.GetReview("daily-2026-07-08")!.State.Should().Be(ReviewState.ChangesRequested);
    }

    [Fact]
    public async Task 既存期間をスキップしても他の期間は生成する()
    {
        var store = new InMemoryReportStore();
        store.UpsertDraft(
            new TradingReport { PeriodKey = "daily-2026-07-31", Kind = ReportKind.Daily, PeriodStart = new DateOnly(2026, 7, 31) },
            expectedVersion: 0);

        var result = await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        result.Generated.Select(r => r.PeriodKey).Should().BeEquivalentTo(["weekly-2026-W31", "monthly-2026-07"]);
    }

    [Fact]
    public async Task 他レプリカとの競合は失敗にせず当該期間を飛ばす()
    {
        // 多重レプリカが同時に同じ期間を作ると expectedVersion 0 の主キー競合で片方が負ける。
        // 二重生成を避けた結果であり異常ではない（失敗として記録しない・警告も出さない）。
        var store = new ThrowingUpsertStore(new ReportConcurrencyException("daily-2026-07-08", 0, 1));

        var result = await NewGenerator(store, WedAfterClose).RunOnceAsync();

        result.Generated.Should().BeEmpty();
        result.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task 生成中に確定された期間は失敗にせず当該期間を飛ばす()
    {
        // 巡回中に利用者が同じ期間を確定した場合、確定済みは不変のため upsert が拒否される。
        // 次巡回では PeriodKey 一致でスキップされるため、失敗として記録しない。
        var store = new ThrowingUpsertStore(new InvalidOperationException("確定済み報告書は変更できません。"));

        var result = await NewGenerator(store, WedAfterClose).RunOnceAsync();

        result.Generated.Should().BeEmpty();
        result.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task 想定外の失敗は期間ごとに記録し他の期間の生成を止めない()
    {
        var store = new ThrowingUpsertStore(new TimeoutException("ストアが応答しません"));

        var result = await NewGenerator(store, MonthEndAfterClose).RunOnceAsync();

        result.Generated.Should().BeEmpty();
        result.Failed.Select(f => f.PeriodKey).Should()
            .BeEquivalentTo(["daily-2026-07-31", "weekly-2026-W31", "monthly-2026-07"]);
        result.Failed.Should().OnlyContain(f => f.Error is TimeoutException);
    }

    [Fact]
    public async Task 提示が受理されなければ生成できても未提示として報告する()
    {
        // 提示が黙って失敗すると承認待ち一覧に並ばず、利用者は報告書の存在に気付けない。
        var store = new RejectingPresentStore();

        var result = await NewGenerator(store, WedAfterClose).RunOnceAsync();

        result.Generated.Should().ContainSingle();
        result.NotPresented.Should().BeEquivalentTo(["daily-2026-07-08"]);
    }

    [Fact]
    public async Task 提示が受理されれば未提示は空になる()
    {
        var result = await NewGenerator(new InMemoryReportStore(), WedAfterClose).RunOnceAsync();

        result.Generated.Should().ContainSingle();
        result.NotPresented.Should().BeEmpty();
    }

    // ---- 提示の通知（IADR-0116） ----

    [Fact]
    public async Task 提示まで到達した報告書を通知する()
    {
        var notifier = new RecordingNotifier();

        await NewGenerator(new InMemoryReportStore(), WedAfterClose, notifier: notifier).RunOnceAsync();

        var notice = notifier.Notices.Should().ContainSingle().Subject;
        notice.PeriodKey.Should().Be("daily-2026-07-08");
        notice.Kind.Should().Be(ReportKind.Daily);
        notice.PeriodLabel.Should().Be("2026-07-08");
        notice.Version.Should().Be(1); // 確定時の expectedVersion として使える
        notice.Summary.Should().Contain("日報 2026-07-08");
    }

    [Fact]
    public async Task 提示が受理されなかった報告書は通知しない()
    {
        // 承認待ち一覧に並んでいないものを「確認してください」と通知すると、利用者が探しても見つからない。
        var notifier = new RecordingNotifier();

        await NewGenerator(new RejectingPresentStore(), WedAfterClose, notifier: notifier).RunOnceAsync();

        notifier.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task 既存の報告書がある期間は通知しない()
    {
        var store = new InMemoryReportStore();
        store.UpsertDraft(
            new TradingReport { PeriodKey = "daily-2026-07-08", Kind = ReportKind.Daily, PeriodStart = new DateOnly(2026, 7, 8) },
            expectedVersion: 0);
        var notifier = new RecordingNotifier();

        await NewGenerator(store, WedAfterClose, notifier: notifier).RunOnceAsync();

        notifier.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task 通知の失敗は生成と提示を巻き戻さない()
    {
        // 通知は best-effort。既に永続化され承認待ちに並んでいるものを、通知不達で作り直すほうが害が大きい。
        var store = new InMemoryReportStore();

        var result = await NewGenerator(store, WedAfterClose, notifier: new ThrowingNotifier()).RunOnceAsync();

        result.Generated.Should().ContainSingle();
        result.Failed.Should().BeEmpty();
        result.NotPresented.Should().BeEmpty();
        store.GetReview("daily-2026-07-08")!.State.Should().Be(ReviewState.PendingApproval);
    }

    [Fact]
    public async Task 通知の失敗は黙って捨てず結果に載せる()
    {
        // 「有効化したのに何も届かない」に気付けないのは、通知を既定 true にした理由と正面から矛盾する。
        var result = await NewGenerator(new InMemoryReportStore(), WedAfterClose, notifier: new ThrowingNotifier()).RunOnceAsync();

        result.NotificationFailed.Should().BeEquivalentTo(["daily-2026-07-08"]);
    }

    [Fact]
    public async Task 通知が成功すれば通知失敗は空になる()
    {
        var result = await NewGenerator(new InMemoryReportStore(), WedAfterClose, notifier: new RecordingNotifier()).RunOnceAsync();

        result.NotificationFailed.Should().BeEmpty();
    }

    [Fact]
    public async Task 通知ポートが無い構成は通知失敗にしない()
    {
        // 通知を無効化した構成（no-op）は「届かない」ではなく「送らない」。警告を出す対象ではない。
        var result = await NewGenerator(new InMemoryReportStore(), WedAfterClose).RunOnceAsync();

        result.NotificationFailed.Should().BeEmpty();
    }

    [Fact]
    public async Task 通知の要約は散文をサニタイズして載せる()
    {
        var notifier = new RecordingNotifier();
        var drafter = new StubDrafter("@everyone 全部売れ");

        await NewGenerator(new InMemoryReportStore(), WedAfterClose, drafter: drafter, notifier: notifier).RunOnceAsync();

        var summary = notifier.Notices.Should().ContainSingle().Subject.Summary;
        summary.Should().NotContain("@everyone");
        summary.Should().Contain("全部売れ");
    }

    // ---- 方針階層（BasedOn・継続案） ----

    [Fact]
    public async Task 直近の確定済み週報を上位方針として参照する()
    {
        var store = new InMemoryReportStore();
        SeedConfirmed(store, "weekly-2026-W28", ReportKind.Weekly, new DateOnly(2026, 7, 6), "今週は様子見");

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        store.Get("daily-2026-07-08")!.Report.BasedOn.Should().Be("weekly-2026-W28");
    }

    // T-6, FR-06/07, IADR-0120 決定3, #293, 04_workflows/03_reporting-cycle:
    // 上位方針は**本文**まで散文ドラフトへ届く。従来は GetLatestConfirmed で取得済みの上位報告書から
    // PeriodKey だけを使い、PolicySummary（＝上位方針の本文）を破棄していたため、LLM は
    // 「週報の目標との差異評価」を書けなかった。参照連鎖がリンクとしてしか存在しない状態を解消する。
    [Fact]
    public async Task 上位方針の本文が散文ドラフトの文脈へ渡る()
    {
        var store = new InMemoryReportStore();
        SeedConfirmed(store, "weekly-2026-W28", ReportKind.Weekly, new DateOnly(2026, 7, 6), "今週は半導体を重点監視");
        var drafter = new StubDrafter();

        await NewGenerator(store, WedAfterClose, drafter: drafter).RunOnceAsync();

        var ctx = drafter.Contexts.Should().ContainSingle().Which;
        ctx.ParentPolicy!.PeriodKey.Should().Be("weekly-2026-W28");
        ctx.ParentPolicy.Summary.Should().Contain("半導体を重点監視");
    }

    // T-7, IADR-0120 決定3, 03_reporting-cycle「上位方針の欠落」: 上位が未確定でも生成は継続する
    // （例外にしない）。文脈は null＝「参照できなかった」ことをプロンプト側が明記する。
    [Fact]
    public async Task 上位方針が未確定なら文脈は空のまま生成を継続する()
    {
        var store = new InMemoryReportStore();
        var drafter = new StubDrafter();

        var result = await NewGenerator(store, WedAfterClose, drafter: drafter).RunOnceAsync();

        result.Generated.Should().ContainSingle();
        result.Failed.Should().BeEmpty();
        var ctx = drafter.Contexts.Should().ContainSingle().Which;
        ctx.ParentPolicy.Should().BeNull();
    }

    // T-6, IADR-0120 決定3: 月報の上位は前月の月報（ParentKind(Monthly) == Monthly）。
    // 最上位ゆえ自種別を遡るという既存の階層定義が、feed-forward でもそのまま効くことを固定する。
    [Fact]
    public async Task 月報の上位方針は前月の月報の本文になる()
    {
        var store = new InMemoryReportStore();
        SeedConfirmed(store, "monthly-2026-06", ReportKind.Monthly, new DateOnly(2026, 6, 1), "6 月は現金比率 30% 以上");
        var drafter = new StubDrafter();

        await NewGenerator(store, MonthEndAfterClose, drafter: drafter).RunOnceAsync();

        var monthly = drafter.Contexts.Should().ContainSingle(c => c.Kind == ReportKind.Monthly).Which;
        monthly.ParentPolicy!.PeriodKey.Should().Be("monthly-2026-06");
        monthly.ParentPolicy.Summary.Should().Contain("現金比率");
    }

    // T-6, IADR-0120 決定3: PolicySummary（確定すると取引に効くフィールド）へ上位方針の本文を
    // 混ぜてはならない（IADR-0115 決定4「自動生成では新しい方針を機械に提案させない」）。
    // 上位方針は散文の文脈としてのみ渡す。同じ値でも投入先で意味が正反対になるため明示的に固定する。
    [Fact]
    public async Task 上位方針の本文は方針文へは混ぜない()
    {
        var store = new InMemoryReportStore();
        SeedConfirmed(store, "weekly-2026-W28", ReportKind.Weekly, new DateOnly(2026, 7, 6), "今週は半導体を重点監視");

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        var saved = store.Get("daily-2026-07-08")!.Report;
        saved.PolicySummary.Should().NotContain("半導体を重点監視");
    }

    // IADR-0125 決定3, #310: 上位が参照できない事実は残すが、「未確定」というドラフトの状態は名乗らせない
    // （確定するとそのまま取引方針のテキストになるため、確定後に自己矛盾する）。
    [Fact]
    public async Task 上位方針が参照できなければ_BasedOn_は空でその旨を方針文に明記する()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        var saved = store.Get("daily-2026-07-08")!.Report;
        saved.BasedOn.Should().BeNull();
        saved.PolicySummary.Should().Contain("上位方針（週報）");
        saved.PolicySummary.Should().NotContain("未確定");
    }

    // T-1, IADR-0125 決定1, #310: 方針文は同種別の直近確定済み方針の**実体だけ**を引き継ぐ。
    // 従来は前置き（「（自動生成ドラフト・未確定）」「…継続する案です。確定前に内容を見直してください。」）
    // ごと引き継いでいたため、継続のたびに定型文が積み上がり、確定後も「未確定」を名乗る本文が
    // 取引判断（G4 の確定方針）へ渡っていた。
    [Fact]
    public async Task 方針文は同種別の直近確定済み方針の実体だけを引き継ぐ()
    {
        var store = new InMemoryReportStore();
        SeedConfirmed(store, "daily-2026-07-07", ReportKind.Daily, new DateOnly(2026, 7, 7), "押し目買い・上限 3 銘柄");
        SeedConfirmed(store, "weekly-2026-W28", ReportKind.Weekly, new DateOnly(2026, 7, 6), "今週は半導体を重点監視");

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        var policy = store.Get("daily-2026-07-08")!.Report.PolicySummary;
        policy.Should().Be("押し目買い・上限 3 銘柄");
    }

    // T-2, IADR-0125 決定1, #310: 生成 → 確定 → 次の生成 を繰り返しても方針文が伸びない（世代累積しない）。
    [Fact]
    public async Task 生成と確定を繰り返しても方針文が累積しない()
    {
        var store = new InMemoryReportStore();
        SeedConfirmed(store, "daily-2026-07-06", ReportKind.Daily, new DateOnly(2026, 7, 6), "押し目買い・上限 3 銘柄");

        // 07-07（火）→ 07-08（水）と 2 世代ぶん、生成のたびに確定する（利用者が承認した状況）。
        foreach (var (now, key) in new[]
                 {
                     (new DateTimeOffset(2026, 7, 7, 7, 0, 0, TimeSpan.Zero), "daily-2026-07-07"),
                     (WedAfterClose, "daily-2026-07-08"),
                 })
        {
            await NewGenerator(store, now).RunOnceAsync();
            var versioned = store.Get(key)!;
            store.Confirm(key, versioned.Version, now);
        }

        store.Get("daily-2026-07-08")!.Report.PolicySummary.Should().Be(
            store.Get("daily-2026-07-07")!.Report.PolicySummary);
    }

    // T-8, IADR-0125 決定4, #310: 散文の文脈へ渡す上位方針（IADR-0120 決定3 の feed-forward）にも実体だけを渡す
    // ＝累積済みの上位本文がプロンプトを定型文で埋めない。
    [Fact]
    public async Task 上位方針は実体だけが散文の文脈へ渡る()
    {
        var store = new InMemoryReportStore();
        var accumulated = string.Join("\n\n",
            "（自動生成ドラフト・未確定）",
            "直近の確定済み週報方針（weekly-2026-W27）を継続する案です。確定前に内容を見直してください。",
            "今週は半導体を重点監視");
        SeedConfirmed(store, "weekly-2026-W28", ReportKind.Weekly, new DateOnly(2026, 7, 6), accumulated);
        var drafter = new StubDrafter();

        await NewGenerator(store, WedAfterClose, drafter: drafter).RunOnceAsync();

        var ctx = drafter.Contexts.Single(c => c.Kind == ReportKind.Daily);
        ctx.ParentPolicy!.Summary.Should().Be("今週は半導体を重点監視");
    }

    // ---- 約定の供給 ----

    [Fact]
    public async Task 期間の約定を集計範囲で照会する()
    {
        var store = new InMemoryReportStore();
        var fills = new StubFillSource();

        await NewGenerator(store, MonthEndAfterClose, fills).RunOnceAsync();

        fills.Requested.Should().Contain((new DateOnly(2026, 7, 31), new DateOnly(2026, 7, 31)));  // 日報
        fills.Requested.Should().Contain((new DateOnly(2026, 7, 27), new DateOnly(2026, 7, 31)));  // 週報（月曜〜最終営業日）
        fills.Requested.Should().Contain((new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)));   // 月報（月初〜最終営業日）
    }

    [Fact]
    public async Task 約定があれば損益がコード集計され本文に載る()
    {
        var store = new InMemoryReportStore();
        var fills = new StubFillSource(
            new PeriodTradeFill("7203", Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 2_000m, new DateTimeOffset(2026, 7, 8, 1, 0, 0, TimeSpan.Zero)),
            new PeriodTradeFill("7203", Market.Japan, TradeSide.Sell, PositionEffect.Close, 100, 2_100m, new DateTimeOffset(2026, 7, 8, 3, 0, 0, TimeSpan.Zero)));

        await NewGenerator(store, WedAfterClose, fills).RunOnceAsync();

        // 取引回数（買/売/決済）＝ 1 / 1 / 1。数値はコード集計（PnlAggregator）で埋まる（FR-16）。
        store.Get("daily-2026-07-08")!.Report.Body.Should().Contain("| 取引回数（買/売/決済） | 1 / 1 / 1 |");
    }

    [Fact]
    public async Task 約定の照会が失敗しても生成は続く()
    {
        var store = new InMemoryReportStore();

        var result = await NewGenerator(store, WedAfterClose, new ThrowingFillSource()).RunOnceAsync();

        result.Generated.Should().ContainSingle().Which.PeriodKey.Should().Be("daily-2026-07-08");
        result.Failed.Should().BeEmpty();
        store.Get("daily-2026-07-08").Should().NotBeNull();
    }

    // ---- 境界 ----

    [Fact]
    public async Task 生成境界に達していなければ何も生成しない()
    {
        var store = new InMemoryReportStore();
        // 2026-07-08 05:00 UTC ＝ 14:00 JST（日報境界 16:00 前）。前営業日 07-07 が対象になる。
        var result = await NewGenerator(store, new DateTimeOffset(2026, 7, 8, 5, 0, 0, TimeSpan.Zero)).RunOnceAsync();

        result.Generated.Should().ContainSingle().Which.PeriodKey.Should().Be("daily-2026-07-07");
    }

    [Fact]
    public async Task 全期間が非営業日なら何も生成しない()
    {
        var store = new InMemoryReportStore();
        var holidays = Enumerable.Range(0, 40).Select(i => new DateOnly(2026, 7, 8).AddDays(-i)).ToHashSet();
        var settings = new ReportAutoGenerationSettings { Schedule = new ReportScheduleOptions { Holidays = holidays } };

        var result = await NewGenerator(store, WedAfterClose, settings: settings).RunOnceAsync();

        result.Generated.Should().BeEmpty();
        store.List().Should().BeEmpty();
    }

    [Fact]
    public async Task 構成した市場と前提条件バージョンを報告書へ反映する()
    {
        var store = new InMemoryReportStore();
        var settings = new ReportAutoGenerationSettings { Markets = ["JP", "US"], AssumptionsVersion = 7 };

        await NewGenerator(store, WedAfterClose, settings: settings).RunOnceAsync();

        var saved = store.Get("daily-2026-07-08")!.Report;
        saved.AssumptionsVersion.Should().Be(7);
        saved.Body.Should().Contain("markets: [JP, US]");
    }

    // IADR-0125 決定3, #310: 自動生成 → 確定 のあとに**取引が読む**テキスト（`GetConfirmedDailyPolicy`＝
    // `GET /reports/daily-policy`・IADR-0028）が自己矛盾しないことを、生成器と消費側の境界で固定する。
    // 従来は確定しても本文が「（自動生成ドラフト・未確定）」を名乗り続け、判断プロンプトへそのまま載っていた。
    [Fact]
    public async Task 自動生成した日報を確定すると取引が読む方針に未確定の文言が残らない()
    {
        var store = new InMemoryReportStore();
        SeedConfirmed(store, "daily-2026-07-07", ReportKind.Daily, new DateOnly(2026, 7, 7), "押し目買い・上限 3 銘柄");
        SeedConfirmed(store, "weekly-2026-W28", ReportKind.Weekly, new DateOnly(2026, 7, 6), "今週は半導体を重点監視");

        await NewGenerator(store, WedAfterClose).RunOnceAsync();
        var generated = store.Get("daily-2026-07-08")!;
        store.Confirm("daily-2026-07-08", generated.Version, WedAfterClose);

        var policy = new Services.ReportAppService(store, new FixedClock(WedAfterClose)).GetConfirmedDailyPolicy();

        policy!.Summary.Should().Be("押し目買い・上限 3 銘柄");
        policy.Summary.Should().NotContain("未確定");
        policy.Summary.Should().NotContain("見直してください");
    }

    // T-10-240（**決定15**）: FR-10, FR-06, ADR-0016 決定15（2026-08-06 追記）, #419, IADR-0159 決定3 ——
    // 強制買戻し（推定）の記録源が**未注入**（既定構成）のとき、報告書は **0 件と書かない**。
    // 自動縮小（供給元が無ければ空列＝「発動なし」が事実として正しい）と**向きが違う**——
    // 推定経路は実在し発火し得るため、報告書は「起きていない」ことを知らないからである。
    [Fact]
    public async Task 強制買戻しの記録源が未注入なら0件と書かず未供給と記す()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        var body = store.Get("daily-2026-07-08")!.Report.Body;
        body.Should().Contain("### 強制買戻し（推定）（当日）");
        body.Should().Contain("記録を照会できませんでした（供給元がありません）");
        body.Should().NotContain("**発生の有無（推定）**: なし");
    }

    // T-10-241: 照会そのものが失敗した場合も **null（未供給）へ倒す**。空列（＝推定 0 件）へ倒すと
    // 「強制買戻しは起きていない」と読める（決定15 が名指しで禁じた向き）。
    [Fact]
    public async Task 強制買戻しの記録を照会できないときも0件と書かない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose, buyInSource: new ThrowingBuyInSource()).RunOnceAsync();

        store.Get("daily-2026-07-08")!.Report.Body
            .Should().Contain("記録を照会できませんでした（供給元がありません）");
    }

    // --- FR-06, FR-10, UC-06, #381, IADR-0199: 為替の情報源の状態を日報へ通す -------------------
    //
    // 🔴 **ここが繋がっていなかったために、レンダラが完成していても日報には何も出なかった。**
    // 供給ポートは定義され、表示側も実装されていたが、**生成器が呼んでいなかった**——
    // 「部品はあるが繋がっていない」は赤くならない（本 PR の起点）。

    private sealed class StubFxSourceStatusSource(FxSourceStatus? status) : IFxSourceStatusSource
    {
        public Task<FxSourceStatus?> GetStatusAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);
    }

    private sealed class ThrowingFxSourceStatusSource : IFxSourceStatusSource
    {
        public Task<FxSourceStatus?> GetStatusAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("監査台帳へ到達できません");
    }

    [Fact]
    public async Task 為替の情報源の状態が日報の本文へ出る()
    {
        var store = new InMemoryReportStore();
        var t0 = new DateTimeOffset(2026, 7, 8, 3, 0, 0, TimeSpan.Zero);
        var status = new FxSourceStatus(
            [new FxRateSourceFellBack("USD", "fred", 2, 2, t0)], [], [], [], [], []);

        await NewGenerator(store, WedAfterClose, fxSourceStatusSource: new StubFxSourceStatusSource(status))
            .RunOnceAsync();

        var body = store.Get("daily-2026-07-08")!.Report.Body;
        body.Should().Contain("### 為替レートの情報源");
        body.Should().Contain("フォールバックへ切替");
        body.Should().Contain("fred");
    }

    // 🔴 **否定形**: 供給元が未注入なら「照会できませんでした」であり、「劣化なし」ではない。
    // **為替のイベントは本番で実際に発行されている**ため、「なし」と書けば端的に嘘になる。
    [Fact]
    public async Task 為替の供給元が無いときは_劣化なしと書かない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose).RunOnceAsync();

        var body = store.Get("daily-2026-07-08")!.Report.Body;
        body.Should().Contain("状態を照会できませんでした（要確認）");
        body.Should().NotContain("情報源の切替・鮮度警告・鮮度切れでの決済の記録はありません");
    }

    // **否定形**: 照会が例外でも同じ（未供給へ倒す。空へ倒さない）。
    [Fact]
    public async Task 為替の状態を照会できないときも_劣化なしと書かない()
    {
        var store = new InMemoryReportStore();

        await NewGenerator(store, WedAfterClose, fxSourceStatusSource: new ThrowingFxSourceStatusSource())
            .RunOnceAsync();

        store.Get("daily-2026-07-08")!.Report.Body
            .Should().Contain("状態を照会できませんでした（要確認）");
    }
}
