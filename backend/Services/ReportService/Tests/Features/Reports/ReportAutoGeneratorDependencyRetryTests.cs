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
        """{"text":"市況は落ち着いていた。","model":"claude-sonnet-5","inputTokens":1,"outputTokens":1,"sent":true}""";

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

        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
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
        ITradeRationaleSource, IOpenDUptimeSource, IPeriodEndFxRateSource, IStageProgressSource
    {
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
        public InMemoryReportStore Store { get; } = new();
        public RecordingNotifier Notifier { get; } = new();
        public CountingDrafter StubDrafter { get; } = new();
        public ReportGenerationDeferralTracker Deferrals { get; }
        public DateTimeOffset Now { get; set; } = WedAfterClose;

        /// <summary>true なら散文も本物の判定器（HttpReportNarrativeDrafter）を本物の鎖越しに使う。</summary>
        public bool UseRealDrafter { get; init; }

        public Rig(int maxDeferrals = ReportDeferralSettings.DefaultMaxDeferrals) =>
            Deferrals = new ReportGenerationDeferralTracker(new ReportDeferralSettings { MaxDeferrals = maxDeferrals });

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
                uptimeSource: supplied,
                stageProgressSource: supplied,
                periodEndFxRateSource: supplied,
                dependencyProbe: Probe,
                deferrals: Deferrals);

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
}
