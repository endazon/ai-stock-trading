using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.InformationCollection.Api.Tests;

// NFR（セキュリティ）, #456, IADR-0176 決定4:
// **認可メタデータを持たないエンドポイントが、明示した無認証許可リスト以外に存在しないこと**を構造で固定する。
// `CollectionIntervalNotConfigurableTests`（IADR-0164 決定1）と同じ `EndpointDataSource` 経路で書く。
//
// **本クラスが #456 の本命である。** 否定形の振る舞いテスト（`RunOnceAuthorizationTests`）は
// **今あるエンドポイント**しか守らないが、本クラスは**次に足されるエンドポイント**を守る。
// #456 が生まれた原因はまさに「#121 でエンドポイントを足したときに認可を忘れた」ことであり、
// **同じ再発を止められるのは後者だけである。**
//
// 「実装していない」と「実装してはならない」は別であり、**後者はコードに書かれない限り誰も守れない。**
public class UnauthenticatedEndpointsNotAllowedTests(InformationCollectionWorkerWebApplicationFactory factory)
    : IClassFixture<InformationCollectionWorkerWebApplicationFactory>
{
    // 無認証を許すのはインフラ用の経路だけである。
    //   - /health/*          : kubelet の liveness/readiness。認可を掛けると Pod を落とせなくなる
    //   - /internal/introspection : 構成の自己申告（機微を出さない設計・IADR-0011）
    // **業務操作をここへ足さないこと。** 足すなら認可を掛ける側が正しい。
    private static readonly string[] AllowedUnauthenticated =
    [
        "/health/live",
        "/health/ready",
        "/internal/introspection",
    ];

    [Fact]
    public void 認可の無いエンドポイントは許可リストのものだけである()
    {
        var endpoints = RouteEndpoints();

        endpoints.Should().NotBeEmpty("ルートを 1 本も取れないなら本検査は空振りしている");

        var unauthenticated = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(e => "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
            .Where(p => !AllowedUnauthenticated.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        unauthenticated.Should().BeEmpty(
            "業務操作を無認証で公開しない（#456）。新しいエンドポイントには RequireAuthorization を掛けるか、"
                + "インフラ用であれば本テストの許可リストへ**理由つきで**足すこと。"
                + $" 違反: {string.Join(", ", unauthenticated)}");
    }

    // 対照（肯定形）: 許可リストの経路が実在すること。**綴りを間違えた許可リストは、何も許していないのに
    // 何も検査していない状態を作る**（違反が 0 件になるのではなく、母集合が空になる）。
    [Fact]
    public void 許可リストの経路はすべて実在する()
    {
        var patterns = RouteEndpoints()
            .Select(e => "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
            .ToList();

        foreach (var allowed in AllowedUnauthenticated)
        {
            patterns.Should().Contain(
                p => string.Equals(p, allowed, StringComparison.OrdinalIgnoreCase),
                $"許可リストの '{allowed}' が実在しない。綴り誤りか、経路が消えている");
        }
    }

    // 対照（肯定形）: 認可つきのエンドポイントが少なくとも 1 本ある。
    // これが無いと「全部を許可リストに入れた」状態でも上のテストが緑になる。
    [Fact]
    public void 認可つきのエンドポイントが少なくとも1本ある()
    {
        RouteEndpoints()
            .Count(e => e.Metadata.GetMetadata<IAuthorizeData>() is not null)
            .Should().BeGreaterThan(0, "run-once に認可が掛かっているはずである");
    }

    private IReadOnlyList<RouteEndpoint> RouteEndpoints() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();
}
