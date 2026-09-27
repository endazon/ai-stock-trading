using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuditService.Domain;
using AuditService.Features.AuditEvents;
using AuditService.Infrastructure.Persistence;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.Audit.V1;

namespace AuditService.Tests;

// T-10-1670, T-10-1671（提供側）, NFR, FR-06, FR-11, MSP:ADR-0029, IADR-0284 決定 5（段 3）, IADR-0445 決定 2・3, #1059 (#753):
// 監査台帳の**種別 × 期間の読み取りの gRPC 面**。REST（`GET /audit/events/by-type`）と**同じ値・同じ認可・同じ入力の検証**であることを、
// 本物の Program.cs（AuditWorkerWebApplicationFactory。InMemory DB・TestAuthHandler・Wolverine の外部輸送の無効化だけを差し替え）で固定する。
//
// 観測の仕方: REST の本文を**送り手の本物の型**（`AuditEntry`）へ戻し、それを提供側の写し（AuditReadWireMapping）で proto にしたものと、
// gRPC の応答を**message ごと等価比較**する。1 項目でも写し漏れ・取り違え・順序の違いがあれば赤になる。
// 🔴 **本物の Program.cs で呼べること自体が「`MapGrpcService` が組み立てに入っている」ことの証拠である**（登録を消すと UNIMPLEMENTED で赤）。
public class AuditEventsReadGrpcServiceTests
{
    private const string ServiceRole = "trading-service";
    private const string OwnerRole = "trading-owner";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    // WebApplicationFactory の TestServer 越しに h2c を張る（実ポートを開かずに gRPC を通す。段 1・段 2 と同じ）。
    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles, string? azp = null)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles, azp);

        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles, string? azp = null) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static HttpClient RestClient(AuditWorkerWebApplicationFactory factory, string roles = ServiceRole)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static Proto.AuditEventsRead.AuditEventsReadClient Grpc(GrpcChannel channel) => new(channel);

    private static string ByType(string from, string to, string types) =>
        $"/audit/events/by-type?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&types={Uri.EscapeDataString(types)}";

    private static Proto.GetEventsByTypeRequest Request(string from, string to, params string[] types)
    {
        var request = new Proto.GetEventsByTypeRequest { From = from, To = to };
        request.EventTypes.AddRange(types);
        return request;
    }

    private static AuditEventRow Row(DateTimeOffset occurredAt, string type, string detail = "{\"a\":1}") => new()
    {
        Id = Guid.NewGuid(),
        EventType = type,
        CorrelationId = Guid.NewGuid(),
        Symbol = "AAPL",
        Summary = $"{type} 要約",
        Detail = detail,
        OccurredAt = occurredAt,
        RecordedAt = occurredAt.AddSeconds(1),
    };

    private static void Seed(AuditWorkerWebApplicationFactory factory, params AuditEventRow[] rows)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        db.AuditEvents.AddRange(rows);
        db.SaveChanges();
    }

    private static async Task<List<Proto.LedgerRecord>> RestAsProto(HttpClient rest, string url)
    {
        var response = await rest.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await response.Content.ReadFromJsonAsync<List<AuditEntry>>(Web);
        return [.. entries!.Select(AuditReadWireMapping.ToProto)];
    }

    // ---- T-10-1670: REST と同じ値（同じストア・同じ種別の解析） ----

    [Fact]
    public async Task T_10_1670_期間と種別で引いた記録は_REST_と件数も順序も中身も同じ()
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        Seed(factory,
            Row(T0.AddHours(3), "OrderApproved", "{\"decisionId\":\"x\",\"quantity\":0}"),
            Row(T0.AddHours(1), "FxRateStale", "{\"currency\":\"USD\"}"),
            Row(T0.AddHours(2), "TradeDecisionMade", "{\"rationale\":\"根拠\"}"),       // 要求しない種別
            Row(T0.AddDays(1), "FxRateStale"),                                           // 終端ちょうど（半開区間の外）
            Row(T0.AddSeconds(-1), "OrderApproved"));                                    // 始端の直前
        using var rest = RestClient(factory);
        using var channel = ChannelFor(factory, ServiceRole);
        var from = T0.ToString("o");
        var to = T0.AddDays(1).ToString("o");

        var expected = await RestAsProto(rest, ByType(from, to, "FxRateStale,OrderApproved"));
        var actual = await Grpc(channel).GetEventsByTypeAsync(Request(from, to, "FxRateStale", "OrderApproved"));

        // 空どうしの一致は何も証明しない。2 件が時系列で載っていることを先に確かめる。
        expected.Select(r => r.EventType).Should().Equal("FxRateStale", "OrderApproved");
        actual.Records.Should().Equal(expected);

        // 名指し: id は GUID の D 書式、本文は書き手の JSON のまま（在る 0 も 0 のまま）。
        var approved = actual.Records[1];
        Guid.TryParseExact(approved.Id, "D", out _).Should().BeTrue();
        approved.Detail.Should().Be("{\"decisionId\":\"x\",\"quantity\":0}");
    }

    // 種別の解析は REST と 1 つ（空・空白の要素は落とす・前後の空白は削る・カンマを含む要素も同じに割る）。
    [Fact]
    public async Task T_10_1670_種別の空白と空要素とカンマの扱いは_REST_と同じ()
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        Seed(factory, Row(T0.AddHours(1), "FxRateStale"), Row(T0.AddHours(2), "OrderApproved"));
        using var rest = RestClient(factory);
        using var channel = ChannelFor(factory, ServiceRole);
        var from = T0.ToString("o");
        var to = T0.AddDays(1).ToString("o");

        var expected = await RestAsProto(rest, ByType(from, to, " FxRateStale , ,OrderApproved"));
        expected.Should().HaveCount(2);

        (await Grpc(channel).GetEventsByTypeAsync(Request(from, to, " FxRateStale ", "", "  ", "OrderApproved")))
            .Records.Should().Equal(expected);
        (await Grpc(channel).GetEventsByTypeAsync(Request(from, to, "FxRateStale,OrderApproved")))
            .Records.Should().Equal(expected, "REST と同じくカンマで割る");
    }

    // 該当なしは「空の一覧」（REST の [] と同じ）。欠落や失敗とは区別される。
    [Fact]
    public async Task T_10_1670_該当が無ければ空の一覧を返す()
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        Seed(factory, Row(T0.AddHours(1), "FxRateStale"));
        using var rest = RestClient(factory);
        using var channel = ChannelFor(factory, ServiceRole);
        var from = T0.AddDays(5).ToString("o");
        var to = T0.AddDays(6).ToString("o");

        (await RestAsProto(rest, ByType(from, to, "FxRateStale"))).Should().BeEmpty();
        (await Grpc(channel).GetEventsByTypeAsync(Request(from, to, "FxRateStale"))).Records.Should().BeEmpty();
    }

    // ---- T-10-1671: 認可（REST の 401 / 403 と同じ）と入力の検証（REST の 400 と同じ） ----

    [Fact]
    public async Task T_10_1671_資格情報が無ければ_UNAUTHENTICATED()
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, roles: null);

        var act = async () => await Grpc(channel).GetEventsByTypeAsync(
            Request(T0.ToString("o"), T0.AddDays(1).ToString("o"), "FxRateStale"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        (await factory.CreateClient().GetAsync(ByType(T0.ToString("o"), T0.AddDays(1).ToString("o"), "FxRateStale")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "REST も同じ");
    }

    // 陰性対照: 認証済みでも OwnerOrService に当たるロールが無ければ PERMISSION_DENIED（REST の 403 と同値）。
    [Fact]
    public async Task T_10_1671_ロールが足りなければ_PERMISSION_DENIED()
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, "some-unrelated-role");

        var act = async () => await Grpc(channel).GetEventsByTypeAsync(
            Request(T0.ToString("o"), T0.AddDays(1).ToString("o"), "FxRateStale"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        using var rest = RestClient(factory, "some-unrelated-role");
        (await rest.GetAsync(ByType(T0.ToString("o"), T0.AddDays(1).ToString("o"), "FxRateStale")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "REST も同じ");
    }

    // 陽性対照: サービスと利用者は読める（REST の当該エンドポイントと同じ OwnerOrService）。
    // #1067: 所有者の分岐は呼び出し元がボットの機密クライアントであるときだけ（T-10-1725）。
    [Theory]
    [InlineData(ServiceRole, null)]
    [InlineData(OwnerRole, "ai-stock-trading-owner")]
    public async Task T_10_1671_サービスと利用者のロールなら読める(string role, string? azp)
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        Seed(factory, Row(T0.AddHours(1), "FxRateStale"));
        using var channel = ChannelFor(factory, role, azp);

        var response = await Grpc(channel).GetEventsByTypeAsync(
            Request(T0.ToString("o"), T0.AddDays(1).ToString("o"), "FxRateStale"));

        response.Records.Should().ContainSingle();
    }

    // ---- T-10-1725: gRPC 面の所有者の門は、呼び出し元のクライアント（azp）が Discord ボットであることを併せて求める ----
    // NFR-06, FR-14, ADR-0047 決定 3, IADR-0448 決定 1, #1067 (#753): 人の利用者のトークン（`trading-owner` を持つが azp は BFF 等）は
    // gRPC 面を通らない。🔴 変種（大小文字・接頭辞・接尾辞）と azp の無いトークンも通さない。s2s の分岐と REST の面は変えない。
    private const string GateOwnerRole = "trading-owner";
    private const string GateServiceRole = "trading-service";
    private const string BotClient = "ai-stock-trading-owner";

    [Theory]
    [InlineData(null)]                          // azp の無いトークン
    [InlineData("ai-stock-trading-dev")]        // 利用者の公開クライアント（ブラウザ・BFF の経路）
    [InlineData("bff")]
    [InlineData("AI-STOCK-TRADING-OWNER")]      // 大小文字の変種
    [InlineData("Ai-Stock-Trading-Owner")]
    [InlineData("ai-stock-trading-owner-bff")]  // ボットの id を接頭辞に持つ別のクライアント
    [InlineData("ai-stock-trading-own")]        // ボットの id の接頭辞
    [InlineData("xai-stock-trading-owner")]     // ボットの id を接尾辞に持つ別のクライアント
    [InlineData("ai-stock-trading-svc")]        // s2s のクライアントでも、所有者の分岐では通さない
    public async Task T_10_1725_所有者のロールでも呼び出し元がボットでなければ_PERMISSION_DENIED(string? azp)
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, GateOwnerRole, azp);

        var act = async () => await Grpc(channel).GetEventsByTypeAsync(Request(T0.ToString("o"), T0.AddDays(1).ToString("o"), "FxRateStale"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // 陽性対照: ボットのトークン（trading-owner ＋ azp＝ボットの機密クライアント）は通る。s2s は azp を問わず従来どおり。
    // 🔴 REST の面の所有者の判定は変えない（azp の無い利用者のトークンで REST は読める＝BFF が中継する経路）。
    [Fact]
    public async Task T_10_1725_ボットのトークンは通り_s2sは従来どおりで_RESTの所有者の判定は変えない()
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        foreach (var (roles, azp) in new (string, string?)[]
                 {
                     (GateOwnerRole, BotClient),
                     (GateServiceRole, null),
                     (GateServiceRole, "bff"),
                     ($"{GateOwnerRole},{GateServiceRole}", "bff"),
                 })
        {
            using var channel = ChannelFor(factory, roles, azp);
            var act = async () => await Grpc(channel).GetEventsByTypeAsync(Request(T0.ToString("o"), T0.AddDays(1).ToString("o"), "FxRateStale"));
            await act.Should().NotThrowAsync($"roles={roles} azp={azp ?? "(無し)"} は通るはず");
        }

        using var rest = RestClient(factory, GateOwnerRole);
        (await rest.GetAsync(ByType(T0.ToString("o"), T0.AddDays(1).ToString("o"), "FxRateStale")))
            .StatusCode.Should().Be(HttpStatusCode.OK, "REST の所有者の判定は OwnerOrService のまま");
    }

    // REST の 400（期間の欠落・書式違い・種別なし・逆順・空区間）は INVALID_ARGUMENT。黙って空を返して「事象なし」に見せない。
    [Theory]
    [InlineData("欠落した始端")]
    [InlineData("読めない終端")]
    [InlineData("種別なし")]
    [InlineData("空白だけの種別")]
    [InlineData("逆順")]
    [InlineData("空区間")]
    public async Task T_10_1671_入力の誤りは_REST_と同じく_INVALID_ARGUMENT(string how)
    {
        await using var factory = new AuditWorkerWebApplicationFactory();
        using var rest = RestClient(factory);
        using var channel = ChannelFor(factory, ServiceRole);
        var ok = T0.ToString("o");
        var next = T0.AddDays(1).ToString("o");
        var (from, to, types) = how switch
        {
            "欠落した始端" => ("", next, new[] { "FxRateStale" }),
            "読めない終端" => (ok, "not-a-date", new[] { "FxRateStale" }),
            "種別なし" => (ok, next, Array.Empty<string>()),
            "空白だけの種別" => (ok, next, new[] { " ", "" }),
            "逆順" => (next, ok, new[] { "FxRateStale" }),
            _ => (ok, ok, new[] { "FxRateStale" }),
        };

        var restUrl = "/audit/events/by-type?"
            + (from.Length > 0 ? $"from={Uri.EscapeDataString(from)}&" : string.Empty)
            + $"to={Uri.EscapeDataString(to)}&types={Uri.EscapeDataString(string.Join(",", types))}";
        (await rest.GetAsync(restUrl)).StatusCode.Should().Be(HttpStatusCode.BadRequest, $"REST は 400（{how}）");

        var act = async () => await Grpc(channel).GetEventsByTypeAsync(Request(from, to, types));
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument, how);
    }
}

// T-10-1670（写し）, IADR-0445 決定 3: 提供側の写しは C# の null を設定しない（受け手が欠落を「契約の食い違い」と読めるように）。
public class AuditReadWireMappingTests
{
    [Fact]
    public void T_10_1670_在る値は写し_null_は設定しない()
    {
        var id = Guid.NewGuid();
        var present = AuditReadWireMapping.ToProto(new AuditEntry(
            id, "FxRateStale", Guid.NewGuid(), null, "要約", "{}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        present.Id.Should().Be(id.ToString("D"));
        present.EventType.Should().Be("FxRateStale");
        present.Detail.Should().Be("{}");

        var missing = AuditReadWireMapping.ToProto(new AuditEntry(
            id, null!, Guid.NewGuid(), null, "要約", null!, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        missing.HasEventType.Should().BeFalse();
        missing.HasDetail.Should().BeFalse();
    }
}
