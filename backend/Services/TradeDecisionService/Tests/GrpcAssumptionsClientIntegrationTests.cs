using System.Globalization;
using System.Net;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.Configuration.V1;

namespace TradeDecisionService.Tests;

// FR-17, NFR, MSP:ADR-0029, IADR-0284 決定 5（段 1）, IADR-0328, IADR-0331, #745 (#584):
// **呼び出し元ごとの timeout / retry が実際に効くこと**の結合試験（#584 併記の積み残し・段 0 から繰り延べ）。
//
// 🔴 **実 Kestrel の h2c ポートへ本当に往復させる。** 単体（モックした生成クライアント）では
// `CallOptions.Deadline` が実際に打ち切ることも、再試行が**新しい RPC を投げ直す**ことも観測できない
// —— どちらも輸送の性質だからである。#584 が「単体では足りない・結合で固定せよ」と書いたのはこの点である。
//
// 🔴 **構成から通す。** 直接 `GrpcAssumptionsClient` を組み立てるのではなく
// `AddAiStockTradingAssumptions(config)` を通す —— 固定したいのは「呼び出し元**ごと**の設定が効く」であり、
// 設定が client まで届いていることまで含めて 1 本で見る必要がある。
//
// 🔴 **Testcontainers を使わない**（Docker 不要。既定 CI の PR でそのまま走る）。
public class GrpcAssumptionsClientIntegrationTests
{
    private const string ExpectedTaxRate = "0.20315";
    private const int ExpectedVersion = 3;

    private static async Task<VersionedAssumptions> ResolveAsync(
        string address, Dictionary<string, string?> extra)
    {
        var values = new Dictionary<string, string?> { ["Configuration:Grpc"] = address };
        foreach (var (k, v) in extra)
            values[k] = v;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAssumptions(config);
        await using var sp = services.BuildServiceProvider();

        return await sp.GetRequiredService<IAssumptionsProvider>().GetCurrentAsync();
    }

    // ---- timeout（試行ごとの deadline） ----

    // 陽性対照: 提供側がすぐ返せば解決する（＝deadline が正常な応答を巻き込んで殺していない）。
    [Fact]
    public async Task 提供側が応答すれば解決する()
    {
        await using var host = await GrpcStubHost.StartAsync(StubAssumptions.AlwaysOk());

        var current = await ResolveAsync(host.Address, new() { ["Configuration:GrpcTimeoutSeconds"] = "5" });

        current.IsResolved.Should().BeTrue();
        current.Version.Should().Be(ExpectedVersion);
        current.Assumptions.CapitalGainsTaxRate.Should()
            .Be(decimal.Parse(ExpectedTaxRate, CultureInfo.InvariantCulture));
        host.Stub.Calls.Should().Be(1);
    }

    // 🔴 陰性対照: 提供側が黙り込んだら **構成した deadline で打ち切って** 安全側既定へ倒れる（無限に待たない）。
    //
    // 🔴 **経過時間まで測る。** 「未解決へ倒れた」だけを見る試験は弱い —— deadline を無視して 30 秒待つ
    // 実装でも通ってしまう（実測: `CallOptions.Deadline` を `AddSeconds(30)` へ変える変異が素通りした）。
    // 固定したいのは「安全側へ倒れる」ではなく「**呼び出し元が構成した秒数で**倒れる」である。
    [Fact]
    public async Task 提供側が黙れば_構成した_deadline_で安全側既定へ倒れる()
    {
        await using var host = await GrpcStubHost.StartAsync(StubAssumptions.NeverResponds());

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var current = await ResolveAsync(host.Address, new() { ["Configuration:GrpcTimeoutSeconds"] = "1" });
        elapsed.Stop();

        current.IsResolved.Should().BeFalse("一度も取得できていなければ既定値（未解決）へ倒す");
        current.Assumptions.Should().Be(TradingAssumptionsDefaults.Create());
        host.Stub.Calls.Should().Be(1, "既定は再試行しない");
        elapsed.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(10),
            "構成した 1 秒の deadline で打ち切られること（値を無視する実装はここで落ちる）");
    }

    // ---- retry（呼び出し元ごとの試行回数） ----

    // 陽性対照: 一時的な UNAVAILABLE は MaxAttempts=2 で **2 回目に成功する**（呼ばれた回数を数える）。
    [Fact]
    public async Task 一時的な_UNAVAILABLE_は再試行して成功する()
    {
        await using var host = await GrpcStubHost.StartAsync(
            StubAssumptions.FailsThenSucceeds(StatusCode.Unavailable, failures: 1));

        var current = await ResolveAsync(host.Address, new() { ["Configuration:GrpcMaxAttempts"] = "2" });

        current.IsResolved.Should().BeTrue();
        current.Version.Should().Be(ExpectedVersion);
        host.Stub.Calls.Should().Be(2);
    }

    // 🔴 陰性対照 1: **既定は再試行しない**（REST と同じ振る舞い）。同じ提供側でも 1 回で諦める。
    [Fact]
    public async Task 既定では再試行しない()
    {
        await using var host = await GrpcStubHost.StartAsync(
            StubAssumptions.FailsThenSucceeds(StatusCode.Unavailable, failures: 1));

        var current = await ResolveAsync(host.Address, []);

        current.IsResolved.Should().BeFalse();
        host.Stub.Calls.Should().Be(1);
    }

    // 🔴 陰性対照 2: **待っても変わらない status は再試行しない**。MaxAttempts=3 でも 1 回しか呼ばない
    // ——「権限が無い」を 3 回聞き直すのは、同期クリティカルパスで安全側既定へ倒れる時間を 3 倍にするだけである。
    [Theory]
    [InlineData("PermissionDenied")]
    [InlineData("Unauthenticated")]
    [InlineData("NotFound")]
    public async Task 恒久的な失敗は再試行しない(string statusName)
    {
        var status = Enum.Parse<StatusCode>(statusName);
        await using var host = await GrpcStubHost.StartAsync(StubAssumptions.AlwaysFails(status));

        var current = await ResolveAsync(host.Address, new() { ["Configuration:GrpcMaxAttempts"] = "3" });

        current.IsResolved.Should().BeFalse();
        host.Stub.Calls.Should().Be(1);
    }

    // 🔴 陰性対照: **線上の 10 進が読めない応答も例外にしない。** IADR-0331 決定 2 が警戒するのは
    // 「写しが静かに壊れる」ことであり、その裏返しとして**壊れた線上値を掴んだときも消費側の巡回を止めない**
    // （IADR-0063 決定 5）。REST 実装の「不正応答 → null」と同じ向きである。
    [Fact]
    public async Task 線上の十進が読めなくても例外を出さず安全側既定へ倒れる()
    {
        await using var host = await GrpcStubHost.StartAsync(StubAssumptions.ReturnsMalformedDecimal());

        var current = await ResolveAsync(host.Address, new() { ["Configuration:GrpcMaxAttempts"] = "3" });

        current.IsResolved.Should().BeFalse();
        current.Assumptions.Should().Be(TradingAssumptionsDefaults.Create());
        host.Stub.Calls.Should().Be(1, "読めない応答は待っても変わらない（再試行しない）");
    }

    // 🔴 陰性対照 3: 再試行し切っても**例外は出さない**（消費側の巡回・要求処理を止めない。IADR-0063 決定 5）。
    [Fact]
    public async Task 再試行し切っても例外を出さない()
    {
        await using var host = await GrpcStubHost.StartAsync(
            StubAssumptions.AlwaysFails(StatusCode.Unavailable));

        var current = await ResolveAsync(host.Address, new() { ["Configuration:GrpcMaxAttempts"] = "3" });

        current.IsResolved.Should().BeFalse();
        host.Stub.Calls.Should().Be(3);
    }
}

// 実 Kestrel の **h2c 専用ポート**（`HttpProtocols.Http2` だけ）を 127.0.0.1 の空きポートへ立てる。
// 段 0（IADR-0328 決定 3）が本番で採った形と同じ —— 平文には ALPN が無く、1 ポートで選ばせる形は避ける。
internal sealed class GrpcStubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GrpcStubHost(WebApplication app, StubAssumptions stub, string address)
    {
        _app = app;
        Stub = stub;
        Address = address;
    }

    internal StubAssumptions Stub { get; }

    internal string Address { get; }

    internal static async Task<GrpcStubHost> StartAsync(StubAssumptions stub)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // 🔴 `ListenLocalhost(0)` は使えない（Kestrel は localhost への動的ポート束縛を拒む）。
        // ループバックの IP へ直接束縛して空きポートを取る。
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(stub);

        var app = builder.Build();
        app.MapGrpcService<StubAssumptionsService>();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        return new GrpcStubHost(app, stub, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class StubAssumptionsService(StubAssumptions stub) : Proto.Assumptions.AssumptionsBase
{
    public override Task<Proto.GetAssumptionsResponse> Get(
        Proto.GetAssumptionsRequest request, ServerCallContext context) =>
        stub.HandleAsync(context.CancellationToken);
}

// 提供側の振る舞いを注入し、**実際に呼ばれた回数**を数える（再試行の有無は回数でしか観測できない）。
internal sealed class StubAssumptions(Func<int, CancellationToken, Task<Proto.GetAssumptionsResponse>> handler)
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    internal Task<Proto.GetAssumptionsResponse> HandleAsync(CancellationToken cancellationToken) =>
        handler(Interlocked.Increment(ref _calls), cancellationToken);

    internal static StubAssumptions AlwaysOk() =>
        new((_, _) => Task.FromResult(Ok()));

    // deadline が打ち切るまで返さない（呼び出し側のキャンセルで解ける＝サーバ側にリークを残さない）。
    internal static StubAssumptions NeverResponds() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        });

    internal static StubAssumptions FailsThenSucceeds(StatusCode status, int failures) =>
        new((call, _) => call <= failures
            ? throw new RpcException(new Status(status, $"stub failure #{call}"))
            : Task.FromResult(Ok()));

    // 線上の 10 進が読めない応答（提供側の写しが壊れた場合・別実装のピアが繋がった場合）。
    internal static StubAssumptions ReturnsMalformedDecimal() =>
        new((_, _) =>
        {
            var response = Ok();
            response.Assumptions.CapitalGainsTaxRate = "not-a-decimal";
            return Task.FromResult(response);
        });

    internal static StubAssumptions AlwaysFails(StatusCode status) =>
        new((call, _) => throw new RpcException(new Status(status, $"stub failure #{call}")));

    private static Proto.GetAssumptionsResponse Ok() =>
        new()
        {
            Version = 3,
            Assumptions = new Proto.TradingAssumptions
            {
                CapitalGainsTaxRate = "0.20315",
                JapanCommission = new Proto.CommissionSchedule { Rate = "0", Minimum = "0", Cap = "0" },
                UnitedStatesCommission = new Proto.CommissionSchedule { Rate = "0", Minimum = "0", Cap = "0" },
                FxSpreadRatio = "0",
                MinimumExpectedProfitMultiple = "2",
                CostLimits = new Proto.MonthlyCostLimits
                {
                    Total = "20000",
                    Llm = "15000",
                    Infrastructure = "5000",
                    Data = "0",
                },
            },
        };
}
