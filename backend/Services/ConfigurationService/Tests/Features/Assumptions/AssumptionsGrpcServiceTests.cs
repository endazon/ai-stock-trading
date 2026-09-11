using System.Net.Http.Json;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using ConfigurationService.Features.Assumptions.GetAssumptions;
using Grpc.Core;
using Grpc.Net.Client;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.Configuration.V1;

namespace ConfigurationService.Tests;

// FR-17, UC-06, NFR, MSP:ADR-0029, IADR-0284 決定 5（段 1）, IADR-0328, IADR-0331, #745 (#584):
// 全体前提条件の照会の **gRPC 面**。REST（`GET /assumptions`）と**同じ値・同じ認可**であることを固定する。
//
// 🔴 ここで守るのは 3 つ。
//   (1) **写像で桁が落ちない**（`decimal` → 線上 → `decimal`）。落ちても例外は 1 つも出ず、
//       採算判定・費用上限判定の結果だけが静かに変わる。
//   (2) **REST と同じ値**（評価器を 2 つにしない）。
//   (3) **認可が REST の読み取りと同じ**（OwnerOrService）。緩めると east-west の面だけが弱くなる。
public class AssumptionsGrpcServiceTests
{
    // WebApplicationFactory の TestServer 越しに h2c を張る（実ポートを開かずに gRPC を通す）。
    private static GrpcChannel ChannelFor(ConfigurationWorkerWebApplicationFactory factory, string? roles)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles);

        return GrpcChannel.ForAddress(
            factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task サービストークンで現在の前提条件と版を返す()
    {
        using var factory = new ConfigurationWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, AiStockTradingAuthPoliciesRoles.Service);

        var response = await new Proto.Assumptions.AssumptionsClient(channel)
            .GetAsync(new Proto.GetAssumptionsRequest());

        response.Version.Should().BeGreaterThan(
            VersionedAssumptions.UnresolvedVersion, "未設定なら既定をシードして版 1 になる");
        response.Assumptions.CapitalGainsTaxRate.Should().Be("0.20315");
        response.Assumptions.CostLimits.Total.Should().Be("20000");
    }

    // 陽性対照（同一評価器）: REST の GET /assumptions と gRPC が同じ値・同じ版を返す。
    [Fact]
    public async Task RESTと同じ値と版を返す()
    {
        using var factory = new ConfigurationWorkerWebApplicationFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, AiStockTradingAuthPoliciesRoles.Service);

        var rest = await http.GetFromJsonAsync<VersionedAssumptions>("/assumptions");

        using var channel = ChannelFor(factory, AiStockTradingAuthPoliciesRoles.Service);
        var grpc = await new Proto.Assumptions.AssumptionsClient(channel)
            .GetAsync(new Proto.GetAssumptionsRequest());

        rest.Should().NotBeNull();
        grpc.Version.Should().Be(rest!.Version);
        AssumptionsWireMapping.FromWire(grpc.Assumptions.CapitalGainsTaxRate)
            .Should().Be(rest.Assumptions.CapitalGainsTaxRate);
        AssumptionsWireMapping.FromWire(grpc.Assumptions.MinimumExpectedProfitMultiple)
            .Should().Be(rest.Assumptions.MinimumExpectedProfitMultiple);
        AssumptionsWireMapping.FromWire(grpc.Assumptions.CostLimits.Llm)
            .Should().Be(rest.Assumptions.CostLimits.Llm);
    }

    // 陰性対照 1: s2s トークン（テストでは X-Test-Roles）が無ければ UNAUTHENTICATED。
    [Fact]
    public async Task 資格情報が無ければ_UNAUTHENTICATED()
    {
        using var factory = new ConfigurationWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, roles: null);

        var act = async () => await new Proto.Assumptions.AssumptionsClient(channel)
            .GetAsync(new Proto.GetAssumptionsRequest());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    // 陰性対照 2: 認証済みでも OwnerOrService に当たるロールが無ければ PERMISSION_DENIED
    //（REST の 403 と同値。east-west の面だけを緩めない）。
    [Fact]
    public async Task ロールが足りなければ_PERMISSION_DENIED()
    {
        using var factory = new ConfigurationWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, "some-unrelated-role");

        var act = async () => await new Proto.Assumptions.AssumptionsClient(channel)
            .GetAsync(new Proto.GetAssumptionsRequest());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }
}

// FR-17, IADR-0331 決定 2: 線上表現（不変文化の 10 進文字列）の写像。
//
// 🔴 **`double` を経由した瞬間に壊れる値だけを並べてある。** 例えば `0.20315m` を `double` にすると
// `0.203150000000000000...` ではなく `0.20314999999999999947...` になり、往復で桁が変わる。
// 例外は 1 つも出ないので、**この試験が落ちること**でしか気付けない。
public class AssumptionsWireMappingTests
{
    [Theory]
    [InlineData("0.20315")]      // 譲渡益税率
    [InlineData("0.0000001")]    // 手数料率の下限側
    [InlineData("0.1")]          // 2 進で表現できない代表値
    [InlineData("20000")]        // 月次費用上限
    [InlineData("1234567.89")]
    [InlineData("0")]
    public void 十進の桁が往復で保たれる(string literal)
    {
        var value = decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);

        var wire = AssumptionsWireMapping.ToWire(value);
        var back = AssumptionsWireMapping.FromWire(wire);

        wire.Should().Be(literal);
        back.Should().Be(value);
    }

    // 陰性対照: proto3 に null は無い。空文字（未指定）は 0 として読む（提供側の写しと対）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 未指定はゼロとして読む(string? wire) => AssumptionsWireMapping.FromWire(wire).Should().Be(0m);

    // 陽性対照: 版と全項目が message へ写ること（写し漏れは既定値 "" ＝ 0 として静かに通る）。
    [Fact]
    public void 全項目が写る()
    {
        var current = new VersionedAssumptions(
            new TradingAssumptions
            {
                CapitalGainsTaxRate = 0.20315m,
                JapanCommission = new CommissionSchedule(0.001m, 100m, 1000m),
                UnitedStatesCommission = new CommissionSchedule(0.002m, 0.99m, 0m),
                FxSpreadRatio = 0.003m,
                MinimumExpectedProfitMultiple = 2m,
                CostLimits = new MonthlyCostLimits(20000m, 15000m, 5000m, 0m),
            },
            Version: 7);

        var proto = AssumptionsWireMapping.ToProto(current);

        proto.Version.Should().Be(7);
        proto.Assumptions.CapitalGainsTaxRate.Should().Be("0.20315");
        proto.Assumptions.JapanCommission.Rate.Should().Be("0.001");
        proto.Assumptions.JapanCommission.Minimum.Should().Be("100");
        proto.Assumptions.JapanCommission.Cap.Should().Be("1000");
        proto.Assumptions.UnitedStatesCommission.Rate.Should().Be("0.002");
        proto.Assumptions.UnitedStatesCommission.Minimum.Should().Be("0.99");
        proto.Assumptions.UnitedStatesCommission.Cap.Should().Be("0");
        proto.Assumptions.FxSpreadRatio.Should().Be("0.003");
        proto.Assumptions.MinimumExpectedProfitMultiple.Should().Be("2");
        proto.Assumptions.CostLimits.Total.Should().Be("20000");
        proto.Assumptions.CostLimits.Llm.Should().Be("15000");
        proto.Assumptions.CostLimits.Infrastructure.Should().Be("5000");
        proto.Assumptions.CostLimits.Data.Should().Be("0");
    }
}

// テストで使うロール名（shim の AiStockTradingAuthPolicies と同値。テストが実装の定数へ依存しないよう写す）。
internal static class AiStockTradingAuthPoliciesRoles
{
    internal const string Service = "trading-service";
}
