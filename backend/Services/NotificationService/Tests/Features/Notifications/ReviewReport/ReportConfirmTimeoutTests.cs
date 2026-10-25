extern alias ReportWorker;

using System.Diagnostics;
using System.Net;
using System.Text;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.ReviewReport;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;
using ReportBudget = ReportWorker::ReportService.Features.Reports.ConfirmReport.ReportConfirmKnowledgeBudget;
using ReportFeatures = ReportWorker::ReportService.Features.Reports;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;

namespace NotificationService.Tests;

// FR-07, FR-14, ADR-0003, ADR-0068 決定6, #1385, IADR-0556: 報告書の確定が時間切れになったとき。
// - 外側（通知サービスの確定の上限。REST・gRPC）は内側（報告書サービスの KB の後処理の時間予算）＋余白より長い（T-10-2729）。
// - 時間切れでは、利用者に返す前にレビュー局面を照会して「この版で確定されたか」を確かめる（T-10-2730〜T-10-2733）。
//   この版で確定済みなら確定として返し、版番号の予約を残す（再押下は確定 API を呼ばない＝二重確定しない）。
// - アダプタは時間切れを「確定されたか分からない」の印つきで返し、照会の新しい項目（最新の確定版の版番号）を読む（T-10-2734）。
public class ReportConfirmTimeoutTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Key = "daily-2026-10-17";

    // 確定は時間切れ（OutcomeUnknown）・照会は台本どおりに返す偽物。
    private sealed class TimingOutController : IReportReviewController
    {
        public bool ConfirmTimesOut { get; set; } = true;

        public bool ConfirmFailsWithoutUnknown { get; set; }

        public Func<ReportReviewResult> Review { get; set; } = () => new ReportReviewResult(true, 4, "版 4");

        public int ConfirmCalls { get; private set; }

        public int ReviewCalls { get; private set; }

        public Task<ReportReviewResult> GetReviewAsync(string periodKey, CancellationToken cancellationToken = default)
        {
            ReviewCalls++;
            return Task.FromResult(Review());
        }

        public Task<ReportConfirmResult> ConfirmAsync(
            string periodKey, int expectedVersion, string onBehalfOf, CancellationToken cancellationToken = default)
        {
            ConfirmCalls++;
            if (ConfirmFailsWithoutUnknown)
                return Task.FromResult(new ReportConfirmResult(false, false, "報告書の確定に失敗しました（HTTP 503）"));

            return Task.FromResult(ConfirmTimesOut
                ? new ReportConfirmResult(false, false, HttpReportReviewController.TimedOutMessage("報告書の確定", write: true), OutcomeUnknown: true)
                : new ReportConfirmResult(true, true, $"報告書 {periodKey}（版 {expectedVersion}）は確定済みです（この版で確定されています）。"));
        }

        public Task<ReportReviewResult> RequestChangesAsync(string periodKey, int expectedVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("差し戻しは呼ばない");

        public Task<IReadOnlyList<string>> ListPeriodKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static DiscordBotOptions Options()
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "developer";
        return options;
    }

    private static ReportCommandHandler Handler(IReportReviewController controller, VersionedConfirmationGuard guard) =>
        new(controller, guard, Options(), NullLogger<ReportCommandHandler>.Instance);

    private static DiscordCommandContext Approve(int version) => new(Guild, Channel, OwnerUser, false, $"/report approve {Key} {version}");

    // ---- T-10-2729: 外側＞内側（全経路） ----

    [Fact]
    public void T_10_2729_確定の外側の上限はRESTとgRPCとも30秒で報告書サービスのKBの時間予算と余白より長い()
    {
        var inner = TimeSpan.FromSeconds(ReportBudget.DefaultKnowledgeBudgetSeconds);
        inner.Should().Be(TimeSpan.FromSeconds(20));
        ReportBudget.Default.Should().Be(inner);

        using var factory = new BotReadGrpcWiringTests.Factory(new() { ["Reports:Grpc"] = "http://report-service:8081" });
        _ = factory.CreateClient();
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();
        var restConfirm = clients.CreateClient("report-confirm").Timeout;
        var grpc = factory.Services.GetRequiredService<ReportsGrpcTransport>();

        restConfirm.Should().Be(TimeSpan.FromSeconds(30));
        grpc.ConfirmCalls.Timeout.Should().Be(restConfirm, "gRPC の deadline の既定は REST の HttpClient.Timeout と同値");
        grpc.ConfirmCalls.MaxAttempts.Should().Be(1, "書き込みは再試行しない");
        // DB の遷移・通知の発行・台帳の書き込みの余白（10 秒）を残して、内側（KB の後処理の予算）より長い。
        restConfirm.Should().BeGreaterThanOrEqualTo(inner + TimeSpan.FromSeconds(10), "外側は内側（KB の時間予算）＋余白より長い");

        // 照会・差し戻し・一覧の 5 秒は変えない（入力補完の予算 #843 と連なる）。
        clients.CreateClient("report-review").Timeout.Should().Be(TimeSpan.FromSeconds(5));
        grpc.Calls.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        (ReportCommandHandler.SuggestionBudget < TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("45", 45)]
    public void T_10_2729_gRPCの確定のdeadlineは構成に従う(string? seconds, int expected)
    {
        var settings = new Dictionary<string, string> { ["Reports:Grpc"] = "http://report-service:8081" };
        if (seconds is not null)
            settings["Reports:GrpcConfirmTimeoutSeconds"] = seconds;
        using var factory = new BotReadGrpcWiringTests.Factory(settings);
        _ = factory.CreateClient();

        factory.Services.GetRequiredService<ReportsGrpcTransport>().ConfirmCalls.Timeout.Should().Be(TimeSpan.FromSeconds(expected));
    }

    // ---- T-10-2730〜T-10-2733: 時間切れの後の照会 ----

    // T-10-2730: 照会でこの版（4 → 確定版 5）の確定を確かめたら確定として返し、予約を残す。再押下は確定 API を呼ばない。
    [Fact]
    public async Task T_10_2730_時間切れでも照会でこの版の確定を確かめたら確定済みと返し再押下で二重確定しない()
    {
        var controller = new TimingOutController { Review = () => new ReportReviewResult(true, 5, "版 5", LatestConfirmedVersion: 5) };
        var guard = new VersionedConfirmationGuard();
        var handler = Handler(controller, guard);

        var first = await handler.HandleAsync(Approve(4));
        var again = await handler.HandleAsync(Approve(4));

        first.WasExecuted.Should().BeTrue();
        first.ConfirmedNow.Should().BeTrue("`/policy` の確認ボタンは入れ替え案の適用へ進む");
        first.Message.Should().Be(ReportCommandHandler.ConfirmedAfterTimeoutMessage(Key, 4)).And.NotContain("不明");
        controller.ReviewCalls.Should().Be(1);
        again.ConfirmedNow.Should().BeFalse("2 回目は窓口で吸収（確定 API を呼ばない）");
        again.Message.Should().Contain("確定済み");
        controller.ConfirmCalls.Should().Be(1, "予約を残すので確定 API は 1 回だけ");
    }

    // T-10-2731: 照会の時点で未確定（行が押した版のまま／版 n+1 のドラフトで確定版はそれより前）なら「確定できていません」。予約を解放し、
    // 押し直しは報告書サービスを呼ぶ（層 2 の版番号つき冪等が二重確定を防ぐ）。
    [Theory]
    [InlineData(4, null)]   // 初回の確定の前の下書き（確定版なし）
    [InlineData(4, 3)]      // 版 n+1 のドラフト（確定版 3 に版を足した版 4）
    public async Task T_10_2731_時間切れで照会の時点で未確定なら確定できていないと返し予約を解放する(int rowVersion, int? latestConfirmed)
    {
        var controller = new TimingOutController
        {
            Review = () => new ReportReviewResult(true, rowVersion, $"版 {rowVersion}", latestConfirmed),
        };
        var handler = Handler(controller, new VersionedConfirmationGuard());

        var first = await handler.HandleAsync(Approve(4));
        controller.ConfirmTimesOut = false;
        var retry = await handler.HandleAsync(Approve(4));

        first.ConfirmedNow.Should().BeFalse();
        first.Message.Should().Be(ReportCommandHandler.NotConfirmedAfterTimeoutMessage(Key, 4)).And.NotContain("不明");
        retry.ConfirmedNow.Should().BeTrue();
        controller.ConfirmCalls.Should().Be(2, "予約を解放したので押し直しは報告書サービスを呼ぶ");
    }

    // T-10-2732: 照会も失敗した／旧版の報告書サービスで項目が無く行の版が進んでいる（確定か改訂か見分けられない）→「不明」に確認の案内を添え、予約を解放。
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 5)]
    public async Task T_10_2732_時間切れで確定の状態を確かめられなければ不明に確認の案内を添える(bool reviewSucceeds, int rowVersion)
    {
        var controller = new TimingOutController
        {
            Review = () => reviewSucceeds
                ? new ReportReviewResult(true, rowVersion, $"版 {rowVersion}")
                : new ReportReviewResult(false, 0, "レビュー局面の照会がタイムアウトしました"),
        };
        var handler = Handler(controller, new VersionedConfirmationGuard());

        var first = await handler.HandleAsync(Approve(4));
        controller.ConfirmTimesOut = false;
        await handler.HandleAsync(Approve(4));

        first.ConfirmedNow.Should().BeFalse("確定したと騙らない");
        first.Message.Should().Contain("結果は不明").And.Contain($"/report show {Key}").And.Contain("二重には確定されません");
        controller.ConfirmCalls.Should().Be(2, "予約を解放する");
    }

    // T-10-2733: 別の版で確定済み（後の版で確定・行のいまの版が確定版）なら「この版は確定していない」。予約を解放。
    [Theory]
    [InlineData(7, 7)]   // 押した版 4 の後に版 6 が確定（確定版 7）
    [InlineData(4, 4)]   // 行は既に確定版 4（下書き 3 の確定）。古い確認ボタン（版 4）の時間切れ
    public async Task T_10_2733_時間切れで照会では別の版で確定済みなら確定していないと返す(int rowVersion, int latestConfirmed)
    {
        var controller = new TimingOutController
        {
            Review = () => new ReportReviewResult(true, rowVersion, $"版 {rowVersion}", latestConfirmed),
        };

        var result = await Handler(controller, new VersionedConfirmationGuard()).HandleAsync(Approve(4));

        result.ConfirmedNow.Should().BeFalse();
        result.Message.Should().Contain("別の版で確定済み").And.Contain($"確定後の版 {latestConfirmed}").And.Contain("版 4 は確定していません");
    }

    // 否定形: 時間切れでない失敗（不明の印なし）は照会しない（従来どおり失敗を返して予約を解放する）。
    [Fact]
    public async Task T_10_2733_時間切れでない失敗では照会しない()
    {
        var controller = new TimingOutController { ConfirmFailsWithoutUnknown = true };

        var result = await Handler(controller, new VersionedConfirmationGuard()).HandleAsync(Approve(4));

        result.ConfirmedNow.Should().BeFalse();
        result.Message.Should().Be("報告書の確定に失敗しました（HTTP 503）");
        controller.ReviewCalls.Should().Be(0);
    }

    // ---- T-10-2734: アダプタ ----

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return respond(request, cancellationToken);
        }
    }

    private static Task<HttpResponseMessage> Json(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    // REST: 確定は専用のクライアントで呼び、その上限で切れたら「不明」の印つきで返す。照会は共用のクライアントで、最新の確定版の版番号を読む。
    [Fact]
    public async Task T_10_2734_RESTの確定は専用のクライアントで呼び時間切れは不明の印つきで返す()
    {
        var reviewHandler = new RecordingHandler((_, _) => Json("""{"version":5,"latestConfirmedVersion":5}"""));
        var confirmHandler = new RecordingHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("届かない");
        });
        var controller = new HttpReportReviewController(
            new HttpClient(reviewHandler) { BaseAddress = new Uri("http://report-service"), Timeout = TimeSpan.FromSeconds(30) },
            NullLogger<HttpReportReviewController>.Instance,
            new HttpClient(confirmHandler) { BaseAddress = new Uri("http://report-service"), Timeout = TimeSpan.FromMilliseconds(200) });

        var watch = Stopwatch.StartNew();
        var confirm = await controller.ConfirmAsync(Key, 4, "developer");
        watch.Stop();
        var review = await controller.GetReviewAsync(Key);

        confirm.Should().Be(new ReportConfirmResult(false, false, "報告書の確定がタイムアウトしました（結果は不明です）", OutcomeUnknown: true));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "確定のクライアントの上限（200 ms）で切れる");
        confirmHandler.Paths.Should().Equal($"/reports/{Key}/confirm");
        reviewHandler.Paths.Should().Equal($"/reports/{Key}/review");
        (review.Succeeded, review.Version, review.LatestConfirmedVersion).Should().Be((true, 5, (int?)5));
    }

    // REST: 送り手の本物の型（ReportReviewView）の JSON から最新の確定版の版番号を読む。項目の無い旧版は null。
    [Fact]
    public async Task T_10_2734_RESTの照会は送り手の型の最新の確定版を読み旧版はnull()
    {
        var sent = System.Text.Json.JsonSerializer.Serialize(
            new ReportFeatures.ReportReviewView(Key, default, 5, [], LatestConfirmedVersion: 5),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var current = new HttpReportReviewController(
            new HttpClient(new RecordingHandler((_, _) => Json(sent))) { BaseAddress = new Uri("http://report-service") },
            NullLogger<HttpReportReviewController>.Instance);
        var legacy = new HttpReportReviewController(
            new HttpClient(new RecordingHandler((_, _) => Json("""{"version":5}"""))) { BaseAddress = new Uri("http://report-service") },
            NullLogger<HttpReportReviewController>.Instance);

        (await current.GetReviewAsync(Key)).LatestConfirmedVersion.Should().Be(5);
        (await legacy.GetReviewAsync(Key)).LatestConfirmedVersion.Should().BeNull();
    }

    private sealed class FixedToken(string? token) : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    // gRPC: 確定は専用の deadline で切れて「不明」の印つき。照会は送り手の写し（ToProto）の最新の確定版の版番号を読む。
    [Fact]
    public async Task T_10_2734_gRPCの確定は専用のdeadlineで切れ照会は最新の確定版を読む()
    {
        var behavior = new BotReadStubBehavior
        {
            Confirm = BotReadStubBehavior.Hangs<ReportProto.ReportConfirmationResponse>(),
            Review = BotReadStubBehavior.Returns(ReportFeatures.ReportOwnerReadWireMapping.ToProto(
                new ReportFeatures.ReportReviewView(Key, default, 5, [], LatestConfirmedVersion: 5))),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var transport = new ReportsGrpcTransport(
            GrpcClientExtensions.CreateAiStockTradingChannel(host.Address, new FixedToken("bot-owner-token")),
            TimeSpan.FromSeconds(30), 1, NullLogger<ReportsGrpcTransport>.Instance, confirmTimeout: TimeSpan.FromMilliseconds(500));
        var controller = new GrpcReportReviewController(transport, NullLogger<GrpcReportReviewController>.Instance);

        var watch = Stopwatch.StartNew();
        var confirm = await controller.ConfirmAsync(Key, 4, "developer");
        watch.Stop();
        var review = await controller.GetReviewAsync(Key);

        confirm.Should().Be(new ReportConfirmResult(false, false, "報告書の確定がタイムアウトしました（結果は不明です）", OutcomeUnknown: true));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "照会の 30 秒ではなく確定の deadline（500 ms）で切れる");
        (review.Succeeded, review.Version, review.LatestConfirmedVersion).Should().Be((true, 5, (int?)5));

        var legacy = ReportFeatures.ReportOwnerReadWireMapping.ToProto(new ReportFeatures.ReportReviewView(Key, default, 3, []));
        legacy.HasLatestConfirmedVersion.Should().BeFalse("確定版が無ければ載せない（C# の null は設定しない）");
    }
}
