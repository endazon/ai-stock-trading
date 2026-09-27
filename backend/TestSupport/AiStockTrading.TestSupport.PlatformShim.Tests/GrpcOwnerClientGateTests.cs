using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// T-10-1723, T-10-1724, NFR-06, FR-14, ADR-0047 決定 3, IADR-0448 決定 1〜3, #1067 (#753):
// east-west gRPC 面の門（`GrpcOwnerOrService`）の判定と構成。**本物の登録（AddAiStockTradingAuth）が作るポリシー**を
// `IAuthorizationService` で評価する（判定関数だけを直に叩くと、ポリシーへの結線が外れても緑のまま残る）。
public class GrpcOwnerClientGateTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";
    private const string Bot = "ai-stock-trading-owner";

    private static ClaimsPrincipal Principal(string[] roles, params string[] azps)
    {
        var claims = new List<Claim> { new("preferred_username", "someone") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(azps.Select(a => new Claim(GrpcOwnerClientGate.AuthorizedPartyClaim, a)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "preferred_username", ClaimTypes.Role));
    }

    private static async Task<bool> Evaluate(ClaimsPrincipal user, IDictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAuth(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, resource: null, AiStockTradingAuthPolicies.GrpcOwnerOrService);
        return result.Succeeded;
    }

    // ---- T-10-1723: 判定（所有者の分岐は azp を併せて求める・s2s は変えない） ----

    [Fact]
    public async Task T_10_1723_ボットのトークンは通る()
    {
        (await Evaluate(Principal([OwnerRole], Bot))).Should().BeTrue();
    }

    // 🔴 否定の試験: trading-owner を持つ人の利用者のトークン（azp が BFF・ブラウザの公開クライアント）と、あらゆる変種。
    [Theory]
    [InlineData("ai-stock-trading-dev")]       // 利用者の公開クライアント
    [InlineData("bff")]                        // BFF のセッションの利用者トークン
    [InlineData("AI-STOCK-TRADING-OWNER")]     // 大小文字
    [InlineData("Ai-stock-trading-owner")]
    [InlineData("ai-stock-trading-owner-bff")] // ボットの id を接頭辞に持つ
    [InlineData("ai-stock-trading-own")]       // ボットの id の接頭辞
    [InlineData("xai-stock-trading-owner")]    // ボットの id を接尾辞に持つ
    [InlineData(" ai-stock-trading-owner")]    // 前後の空白（トークン側は落とさない）
    [InlineData("ai-stock-trading-owner ")]
    [InlineData("ai-stock-trading-owner\n")]
    [InlineData("")]                           // 空
    [InlineData("ai-stock-trading-svc")]       // s2s のクライアントでも所有者の分岐では通さない
    public async Task T_10_1723_所有者のロールでも呼び出し元がボットでなければ拒否(string azp)
    {
        (await Evaluate(Principal([OwnerRole], azp))).Should().BeFalse($"azp='{azp}' は人の利用者または別のクライアント");
    }

    [Fact]
    public async Task T_10_1723_azpの無い所有者のトークンは拒否_名前からは復元しない()
    {
        (await Evaluate(Principal([OwnerRole]))).Should().BeFalse();

        // `service-account-<clientId>` の名前を持っていても azp が無ければ通さない（基盤の ClientIdOf の復元は採らない）。
        var named = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", $"service-account-{Bot}"), new Claim(ClaimTypes.Role, OwnerRole)],
            "Test", "preferred_username", ClaimTypes.Role));
        (await Evaluate(named)).Should().BeFalse();
    }

    // 🔴 #1067 監査の変異 M2: azp が無いとき client_id（Keycloak の client_credentials トークンが持つ）で代わりに照合しない。
    // 名前（preferred_username・ClaimTypes.Name）の `service-account-<id>` と client_id を併せ持っても、azp が無ければ拒否。
    [Fact]
    public async Task T_10_1723_azpの無い所有者のトークンはclient_idやサービスアカウント名がボットでも拒否()
    {
        static ClaimsPrincipal Without(params Claim[] extra) => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, OwnerRole), .. extra], "Test", ClaimTypes.Name, ClaimTypes.Role));

        (await Evaluate(Without(new Claim("client_id", Bot)))).Should().BeFalse("client_id は azp の代わりにしない");
        (await Evaluate(Without(new Claim("clientId", Bot)))).Should().BeFalse();
        (await Evaluate(Without(new Claim(ClaimTypes.Name, $"service-account-{Bot}")))).Should().BeFalse("名前から復元しない");
        (await Evaluate(Without(
            new Claim("client_id", Bot),
            new Claim("preferred_username", $"service-account-{Bot}"),
            new Claim(ClaimTypes.Name, $"service-account-{Bot}")))).Should().BeFalse("全部揃っても azp が無ければ拒否");

        // 対照: 同じトークンに azp＝ボットを足せば通る（拒否の理由が azp の欠落だけであることを示す）。
        (await Evaluate(Without(new Claim("client_id", Bot), new Claim(GrpcOwnerClientGate.AuthorizedPartyClaim, Bot))))
            .Should().BeTrue();
    }

    [Fact]
    public async Task T_10_1723_azpが2つ以上なら拒否()
    {
        (await Evaluate(Principal([OwnerRole], Bot, "bff"))).Should().BeFalse();
        (await Evaluate(Principal([OwnerRole], "bff", Bot))).Should().BeFalse();
        (await Evaluate(Principal([OwnerRole], Bot, Bot))).Should().BeFalse("同じ値でも 2 つ載るトークンは作られない＝曖昧");
    }

    [Fact]
    public async Task T_10_1723_ロールが無ければボットのazpでも拒否_未認証も拒否()
    {
        (await Evaluate(Principal(["some-unrelated-role"], Bot))).Should().BeFalse();
        (await Evaluate(Principal([], Bot))).Should().BeFalse();
        (await Evaluate(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, OwnerRole), new Claim(GrpcOwnerClientGate.AuthorizedPartyClaim, Bot)])))).Should().BeFalse("未認証");
    }

    // 陽性対照: s2s の分岐は従来どおり（azp を問わない）。
    [Theory]
    [InlineData(null)]
    [InlineData("ai-stock-trading-svc")]
    [InlineData("bff")]
    public async Task T_10_1723_s2sの分岐は変えない(string? azp)
    {
        var user = azp is null ? Principal([ServiceRole]) : Principal([ServiceRole], azp);
        (await Evaluate(user)).Should().BeTrue();
        (await Evaluate(Principal([OwnerRole, ServiceRole], "bff"))).Should().BeTrue("サービスのロールを持てばサービスの分岐で通る");
    }

    // 🔴 REST の面の門（OwnerOrService）は変えない —— 人の利用者のトークンは REST（BFF が中継する経路）で通る。
    [Fact]
    public async Task T_10_1723_RESTの門OwnerOrServiceは変えない()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAuth(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var authz = provider.GetRequiredService<IAuthorizationService>();

        (await authz.AuthorizeAsync(Principal([OwnerRole], "ai-stock-trading-dev"), null, AiStockTradingAuthPolicies.OwnerOrService))
            .Succeeded.Should().BeTrue();
        (await authz.AuthorizeAsync(Principal([OwnerRole]), null, AiStockTradingAuthPolicies.OwnerOrService))
            .Succeeded.Should().BeTrue();
    }

    // ---- T-10-1724: 構成（既定・置き換え・空・1 つの値） ----

    [Fact]
    public void T_10_1724_未構成なら既定はボットの機密クライアントだけ()
    {
        GrpcOwnerClientGate.EffectiveClients(new ConfigurationBuilder().Build()).Should().Equal(Bot);
    }

    [Fact]
    public async Task T_10_1724_構成すると既定を置き換える()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:GrpcOwnerClients:0"] = " other-bot ",
            ["Auth:GrpcOwnerClients:1"] = "  ",
        };

        GrpcOwnerClientGate.EffectiveClients(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .Should().Equal("other-bot");
        (await Evaluate(Principal([OwnerRole], "other-bot"), settings)).Should().BeTrue();
        (await Evaluate(Principal([OwnerRole], Bot), settings)).Should().BeFalse("既定は足し合わせない");
    }

    // fail-closed: 空の値（secret の鍵が空）は「誰の所有者トークンも通さない」。s2s は通る。
    [Fact]
    public async Task T_10_1724_空の値で構成すると誰の所有者トークンも通さない()
    {
        var settings = new Dictionary<string, string?> { ["Auth:GrpcOwnerClients:0"] = "" };

        GrpcOwnerClientGate.EffectiveClients(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()).Should().BeEmpty();
        (await Evaluate(Principal([OwnerRole], Bot), settings)).Should().BeFalse();
        (await Evaluate(Principal([ServiceRole]), settings)).Should().BeTrue();
    }

    // 🔴 1 つの値（配列でない）は起動時（登録時）に例外。既定へ静かに戻さない。空文字・カンマ区切りも同じ。
    [Theory]
    [InlineData("ai-stock-trading-owner")]
    [InlineData("ai-stock-trading-owner,other-bot")]
    [InlineData("")]
    public void T_10_1724_1つの値の構成は起動時に例外(string scalar)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:GrpcOwnerClients"] = scalar })
            .Build();
        var services = new ServiceCollection();

        var act = () => services.AddAiStockTradingAuth(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:GrpcOwnerClients*配列*");
    }
}

// T-10-1726, NFR-06, ADR-0047 決定 3, IADR-0448 決定 4, #1067: **既定・realm・helm・compose を一致させる配線試験。**
// 許可するのはボットが実際に使う機密クライアントであり、その値は notification の OwnerAuth と**同じ出所**から来なければならない。
// 片方だけ変えると、gRPC へ移したボットが PERMISSION_DENIED で止まる（fail-closed だが気付きにくい）か、別のクライアントを通してしまう。
public class GrpcOwnerGateWiringTests
{
    private const string OwnerSecretKey = "discord-owner-auth-client-id";
    private static readonly string[] GrpcFaceServices =
        ["audit", "configuration", "cost-control", "market-monitor", "report", "risk-management"];

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([RepoRoot(), .. path]));

    // 🔴 既定は realm の owner 機密クライアントであり、そのクライアントは client_credentials 専用・trading-owner だけを持つ。
    //   （standardFlow / directAccessGrants が有効だと、人がそのクライアントの azp でトークンを取れてしまい、門の意味が消える。）
    [Fact]
    public void T_10_1726_既定はrealmのボットの機密クライアントで_人のトークンのazpになり得ない()
    {
        GrpcOwnerClientGate.DefaultClients.Should().ContainSingle();
        var bot = GrpcOwnerClientGate.DefaultClients[0];

        using var realm = JsonDocument.Parse(Read("infra", "keycloak", "realm-export.json"));
        var client = realm.RootElement.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == bot);
        client.GetProperty("publicClient").GetBoolean().Should().BeFalse();
        client.GetProperty("serviceAccountsEnabled").GetBoolean().Should().BeTrue();
        client.GetProperty("standardFlowEnabled").GetBoolean().Should().BeFalse();
        client.GetProperty("directAccessGrantsEnabled").GetBoolean().Should().BeFalse();
        if (client.TryGetProperty("implicitFlowEnabled", out var implicitFlow))
            implicitFlow.GetBoolean().Should().BeFalse();

        var serviceAccount = realm.RootElement.GetProperty("users").EnumerateArray()
            .Single(u => u.TryGetProperty("serviceAccountClientId", out var id) && id.GetString() == bot);
        serviceAccount.GetProperty("realmRoles").EnumerateArray().Select(r => r.GetString())
            .Should().Equal("trading-owner");
    }

    [Fact]
    public void T_10_1726_composeは6面とボットが同じ変数と同じ既定から採る()
    {
        var compose = Read("docker-compose.yml");
        var bot = GrpcOwnerClientGate.DefaultClients[0];

        compose.Should().Contain($"Notifications__Discord__OwnerAuth__ClientId: ${{DISCORD_OWNERAUTH_CLIENTID:-{bot}}}");
        Regex.Matches(compose, @"^\s+Auth__GrpcOwnerClients__0: \$\{DISCORD_OWNERAUTH_CLIENTID:-" + Regex.Escape(bot) + @"\}\s*$", RegexOptions.Multiline)
            .Count.Should().Be(GrpcFaceServices.Length);
        compose.Should().NotMatchRegex(@"(?m)^\s+Auth__GrpcOwnerClients:", "1 つの値の書き方は起動時に落ちる");
        foreach (var svc in GrpcFaceServices)
            ServiceBlock(compose, $"{svc}-service", indent: 2).Should().Contain("Auth__GrpcOwnerClients__0:", svc);
    }

    // helm: 6 面すべてが notification の OwnerAuth と同じ秘密鍵から配列の 1 要素目で採る。values-local で extraEnv を
    // 丸ごと上書きしているサービス（helm はリストを置き換える）でも落とさない。
    [Theory]
    [InlineData("values.yaml")]
    [InlineData("values-local.yaml")]
    public void T_10_1726_helmは6面とボットが同じ秘密鍵から採る(string valuesFile)
    {
        var values = Read("deploy", "helm", "ai-stock-trading", valuesFile);
        var baseValues = Read("deploy", "helm", "ai-stock-trading", "values.yaml");

        values.Should().NotMatchRegex(@"(?m)name: Auth__GrpcOwnerClients\s*$", "1 つの値の書き方は起動時に落ちる");
        foreach (var svc in GrpcFaceServices)
        {
            var block = ServiceBlock(values, svc, indent: 2);
            if (valuesFile != "values.yaml" && (block.Length == 0 || !block.Contains("extraEnv:")))
                continue; // 上書きしていない＝values.yaml の extraEnv がそのまま効く（下の values.yaml の行で固定）
            EnvSecretKey(block, "Auth__GrpcOwnerClients__0").Should().Be(OwnerSecretKey, $"{valuesFile} の {svc}");
        }

        var notification = ServiceBlock(values, "notification", indent: 2);
        if (!notification.Contains("extraEnv:"))
            notification = ServiceBlock(baseValues, "notification", indent: 2);
        EnvSecretKey(notification, "Notifications__Discord__OwnerAuth__ClientId")
            .Should().Be(OwnerSecretKey, "ボットの機密クライアントの出所");
    }

    // `  <name>:` から次の同じ深さのキーまで（なければ空）。
    private static string ServiceBlock(string text, string name, int indent)
    {
        var pad = new string(' ', indent);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, $"{pad}{name}:");
        if (start < 0) return string.Empty;
        var end = start + 1;
        var sibling = new Regex($@"^{pad}[A-Za-z0-9_-]+:\s*$|^[A-Za-z]");
        while (end < lines.Length && !sibling.IsMatch(lines[end])) end++;
        return string.Join('\n', lines[start..end]);
    }

    // `- name: <env>` の直後の `secretKeyRef: { ..., key: <key>, ... }` の key。無ければ null。
    private static string? EnvSecretKey(string block, string env)
    {
        var m = Regex.Match(block, @"- name: " + Regex.Escape(env) + @"\s*\n\s*secretKeyRef: \{[^}]*key: ([A-Za-z0-9-]+)[^}]*optional: true[^}]*\}");
        return m.Success ? m.Groups[1].Value : null;
    }
}
