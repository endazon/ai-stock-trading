extern alias AuditWorker;

using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Web;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using Wolverine;
using Xunit;
using AuditDb = AuditWorker::AuditService.Infrastructure.Persistence;
using AuditEntryFactory = AuditWorker::AuditService.Domain.AuditEntryFactory;
using AuditProgram = AuditWorker::Program;
using AuditReadWireMapping = AuditWorker::AuditService.Features.AuditEvents.AuditReadWireMapping;
using IAuditEventStore = AuditWorker::AuditService.Features.AuditEvents.IAuditEventStore;
using Proto = AiStockTrading.Shared.Grpc.Audit.V1;

namespace ReportService.Tests;

// T-10-1672, T-10-1673, T-10-1675, T-10-1676, T-10-1677, NFR, FR-06, FR-11, FR-10, FR-16, IADR-0352, IADR-0445, #1059 (#753):
// 報告書が gRPC で読む監査台帳の 6 つの供給元（為替の情報源・LLM 利用実績・借株料・承認の手法・解決結果・判断根拠）の
// **原則 A**・**REST との同値（本物の提供側）**・**timeout / retry**・**依存先の門と観測（#840）**・**照会の窓と種別**。
//
// 🔴 偽の提供側は実 Kestrel の h2c（127.0.0.1）で本当に往復させる（AuditReadStubHost）。欠落は proto3 の optional の「無い」で線に乗るので、
// 受け手の写しが「無い」を「契約の食い違い＝未供給」として扱っているかは、実際に符号化・復号された message でしか確かめられない。
public class GrpcAuditLedgerSourcesTests
{
    private static readonly DateOnly From = new(2026, 8, 3);
    private static readonly DateOnly To = new(2026, 8, 3);
    private static readonly DateTimeOffset T0 = new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);

    private static (ServiceProvider Sp, AuditGrpcTransport Transport) Compose(
        string address, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["Audit:Grpc"] = address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ReportDependencyProbe>();
        services.AddAiStockTradingAuditGrpc(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<AuditGrpcTransport>());
    }

    private static ILogger<T> Log<T>() => NullLogger<T>.Instance;

    // 6 つの供給元を名前で呼び分ける（結果は「未供給か」だけを見る試験で使う）。
    public static TheoryData<string> Sources => new()
    {
        "為替の情報源", "LLM 利用実績", "借株料", "承認の手法", "解決結果", "判断根拠",
    };

    private static Task<object?> ReadAsync(string source, AuditGrpcTransport t) => source switch
    {
        "為替の情報源" => Box(new GrpcFxSourceStatusSource(t, Log<GrpcFxSourceStatusSource>()).GetStatusAsync(From, To)),
        "LLM 利用実績" => Box(new GrpcLlmUsageRecordSource(t, Log<GrpcLlmUsageRecordSource>()).GetUsageAsync(From, To)),
        "借株料" => Box(new GrpcBorrowFeeRecordSource(t, Log<GrpcBorrowFeeRecordSource>()).GetBorrowFeesAsync(From, To)),
        "承認の手法" => Box(new GrpcStopLossMethodUsageSource(t, Log<GrpcStopLossMethodUsageSource>()).GetUsageAsync(From, To)),
        "解決結果" => Box(new GrpcStopLossMethodResolutionSource(t, Log<GrpcStopLossMethodResolutionSource>()).GetResolutionsAsync(From, To)),
        _ => Box(new GrpcTradeRationaleSource(t, Log<GrpcTradeRationaleSource>()).GetRationalesAsync(From, To)),
    };

    private static async Task<object?> Box<T>(Task<T> task) => await task;

    private static Proto.LedgerRecord Record(string eventType, string detail = "{}") =>
        new() { Id = Guid.NewGuid().ToString("D"), EventType = eventType, Detail = detail };

    private static async Task<object?> ReadThroughStubAsync(string source, params Proto.LedgerRecord[] records)
    {
        await using var host = await AuditReadStubHost.StartAsync(
            new AuditReadStubBehavior { EventsByType = AuditReadStubBehavior.Returns(records) });
        var (sp, t) = Compose(host.Address);
        await using (sp)
        {
            var result = await ReadAsync(source, t);
            host.Behavior.Calls.Should().Be(1);
            return result;
        }
    }

    // ---- T-10-1672: 原則 A（欠けた記録で応答全体が未供給。空は「事象なし」） ----

    // 🔴 id・種別・本文のどれかが欠けた記録を既定値（空の GUID・空文字）で作らない —— 応答全体を未供給にする。
    // REST では本文の欠落は例外経由で未供給、種別の欠落は「要求していない種別」として黙って捨てられ、1 件しか無ければ「事象なし」に化けた。
    [Theory]
    [MemberData(nameof(Sources))]
    public async Task T_10_1672_id_種別_本文の欠けた記録があれば応答全体を未供給にする(string source)
    {
        foreach (var how in new[] { "id なし", "読めない id", "種別なし", "空の種別", "本文なし" })
        {
            var broken = Record(nameof(FxRateStale));
            switch (how)
            {
                case "id なし": broken.ClearId(); break;
                case "読めない id": broken.Id = "not-a-guid"; break;
                case "種別なし": broken.ClearEventType(); break;
                case "空の種別": broken.EventType = string.Empty; break;
                default: broken.ClearDetail(); break;
            }

            (await ReadThroughStubAsync(source, Record(nameof(FxRateStale)), broken)).Should().BeNull($"{source}: {how}");
        }
    }

    // 空の応答は「事象なし」（未供給ではない）。ここを null にすると「照会できませんでした」が常に出る。
    [Theory]
    [MemberData(nameof(Sources))]
    public async Task T_10_1672_空の応答は未供給ではなく事象なし(string source)
    {
        (await ReadThroughStubAsync(source)).Should().NotBeNull(source);
    }

    // 取得の失敗は未供給（空＝「事象なし」へ倒さない）。
    [Theory]
    [MemberData(nameof(Sources))]
    public async Task T_10_1672_照会に失敗すれば未供給(string source)
    {
        await using var host = await AuditReadStubHost.StartAsync(
            new AuditReadStubBehavior { EventsByType = AuditReadStubBehavior.Fails(StatusCode.Internal) });
        var (sp, t) = Compose(host.Address);
        await using (sp)
        {
            (await ReadAsync(source, t)).Should().BeNull(source);
        }
    }

    // 本文の読めない 1 件は REST と同じ解釈（共有の Build）で扱う —— 期間全体は落とさず、件数から除いて数を返す。
    [Fact]
    public async Task T_10_1672_本文の読めない記録は_REST_と同じく_1_件だけ除く()
    {
        var approved = new OrderApproved(
            Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 200m),
            10, T0, StopLossMethod: StopLossExecutionMethod.NoProtectiveStop);
        var good = AuditReadWireMapping.ToProto(AuditEntryFactory.From(approved, Guid.NewGuid(), T0));

        var usage = (StopLossMethodUsage?)await ReadThroughStubAsync(
            "承認の手法", good, Record(nameof(OrderApproved), "{not json"));

        usage.Should().NotBeNull();
        usage!.Counts.Should().ContainSingle();
        usage.UnreadableCount.Should().Be(1);
    }

    // ---- T-10-1673: REST と同値（本物の監査サービスの Program.cs を提供側にして、送り手の本物の記録を両輸送で読む） ----

    // 🔴 送り手の本物の記録の組み立て（AuditEntryFactory）で台帳へ書き、**本物の提供側**の REST と gRPC の両方から 6 つの供給元で読む。
    // どちらかの写し（提供側の AuditReadWireMapping・受け手の AuditGrpcTransport.ToEntry）や窓・種別を取り違えれば結果が食い違って赤になる。
    [Fact]
    public async Task T_10_1673_本物の提供側から_REST_と_gRPC_で読んだ_6_つの供給元は同じ値になる()
    {
        await using var audit = new AuditHost();
        var decisionId = Guid.NewGuid();
        var entries = new[]
        {
            AuditEntryFactory.From(new FxRateSourceFellBack("USD", "fred", 2, 2, T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new FxRateSourceUsed("USD", "fred", 2, 2, T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new LlmCostIncurred(3_000m, T0, LlmPurposes.TradeDecision, "claude-sonnet-5"), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new LlmFallbackFired("report-daily", "a", "b", "FallbackFired", T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "a", null, T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new BorrowFeeAccrued("AAPL", Market.UnitedStates, From, 0.06m, 10_000m, 1.64m, T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new BorrowFeeAccrualUnavailable("TSLA", Market.UnitedStates, From, "照会失敗", T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new OrderApproved(
                decisionId,
                new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 200m),
                10, T0, StopLossMethod: StopLossExecutionMethod.NoProtectiveStop), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new StopLossMethodResolved(
                decisionId, "AAPL", Market.UnitedStates, ProductType.Cash, StopLossExecutionMethod.NoProtectiveStop,
                AppliedMethod: null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal, T0), Guid.NewGuid(), T0),
            AuditEntryFactory.From(new TradeDecisionMade(
                decisionId,
                new OrderIntent("7203", Market.Japan, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 100, 2_500m),
                "始値が支持線で反発。",
                T0), Guid.NewGuid(), T0),
        };
        using (var scope = audit.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAuditEventStore>();
            foreach (var entry in entries)
                store.Append(entry);
        }

        var rest = audit.CreateClient();
        var channel = GrpcChannel.ForAddress(
            audit.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = audit.Server.CreateHandler() });
        using var grpc = new AuditGrpcTransport(
            channel, TimeSpan.FromSeconds(10), 1, new ReportDependencyProbe(), null, Log<AuditGrpcTransport>());

        var restFx = await new HttpFxSourceStatusSource(rest, Log<HttpFxSourceStatusSource>()).GetStatusAsync(From, To);
        var grpcFx = await new GrpcFxSourceStatusSource(grpc, Log<GrpcFxSourceStatusSource>()).GetStatusAsync(From, To);
        restFx!.FellBacks.Should().ContainSingle("空どうしの一致は何も証明しない");
        grpcFx.Should().BeEquivalentTo(restFx);

        var restLlm = await new HttpLlmUsageRecordSource(rest, Log<HttpLlmUsageRecordSource>()).GetUsageAsync(From, To);
        var grpcLlm = await new GrpcLlmUsageRecordSource(grpc, Log<GrpcLlmUsageRecordSource>()).GetUsageAsync(From, To);
        restLlm!.Costs.Should().ContainSingle();
        grpcLlm.Should().BeEquivalentTo(restLlm);

        var restFee = await new HttpBorrowFeeRecordSource(rest, Log<HttpBorrowFeeRecordSource>()).GetBorrowFeesAsync(From, To);
        var grpcFee = await new GrpcBorrowFeeRecordSource(grpc, Log<GrpcBorrowFeeRecordSource>()).GetBorrowFeesAsync(From, To);
        restFee!.Accruals.Should().ContainSingle().Which.AmountUsd.Should().Be(1.64m);
        grpcFee.Should().BeEquivalentTo(restFee);

        var restUsage = await new HttpStopLossMethodUsageSource(rest, Log<HttpStopLossMethodUsageSource>()).GetUsageAsync(From, To);
        var grpcUsage = await new GrpcStopLossMethodUsageSource(grpc, Log<GrpcStopLossMethodUsageSource>()).GetUsageAsync(From, To);
        restUsage!.Counts.Should().ContainSingle();
        grpcUsage.Should().BeEquivalentTo(restUsage);

        var restResolved = await new HttpStopLossMethodResolutionSource(rest, Log<HttpStopLossMethodResolutionSource>())
            .GetResolutionsAsync(From, To);
        var grpcResolved = await new GrpcStopLossMethodResolutionSource(grpc, Log<GrpcStopLossMethodResolutionSource>())
            .GetResolutionsAsync(From, To);
        restResolved!.Resolutions.Should().ContainSingle();
        grpcResolved.Should().BeEquivalentTo(restResolved);

        var restRationale = await new HttpTradeRationaleSource(rest, Log<HttpTradeRationaleSource>()).GetRationalesAsync(From, To);
        var grpcRationale = await new GrpcTradeRationaleSource(grpc, Log<GrpcTradeRationaleSource>()).GetRationalesAsync(From, To);
        restRationale.Should().ContainKey(decisionId);
        grpcRationale.Should().BeEquivalentTo(restRationale);
    }

    // ---- T-06-073: 発生時刻（OccurredAt）を両経路で運ぶ（#1255 / IADR-0516 の 2026-10-08 追記） ----

    // FR-06, #1255, IADR-0516（2026-10-08 追記）: 本物の提供側の REST と gRPC の両方から読んだ承認の供給は、本文を復元できなかった記録の
    // 発生時刻を同じ値で持つ（REST は応答の occurredAt、gRPC は occurred_at）。時刻の取り違え・片側の落としは赤になる。
    [Fact]
    public async Task T_06_073_本文の読めない記録の発生時刻を_REST_と_gRPC_の両方で同じ値で運ぶ()
    {
        await using var audit = new AuditHost();
        var approved = new OrderApproved(
            Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 200m),
            10, T0, StopLossMethod: StopLossExecutionMethod.BrokerStopOrder);
        var unreadableAt = T0.AddHours(2).AddTicks(1234567);
        using (var scope = audit.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IAuditEventStore>();
            store.Append(AuditEntryFactory.From(approved, Guid.NewGuid(), T0));
            store.Append(AuditEntryFactory.From(approved, Guid.NewGuid(), T0) with
            {
                Id = Guid.NewGuid(),
                Detail = "{not json",
                OccurredAt = unreadableAt,
            });
        }

        var rest = audit.CreateClient();
        var channel = GrpcChannel.ForAddress(
            audit.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = audit.Server.CreateHandler() });
        using var grpc = new AuditGrpcTransport(
            channel, TimeSpan.FromSeconds(10), 1, new ReportDependencyProbe(), null, Log<AuditGrpcTransport>());

        var restUsage = await new HttpStopLossMethodUsageSource(rest, Log<HttpStopLossMethodUsageSource>()).GetUsageAsync(From, To);
        var grpcUsage = await new GrpcStopLossMethodUsageSource(grpc, Log<GrpcStopLossMethodUsageSource>()).GetUsageAsync(From, To);

        restUsage!.UnreadableCount.Should().Be(1);
        restUsage.UnreadableOccurredAt.Should().Equal(unreadableAt);
        grpcUsage!.UnreadableOccurredAt.Should().Equal(unreadableAt);
        grpcUsage.Should().BeEquivalentTo(restUsage);
    }

    // FR-06, #1255: gRPC の occurred_at が無い記録（旧版の提供側）は時刻なし（null）で受け、在るのに読めない値は id と同じく契約の食い違い
    // （応答全体を未供給）。在る値は往復書式のまま読む。線上で実際に符号化・復号された記録で確かめる（偽の提供側）。
    [Fact]
    public async Task T_06_073_gRPC_の発生時刻は無ければ時刻なし_読めなければ応答全体を未供給にする()
    {
        var absent = Record(nameof(OrderApproved), "{not json");
        var present = Record(nameof(OrderApproved), "{not json");
        present.OccurredAt = "2026-08-03T19:00:00.1234567+09:00";
        // 秒精度の ISO 8601 も受ける（REST の JSON と同じく往復書式に縛らない）。
        var secondPrecision = Record(nameof(OrderApproved), "{not json");
        secondPrecision.OccurredAt = "2026-08-03T20:00:00+09:00";

        var usage = (StopLossMethodUsage?)await ReadThroughStubAsync("承認の手法", absent, present, secondPrecision);

        usage.Should().NotBeNull();
        usage!.UnreadableCount.Should().Be(3);
        usage.UnreadableOccurredAt.Should().Equal(
            null,
            new DateTimeOffset(2026, 8, 3, 19, 0, 0, TimeSpan.FromHours(9)).AddTicks(1234567),
            new DateTimeOffset(2026, 8, 3, 20, 0, 0, TimeSpan.FromHours(9)));

        foreach (var garbage in new[] { "not-a-time", "", "2026-13-45T99:00:00+09:00" })
        {
            var broken = Record(nameof(OrderApproved), "{not json");
            broken.OccurredAt = garbage;
            (await ReadThroughStubAsync("承認の手法", broken)).Should().BeNull($"読めない発生時刻「{garbage}」");
        }
    }

    // ---- T-10-1677: 照会の窓と種別は REST と同じ ----

    // 🔴 窓（JST の半開区間・解決結果は前後 1 日を含む）と引く種別を、REST の要求（クエリ）と gRPC の要求で突き合わせる。
    [Theory]
    [MemberData(nameof(Sources))]
    public async Task T_10_1677_照会の窓と引く種別は_REST_の要求と同じ(string source)
    {
        var capture = new QueryCapture();
        var http = new HttpClient(capture) { BaseAddress = new Uri("http://audit") };
        await ReadRestAsync(source, http);

        await using var host = await AuditReadStubHost.StartAsync(new AuditReadStubBehavior());
        var (sp, t) = Compose(host.Address);
        await using (sp)
        {
            await ReadAsync(source, t);
        }

        var query = HttpUtility.ParseQueryString(capture.Last!.Query);
        var grpcRequest = host.Behavior.LastRequest!;
        DateTimeOffset.Parse(grpcRequest.From).Should().Be(DateTimeOffset.Parse(query["from"]!), source);
        DateTimeOffset.Parse(grpcRequest.To).Should().Be(DateTimeOffset.Parse(query["to"]!), source);
        grpcRequest.From.Should().Be(query["from"], "往復書式の文字列そのものも同じ");
        grpcRequest.EventTypes.Should().Equal(query["types"]!.Split(','), source);
        grpcRequest.EventTypes.Should().NotBeEmpty();
    }

    private static Task ReadRestAsync(string source, HttpClient http) => source switch
    {
        "為替の情報源" => new HttpFxSourceStatusSource(http, Log<HttpFxSourceStatusSource>()).GetStatusAsync(From, To),
        "LLM 利用実績" => new HttpLlmUsageRecordSource(http, Log<HttpLlmUsageRecordSource>()).GetUsageAsync(From, To),
        "借株料" => new HttpBorrowFeeRecordSource(http, Log<HttpBorrowFeeRecordSource>()).GetBorrowFeesAsync(From, To),
        "承認の手法" => new HttpStopLossMethodUsageSource(http, Log<HttpStopLossMethodUsageSource>()).GetUsageAsync(From, To),
        "解決結果" => new HttpStopLossMethodResolutionSource(http, Log<HttpStopLossMethodResolutionSource>()).GetResolutionsAsync(From, To),
        _ => new HttpTradeRationaleSource(http, Log<HttpTradeRationaleSource>()).GetRationalesAsync(From, To),
    };

    private sealed class QueryCapture : HttpMessageHandler
    {
        public Uri? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // ---- T-10-1675: timeout / retry（既定の deadline は REST の audit-ledger と同じ 10 秒） ----

    [Fact]
    public async Task T_10_1675_提供側が黙れば構成した_deadline_で未供給へ倒れ再試行しない()
    {
        await using var host = await AuditReadStubHost.StartAsync(new AuditReadStubBehavior
        {
            EventsByType = AuditReadStubBehavior.RespondsOnceThenHangs(),
        });
        var (sp, t) = Compose(host.Address, new() { ["Audit:GrpcTimeoutSeconds"] = "3" });
        await using (sp)
        {
            var source = new GrpcTradeRationaleSource(t, Log<GrpcTradeRationaleSource>());
            (await source.GetRationalesAsync(From, To)).Should().NotBeNull("暖機");

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var rationales = await source.GetRationalesAsync(From, To);
            elapsed.Stop();

            rationales.Should().BeNull();
            host.Behavior.Calls.Should().Be(2, "既定は再試行しない");
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "構成した 3 秒の deadline で打ち切られること");
        }
    }

    [Fact]
    public async Task T_10_1675_一時的な_UNAVAILABLE_は構成した回数だけ再試行し恒久的な失敗は再試行しない()
    {
        await using var transient = await AuditReadStubHost.StartAsync(new AuditReadStubBehavior
        {
            EventsByType = AuditReadStubBehavior.FailsThenReturns(StatusCode.Unavailable, 1),
        });
        await using var permanent = await AuditReadStubHost.StartAsync(new AuditReadStubBehavior
        {
            EventsByType = AuditReadStubBehavior.Fails(StatusCode.PermissionDenied),
        });
        var (sp1, t1) = Compose(transient.Address, new() { ["Audit:GrpcMaxAttempts"] = "2" });
        var (sp2, t2) = Compose(permanent.Address, new() { ["Audit:GrpcMaxAttempts"] = "3" });
        await using (sp1)
        await using (sp2)
        {
            (await new GrpcTradeRationaleSource(t1, Log<GrpcTradeRationaleSource>()).GetRationalesAsync(From, To))
                .Should().NotBeNull();
            (await new GrpcTradeRationaleSource(t2, Log<GrpcTradeRationaleSource>()).GetRationalesAsync(From, To))
                .Should().BeNull();
            transient.Behavior.Calls.Should().Be(2);
            permanent.Behavior.Calls.Should().Be(1);
        }
    }

    // ---- T-10-1676: 依存先の門と観測（#840 / IADR-0352）を gRPC でも失わない ----

    private sealed class FailingTokenProvider : IServiceAccessTokenProvider
    {
        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private static AuditGrpcTransport GatedTransport(
        string address, ReportDependencyProbe probe, IServiceAccessTokenProvider? tokenProvider) =>
        new(
            GrpcClientExtensions.CreateAiStockTradingChannel(address, tokenProvider ?? NoServiceAccessTokenProvider.Instance),
            TimeSpan.FromSeconds(10), 1, probe, tokenProvider, NullLogger<AuditGrpcTransport>.Instance);

    // 🔴 トークンを取れないなら**送信しない**（提供側は 1 度も呼ばれない）。未供給へ倒し、一過性として記録する。
    [Fact]
    public async Task T_10_1676_トークンを取れなければ送信せず未供給へ倒し一過性として記録する()
    {
        await using var host = await AuditReadStubHost.StartAsync(new AuditReadStubBehavior());
        var probe = new ReportDependencyProbe();
        using var transport = GatedTransport(host.Address, probe, new FailingTokenProvider());
        using var observation = probe.Begin();

        var rationales = await new GrpcTradeRationaleSource(transport, Log<GrpcTradeRationaleSource>()).GetRationalesAsync(From, To);

        rationales.Should().BeNull();
        host.Behavior.Calls.Should().Be(0, "トークンの無い要求を上流へ投げない（REST の門と同じ）");
        observation.Failures.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Dependency = "audit-ledger",
            Kind = ReportDependencyFailureKind.ServiceTokenUnavailable,
            Transient = true,
        });
    }

    // 陰性対照: 資格情報が未整備の構成（no-op）では門は素通しする（dev・単体実行。REST と同じ）。
    [Fact]
    public async Task T_10_1676_資格情報が未整備なら門は素通しする()
    {
        await using var host = await AuditReadStubHost.StartAsync(new AuditReadStubBehavior());
        var probe = new ReportDependencyProbe();
        using var transport = GatedTransport(host.Address, probe, NoServiceAccessTokenProvider.Instance);
        using var observation = probe.Begin();

        (await new GrpcTradeRationaleSource(transport, Log<GrpcTradeRationaleSource>()).GetRationalesAsync(From, To))
            .Should().NotBeNull();
        observation.Failures.Should().BeEmpty();
    }

    // 🔴 失敗の分類は REST と同じ判定（HTTP 相当へ写して ReportDependencyHandler.IsTransient）。依存先の名前は REST と同じ audit-ledger。
    [Theory]
    [InlineData("Unauthenticated", "GrpcStatus", true)]
    [InlineData("PermissionDenied", "GrpcStatus", false)]
    [InlineData("Internal", "GrpcStatus", true)]
    [InlineData("InvalidArgument", "GrpcStatus", false)]
    [InlineData("Unavailable", "Unreachable", true)]
    [InlineData("DeadlineExceeded", "Timeout", true)]
    public async Task T_10_1676_失敗を_REST_と同じ判定で一過性と恒常に分けて記録する(string status, string kind, bool transient)
    {
        await using var host = await AuditReadStubHost.StartAsync(new AuditReadStubBehavior
        {
            EventsByType = AuditReadStubBehavior.Fails(Enum.Parse<StatusCode>(status)),
        });
        var probe = new ReportDependencyProbe();
        using var transport = GatedTransport(host.Address, probe, NoServiceAccessTokenProvider.Instance);
        using var observation = probe.Begin();

        (await new GrpcTradeRationaleSource(transport, Log<GrpcTradeRationaleSource>()).GetRationalesAsync(From, To))
            .Should().BeNull();

        observation.Failures.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Dependency = "audit-ledger",
            Kind = Enum.Parse<ReportDependencyFailureKind>(kind),
            Transient = transient,
        });
    }

    // ---- 本物の監査サービス（Program.cs の組み立て・InMemory DB・外部輸送なし・サービスロールで認証） ----

    private sealed class AuditHost : WebApplicationFactory<AuditProgram>
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
            }));
            builder.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<AuditDb.AuditDbContext>)
                        || (d.ServiceType.IsGenericType
                            && d.ServiceType.GetGenericTypeDefinition().FullName?.Contains("IDbContextOptionsConfiguration") == true
                            && d.ServiceType.GenericTypeArguments.Length == 1
                            && d.ServiceType.GenericTypeArguments[0] == typeof(AuditDb.AuditDbContext)))
                    .ToList();
                foreach (var d in toRemove)
                    services.Remove(d);
                services.AddDbContext<AuditDb.AuditDbContext>(o => o.UseInMemoryDatabase(_dbName));
                services.DisableAllExternalWolverineTransports();
                services.AddAuthentication(ServiceAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, ServiceAuthHandler>(ServiceAuthHandler.SchemeName, _ => { });
            });
        }
    }

    // 報告書サービス自身の client credentials（trading-service ロール）を模す。
    private sealed class ServiceAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Service";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim("azp", "ai-stock-trading-report"), new Claim(ClaimTypes.Role, "trading-service")],
                SchemeName, ClaimTypes.Name, ClaimTypes.Role);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
