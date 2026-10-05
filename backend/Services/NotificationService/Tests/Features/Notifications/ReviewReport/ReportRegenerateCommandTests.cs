extern alias ReportWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.ReviewReport;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;
using ReportFeatures = ReportWorker::ReportService.Features.Reports;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;
using ReportRegenerate = ReportWorker::ReportService.Features.Reports.RegenerateReport;

namespace NotificationService.Tests;

// T-10-2274, T-10-2275, FR-06, FR-14, UC-03〜05, 計画 ADR-0052 決定 1・4, #1156, IADR-0491 決定 1: Discord の `/report regenerate <periodKey>`。
// 解析（版番号を取らない・書式外は Unknown）・所有者の門（多層認証を通らない要求は報告書サービスを呼ばない）・操作者を代理の利用者として運ぶ・
// REST と gRPC の結果の等価（送り手の本物の型と写し）・断った結果と「不明」（届いたか分からない＝作り直したかもしれない）の区別。
public class ReportRegenerateCommandTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Key = "daily-2026-10-02";
    private static readonly JsonSerializerOptions ReportWire = new(JsonSerializerDefaults.Web);

    private sealed class RecordingRegenerations(ReportRegenerationCommandOutcome outcome) : IReportRegenerationController
    {
        public List<(string PeriodKey, string OnBehalfOf)> Calls { get; } = [];

        public Task<ReportRegenerationCommandOutcome> RegenerateAsync(
            string periodKey, string onBehalfOf, CancellationToken cancellationToken = default)
        {
            Calls.Add((periodKey, onBehalfOf));
            return Task.FromResult(outcome);
        }
    }

    private sealed class NoReview : IReportReviewController
    {
        public int Calls { get; private set; }

        public Task<ReportReviewResult> GetReviewAsync(string periodKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ReportReviewResult(true, 1, "x"));
        }

        public Task<ReportConfirmResult> ConfirmAsync(string periodKey, int expectedVersion, string onBehalfOf, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ReportConfirmResult(true, true, "x"));
        }

        public Task<ReportReviewResult> RequestChangesAsync(string periodKey, int expectedVersion, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ReportReviewResult(true, 1, "x"));
        }

        public Task<IReadOnlyList<string>> ListPeriodKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static DiscordBotOptions FullyConfigured()
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static ReportCommandHandler Handler(NoReview review, IReportRegenerationController? regenerations) =>
        new(review, new VersionedConfirmationGuard(), FullyConfigured(), NullLogger<ReportCommandHandler>.Instance, regenerations);

    // ---- 解析 ----

    // T-10-2274, 計画 ADR-0052 決定 1: `/report regenerate <periodKey>` だけを作り直しと解釈する（会話キーは原文の大小文字のまま）。
    // 版番号を添えた形・会話キーの無い形・書式外の会話キーは Unknown（誤起動させない）。
    [Theory]
    [InlineData("/report regenerate daily-2026-10-02", BotCommandKind.ReportRegenerate)]
    [InlineData("report regenerate weekly-2026-W40", BotCommandKind.ReportRegenerate)]
    [InlineData("/report regenerate daily-2026-10-02 3", BotCommandKind.Unknown)]
    [InlineData("/report regenerate", BotCommandKind.Unknown)]
    [InlineData("/report regenerate ../etc", BotCommandKind.Unknown)]
    [InlineData("/report regenerate-all daily-2026-10-02", BotCommandKind.Unknown)]
    public void 作り直しの解析は会話キー1つだけを取る(string raw, BotCommandKind expected)
    {
        var command = BotCommandParser.Parse(raw);

        command.Kind.Should().Be(expected);
        if (expected == BotCommandKind.ReportRegenerate)
            command.PeriodKey.Should().Be(raw.Split(' ')[2]);
    }

    // ---- 所有者の門・操作者 ----

    // T-10-2274, FR-14, 計画 ADR-0052 決定 1: 所有者（多層認証を通った利用者）だけが作り直せる。報告書サービスへは多層認証が解決した
    // 利用者（Keycloak の利用者名）を代理の利用者として運ぶ（Bot のトークンは人を表さない）。
    [Fact]
    public async Task 所有者の作り直しは解決した利用者を添えて報告書サービスを呼ぶ()
    {
        var regenerations = new RecordingRegenerations(new ReportRegenerationCommandOutcome(true, false, "版 3 として承認待ちにしました", 3));
        var review = new NoReview();

        var result = await Handler(review, regenerations)
            .HandleAsync(new DiscordCommandContext(Guild, Channel, OwnerUser, false, $"/report regenerate {Key}"));

        result.WasExecuted.Should().BeTrue();
        result.Version.Should().Be(3);
        result.Message.Should().Be("版 3 として承認待ちにしました");
        regenerations.Calls.Should().Equal((Key, "endazon"));
        review.Calls.Should().Be(0, "作り直しは確定・差し戻しを呼ばない");
    }

    // T-10-2274（否定形）, 計画 ADR-0052 決定 1: 多層認証を通らない要求（許可外の利用者・DM・別チャンネル）は報告書サービスを呼ばない。
    [Theory]
    [InlineData("intruder", false, Channel)]
    [InlineData(OwnerUser, true, Channel)]
    [InlineData(OwnerUser, false, "other-channel")]
    public async Task 所有者以外の作り直しは報告書サービスを呼ばない(string user, bool isDm, string channel)
    {
        var regenerations = new RecordingRegenerations(new ReportRegenerationCommandOutcome(true, false, "x", 3));

        var result = await Handler(new NoReview(), regenerations)
            .HandleAsync(new DiscordCommandContext(isDm ? null : Guild, channel, user, isDm, $"/report regenerate {Key}"));

        result.IsDenied.Should().BeTrue();
        result.WasExecuted.Should().BeFalse();
        regenerations.Calls.Should().BeEmpty();
    }

    // T-10-2274: 断った結果（中核の入力・上限）はそのまま失敗として伝え、作り直しの窓口が構成されていなければ呼ばずにその旨を返す。
    [Fact]
    public async Task 断った結果と未構成は失敗として伝える()
    {
        var refused = new RecordingRegenerations(new ReportRegenerationCommandOutcome(false, false, "中核の入力（建玉）をいま取得できない"));

        var rejected = await Handler(new NoReview(), refused)
            .HandleAsync(new DiscordCommandContext(Guild, Channel, OwnerUser, false, $"/report regenerate {Key}"));
        var unconfigured = await Handler(new NoReview(), null)
            .HandleAsync(new DiscordCommandContext(Guild, Channel, OwnerUser, false, $"/report regenerate {Key}"));

        rejected.WasExecuted.Should().BeFalse();
        rejected.Message.Should().Contain("中核の入力");
        unconfigured.WasExecuted.Should().BeFalse();
        unconfigured.IsDenied.Should().BeFalse();
        unconfigured.Message.Should().Contain("使えません");
    }

    // ---- REST と gRPC（T-10-2275） ----

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request);
        }
    }

    private static (HttpReportRegenerationController Controller, StubHandler Handler) Rest(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        return (new HttpReportRegenerationController(
            new HttpClient(handler) { BaseAddress = new Uri("http://report") }, NullLogger<HttpReportRegenerationController>.Instance), handler);
    }

    private sealed class FixedToken(string? token) : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private static GrpcReportRegenerationController Grpc(BotReadGrpcStubHost host, TimeSpan? timeout = null) =>
        new(new ReportsGrpcTransport(
                GrpcClientExtensions.CreateAiStockTradingChannel(host.Address, new FixedToken("bot-owner-token")),
                TimeSpan.FromSeconds(5), 1, NullLogger<ReportsGrpcTransport>.Instance, regenerationTimeout: timeout),
            NullLogger<GrpcReportRegenerationController>.Instance);

    // 送り手（報告書サービス）の本物の応答。
    private static readonly ReportRegenerate.ReportRegenerationResponse Sent = new(
        Key, 2, 3, true, "報告書 daily-2026-10-02 を作り直し、版 3 として承認待ちにしました。", ["建玉"], ["建玉"]);

    // T-10-2275, NFR, 計画 ADR-0052 決定 1: 成功は REST（送り手の JSON）と gRPC（送り手の写し）で同じ結果。要求は会話キーの経路と代理の利用者を運ぶ。
    [Fact]
    public async Task 作り直しの成功は_REST_と_gRPC_で同じ結果()
    {
        var behavior = new BotReadStubBehavior
        {
            Regenerate = BotReadStubBehavior.Returns(ReportFeatures.ReportWriteWireMapping.ToProto(Sent)),
        };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var (rest, handler) = Rest(HttpStatusCode.OK, JsonSerializer.Serialize(Sent, ReportWire));

        var viaRest = await rest.RegenerateAsync(Key, "endazon");
        var viaGrpc = await Grpc(host).RegenerateAsync(Key, "endazon");

        viaRest.Should().Be(new ReportRegenerationCommandOutcome(true, false, Sent.Message, 3));
        viaGrpc.Should().Be(viaRest);
        handler.Requests.Single().Path.Should().Be($"/reports/{Key}/regenerate");
        handler.Requests.Single().Body.Should().Contain("\"onBehalfOf\":\"endazon\"");
        var sentRequest = behavior.Requests.Select(r => r.Request).OfType<ReportProto.ReportRegenerationRequest>().Single();
        (sentRequest.PeriodKey, sentRequest.OnBehalfOf).Should().Be((Key, "endazon"));
    }

    // T-10-2275, 計画 ADR-0052 決定 4: 提供側が断った（REST 422 / gRPC FAILED_PRECONDITION・REST 429 / gRPC RESOURCE_EXHAUSTED）＝作り直していない。
    // 提供側の説明（理由）をそのまま見せ、REST と gRPC で同じ結果。
    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, StatusCode.FailedPrecondition)]
    [InlineData(HttpStatusCode.TooManyRequests, StatusCode.ResourceExhausted)]
    [InlineData(HttpStatusCode.Conflict, StatusCode.Aborted)]
    public async Task 断られた作り直しは理由を見せ_REST_と_gRPC_で同じ結果(HttpStatusCode http, StatusCode grpc)
    {
        const string Why = "中核の入力（手動売買の取り込み）をいま取得できないため、作り直しませんでした。";
        var behavior = new BotReadStubBehavior { Regenerate = BotReadStubBehavior.Fails<ReportProto.ReportRegenerationReply>(grpc, Why) };
        await using var host = await BotReadGrpcStubHost.StartAsync(behavior);
        var (rest, _) = Rest(http, JsonSerializer.Serialize(new { error = Why }));

        var viaRest = await rest.RegenerateAsync(Key, "endazon");
        var viaGrpc = await Grpc(host).RegenerateAsync(Key, "endazon");

        viaRest.Should().Be(new ReportRegenerationCommandOutcome(false, false, Why));
        viaGrpc.Should().Be(viaRest);
    }

    // T-10-2275, IADR-0491 決定 1: 届いたか分からない（REST のタイムアウト・gRPC の UNAVAILABLE / DEADLINE_EXCEEDED）＝**不明**。
    // 「失敗」と言わず /report show へ誘導する（作り直されたかもしれない）。**再試行しない**（冪等でない）。
    [Fact]
    public async Task 届いたか分からない作り直しは不明として伝え再試行しない()
    {
        var unavailable = new BotReadStubBehavior { Regenerate = BotReadStubBehavior.Fails<ReportProto.ReportRegenerationReply>(StatusCode.Unavailable) };
        await using var unavailableHost = await BotReadGrpcStubHost.StartAsync(unavailable);
        var hanging = new BotReadStubBehavior { Regenerate = BotReadStubBehavior.Hangs<ReportProto.ReportRegenerationReply>() };
        await using var hangingHost = await BotReadGrpcStubHost.StartAsync(hanging);
        var timeoutRest = new HttpReportRegenerationController(
            new HttpClient(new StubHandler(_ => throw new TaskCanceledException("timeout"))) { BaseAddress = new Uri("http://report") },
            NullLogger<HttpReportRegenerationController>.Instance);

        var unknown = new ReportRegenerationCommandOutcome(false, true, ReportRegenerationCommandOutcome.UnknownMessage);
        (await timeoutRest.RegenerateAsync(Key, "endazon")).Should().Be(unknown);
        (await Grpc(unavailableHost).RegenerateAsync(Key, "endazon")).Should().Be(unknown);
        (await Grpc(hangingHost, TimeSpan.FromMilliseconds(300)).RegenerateAsync(Key, "endazon")).Should().Be(unknown);
        unavailable.Calls("RegenerateReport").Should().Be(1, "冪等でない作り直しは再試行しない");
        hanging.Calls("RegenerateReport").Should().Be(1);
    }

    // T-10-2275: 200 でも本文を解釈できない（版が無い）なら、作り直された可能性があるので不明として伝える。
    [Fact]
    public async Task 解釈できない成功応答は不明として伝える()
    {
        var (rest, _) = Rest(HttpStatusCode.OK, "{\"periodKey\":\"daily-2026-10-02\"}");

        var outcome = await rest.RegenerateAsync(Key, "endazon");

        outcome.Succeeded.Should().BeFalse();
        outcome.OutcomeUnknown.Should().BeTrue();
        outcome.Message.Should().Contain("/report show");
    }
}
