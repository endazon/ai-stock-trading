using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Errors;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Common.Exceptions;
using ReportService.Features.Reports;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace ReportService.Tests;

// NFR-06, FR-07, ADR-0003, IADR-0503, #1206: 報告書の例外の写し（REST の群のフィルタと gRPC の書き込み・読み取り）が、
// 業務の例外（確定済みの変更・自前の入力検証）の状態と文言を保ち、EF・フレームワーク由来の例外の文言を応答へ載せないことを、
// 本物の Program.cs（ReportWorkerWebApplicationFactory）で固定する。
public class ReportExceptionMessageExposureTests
{
    private const string OwnerRole = "trading-owner";
    private const string Bot = "ai-stock-trading-owner";
    private const string Key = "daily-2026-07-10";

    // 接続文字列に似た目印（資格情報は含めない）。応答に出たら漏れである。
    private const string ConnectionLikeMarker = "Host=reports-db.internal;Port=5432;Database=reports;Username=report_app";

    // EF が実際に投げる InvalidOperationException（プロバイダ未構成）。文言はフレームワークのもの。
    private static InvalidOperationException EfInvalidOperation()
    {
        using var db = new DbContext(new DbContextOptionsBuilder().Options);
        try
        {
            _ = db.Model;
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("EF が例外を投げなかった（前提の崩れ）。");
    }

    // フレームワーク（System.Text.RegularExpressions）が実際に投げる ArgumentException。文言に目印を含む（パターンを引用する）。
    // 🔴 捕まえて返し直すと `throw ex` でスタックが投げ直した側（試験のアセンブリ）へ付け替わり、「フレームワーク由来」を
    // 試せなくなる（独立監査の指摘）。保存の呼び出しの中で Regex にそのまま投げさせ、スタックの先頭をフレームワークに保つ。
    private static Exception RaiseFrameworkArgumentException()
    {
        _ = new Regex(ConnectionLikeMarker + "(");
        throw new InvalidOperationException("Regex が例外を投げなかった（前提の崩れ）。");
    }

    // どのメンバーを呼んでも与えた例外を投げる報告書の保存（保存の実装に依らず「処理中の例外」を再現する）。
    public class ThrowingStore : DispatchProxy
    {
        internal Func<Exception> Throw { get; set; } = () => new InvalidOperationException(ConnectionLikeMarker);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw Throw();
    }

    private static WebApplicationFactory<Program> WithThrowingStore(ReportWorkerWebApplicationFactory baseFactory, Func<Exception> exception) =>
        baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddScoped<IReportStore>(_ =>
        {
            var store = DispatchProxy.Create<IReportStore, ThrowingStore>();
            ((ThrowingStore)(object)store).Throw = exception;
            return store;
        })));

    private static HttpClient Owner(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        return client;
    }

    private sealed class BotHeaders(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, OwnerRole);
            request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, Bot);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static GrpcChannel Channel(WebApplicationFactory<Program> factory) =>
        GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = new BotHeaders(factory.Server.CreateHandler()) });

    public static TheoryData<string> InvalidOperationCases => new() { "crafted", "ef" };

    private static (Func<Exception> Throw, string Leak) InvalidOperationCase(string kind) => kind switch
    {
        "crafted" => (() => new InvalidOperationException(ConnectionLikeMarker), ConnectionLikeMarker),
        _ => (EfInvalidOperation, EfInvalidOperation().Message),
    };

    // T-10-2388（受け入れ基準 1・REST）: 群のフィルタの中で EF／フレームワーク由来の InvalidOperationException が投げられても、
    // 409 に例外の文言を載せず、共通の例外処理の 500 の ProblemDetails（type・title・status・traceId だけ）になる。
    [Theory]
    [MemberData(nameof(InvalidOperationCases))]
    public async Task 業務でない_InvalidOperationException_は_REST_で文言を返さず_500_の_ProblemDetails(string kind)
    {
        var (thrower, leak) = InvalidOperationCase(kind);
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithThrowingStore(baseFactory, thrower);

        using var res = await Owner(factory).GetAsync("/reports", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "409（業務の競合）に見せない");
        res.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.Should().Contain("traceId");
        body.Should().NotContain(leak);
        body.Should().NotContain("reports-db.internal");
        body.Should().NotContain(nameof(InvalidOperationException));
        body.Should().NotContain("   at ");
    }

    // T-10-2389（受け入れ基準 1・gRPC の書き込み）: 同じ例外を gRPC の確定で受けても、status detail に例外の文言を載せない
    // （ABORTED＝409 に写さない。Grpc.AspNetCore の未処理例外の既定＝詳細なしの UNKNOWN）。
    [Theory]
    [MemberData(nameof(InvalidOperationCases))]
    public async Task 業務でない_InvalidOperationException_は_gRPC_の確定でも_detail_に文言を載せない(string kind)
    {
        var (thrower, leak) = InvalidOperationCase(kind);
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithThrowingStore(baseFactory, thrower);
        using var channel = Channel(factory);

        var act = async () => await new Proto.ReportOwnerWrite.ReportOwnerWriteClient(channel)
            .ConfirmReportAsync(new Proto.ReportConfirmationRequest { PeriodKey = Key, ExpectedVersion = 1 },
                cancellationToken: TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().NotBe(StatusCode.Aborted, "業務の競合に見せない");
        ex.Status.Detail.Should().NotContain(leak);
        ex.Status.Detail.Should().NotContain("reports-db.internal");
    }

    // T-10-2390（受け入れ基準 2・409）: 確定済みの報告書の変更（業務の例外）は 409 で文言を保つ。
    [Fact]
    public async Task 確定済みの報告書の変更は_409_で文言を保つ()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = Owner(factory);
        var draft = new { Kind = "Daily", PeriodStart = "2026-07-10", BasedOn = (string?)null, AssumptionsVersion = 1, PolicySummary = "押し目買い", ExpectedVersion = 0 };
        (await client.PutAsJsonAsync($"/reports/{Key}", draft, TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();
        (await client.PostAsJsonAsync($"/reports/{Key}/confirm", new { ExpectedVersion = 1 }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var res = await client.PutAsJsonAsync($"/reports/{Key}", draft, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOf(res)).Should().Be($"確定済み報告書 {Key} は変更できません。");
    }

    // T-10-2391（受け入れ基準 2・400）: 自前の入力検証（PeriodKey と種別・対象日の不一致）は 400 で文言を保つ。
    // NFR-06, IADR-0509, #1230: 文言が載るのは送出点に印（ClientVisibleArgument）があるため。
    [Fact]
    public async Task 自前の入力検証の_ArgumentException_は_400_で文言を保つ()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        using var res = await Owner(factory).PostAsJsonAsync(
            "/reports/daily-2026-07-11/draft",
            new { Kind = "Daily", Date = "2026-07-10", AssumptionsVersion = 1 },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(res)).Should().Contain("PeriodKey は種別・対象日と一致する必要があります");
    }

    // T-10-2392（REST の 400・gRPC の INVALID_ARGUMENT）: フレームワークが投げた ArgumentException は状態を保ち、文言は固定文言にする。
    [Fact]
    public async Task フレームワークの_ArgumentException_は_400_と_INVALID_ARGUMENT_を保ち文言は固定文言()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithThrowingStore(baseFactory, RaiseFrameworkArgumentException);

        using var rest = await Owner(factory).GetAsync("/reports", TestContext.Current.CancellationToken);
        var body = await rest.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        rest.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotContain("reports-db.internal");
        (await ErrorOf(rest)).Should().Be(ClientFacingErrors.InvalidRequestMessage);

        using var channel = Channel(factory);
        var read = async () => await new Proto.ReportOwnerRead.ReportOwnerReadClient(channel)
            .ListReportPeriodKeysAsync(new Proto.ListReportPeriodKeysRequest(), cancellationToken: TestContext.Current.CancellationToken);
        var readEx = (await read.Should().ThrowAsync<RpcException>()).Which;
        readEx.StatusCode.Should().Be(StatusCode.InvalidArgument);
        readEx.Status.Detail.Should().Be(ClientFacingErrors.InvalidRequestMessage);

        var write = async () => await new Proto.ReportOwnerWrite.ReportOwnerWriteClient(channel)
            .ConfirmReportAsync(new Proto.ReportConfirmationRequest { PeriodKey = Key, ExpectedVersion = 1 },
                cancellationToken: TestContext.Current.CancellationToken);
        var writeEx = (await write.Should().ThrowAsync<RpcException>()).Which;
        writeEx.StatusCode.Should().Be(StatusCode.InvalidArgument);
        writeEx.Status.Detail.Should().Be(ClientFacingErrors.InvalidRequestMessage);
    }

    // T-10-2393（受け入れ基準 2・gRPC）: 409 と文言を返す InvalidOperationException は業務の型だけで、gRPC の確定でも ABORTED と文言を保つ。
    // 業務の型は InvalidOperationException の派生のまま（自動生成の捕捉の挙動を変えない）。
    [Fact]
    public async Task 業務の型の確定済みの変更は_gRPC_でも_ABORTED_と文言を保つ()
    {
        new ReportAlreadyConfirmedException(Key).Should().BeAssignableTo<InvalidOperationException>();
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithThrowingStore(baseFactory, () => new ReportAlreadyConfirmedException(Key));

        using var rest = await Owner(factory).GetAsync("/reports", TestContext.Current.CancellationToken);
        rest.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorOf(rest)).Should().Be($"確定済み報告書 {Key} は変更できません。");

        using var channel = Channel(factory);
        var act = async () => await new Proto.ReportOwnerWrite.ReportOwnerWriteClient(channel)
            .ConfirmReportAsync(new Proto.ReportConfirmationRequest { PeriodKey = Key, ExpectedVersion = 1 },
                cancellationToken: TestContext.Current.CancellationToken);
        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.Aborted);
        ex.Status.Detail.Should().Be($"確定済み報告書 {Key} は変更できません。");
    }

    // 第三者のライブラリの小さな補助の代役（呼び出し元へインライン化を強制）。CoreLib のコレクションに重複キーで投げさせる
    // （文言はキーの値＝目印を引用する）。NFR-06, IADR-0509, #1230: 判定がスタックに依らないことを固定するための送出元。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThirdPartyStyleAdd(Dictionary<string, int> map, string key) => map.Add(key, map.Count);

    private static Exception RaiseInlinedThirdPartyDuplicateKey()
    {
        var map = new Dictionary<string, int>();
        ThirdPartyStyleAdd(map, ConnectionLikeMarker);
        ThirdPartyStyleAdd(map, ConnectionLikeMarker);
        throw new InvalidOperationException("重複キーが例外を投げなかった（前提の崩れ）。");
    }

    // 印の無い自前の検証（CoreLib の補助）の代役。
    private static Exception RaiseUnmarkedThrowIf()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(" ", ConnectionLikeMarker);
        throw new InvalidOperationException("ThrowIfNullOrWhiteSpace が例外を投げなかった（前提の崩れ）。");
    }

    public static TheoryData<string> UnmarkedArgumentCases => new() { "inlined-third-party", "unmarked-throw-if" };

    private static Func<Exception> UnmarkedArgumentCase(string kind) => kind switch
    {
        "inlined-third-party" => RaiseInlinedThirdPartyDuplicateKey,
        _ => RaiseUnmarkedThrowIf,
    };

    private static async Task<(HttpStatusCode Status, string? Error, RpcException Read, RpcException Write)> AllPathsAsync(
        WebApplicationFactory<Program> factory)
    {
        using var rest = await Owner(factory).GetAsync("/reports", TestContext.Current.CancellationToken);
        var error = await ErrorOf(rest);

        using var channel = Channel(factory);
        var read = async () => await new Proto.ReportOwnerRead.ReportOwnerReadClient(channel)
            .ListReportPeriodKeysAsync(new Proto.ListReportPeriodKeysRequest(), cancellationToken: TestContext.Current.CancellationToken);
        var readEx = (await read.Should().ThrowAsync<RpcException>()).Which;

        var write = async () => await new Proto.ReportOwnerWrite.ReportOwnerWriteClient(channel)
            .ConfirmReportAsync(new Proto.ReportConfirmationRequest { PeriodKey = Key, ExpectedVersion = 1 },
                cancellationToken: TestContext.Current.CancellationToken);
        var writeEx = (await write.Should().ThrowAsync<RpcException>()).Which;
        return (rest.StatusCode, error, readEx, writeEx);
    }

    // T-10-2420（否定形・NFR-06, IADR-0509, #1230）: 印の無い ArgumentException（第三者相当の補助がインライン化される送出・印の無い
    // ThrowIfNullOrWhiteSpace）は、REST・gRPC の読み取り・gRPC の書き込みのいずれでも 400／INVALID_ARGUMENT の固定文言。
    // 200 回繰り返す（ホストが温まって段階コンパイルが上がっても応答の文言が変わらないこと）。
    [Theory]
    [MemberData(nameof(UnmarkedArgumentCases))]
    public async Task 印の無い_ArgumentException_は_REST_と_gRPC_で固定文言(string kind)
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithThrowingStore(baseFactory, UnmarkedArgumentCase(kind));

        for (var i = 0; i < 200; i++)
        {
            var (status, error, read, write) = await AllPathsAsync(factory);
            status.Should().Be(HttpStatusCode.BadRequest);
            error.Should().Be(ClientFacingErrors.InvalidRequestMessage);
            (read.StatusCode, read.Status.Detail).Should().Be((StatusCode.InvalidArgument, ClientFacingErrors.InvalidRequestMessage));
            (write.StatusCode, write.Status.Detail).Should().Be((StatusCode.InvalidArgument, ClientFacingErrors.InvalidRequestMessage));
        }
    }

    // T-10-2421（NFR-06, IADR-0509, #1230）: 印のある ArgumentException は、REST・gRPC の読み取り・gRPC の書き込みのいずれでも文言を保つ。
    [Fact]
    public async Task 印のある_ArgumentException_は_REST_と_gRPC_で文言を保つ()
    {
        const string Visible = "期間キーが不正です（利用者へ見せる文言）。";
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithThrowingStore(baseFactory, () => new ArgumentException(Visible).ClientVisible());

        var (status, error, read, write) = await AllPathsAsync(factory);

        status.Should().Be(HttpStatusCode.BadRequest);
        error.Should().Be(Visible);
        (read.StatusCode, read.Status.Detail).Should().Be((StatusCode.InvalidArgument, Visible));
        (write.StatusCode, write.Status.Detail).Should().Be((StatusCode.InvalidArgument, Visible));
    }

    // T-10-2422（否定形・NFR-06, IADR-0509, #1230）: 自前のコードの印の無い検証（下書きの生成の ThrowIfNullOrWhiteSpace(PeriodKey)）は、
    // 実際の経路でも 400 の固定文言になる（印を付けた期間キーの不一致の文言は T-10-2391 が保つ）。
    [Fact]
    public async Task 自前のコードの印の無い_ThrowIfNullOrWhiteSpace_は_400_で固定文言()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        using var res = await Owner(factory).PostAsJsonAsync(
            "/reports/%20/draft",
            new { Kind = "Daily", Date = "2026-07-10", AssumptionsVersion = 1 },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(res)).Should().Be(ClientFacingErrors.InvalidRequestMessage);
    }

    private static async Task<string?> ErrorOf(HttpResponseMessage res)
    {
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
    }
}
