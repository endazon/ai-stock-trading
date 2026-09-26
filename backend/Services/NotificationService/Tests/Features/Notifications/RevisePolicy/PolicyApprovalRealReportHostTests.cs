extern alias ReportWorker;

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.ReviewReport;
using NotificationService.Features.Notifications.RevisePolicy;
using NotificationService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;
using ReportDb = ReportWorker::ReportService.Infrastructure.Persistence;
using ReportProgram = ReportWorker::Program;

namespace NotificationService.Tests;

// FR-13, FR-14, FR-07, ADR-0042 決定 1, #1025, IADR-0433 決定 7（PR #1027 の監査 H1・M1。T-10-1421・T-10-1422）:
// **本物の報告書サービス（Program.cs の組み立て・InMemory DB）** に、本物の通知側のアダプタとハンドラを繋ぎ、Bot の再起動
// （窓口の版番号ガードが空）を挟んだ確認ボタンの流れを通す。市場監視だけは偽物（実クラスタに触れない）。
public class PolicyApprovalRealReportHostTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Key = "daily-2026-09-10";

    private static readonly IReadOnlyList<WatchlistSnapshotItemView> Snapshot = [new("AAPL", "UnitedStates")];

    private static DiscordBotOptions Options()
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel, KillSwitchConfirmationPhrase = "STOP TRADING" };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "developer";
        return options;
    }

    private static DiscordCommandContext Press(int version) =>
        new(Guild, Channel, OwnerUser, false, $"/policy approve {Key} {version}");

    // Bot の再起動を模す: 窓口の版番号ガード（プロセス内）も含めてすべて新しく作る。
    private static (PolicyApprovalCommandHandler Handler, FakeWatchlistController Watchlist) NewBot(HttpClient report)
    {
        var watchlist = new FakeWatchlistController
        {
            ApplyOutcome = new WatchlistApplyOutcome(
                WatchlistApplyStatus.Applied, [new WatchlistApplyItemView("add", "NVDA", true, null)], null, "適用しました"),
        };
        var handler = new PolicyApprovalCommandHandler(
            new ReportCommandHandler(
                new HttpReportReviewController(report, NullLogger<HttpReportReviewController>.Instance),
                new VersionedConfirmationGuard(), Options(), NullLogger<ReportCommandHandler>.Instance),
            new HttpPolicyRevisionController(report, NullLogger<HttpPolicyRevisionController>.Instance),
            watchlist, Options(), NullLogger<PolicyApprovalCommandHandler>.Instance);
        return (handler, watchlist);
    }

    private static async Task<HttpClient> StartAsync(ReportHost host)
    {
        var client = host.CreateClient();
        (await client.PutAsync($"/reports/{Key}", Json(new
        {
            Kind = "Daily",
            PeriodStart = "2026-09-10",
            BasedOn = (string?)null,
            AssumptionsVersion = 1,
            PolicySummary = "元の方針",
            ExpectedVersion = 0,
        }))).StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task ReviseAsync(HttpClient report, string instruction)
    {
        var outcome = await new HttpPolicyRevisionController(report, NullLogger<HttpPolicyRevisionController>.Instance)
            .ReviseAsync(Key, instruction, "developer", Snapshot);
        outcome.Succeeded.Should().BeTrue(outcome.Message);
    }

    // T-10-1421（監査 H1 の再現そのもの）: 版 2（案 A）→ 版 3（案 B）→ 版 3 を確定・適用 → **Bot の再起動** → 古い版 2 のボタン。
    // 「（版 2）を確定しました」と言わず、案 A を照会も適用もしない。
    [Fact]
    public async Task 再起動の後に古い版のボタンを押しても確定済みの別の版の案は適用しない()
    {
        await using var host = new ReportHost();
        var report = await StartAsync(host);
        await ReviseAsync(report, "案 A");
        await ReviseAsync(report, "案 B");

        var (bot1, watchlist1) = NewBot(report);
        var v3 = await bot1.HandleAsync(Press(3));
        (v3.ConfirmedNow, v3.WatchlistApplyStatus).Should().Be((true, WatchlistApplyStatus.Applied));
        watchlist1.Applies.Should().ContainSingle();

        var (restarted, watchlist2) = NewBot(report);
        var v2 = await restarted.HandleAsync(Press(2));

        v2.ConfirmedNow.Should().BeFalse();
        v2.Message.Should().NotContain("（版 2）を確定しました").And.Contain("別の版で確定済み").And.Contain("適用していません");
        watchlist2.Applies.Should().BeEmpty("確定されていない版の案を適用しない");
        watchlist2.Gets.Should().Be(0);
    }

    // T-10-1422（監査 M1・決定 7）: 確定の後・適用の前に Bot が落ちた（確定だけ済んだ）状態から、再起動の後に**同じ版の**ボタンを押し直すと
    // 冪等な再確定を「この版で確定済み」と確かめて入れ替えを適用し、内訳を記録する。もう一度押しても二重に適用しない。
    // 落ちている間も、台帳には「この版で確定された」時刻が残っている（適用の内訳は無い）。
    [Fact]
    public async Task 確定だけ済んで落ちた後は同じ版の押し直しで適用を回復する()
    {
        await using var host = new ReportHost();
        var report = await StartAsync(host);
        await ReviseAsync(report, "案 A");

        var confirmOnly = await new HttpReportReviewController(report, NullLogger<HttpReportReviewController>.Instance)
            .ConfirmAsync(Key, 2, "developer");
        confirmOnly.Confirmed.Should().BeTrue();
        using (var scope = host.Services.CreateScope())
        {
            var attempt = scope.ServiceProvider.GetRequiredService<ReportDb.ReportDbContext>().PolicyRevisionAttempts.Single();
            (attempt.ProposalConfirmedAt is not null, attempt.WatchlistAppliedAt is null)
                .Should().Be((true, true), "確定されたが適用を試みていないことが台帳で見える");
        }

        var (restarted, watchlist) = NewBot(report);
        var recovered = await restarted.HandleAsync(Press(2));

        recovered.ConfirmedNow.Should().BeTrue();
        recovered.Message.Should().Contain("確定済みです（この版で確定されています）").And.NotContain("を確定しました");
        recovered.WatchlistApplyStatus.Should().Be(WatchlistApplyStatus.Applied);
        var apply = watchlist.Applies.Should().ContainSingle().Subject;
        apply.Expected.Should().Equal(Snapshot);
        apply.Changes.Should().ContainSingle().Which.Symbol.Should().Be("NVDA");

        var (again, watchlist3) = NewBot(report);
        (await again.HandleAsync(Press(2))).Message.Should().Contain("既に適用の記録があります");
        watchlist3.Applies.Should().BeEmpty();
    }

    // 本物の報告書サービス（Program.cs）を InMemory DB・外部送信なし・Bot の資格（信頼クライアント・名前なし）で組む。
    private sealed class ReportHost : WebApplicationFactory<ReportProgram>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
                ["Reports:DelegatedActor:TrustedClientIds"] = BotAuthHandler.ClientId,
                ["LlmGateway:BaseUrl"] = "http://llm-gateway",
            }));
            builder.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<ReportDb.ReportDbContext>)
                        || (d.ServiceType.IsGenericType
                            && d.ServiceType.GetGenericTypeDefinition().FullName?.Contains("IDbContextOptionsConfiguration") == true
                            && d.ServiceType.GenericTypeArguments.Length == 1
                            && d.ServiceType.GenericTypeArguments[0] == typeof(ReportDb.ReportDbContext)))
                    .ToList();
                foreach (var d in toRemove)
                    services.Remove(d);
                services.AddDbContext<ReportDb.ReportDbContext>(o => o.UseInMemoryDatabase(_dbName));
                services.DisableAllExternalWolverineTransports();
                services.AddAuthentication(BotAuthHandler.Scheme)
                    .AddScheme<AuthenticationSchemeOptions, BotAuthHandler>(BotAuthHandler.Scheme, _ => { });
                services.AddHttpClient("report-llm").ConfigurePrimaryHttpMessageHandler(() => new LlmGateway());
            });
        }
    }

    // Bot の owner マップ機密クライアント（client_credentials・名前クレームなし・trading-owner）。
    private sealed class BotAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "Bot";
        public const string ClientId = "ai-stock-trading-owner";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim("azp", ClientId), new Claim(ClaimTypes.Role, "trading-owner")], Scheme, ClaimTypes.Name, ClaimTypes.Role);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
        }
    }

    // LLM ゲートウェイの偽物（入れ替え 1 件の案を返す）。実 LLM には繋がない。
    private sealed class LlmGateway : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = """{"policySummary": "押し目買いを優先する", "watchlistChanges": [{"action": "add", "symbol": "NVDA", "reason": "AI 需要"}], "rationale": "指示どおり"}""";
            var payload = JsonSerializer.Serialize(new
            {
                text,
                sent = true,
                model = "claude-sonnet-5",
                stopReason = "end_turn",
                inputTokens = 10,
                outputTokens = 20,
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
