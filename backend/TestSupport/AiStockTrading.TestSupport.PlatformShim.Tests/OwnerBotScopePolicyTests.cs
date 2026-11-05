using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR-06, FR-14, UC-06, #1554, IADR-0622: ボットの資格情報で届く REST の所有者の操作を、ボットの許可表（経路の印）とボット専用のロールで絞る。
// 本物の登録（AddAiStockTradingAuth）が作るポリシーを、印の有る／無いエンドポイントを認可の対象にして IAuthorizationService で評価する。
public class OwnerBotScopePolicyTests
{
    private const string OwnerRole = "trading-owner";
    private const string BotRole = "trading-bot";
    private const string ServiceRole = "trading-service";
    private const string BotClient = "ai-stock-trading-owner";
    private const string Audience = "ai-stock-trading-api";

    private static readonly Endpoint Marked = new(_ => Task.CompletedTask, new EndpointMetadataCollection(OwnerBotOperation.Instance), "marked");
    private static readonly Endpoint Unmarked = new(_ => Task.CompletedTask, new EndpointMetadataCollection(), "unmarked");

    private static ClaimsPrincipal Principal(string azp, params string[] roles)
    {
        var claims = new List<Claim> { new(OwnerTokenBinding.AudienceClaim, Audience), new(GrpcOwnerClientGate.AuthorizedPartyClaim, azp) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "preferred_username", ClaimTypes.Role));
    }

    private static Dictionary<string, string?> Mode(string? bot = null, string? token = null)
    {
        var settings = new Dictionary<string, string?>();
        if (bot is not null)
            settings[OwnerBotScope.ModeKey] = bot;
        if (token is not null)
            settings[OwnerTokenBinding.ModeKey] = token;
        return settings;
    }

    private static async Task<bool> Evaluate(ClaimsPrincipal user, string policy, Endpoint endpoint, IDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAuth(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        var http = new DefaultHttpContext();
        http.SetEndpoint(endpoint);
        return (await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, http, policy)).Succeeded;
    }

    public static TheoryData<string> OwnerPolicies => [AiStockTradingAuthPolicies.OwnerOnly, AiStockTradingAuthPolicies.OwnerOrService];

    // ---- ボットのクライアントの trading-owner（いまの資格情報） ----

    [Theory]
    [MemberData(nameof(OwnerPolicies))]
    public async Task ボットのトークンは許可表の経路では段に関係なく通る(string policy)
    {
        (await Evaluate(Principal(BotClient, OwnerRole), policy, Marked, Mode())).Should().BeTrue();
        (await Evaluate(Principal(BotClient, OwnerRole), policy, Marked, Mode("Enforce"))).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(OwnerPolicies))]
    public async Task ボットのトークンが許可表の外へ来たら_段1は通し_段2は拒む(string policy)
    {
        (await Evaluate(Principal(BotClient, OwnerRole), policy, Unmarked, Mode())).Should().BeTrue("段 1（既定）は記録だけ");
        (await Evaluate(Principal(BotClient, OwnerRole), policy, Unmarked, Mode("Report"))).Should().BeTrue();
        (await Evaluate(Principal(BotClient, OwnerRole), policy, Unmarked, Mode("Enforce"))).Should().BeFalse("段 2 は許可表の外を拒む");
    }

    // 利用者（BFF）のトークンは許可表を見ない（変えない）。
    [Theory]
    [MemberData(nameof(OwnerPolicies))]
    public async Task 利用者のトークンは段2でも許可表の外を通る(string policy) =>
        (await Evaluate(Principal("bff", OwnerRole), policy, Unmarked, Mode("Enforce"))).Should().BeTrue();

    // aud / azp の段（#1541）と許可表の段は別。片方だけ段 2 にしても、もう片方は段 1 のまま。
    [Fact]
    public async Task 許可表の段とトークンの宛先の段は別々に上げられる()
    {
        (await Evaluate(Principal(BotClient, OwnerRole), AiStockTradingAuthPolicies.OwnerOnly, Unmarked, Mode(token: "Enforce")))
            .Should().BeTrue("aud / azp の段 2 だけでは許可表の外を拒まない");
        (await Evaluate(Principal("bff", OwnerRole), AiStockTradingAuthPolicies.OwnerOnly, Unmarked, Mode(bot: "Enforce")))
            .Should().BeTrue();
    }

    // ---- ボット専用のロール ----

    [Theory]
    [MemberData(nameof(OwnerPolicies))]
    public async Task ボット専用のロールはボットのクライアントかつ許可表の経路でだけ通る(string policy)
    {
        (await Evaluate(Principal(BotClient, BotRole), policy, Marked, Mode())).Should().BeTrue();
        (await Evaluate(Principal(BotClient, BotRole), policy, Marked, Mode("Enforce"))).Should().BeTrue();
        (await Evaluate(Principal(BotClient, BotRole), policy, Unmarked, Mode())).Should().BeFalse("段 1 でも許可表の外は通さない");
        (await Evaluate(Principal("bff", BotRole), policy, Marked, Mode())).Should().BeFalse("ボット以外のクライアントは通さない");
    }

    [Fact]
    public async Task 利用者のトークンがボット専用のロールを持っていたら所有者のロールがあっても拒む() =>
        (await Evaluate(Principal("bff", OwnerRole, BotRole), AiStockTradingAuthPolicies.OwnerOnly, Marked, Mode())).Should().BeFalse();

    // 段 ②（ボットのクライアントが両方のロールを持つ）の間も、許可表の経路は通る・外は段に従う。
    [Fact]
    public async Task ボットが両方のロールを持つ間も許可表の判定は同じ()
    {
        (await Evaluate(Principal(BotClient, OwnerRole, BotRole), AiStockTradingAuthPolicies.OwnerOnly, Marked, Mode("Enforce"))).Should().BeTrue();
        (await Evaluate(Principal(BotClient, OwnerRole, BotRole), AiStockTradingAuthPolicies.OwnerOnly, Unmarked, Mode())).Should().BeFalse(
            "ボット専用のロールを持つトークンは、段 1 でも許可表の外を通さない");
    }

    [Fact]
    public async Task サービスの分岐は変わらない()
    {
        (await Evaluate(Principal("ai-stock-trading-svc", ServiceRole), AiStockTradingAuthPolicies.OwnerOrService, Unmarked, Mode("Enforce")))
            .Should().BeTrue();
        (await Evaluate(Principal("ai-stock-trading-svc", ServiceRole), AiStockTradingAuthPolicies.OwnerOnly, Marked, Mode()))
            .Should().BeFalse();
    }

    // ---- gRPC の所有者の面（ロールの条件を広げるだけ） ----

    [Fact]
    public void gRPC_の門はボット専用のロールもボットのクライアントなら通す()
    {
        IReadOnlyList<string> clients = [BotClient];
        GrpcOwnerClientGate.AllowsOwner(Principal(BotClient, BotRole), clients).Should().BeTrue();
        GrpcOwnerClientGate.AllowsOwner(Principal(BotClient, OwnerRole), clients).Should().BeTrue();
        GrpcOwnerClientGate.AllowsOwner(Principal("bff", BotRole), clients).Should().BeFalse();
        GrpcOwnerClientGate.AllowsOwner(Principal(BotClient), clients).Should().BeFalse();
    }

    // ---- 構成 ----

    [Fact]
    public void 段の構成は既定が段1で_知らない値は起動で止める()
    {
        OwnerBotScope.Resolve(new ConfigurationBuilder().Build()).Mode.Should().Be(OwnerTokenBindingMode.Report);
        OwnerBotScope.Resolve(new ConfigurationBuilder().AddInMemoryCollection(Mode("Enforce")).Build()).Mode
            .Should().Be(OwnerTokenBindingMode.Enforce);
        var act = () => OwnerBotScope.Resolve(new ConfigurationBuilder().AddInMemoryCollection(Mode("enforce")).Build());
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("/reports/{periodKey}/confirm", "/reports/{}/confirm")]
    [InlineData("/reports/policy-revisions/{attemptId:guid}/watchlist-apply-result", "/reports/policy-revisions/{}/watchlist-apply-result")]
    [InlineData("/reports/", "/reports")]
    [InlineData("/reports/policy-revisions/watchlist-proposal?periodKey={key}", "/reports/policy-revisions/watchlist-proposal")]
    [InlineData("/reports/{Uri.EscapeDataString(periodKey)}/regenerate", "/reports/{}/regenerate")]
    public void 経路の雛形は引数と問い合わせを落として比べる(string template, string expected) =>
        OwnerBotRoutes.Normalize(template).Should().Be(expected);

    [Fact]
    public void 許可表は重複が無い() =>
        OwnerBotRoutes.All.Select(r => (r.Method, r.NormalizedTemplate)).Should().OnlyHaveUniqueItems();
}
