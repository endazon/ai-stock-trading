using System.Security.Claims;
using AwesomeAssertions;
using ReportService.Features.Reports.ConfirmReport;
using Xunit;

namespace ReportService.Tests;

// FR-09, UC-03, ADR-0003, IADR-0240 決定11, #774: 確定者の解決（純関数）の全数テスト。
// 「本文の名前を信じてよいのは、信頼するクライアントのトークンだけ」を否定形まで含めて固定する。
public class ConfirmingActorResolverTests
{
    private const string OwnerClientId = "ai-stock-trading-owner";

    private static readonly IReadOnlySet<string> Trusted = new HashSet<string>(StringComparer.Ordinal) { OwnerClientId };
    private static readonly IReadOnlySet<string> NoneTrusted = new HashSet<string>(StringComparer.Ordinal);

    private static ClaimsPrincipal Principal(string? name, string? azp)
    {
        var claims = new List<Claim>();
        if (name is not null) claims.Add(new Claim(ClaimTypes.Name, name));
        if (azp is not null) claims.Add(new Claim(ConfirmingActorResolver.AuthorizedPartyClaim, azp));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
    }

    [Fact]
    public void 信頼クライアントの代理指定は確定者になり_クライアントが認可の主体として残る()
    {
        var actor = ConfirmingActorResolver.Resolve(Principal(null, OwnerClientId), "developer", Trusted);

        actor.Should().Be(new ConfirmingActor("developer", OwnerClientId, IgnoredOnBehalfOf: false, Rejected: false));
    }

    // 実 Keycloak の service account は preferred_username（service-account-<clientId>）を持つこともある。
    // その場合も代理指定が優先する（service account 名は人ではない）。
    [Fact]
    public void 信頼クライアントが名前クレームを持っていても_代理指定が確定者になる()
    {
        var actor = ConfirmingActorResolver.Resolve(
            Principal("service-account-ai-stock-trading-owner", OwnerClientId), "developer", Trusted);

        actor.Actor.Should().Be("developer");
        actor.AuthorizedBy.Should().Be(OwnerClientId);
    }

    // 🔴 なりすましの否定形。
    [Theory]
    [InlineData("ai-stock-trading-dev")]      // 利用者が SPA 経由で得たトークン
    [InlineData("some-other-client")]         // 一覧に無い機密クライアント
    [InlineData("AI-STOCK-TRADING-OWNER")]    // 大小文字違い（クライアント ID は厳密一致）
    [InlineData("ai-stock-trading-owner ")]   // 前後の空白違い
    [InlineData(null)]                        // azp 自体が無い
    public void 信頼クライアント以外の代理指定は無視され_確定者はトークンの本人のまま(string? azp)
    {
        var actor = ConfirmingActorResolver.Resolve(Principal("owner", azp), "someone-else", Trusted);

        actor.Actor.Should().Be("owner");
        actor.AuthorizedBy.Should().BeNull();
        actor.IgnoredOnBehalfOf.Should().BeTrue("無視したことを呼び出し側がログに残せる");
        actor.Rejected.Should().BeFalse("確定そのものは本人の権限で通す");
    }

    // 🔴 名前クレームに信頼クライアントの ID を入れても代理は成立しない（見るのは azp だけ）。
    [Fact]
    public void 名前が信頼クライアント_ID_でも_azp_が違えば代理指定は無視される()
    {
        var actor = ConfirmingActorResolver.Resolve(
            Principal(OwnerClientId, "ai-stock-trading-dev"), "someone-else", Trusted);

        actor.Actor.Should().Be(OwnerClientId);
        actor.AuthorizedBy.Should().BeNull();
        actor.IgnoredOnBehalfOf.Should().BeTrue();
    }

    [Fact]
    public void 信頼一覧が空なら_owner_クライアントの代理指定も無視する()
    {
        var actor = ConfirmingActorResolver.Resolve(Principal(null, OwnerClientId), "developer", NoneTrusted);

        actor.Actor.Should().Be($"client:{OwnerClientId}");
        actor.AuthorizedBy.Should().BeNull();
        actor.IgnoredOnBehalfOf.Should().BeTrue();
    }

    [Theory]
    [InlineData("developer\n")]   // 末尾 LF（`$` なら通る。`\z` で落とす）
    [InlineData("\ndeveloper")]
    [InlineData("dev eloper")]
    [InlineData("<@everyone>")]
    [InlineData("**developer**")]
    [InlineData("開発者")]
    [InlineData("")]
    [InlineData(" ")]
    public void 信頼クライアントでも値域外の代理指定は拒否する(string onBehalfOf)
    {
        var actor = ConfirmingActorResolver.Resolve(Principal(null, OwnerClientId), onBehalfOf, Trusted);

        actor.Rejected.Should().BeTrue();
        actor.AuthorizedBy.Should().BeNull();
    }

    [Fact]
    public void 六十四文字までは通し_六十五文字は拒否する()
    {
        ConfirmingActorResolver.Resolve(Principal(null, OwnerClientId), new string('a', 64), Trusted)
            .Rejected.Should().BeFalse();
        ConfirmingActorResolver.Resolve(Principal(null, OwnerClientId), new string('a', 65), Trusted)
            .Rejected.Should().BeTrue();
    }

    [Theory]
    [InlineData("developer")]
    [InlineData("first.last")]
    [InlineData("user@example.com")]
    [InlineData("user+tag_1-x")]
    public void Keycloak_利用者名として妥当な代理指定は通す(string onBehalfOf)
    {
        var actor = ConfirmingActorResolver.Resolve(Principal(null, OwnerClientId), onBehalfOf, Trusted);

        actor.Rejected.Should().BeFalse();
        actor.Actor.Should().Be(onBehalfOf);
    }

    // 操作者が取れないときの倒し方: 名前 → client:<azp> → unknown。
    [Fact]
    public void 代理指定が無ければ_名前_client_azp_unknown_の順に倒す()
    {
        ConfirmingActorResolver.Resolve(Principal("owner", "ai-stock-trading-dev"), null, Trusted)
            .Should().Be(new ConfirmingActor("owner", null, IgnoredOnBehalfOf: false, Rejected: false));

        ConfirmingActorResolver.Resolve(Principal(null, OwnerClientId), null, Trusted)
            .Should().Be(new ConfirmingActor($"client:{OwnerClientId}", null, IgnoredOnBehalfOf: false, Rejected: false));

        ConfirmingActorResolver.Resolve(Principal(null, null), null, Trusted)
            .Should().Be(new ConfirmingActor("unknown", null, IgnoredOnBehalfOf: false, Rejected: false));
    }

    // 前提の固定: JwtBearer は受信クレームを既定の写像表で改名する（例: sub → nameidentifier）。
    // **`azp` がその表に載っていない**から、解決器は `azp` という名前のまま読める。将来の版で表に載ったら
    // 代理確定が黙って不成立（確定者が client:… / unknown へ落ちる）になるため、ここで赤にする。
    [Fact]
    public void JwtBearer_の受信クレーム写像は_azp_を改名しない()
    {
        Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler.DefaultInboundClaimTypeMap
            .Should().NotContainKey(ConfirmingActorResolver.AuthorizedPartyClaim);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" , ,")]
    public void 信頼一覧の構成が空なら誰も信じない(string? configured)
    {
        ConfirmingActorResolver.ParseTrustedClientIds(configured).Should().BeEmpty();
    }

    [Fact]
    public void 信頼一覧の構成はカンマ区切りで前後の空白を落とす()
    {
        ConfirmingActorResolver.ParseTrustedClientIds(" ai-stock-trading-owner , other-bot ")
            .Should().BeEquivalentTo(["ai-stock-trading-owner", "other-bot"]);
    }
}
