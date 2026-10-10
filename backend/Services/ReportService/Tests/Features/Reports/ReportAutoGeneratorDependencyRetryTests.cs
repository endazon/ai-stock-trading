using System.Net;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Common.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using ReportService.Infrastructure.Persistence;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-07, FR-09, UC-03〜05, ADR-0003, #840, IADR-0352: 依存先が一過性に落ちている間は生成を見送って再試行し、
// 恒常的な失敗・上限到達では縮退した報告書を**未供給の入力を記録・提示して**出すことを、結果で検証する。
//
// 供給元は本物（HttpOpenPositionSource / HttpStageProgressSource / HttpReportNarrativeDrafter）を
// 本物の鎖（ReportDependencyHandler）越しに使う。偽物は「上流（一次ハンドラ）」と「トークン供給元」だけである
// ——門・観測・供給元の縮退・生成器の判定が**繋がって**初めて成立する振る舞いを見るため。
public class ReportAutoGeneratorDependencyRetryTests
{
    // 2026-07-08（水）16:00 JST ＝ 07:00 UTC。日報だけが生成境界を越えている時刻。
    private static readonly DateTimeOffset WedAfterClose = new(2026, 7, 8, 7, 0, 0, TimeSpan.Zero);
    private const string DailyKey = "daily-2026-07-08";

    // 2026-07-10（金）16:30 JST ＝ 07:30 UTC。日報と週報が生成境界を越えている時刻。
    private static readonly DateTimeOffset FriAfterClose = new(2026, 7, 10, 7, 30, 0, TimeSpan.Zero);

    // #866: 生成窓の終端。2026-07-31（金）は**月末の最終営業日**であり、月報の窓は
    // 「17:00 JST（MonthlyAt）〜 月末 24:00 JST」＝この日だけの 7 時間しかない（当月を過ぎると Due から消える）。
    // 23:55 JST ＝ 14:55 UTC / 23:59:45 JST ＝ 14:59:45 UTC / 翌 00:01 JST ＝ 15:01 UTC。
    private static readonly DateTimeOffset MonthEnd2355 = new(2026, 7, 31, 14, 55, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MonthEnd235945 = new(2026, 7, 31, 14, 59, 45, TimeSpan.Zero);
    private static readonly DateTimeOffset MonthWindowClosed = new(2026, 7, 31, 15, 1, 0, TimeSpan.Zero);

    // 2026-08-02（日）23:59:45 JST ＝ 14:59:45 UTC。次の試行は月曜＝当 ISO 週が変わる（週報の窓の終端）。
    private static readonly DateTimeOffset WeekEnd235945 = new(2026, 8, 2, 14, 59, 45, TimeSpan.Zero);

    private const string MonthlyKey = "monthly-2026-07";
    private const string WeeklyW31Key = "weekly-2026-W31";
    private const string DailyJul31Key = "daily-2026-07-31";

    private const string PositionsJson =
        """[{"symbol":"AAPL","market":1,"side":0,"quantity":1,"entryPrice":190.5,"stopLossPrice":180.0}]""";

    private const string LlmJson =
        """{"text":"市況は落ち着いていた。","model":"claude-sonnet-5-5","inputTokens":1,"outputTokens":1,"sent":true}""";

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    // 呼び出しのたびに差し替えられるトークン供給元（null＝取得できない）。
    private sealed class SwitchableTokenProvider : IServiceAccessTokenProvider
    {
        public string? Token { get; set; }

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(Token);
    }

    // 上流。応答を差し替えられ、受けた要求を記録する。
    private sealed class Upstream : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = "[]";

        public List<(string Path, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CountingDrafter : IReportNarrativeDrafter
    {
        public int Calls { get; private set; }

        /// <summary>#1156: 最後に受け取った散文の文脈（未供給の一覧・建玉が渡っているかを見る）。</summary>
        public ReportNarrativeContext? LastContext { get; private set; }

        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastContext = context;
            return Task.FromResult("自動生成の散文");
        }
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

    // 台帳以外の入力は「供給できている」状態に固定する（空の記録＝事象なし。null＝未供給ではない）。
    // こうしておかないと、未設定（Unsupplied*）ぶんの未供給が混ざり「縮退していない」を結果で言えない。
    private sealed class SuppliedSources :
        IBuyInInferenceRecordSource, IFxSourceStatusSource, ILlmUsageRecordSource, IBorrowFeeRecordSource,
        ITradeRationaleSource, IOpenDUptimeSource, IPeriodEndFxRateSource, IStageProgressSource,
        IPeriodDriftAdoptionSource, IStopLossMethodUsageSource, IStopLossMethodResolutionSource
    {
        // #1002: 日報・月報の損切りの実行機構（発注執行の解決結果）。空の記録（null＝未供給ではない）。
        public Task<StopLossMethodResolutionFeed?> GetResolutionsAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<StopLossMethodResolutionFeed?>(new StopLossMethodResolutionFeed([]));

        // #823: 日報 §4 の損切りの実行機構。空の集計＝承認なし（null＝未供給ではない）。
        Task<StopLossMethodUsage?> IStopLossMethodUsageSource.GetUsageAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
            Task.FromResult<StopLossMethodUsage?>(StopLossMethodUsage.From([]));

        // #870: 全種別が使う入力（在庫の畳み込み）。空列＝該当なし（null＝未供給ではない）。
        public Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodDriftAdoption>?>([]);

        // #866: 月報だけが使う入力。供給しておかないと「段階が未設定」で月報が常に縮退し、
        // 窓の終端の検証（何が欠けて縮退したのか）が読み取れなくなる。
        public Task<TradingStage?> GetCurrentStageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TradingStage?>(TradingStage.Stage1Simulate);

        public Task<IReadOnlyList<AiStockTrading.Shared.Contracts.Events.BuyInInferred>?> GetInferencesAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AiStockTrading.Shared.Contracts.Events.BuyInInferred>?>([]);

        public Task<FxSourceStatus?> GetStatusAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<FxSourceStatus?>(new FxSourceStatus([], [], [], [], [], []));

        public Task<LlmUsageRecord?> GetUsageAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmUsageRecord?>(new LlmUsageRecord([], [], []));

        public Task<BorrowFeeRecord?> GetBorrowFeesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<BorrowFeeRecord?>(new BorrowFeeRecord([], []));

        public Task<IReadOnlyDictionary<Guid, string>?> GetRationalesAsync(
            DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>?>(new Dictionary<Guid, string>());

        public Task<OpenDUptimeRecord?> GetUptimeAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            Task.FromResult<OpenDUptimeRecord?>(new OpenDUptimeRecord([]));

        public Task<PeriodEndFxRate?> GetRateAsync(DateOnly periodEnd, CancellationToken cancellationToken = default) =>
            Task.FromResult<PeriodEndFxRate?>(new PeriodEndFxRate(150m, periodEnd));
    }

    // 1 つの「環境」。巡回（RunOnceAsync）のたびに生成器を作り直す＝本番の scoped と同じ寿命にし、
    // 巡回を跨いで残るのはストア・見送り回数・観測点（singleton）だけにする。
    private sealed class Rig
    {
        public ReportDependencyProbe Probe { get; } = new();

        /// <summary>AST レルム（台帳向け）のトークン。</summary>
        public SwitchableTokenProvider Tokens { get; } = new() { Token = "T" };

        /// <summary>MSP レルム（LLM ゲートウェイ向け）のトークン。レルムが違うので供給元も別である。</summary>
        public SwitchableTokenProvider LlmTokens { get; } = new() { Token = "L" };
        public Upstream Risk { get; } = new() { Body = PositionsJson };
        public Upstream Llm { get; } = new() { Body = LlmJson };

        /// <summary>
        /// #1156（独立監査）: OpenD 稼働率（中核でない入力）の上流。<see cref="UseHttpUptime"/> のときだけ使う。
        /// 台帳と同じ AST レルムのトークン（<see cref="Tokens"/>）で送る＝Keycloak が未準備なら建玉と一緒に欠ける。
        /// </summary>
        public Upstream Uptime { get; } = new() { Body = """{"days":[]}""" };

        /// <summary>true なら稼働率も本物の供給元（HttpOpenDUptimeSource）を本物の鎖越しに使う。</summary>
        public bool UseHttpUptime { get; init; }

        /// <summary>#1181, IADR-0493 決定 4: 期間開始時点の在庫（中核の入力）の上流。<see cref="UseHttpOpening"/> のときだけ使う。</summary>
        public Upstream Opening { get; } = new() { Body = "[]" };

        /// <summary>true なら期間開始時点の在庫も本物の供給元（HttpOpeningInventorySource）を本物の鎖越しに使う。</summary>
        public bool UseHttpOpening { get; init; }
        public InMemoryReportStore Store { get; } = new();
        public RecordingNotifier Notifier { get; } = new();
        public CountingDrafter StubDrafter { get; } = new();
        public ReportGenerationDeferralTracker Deferrals { get; }
        public DateTimeOffset Now { get; set; } = WedAfterClose;

        /// <summary>true なら散文も本物の判定器（HttpReportNarrativeDrafter）を本物の鎖越しに使う。</summary>
        public bool UseRealDrafter { get; init; }

        // #1156, IADR-0480 決定 3: 中核の上限は既定で通常の上限と同じにする（既存の試験は建玉＝中核の欠落で
        // 通常の上限を見ている）。中核の上限を見る試験だけが coreMaxDeferrals を与える。
        public Rig(int maxDeferrals = ReportDeferralSettings.DefaultMaxDeferrals, int? coreMaxDeferrals = null)
        {
            Deferrals = new ReportGenerationDeferralTracker(new ReportDeferralSettings
            {
                MaxDeferrals = maxDeferrals,
                CoreMaxDeferrals = coreMaxDeferrals ?? maxDeferrals,
            });

            // #839, IADR-0382: **方針の連鎖を張っておく。** 本ファイルの検証対象は「依存先が一過性に落ちている
            // ときの見送り」であり、上位方針・前期方針の欠落（別の未供給）を混ぜると
            // 「欠けたのは建玉だけ」という否定形が成立しなくなる。
            SeedConfirmed("weekly-2026-W27", ReportKind.Weekly, new DateOnly(2026, 6, 29));
            SeedConfirmed("daily-2026-07-07", ReportKind.Daily, new DateOnly(2026, 7, 7));
        }

        private void SeedConfirmed(string periodKey, ReportKind kind, DateOnly start)
        {
            var version = Store.UpsertDraft(
                new TradingReport
                {
                    PeriodKey = periodKey,
                    Kind = kind,
                    PeriodStart = start,
                    PolicySummary = "実質のある方針",
                },
                expectedVersion: 0);
            Store.Confirm(periodKey, version, DateTimeOffset.UnixEpoch);
        }

        private HttpClient Client(
            Upstream upstream, string dependency, IServiceAccessTokenProvider tokens, bool timeoutIsTransient = true) =>
            new(new ReportDependencyHandler(
                Probe, tokens, dependency, timeoutIsTransient, NullLogger<ReportDependencyHandler>.Instance)
            {
                InnerHandler = upstream,
            })
            {
                BaseAddress = new Uri($"http://{dependency}"),
            };

        public Task<ReportAutoGenerationResult> RunOnceAsync()
        {
            IReportNarrativeDrafter drafter = UseRealDrafter
                ? new HttpReportNarrativeDrafter(
                    Client(Llm, "report-llm", LlmTokens, timeoutIsTransient: false),
                    NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", purposeOverride: null)
                : StubDrafter;
            var supplied = new SuppliedSources();

            var generator = new ReportAutoGenerator(
                Store,
                new ReportDraftService(drafter),
                new NoOpPeriodFillSource(),
                new FixedClock(Now),
                new ReportAutoGenerationSettings(),
                Notifier,
                // 発火元が無い＝「発動なし」が事実（未供給ではない）。
                reductionSource: new NoMarginReductionRecordSource(),
                buyInSource: supplied,
                fxSourceStatusSource: supplied,
                llmUsageSource: supplied,
                borrowFeeSource: supplied,
                rationaleSource: supplied,
                openPositionSource: new HttpOpenPositionSource(
                    Client(Risk, "risk-ledger", Tokens), NullLogger<HttpOpenPositionSource>.Instance),
                uptimeSource: UseHttpUptime
                    ? new HttpOpenDUptimeSource(
                        Client(Uptime, "risk-ledger", Tokens), NullLogger<HttpOpenDUptimeSource>.Instance)
                    : supplied,
                stageProgressSource: supplied,
                periodEndFxRateSource: supplied,
                dependencyProbe: Probe,
                deferrals: Deferrals,
                driftAdoptionSource: supplied,
                stopLossMethodUsageSource: supplied,
                stopLossMethodResolutionSource: supplied,
                openingInventorySource: UseHttpOpening
                    ? new HttpOpeningInventorySource(
                        Client(Opening, "risk-ledger", Tokens), NullLogger<HttpOpeningInventorySource>.Instance)
                    : null);

            return generator.RunOnceAsync();
        }
    }

    // ---- 一過性の失敗 → 見送り → 成功 --------------------------------------------------------------

    [Fact]
    public async Task トークンを取得できない巡回は_報告書を保存も提示も通知もせず_上流へ何も送らない()
    {
        var rig = new Rig();
        rig.Tokens.Token = null; // Keycloak がまだ起動していない。

        var result = await rig.RunOnceAsync();

        var deferral = result.Deferred.Should().ContainSingle().Subject;
        deferral.PeriodKey.Should().Be(DailyKey);
        deferral.WaitingFor.Should().Equal(ReportInput.OpenPositions);
        deferral.Attempt.Should().Be(1);
        deferral.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
        deferral.Causes.Should().ContainSingle().Which.Should().Contain("risk-ledger");

        // 🔴 否定形: 縮退した報告書は出来上がっていない（本変更前はここで承認待ちに並んでいた）。
        result.Generated.Should().BeEmpty();
        rig.Store.Get(DailyKey).Should().BeNull();
        rig.Notifier.Notices.Should().BeEmpty();
        // 🔴 否定形: 認証なしの要求を上流へ投げていない。
        rig.Risk.Requests.Should().BeEmpty();
        // 🔴 否定形: 見送る回に LLM を呼んでいない（捨てる散文に費用を出さない）。
        rig.StubDrafter.Calls.Should().Be(0);
    }

    [Fact]
    public async Task 一過性の失敗の後に成功すれば_縮退しない報告書が出る()
    {
        var rig = new Rig();
        rig.Tokens.Token = null;
        (await rig.RunOnceAsync()).Deferred.Should().ContainSingle();

        rig.Tokens.Token = "T"; // Keycloak が立ち上がった。次の巡回。
        var result = await rig.RunOnceAsync();

        result.Deferred.Should().BeEmpty();
        result.Degraded.Should().BeEmpty();
        var report = result.Generated.Should().ContainSingle().Subject;
        report.UnsuppliedInputs.Should().NotContain(ReportInput.OpenPositions);
        // 建玉は本文に載っている（「照会できませんでした」ではない）。
        report.Body.Should().Contain("AAPL");
        report.Body.Should().NotContain("建玉を照会できませんでした");
        rig.Store.GetReview(DailyKey)!.State.Should().Be(ReviewState.PendingApproval);
        // 提示通知は 1 回だけ・警告なし。
        rig.Notifier.Notices.Should().ContainSingle()
            .Which.Summary.Should().NotContain(ReportSummary.UnsuppliedWarningPrefix);
        // 送った要求にはトークンが付いている。
        rig.Risk.Requests.Should().OnlyContain(r => r.Authorization == "Bearer T");
        // 見送り回数は生成できた時点で捨てる。
        rig.Deferrals.DeferralsOf(DailyKey).Should().Be(0);
    }

    [Fact]
    public async Task 依存先が_503_を返す間も見送り_回復すれば縮退しない報告書が出る()
    {
        var rig = new Rig();
        rig.Risk.Status = HttpStatusCode.ServiceUnavailable;

        var first = await rig.RunOnceAsync();
        first.Deferred.Should().ContainSingle().Which.Causes.Should().ContainSingle().Which.Should().Contain("503");
        rig.Store.Get(DailyKey).Should().BeNull();

        rig.Risk.Status = HttpStatusCode.OK;
        var second = await rig.RunOnceAsync();

        second.Generated.Should().ContainSingle().Which.UnsuppliedInputs.Should().BeEmpty();
        second.Degraded.Should().BeEmpty();
    }

    // ---- 一過性の失敗が続く → 上限で打ち切り --------------------------------------------------------

    [Fact]
    public async Task 一過性の失敗が続けば_上限で打ち切って縮退した報告書と警告を出す()
    {
        var rig = new Rig(maxDeferrals: 3);
        rig.Tokens.Token = null;

        // 上限までは見送り続ける（否定形: 途中で縮退した報告書を出さない）。待ち時間は倍々。
        var waits = new List<TimeSpan>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var deferred = await rig.RunOnceAsync();
            deferred.Generated.Should().BeEmpty();
            var deferral = deferred.Deferred.Should().ContainSingle().Subject;
            deferral.Attempt.Should().Be(attempt);
            deferral.MaxDeferrals.Should().Be(3);
            waits.Add(deferral.RetryAfter);
            rig.Store.Get(DailyKey).Should().BeNull();
        }

        waits.Should().Equal(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120));

        // 上限の次の巡回: 見送らず、縮退した報告書を出す（依存先が戻らないまま報告書が永久に出ない、を作らない）。
        var result = await rig.RunOnceAsync();

        result.Deferred.Should().BeEmpty();
        var degradation = result.Degraded.Should().ContainSingle().Subject;
        degradation.PeriodKey.Should().Be(DailyKey);
        degradation.RetriesExhausted.Should().BeTrue();
        // 🔴 否定形: 上限到達であり、窓の終端ではない（#866）。
        degradation.WindowClosing.Should().BeFalse();
        // 欠けたのは建玉だけ（他の入力は供給できている。欠けていない入力を警告に混ぜない）。
        degradation.UnsuppliedInputs.Should().Equal(ReportInput.OpenPositions);

        var stored = rig.Store.Get(DailyKey)!.Report;
        stored.UnsuppliedInputs.Should().Equal(ReportInput.OpenPositions);
        // 値を騙らない倒れ方は従来どおり（「建玉なし」とは書かない）。
        stored.Body.Should().Contain("建玉を照会できませんでした");
        stored.Body.Should().NotContain("AAPL");
        rig.Store.GetReview(DailyKey)!.State.Should().Be(ReviewState.PendingApproval);
        // 提示の時点で欠落が見える。
        var notice = rig.Notifier.Notices.Should().ContainSingle().Subject;
        notice.Summary.Should().Contain(ReportSummary.UnsuppliedWarningPrefix);
        notice.Summary.Should().Contain(ReportInputs.Label(ReportInput.OpenPositions));
        // 🔴 否定形: 打ち切った回も認証なしの要求は出していない。
        rig.Risk.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 見送りの上限を_0_にすれば_見送らずその巡回で縮退した報告書を出す()
    {
        var rig = new Rig(maxDeferrals: 0);
        rig.Tokens.Token = null;

        var result = await rig.RunOnceAsync();

        result.Deferred.Should().BeEmpty();
        var degradation = result.Degraded.Should().ContainSingle().Subject;
        degradation.UnsuppliedInputs.Should().Contain(ReportInput.OpenPositions);
        // 見送りを使っていないので「上限に達した」とは言わない。
        degradation.RetriesExhausted.Should().BeFalse();
    }

    [Fact]
    public async Task 見送っている間に他で行が出来た期間は_踏まずに見送り回数を捨てる()
    {
        var rig = new Rig();
        rig.Tokens.Token = null;
        (await rig.RunOnceAsync()).Deferred.Should().ContainSingle();
        rig.Deferrals.DeferralsOf(DailyKey).Should().Be(1);

        // 他レプリカ・利用者の手で同じ期間の行が出来た。
        rig.Store.UpsertDraft(
            new TradingReport { PeriodKey = DailyKey, Kind = ReportKind.Daily, PeriodStart = new DateOnly(2026, 7, 8) }, 0);

        var result = await rig.RunOnceAsync();

        // 冪等（IADR-0115 決定3）: 既にある行は踏まない。見送りも生成もしない。
        result.Deferred.Should().BeEmpty();
        result.Generated.Should().BeEmpty();
        rig.Deferrals.DeferralsOf(DailyKey).Should().Be(0);
    }

    // ---- 恒常的な失敗 → 見送らない ----------------------------------------------------------------

    // #866: **401 はここから外した**（一過性へ移した）。上流の設定取得器のバックオフにより、
    // Keycloak 回復から 24.6 秒は正しいトークンでも 401 が返る（実測）。恒常なのは 403 である。
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 恒常的な設定誤りは見送らず_その巡回で縮退した報告書と警告を出す(HttpStatusCode status)
    {
        var rig = new Rig();
        rig.Risk.Status = status; // トークンは付いている。ロール未付与などで拒否された。

        var result = await rig.RunOnceAsync();

        // 🔴 否定形: 待っても変わらない失敗で生成を遅らせない。
        result.Deferred.Should().BeEmpty();
        rig.Deferrals.DeferralsOf(DailyKey).Should().Be(0);

        var degradation = result.Degraded.Should().ContainSingle().Subject;
        degradation.UnsuppliedInputs.Should().Contain(ReportInput.OpenPositions);
        degradation.RetriesExhausted.Should().BeFalse();
        result.Generated.Should().ContainSingle();
        rig.Notifier.Notices.Should().ContainSingle()
            .Which.Summary.Should().Contain(ReportInputs.Label(ReportInput.OpenPositions));
        rig.Risk.Requests.Should().ContainSingle().Which.Authorization.Should().Be("Bearer T");
    }

    // ---- 種別が使わない入力は数えない ---------------------------------------------------------------

    [Fact]
    public async Task 週報は建玉を描かないので_建玉の欠落で見送らず_未供給にも数えない()
    {
        // 対照: トークンが取れていれば、金曜の閉場後は日報と週報の両方が生成される。
        var healthy = new Rig { Now = FriAfterClose };
        (await healthy.RunOnceAsync()).Generated.Select(r => r.Kind)
            .Should().BeEquivalentTo([ReportKind.Daily, ReportKind.Weekly]);

        var rig = new Rig { Now = FriAfterClose };
        rig.Tokens.Token = null;
        var result = await rig.RunOnceAsync();

        // 日報は建玉の一過性の失敗で見送り、週報はそのまま生成される。
        result.Deferred.Should().ContainSingle().Which.PeriodKey.Should().StartWith("daily-");
        var weekly = result.Generated.Should().ContainSingle().Subject;
        weekly.Kind.Should().Be(ReportKind.Weekly);
        // 🔴 否定形: 使わない入力の欠落を警告に混ぜない。
        weekly.UnsuppliedInputs.Should().NotContain(ReportInput.OpenPositions);
    }

    // ---- 散文（LLM） -----------------------------------------------------------------------------

    [Fact]
    public async Task LLM_ゲートウェイが一過性に落ちていれば見送り_回復すれば散文つきの報告書が出る()
    {
        var rig = new Rig { UseRealDrafter = true };
        rig.Llm.Status = HttpStatusCode.ServiceUnavailable;

        var first = await rig.RunOnceAsync();

        first.Deferred.Should().ContainSingle().Which.WaitingFor.Should().Equal(ReportInput.Narrative);
        rig.Store.Get(DailyKey).Should().BeNull();

        rig.Llm.Status = HttpStatusCode.OK;
        var second = await rig.RunOnceAsync();

        var report = second.Generated.Should().ContainSingle().Subject;
        report.UnsuppliedInputs.Should().BeEmpty();
        report.Body.Should().Contain("市況は落ち着いていた。");
        report.Body.Should().NotContain(ReportNarrativeDefaults.PlaceholderText);
    }

    [Fact]
    public async Task LLM_が認可を拒否するなら見送らず_プレースホルダ散文であることを提示に見せる()
    {
        // #866: 403（ロール未付与などの設定誤り）＝恒常。401 は一過性であり、下の「401 は見送る」で固定する。
        var rig = new Rig { UseRealDrafter = true };
        rig.Llm.Status = HttpStatusCode.Forbidden;

        var result = await rig.RunOnceAsync();

        result.Deferred.Should().BeEmpty();
        var report = result.Generated.Should().ContainSingle().Subject;
        report.UnsuppliedInputs.Should().Contain(ReportInput.Narrative);
        report.Body.Should().Contain(ReportNarrativeDefaults.PlaceholderText);
        rig.Notifier.Notices.Should().ContainSingle()
            .Which.Summary.Should().Contain(ReportInputs.Label(ReportInput.Narrative));
    }

    [Fact]
    public async Task LLM_のトークンを取得できなければ_LLM_へ送信せず見送る()
    {
        // 実測（#840）と同じ形: MSP レルム（LLM 向け）のトークンだけが取れない。台帳（AST レルム）は取れている。
        var rig = new Rig { UseRealDrafter = true };
        rig.LlmTokens.Token = null;

        var result = await rig.RunOnceAsync();

        result.Deferred.Should().ContainSingle().Which.WaitingFor.Should().Equal(ReportInput.Narrative);
        // 🔴 否定形: 認証なしで LLM へ送っていない（本変更前は 401 を踏んでからプレースホルダへ倒れていた）。
        rig.Llm.Requests.Should().BeEmpty();
        rig.Store.Get(DailyKey).Should().BeNull();
        // 台帳へはトークン付きで送れている。
        rig.Risk.Requests.Should().ContainSingle().Which.Authorization.Should().Be("Bearer T");
    }

    // ---- #866 R1: 上流の 401 は一過性（上流自身が起動直後で公開鍵を引けていない窓がある） ----------

    [Fact]
    public async Task 上流が_401_を返す間は一過性として見送り_回復すれば縮退しない報告書が出る()
    {
        // 監査の実測: 上流の JwtBearer 設定取得器（IdentityModel 8.0.1）は起動時の取得失敗にバックオフを持ち、
        // **Keycloak 回復から 24.6 秒は正しいトークンでも 401** を返す。恒常と分類すると #840 の事故が再発する。
        var rig = new Rig();
        rig.Risk.Status = HttpStatusCode.Unauthorized; // トークンは付いているが上流がまだ検証できない。

        var first = await rig.RunOnceAsync();

        first.Deferred.Should().ContainSingle().Which.Causes.Should().ContainSingle().Which.Should().Contain("401");
        // 🔴 否定形: 待てば直る 401 で縮退した報告書を出さない（本是正前はここで承認待ちに並んでいた）。
        first.Generated.Should().BeEmpty();
        rig.Store.Get(DailyKey).Should().BeNull();

        rig.Risk.Status = HttpStatusCode.OK; // 上流の検証器が回復した。
        var second = await rig.RunOnceAsync();

        second.Generated.Should().ContainSingle().Which.UnsuppliedInputs.Should().BeEmpty();
        second.Degraded.Should().BeEmpty();
    }

    // ---- #866 B1: 見送りが生成窓の終端を跨ぐなら見送らない ------------------------------------------

    [Fact]
    public async Task 月報は_次の試行時刻に生成窓が閉じているなら見送らず_その巡回で縮退版を出す()
    {
        // 2026-07-31（金・月末最終営業日）23:59:45 JST。次の試行（+30 秒）は 08-01 00:00:15 JST であり、
        // 月報 monthly-2026-07 はもう Due に現れない＝見送ると**二度と生成されない**。
        var rig = new Rig { UseRealDrafter = true, Now = MonthEnd235945 };
        rig.LlmTokens.Token = null; // 散文（全種別が使う入力）が一過性に落ちている。

        var result = await rig.RunOnceAsync();

        var degradation = result.Degraded.Should().ContainSingle().Subject;
        degradation.PeriodKey.Should().Be(MonthlyKey);
        degradation.WindowClosing.Should().BeTrue();
        // 上限を使い切ったのではない（原因が違うので常駐の文言も分ける）。
        degradation.RetriesExhausted.Should().BeFalse();
        degradation.UnsuppliedInputs.Should().Contain(ReportInput.Narrative);
        // 縮退版でも保存・提示されている（無音で消えない）。
        rig.Store.Get(MonthlyKey).Should().NotBeNull();
        rig.Store.GetReview(MonthlyKey)!.State.Should().Be(ReviewState.PendingApproval);
        rig.Notifier.Notices.Should().ContainSingle()
            .Which.Summary.Should().Contain(ReportSummary.UnsuppliedWarningPrefix);
        // 日報・週報は窓が残っているので従来どおり見送る（窓の判定は期間ごとに行う）。
        result.Deferred.Select(d => d.PeriodKey).Should().BeEquivalentTo([DailyJul31Key, WeeklyW31Key]);
        // 出した期間の見送り回数は残らない。
        rig.Deferrals.DeferralsOf(MonthlyKey).Should().Be(0);
    }

    [Fact]
    public async Task 週報は_次の試行時刻に_ISO_週が変わるなら見送らず_その巡回で縮退版を出す()
    {
        // 2026-08-02（日）23:59:45 JST。次の試行（+30 秒）は月曜＝当 ISO 週が変わり weekly-2026-W31 は Due から消える。
        var rig = new Rig { UseRealDrafter = true, Now = WeekEnd235945 };
        rig.LlmTokens.Token = null;

        var result = await rig.RunOnceAsync();

        var degradation = result.Degraded.Should().ContainSingle().Subject;
        degradation.PeriodKey.Should().Be(WeeklyW31Key);
        degradation.WindowClosing.Should().BeTrue();
        degradation.RetriesExhausted.Should().BeFalse();
        degradation.UnsuppliedInputs.Should().Contain(ReportInput.Narrative);
        rig.Store.Get(WeeklyW31Key).Should().NotBeNull();
        rig.Store.GetReview(WeeklyW31Key)!.State.Should().Be(ReviewState.PendingApproval);
        // 日報は直近営業日（金）を指したまま窓が続くので見送る。
        result.Deferred.Should().ContainSingle().Which.PeriodKey.Should().Be(DailyJul31Key);
    }

    [Fact]
    public async Task 監査の再現_月末の深夜に見送っても_窓が閉じる前に月報が出る()
    {
        // 監査の再現: 2026-07-31 23:55 JST に LLM のトークンが取れず全種別が見送りになる。
        // 常駐と同じカデンツ（見送りの待ち時間ぶん時計を進める）で回したとき、**月報は窓が閉じる前に出る**こと。
        var rig = new Rig { UseRealDrafter = true, Now = MonthEnd2355 };
        rig.LlmTokens.Token = null;
        var windowEnds = new DateTimeOffset(2026, 7, 31, 15, 0, 0, TimeSpan.Zero); // 08-01 00:00 JST

        DateTimeOffset? generatedAt = null;
        for (var cycle = 0; cycle < 10 && rig.Now < windowEnds; cycle++)
        {
            var result = await rig.RunOnceAsync();
            if (rig.Store.Get(MonthlyKey) is not null)
            {
                generatedAt = rig.Now;
                break;
            }

            result.Deferred.Should().NotBeEmpty("見送りが無いのに月報が出ていないなら、期間が黙って消えている");
            rig.Now += result.Deferred.Min(d => d.RetryAfter);
        }

        generatedAt.Should().NotBeNull(
            "月報の生成窓（当月内）が閉じる前に、縮退版でも生成・提示されなければならない"
            + "（見送ったまま窓が閉じると二度と Due にならず、警告も通知も出ないまま消える）");
        generatedAt!.Value.Should().BeBefore(windowEnds);

        var stored = rig.Store.Get(MonthlyKey)!.Report;
        stored.UnsuppliedInputs.Should().Contain(ReportInput.Narrative);
        rig.Store.GetReview(MonthlyKey)!.State.Should().Be(ReviewState.PendingApproval);
    }

    [Fact]
    public async Task 窓が閉じて対象から外れた期間の見送り回数は_巡回の終わりに捨てる()
    {
        // 23:55 JST の見送りは成立する（次の試行 23:55:30 はまだ窓の中）。その後、常駐の巡回が遅れて
        // 窓が閉じた後に回ると、月報はもう Due に現れない＝**生成による解放が起きない**。
        var rig = new Rig { UseRealDrafter = true, Now = MonthEnd2355 };
        rig.LlmTokens.Token = null;

        (await rig.RunOnceAsync()).Deferred.Select(d => d.PeriodKey).Should().Contain(MonthlyKey);
        rig.Deferrals.DeferralsOf(MonthlyKey).Should().Be(1);

        rig.Now = MonthWindowClosed; // 08-01 00:01 JST。
        var after = await rig.RunOnceAsync();

        after.Deferred.Should().NotContain(d => d.PeriodKey == MonthlyKey);
        rig.Deferrals.DeferralsOf(MonthlyKey).Should().Be(0);
    }

    // ---- #1156, IADR-0480: 中核の入力の一過性の欠落は長く見送る（規則 11 のプローブ） ------------------

    // T-10-2168, FR-06, FR-16, #1156, IADR-0480 決定 3: P1（増える側）。全 Pod の同時起動で Keycloak の準備が
    // 通常の上限（5 回 ≒ 12.5 分）に間に合わない。建玉（中核）が欠けている間は通常の上限を超えても見送り続け、
    // 回復すれば縮退しない報告書を出す。
    [Fact]
    public async Task 中核の入力が一過性に欠ける間は_通常の上限を超えても見送り続け_回復すれば縮退しない報告書が出る()
    {
        var rig = new Rig(maxDeferrals: 2, coreMaxDeferrals: 6);
        rig.Tokens.Token = null;

        var waits = new List<TimeSpan>();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var deferred = await rig.RunOnceAsync();
            // 🔴 否定形: 通常の上限（2 回）を超えた 3〜6 回目も縮退した報告書を出さない。
            deferred.Generated.Should().BeEmpty();
            deferred.Degraded.Should().BeEmpty();
            var deferral = deferred.Deferred.Should().ContainSingle().Subject;
            deferral.Attempt.Should().Be(attempt);
            deferral.MaxDeferrals.Should().Be(6);
            deferral.WaitingFor.Should().Equal(ReportInput.OpenPositions);
            waits.Add(deferral.RetryAfter);
        }

        // 待ち時間は従来どおり倍々で、巡回間隔（既定 300 秒）が上限。
        waits.Should().Equal(
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(300));

        rig.Tokens.Token = "T"; // Keycloak が立ち上がった。
        var result = await rig.RunOnceAsync();

        var report = result.Generated.Should().ContainSingle().Subject;
        report.UnsuppliedInputs.Should().BeEmpty();
        result.Degraded.Should().BeEmpty();
        report.Body.Should().Contain("AAPL");
    }

    // T-10-2169, #1156, IADR-0480 決定 3: P3（減る側・回復しない）。中核の上限に達したら縮退した報告書と警告を出す
    // （依存先が戻らないまま報告書が永久に出ない、を作らない）。
    [Fact]
    public async Task 中核の上限に達したら_縮退した報告書を出して上限到達を返す()
    {
        var rig = new Rig(maxDeferrals: 1, coreMaxDeferrals: 3);
        rig.Tokens.Token = null;

        for (var attempt = 1; attempt <= 3; attempt++)
            (await rig.RunOnceAsync()).Deferred.Should().ContainSingle().Which.Attempt.Should().Be(attempt);

        var result = await rig.RunOnceAsync();

        result.Deferred.Should().BeEmpty();
        var degradation = result.Degraded.Should().ContainSingle().Subject;
        degradation.RetriesExhausted.Should().BeTrue();
        degradation.WindowClosing.Should().BeFalse();
        degradation.UnsuppliedInputs.Should().Equal(ReportInput.OpenPositions);
        rig.Store.Get(DailyKey)!.Report.Body.Should().Contain("建玉を照会できませんでした");
    }

    // T-10-2170, #1156, IADR-0480 決定 3: P4（対照）。中核でない入力（散文の LLM）の一過性の失敗は、
    // 中核の上限を広げても従来の上限で打ち切る（中核でない入力の待ちを延ばさない）。
    [Fact]
    public async Task 中核でない入力の一過性の欠落は_中核の上限ではなく通常の上限で打ち切る()
    {
        var rig = new Rig(maxDeferrals: 2, coreMaxDeferrals: 6) { UseRealDrafter = true };
        rig.LlmTokens.Token = null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var deferral = (await rig.RunOnceAsync()).Deferred.Should().ContainSingle().Subject;
            deferral.WaitingFor.Should().Equal(ReportInput.Narrative);
            deferral.MaxDeferrals.Should().Be(2);
        }

        var result = await rig.RunOnceAsync();

        // 🔴 否定形: 3 回目は見送らない（中核の上限 6 を使っていない）。
        result.Deferred.Should().BeEmpty();
        var degradation = result.Degraded.Should().ContainSingle().Subject;
        degradation.RetriesExhausted.Should().BeTrue();
        degradation.UnsuppliedInputs.Should().Equal(ReportInput.Narrative);
    }

    // T-10-2173, #1156, IADR-0480 決定 3: P2（減る側・窓の終端）を**中核の入力で**も固定する。中核の上限を広げても、
    // 次の試行時刻に生成窓が閉じるなら待たずに縮退版を出す（#866 の規則は中核にも効く）。
    // 2026-07-09（木）15:59:45 JST ＝ 06:59:45 UTC。次の試行（+30 秒）では日報の対象が 07-09 へ移り、
    // daily-2026-07-08 は Due から消える。
    [Fact]
    public async Task 中核の入力の欠落でも_次の試行時刻に生成窓が閉じるなら見送らずに縮退版を出す()
    {
        var rig = new Rig(maxDeferrals: 5, coreMaxDeferrals: 12)
        {
            Now = new DateTimeOffset(2026, 7, 9, 6, 59, 45, TimeSpan.Zero),
        };
        rig.Tokens.Token = null;

        var result = await rig.RunOnceAsync();

        result.Deferred.Should().BeEmpty();
        var degradation = result.Degraded.Should().ContainSingle().Subject;
        degradation.PeriodKey.Should().Be(DailyKey);
        degradation.WindowClosing.Should().BeTrue();
        degradation.UnsuppliedInputs.Should().Equal(ReportInput.OpenPositions);
    }

    // T-10-2167, FR-06, FR-16, #1156, IADR-0480 決定 1: 生成器が未供給の入力と建玉を散文の文脈へ渡す
    // （生成器 → 下書きの要求 → 散文の文脈が繋がっている。部品だけの試験では結線の漏れを捕まえられない）。
    [Fact]
    public async Task 縮退して生成するとき_散文の文脈へ未供給の入力を渡し_建玉は未供給のまま渡す()
    {
        var rig = new Rig(maxDeferrals: 0);
        rig.Risk.Status = HttpStatusCode.Forbidden; // 恒常＝見送らずに縮退して生成する。

        (await rig.RunOnceAsync()).Generated.Should().ContainSingle();

        var context = rig.StubDrafter.LastContext!;
        context.UnsuppliedInputs.Should().Equal(ReportInput.OpenPositions);
        // 🔴 否定形: 照会できていない建玉を空列（＝建玉なし）として渡していない。
        context.Positions.Should().BeNull();
        ReportNarrativePromptBuilder.Build(context).Should().Contain("建玉（現在の台帳）: 未供給（取得できなかった）");
    }

    // T-10-2176, #1156, IADR-0480 決定 1: T-10-2167 の対（供給できた建玉は件数と銘柄で渡り、未供給の一覧は空）。
    [Fact]
    public async Task 供給できたときは_散文の文脈へ建玉を渡し_未供給の一覧は空()
    {
        var rig = new Rig();

        (await rig.RunOnceAsync()).Generated.Should().ContainSingle();

        var context = rig.StubDrafter.LastContext!;
        context.UnsuppliedInputs.Should().BeEmpty();
        context.Positions.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
        ReportNarrativePromptBuilder.Build(context).Should().Contain("建玉（現在の台帳）: 1 件（銘柄: AAPL）");
    }

    // T-06-058, FR-06, FR-16, #1181（独立監査 🟢2）, IADR-0493 決定 4: 期間開始時点の在庫（中核の入力）が一過性（503）に欠ける間は、
    // 中核の上限で見送り、回復すれば縮退しない報告書を出す。
    [Fact]
    public async Task 期間開始時点の在庫が一過性に欠ける間は_中核の上限で見送り_回復すれば縮退しない()
    {
        var rig = new Rig(maxDeferrals: 2, coreMaxDeferrals: 6) { UseHttpOpening = true };
        rig.Opening.Status = HttpStatusCode.ServiceUnavailable;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var deferred = await rig.RunOnceAsync();
            deferred.Generated.Should().BeEmpty("通常の上限（2 回）を超えても縮退した報告書を出さない");
            var deferral = deferred.Deferred.Should().ContainSingle().Subject;
            deferral.WaitingFor.Should().Equal(ReportInput.OpeningInventory);
            deferral.Attempt.Should().Be(attempt);
            deferral.MaxDeferrals.Should().Be(6, "中核の上限");
        }

        rig.Opening.Status = HttpStatusCode.OK;
        var result = await rig.RunOnceAsync();

        result.Generated.Should().ContainSingle().Which.UnsuppliedInputs.Should().BeEmpty();
        rig.Opening.Requests.Should().Contain(r => r.Path == "/risk-controls/opening-inventory");
    }

    // T-10-2174, FR-06, FR-16, #1156（独立監査）, IADR-0480 決定 3: **事故の実際の形**。Keycloak が未準備で
    // 建玉（中核）と OpenD 稼働率（中核でない）が**同時に**一過性に欠ける（実測は建玉＋稼働率＋運用段階＋risk-ledger）。
    // 欠けた入力に中核が 1 つでもあれば中核の上限まで見送る（「全部が中核なら」ではない）。
    [Fact]
    public async Task 中核と中核でない入力が同時に一過性に欠けても_中核の上限まで見送り続ける()
    {
        var rig = new Rig(maxDeferrals: 2, coreMaxDeferrals: 6) { UseHttpUptime = true };
        rig.Tokens.Token = null; // 全 Pod の同時起動の直後。

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var deferred = await rig.RunOnceAsync();
            // 🔴 否定形: 通常の上限（2 回）を超えた 3〜6 回目も縮退した報告書を出さない。
            deferred.Generated.Should().BeEmpty();
            var deferral = deferred.Deferred.Should().ContainSingle().Subject;
            deferral.WaitingFor.Should().Equal(ReportInput.OpenPositions, ReportInput.OpenDUptime);
            deferral.Attempt.Should().Be(attempt);
            deferral.MaxDeferrals.Should().Be(6);
        }

        rig.Store.Get(DailyKey).Should().BeNull();

        rig.Tokens.Token = "T"; // Keycloak が立ち上がった。
        var result = await rig.RunOnceAsync();

        var report = result.Generated.Should().ContainSingle().Subject;
        report.UnsuppliedInputs.Should().BeEmpty();
        result.Degraded.Should().BeEmpty();
        rig.Uptime.Requests.Should().ContainSingle().Which.Authorization.Should().Be("Bearer T");
    }

    // T-10-2175, #1156（独立監査）, IADR-0480 決定 3・決定 5: 中核かどうかは**一過性の失敗で欠けた入力だけ**で決める。
    // 建玉（中核）が 403（恒常＝待っても変わらない）で、稼働率（中核でない）だけが一過性に欠けているなら、
    // 中核の上限ではなく通常の上限で打ち切る（恒常的な失敗で報告書を長く遅らせない）。
    [Fact]
    public async Task 中核が恒常的に欠け_中核でない入力だけが一過性なら_通常の上限で打ち切る()
    {
        var rig = new Rig(maxDeferrals: 2, coreMaxDeferrals: 6) { UseHttpUptime = true };
        rig.Risk.Status = HttpStatusCode.Forbidden;
        rig.Uptime.Status = HttpStatusCode.ServiceUnavailable;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var deferral = (await rig.RunOnceAsync()).Deferred.Should().ContainSingle().Subject;
            // 待っているのは稼働率だけ（403 の建玉は待たない）。
            deferral.WaitingFor.Should().Equal(ReportInput.OpenDUptime);
            deferral.MaxDeferrals.Should().Be(2);
        }

        var result = await rig.RunOnceAsync();

        // 🔴 否定形: 3 回目は見送らない（中核の上限 6 を使っていない）。
        result.Deferred.Should().BeEmpty();
        var degradation = result.Degraded.Should().ContainSingle().Subject;
        degradation.RetriesExhausted.Should().BeTrue();
        degradation.UnsuppliedInputs.Should().Equal(ReportInput.OpenPositions, ReportInput.OpenDUptime);
    }
}
