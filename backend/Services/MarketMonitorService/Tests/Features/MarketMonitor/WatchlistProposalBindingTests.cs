extern alias ReportWorker;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using MarketMonitorService.Domain;
using MarketMonitorService.Features.MarketMonitor.ApplyWatchlistProposal;
using MarketMonitorService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportRevise = ReportWorker::ReportService.Features.Reports.RevisePolicy;
using ReportWatchlist = ReportWorker::ReportService.Features.Reports.WatchlistProposal;

namespace MarketMonitorService.Tests;

// 🔴 NFR-06, FR-13, FR-14, ADR-0042 決定 1, IADR-0634: 監視銘柄の入れ替えの適用は、提供側（市場監視）で、報告書の台帳の
// 「その版で確定された（確定は第 2 要素つき）・未適用の案」と要求を結び付けて確かめる。Enforce で結び付かなければ 403（gRPC は
// PERMISSION_DENIED）で 1 件も適用しない。Audit は記録だけ、Off は照会しない。ボットの渡す任意の銘柄の一覧では入れ替えない。
public class WatchlistProposalBindingTests
{
    private const string ProposalRef = "daily-2026-09-28-v3";
    private static readonly MonitoredSymbol Aapl = new("AAPL", Market.UnitedStates);
    private static readonly MonitoredSymbol Msft = new("MSFT", Market.UnitedStates);
    private static readonly ProposedWatchlistChange AddNvda = new(ProposedWatchlistAction.Add, "NVDA", "AI 需要");

    private sealed class FakeSource(WatchlistProposalLookupResult result) : IWatchlistProposalSource
    {
        public List<(string Key, int Version)> Calls { get; } = [];

        public WatchlistProposalLookupResult Result { get; set; } = result;

        public List<(string Key, int Version)> Reservations { get; } = [];

        /// <summary>台帳と同じく、予約は 1 回だけ通る（2 回目は AlreadyReserved）。null なら届かない。</summary>
        public bool? Reservable { get; set; } = true;

        public Task<WatchlistProposalLookupResult> FindAsync(string periodKey, int version, CancellationToken cancellationToken)
        {
            Calls.Add((periodKey, version));
            return Task.FromResult(Result);
        }

        public Task<WatchlistProposalReserveStatus> ReserveAsync(string periodKey, int version, CancellationToken cancellationToken)
        {
            Reservations.Add((periodKey, version));
            if (Reservable is null)
                return Task.FromResult(WatchlistProposalReserveStatus.Unavailable);
            var first = Reservable.Value && Reservations.Count(r => r == (periodKey, version)) == 1;
            return Task.FromResult(first ? WatchlistProposalReserveStatus.Reserved : WatchlistProposalReserveStatus.AlreadyReserved);
        }
    }

    private static BoundWatchlistProposal Proposal(
        bool applied = false, bool verified = true, bool snapshot = true, bool reserved = false) =>
        new(
            [new BoundWatchlistChange("add", "NVDA", "台帳の理由")],
            snapshot ? [new BoundWatchlistSnapshotEntry("AAPL", "UnitedStates"), new BoundWatchlistSnapshotEntry("MSFT", "UnitedStates")] : null,
            applied,
            verified,
            reserved);

    private static WatchlistProposalLookupResult Found(BoundWatchlistProposal proposal) => new(WatchlistProposalLookupStatus.Found, proposal);

    private static WatchlistProposalBindingGate Gate(WatchlistProposalBindingMode mode, IWatchlistProposalSource source) =>
        new(new WatchlistProposalBindingOptions { Mode = mode }, source, NullLogger<WatchlistProposalBindingGate>.Instance);

    [Fact]
    public async Task 確定済み未適用で案と一致すれば結び付き照会は会話キーと版で行う()
    {
        var source = new FakeSource(Found(Proposal()));

        var decision = await Gate(WatchlistProposalBindingMode.Enforce, source).CheckAsync(ProposalRef, [Msft, Aapl], [AddNvda], default);

        (decision.Outcome, decision.Allowed).Should().Be((WatchlistProposalBindingOutcome.Bound, true));
        source.Calls.Should().Equal(("daily-2026-09-28", 3));
        source.Reservations.Should().Equal(("daily-2026-09-28", 3));
        // 🔴 適用と変更履歴には台帳の理由を使う（要求の理由「AI 需要」ではない）。
        decision.BoundChanges.Should().Equal(new ProposedWatchlistChange(ProposedWatchlistAction.Add, "NVDA", "台帳の理由"));
    }

    // 🔴 IADR-0634 決定 8: 同じ確定済みの案の 2 度目は、台帳の予約が 1 回だけなので拒む（監視銘柄を変えない案でも）。
    [Fact]
    public async Task 同じ案の2度目の適用は予約が通らず拒まれる()
    {
        var source = new FakeSource(Found(Proposal()));
        var gate = Gate(WatchlistProposalBindingMode.Enforce, source);

        (await gate.CheckAsync(ProposalRef, [Aapl, Msft], [AddNvda], default)).Allowed.Should().BeTrue();
        (await gate.CheckAsync(ProposalRef, [Aapl, Msft], [AddNvda], default))
            .Should().Be(new WatchlistProposalBindingDecision(WatchlistProposalBindingOutcome.AlreadyApplied, false));
    }

    [Fact]
    public async Task 一致しない要求では予約しない()
    {
        var source = new FakeSource(Found(Proposal()));

        (await Gate(WatchlistProposalBindingMode.Enforce, source).CheckAsync(ProposalRef, [Aapl, Msft], [new(ProposedWatchlistAction.Add, "TSLA", "r")], default))
            .Allowed.Should().BeFalse();
        source.Reservations.Should().BeEmpty("食い違う要求で予約を使い切らせない");
    }

    [Fact]
    public async Task 予約が届かなければEnforceは拒む()
    {
        var source = new FakeSource(Found(Proposal())) { Reservable = null };

        (await Gate(WatchlistProposalBindingMode.Enforce, source).CheckAsync(ProposalRef, [Aapl, Msft], [AddNvda], default))
            .Should().Be(new WatchlistProposalBindingDecision(WatchlistProposalBindingOutcome.Unavailable, false));
    }

    public static TheoryData<string, WatchlistProposalLookupResult, MonitoredSymbol[], ProposedWatchlistChange[], WatchlistProposalBindingOutcome> Unbound() => new()
    {
        // 🔴 ボットが案に無い銘柄を足す・別の銘柄へ替える・操作を替える・件数を変える。
        { ProposalRef, Found(Proposal()), [Aapl, Msft], [new(ProposedWatchlistAction.Add, "TSLA", "r")], WatchlistProposalBindingOutcome.Mismatch },
        { ProposalRef, Found(Proposal()), [Aapl, Msft], [new(ProposedWatchlistAction.Remove, "NVDA", "r")], WatchlistProposalBindingOutcome.Mismatch },
        { ProposalRef, Found(Proposal()), [Aapl, Msft], [AddNvda, new(ProposedWatchlistAction.Add, "TSLA", "r")], WatchlistProposalBindingOutcome.Mismatch },
        // 期待値（案を作った時点の監視銘柄）が台帳と違う（楽観排他の基準のすり替え）。
        { ProposalRef, Found(Proposal()), [Aapl], [AddNvda], WatchlistProposalBindingOutcome.Mismatch },
        { ProposalRef, Found(Proposal()), [Aapl, Msft, new("TSLA", Market.UnitedStates)], [AddNvda], WatchlistProposalBindingOutcome.Mismatch },
        { ProposalRef, Found(Proposal(applied: true)), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.AlreadyApplied },
        { ProposalRef, Found(Proposal(reserved: true)), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.AlreadyApplied },
        // 🔴 確定が第 2 要素で Verified だった記録が無い（段 1 で確認コードなしに確定した案）は、段 2 では拒む。
        { ProposalRef, Found(Proposal(verified: false)), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.SecondFactorNotVerified },
        { ProposalRef, Found(Proposal(snapshot: false)), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.SnapshotUnknown },
        { ProposalRef, new(WatchlistProposalLookupStatus.NotConfirmed, null), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.NotConfirmed },
        { ProposalRef, new(WatchlistProposalLookupStatus.NotProposal, null), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.NotProposal },
        { ProposalRef, new(WatchlistProposalLookupStatus.Unavailable, null), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.Unavailable },
        { ProposalRef, new(WatchlistProposalLookupStatus.Invalid, null), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.MalformedReference },
        // 案の出所が「会話キー-v版」でない（任意の文字列で台帳を引かせない）。
        { "manual-apply", Found(Proposal()), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.MalformedReference },
        { "daily-v0", Found(Proposal()), [Aapl, Msft], [AddNvda], WatchlistProposalBindingOutcome.MalformedReference },
    };

    [Theory]
    [MemberData(nameof(Unbound))]
    public async Task 結び付かなければEnforceは拒みAuditは記録だけで通す(
        string proposalRef, WatchlistProposalLookupResult found, MonitoredSymbol[] expected, ProposedWatchlistChange[] changes,
        WatchlistProposalBindingOutcome outcome)
    {
        (await Gate(WatchlistProposalBindingMode.Enforce, new FakeSource(found)).CheckAsync(proposalRef, expected, changes, default))
            .Should().Be(new WatchlistProposalBindingDecision(outcome, false));
        (await Gate(WatchlistProposalBindingMode.Audit, new FakeSource(found)).CheckAsync(proposalRef, expected, changes, default))
            .Should().Be(new WatchlistProposalBindingDecision(outcome, true));
    }

    [Fact]
    public async Task Offは照会しない()
    {
        var source = new FakeSource(Found(Proposal()));

        (await Gate(WatchlistProposalBindingMode.Off, source).CheckAsync(ProposalRef, [Aapl], [AddNvda], default))
            .Should().Be(new WatchlistProposalBindingDecision(WatchlistProposalBindingOutcome.Skipped, true));
        source.Calls.Should().BeEmpty();
    }

    // ---- 台帳の照会の口（報告書の GET /reports/policy-revisions/watchlist-proposal/binding） ----

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpWatchlistProposalSource Source(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://report-service:8080") }, NullLogger<HttpWatchlistProposalSource>.Instance);

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, WatchlistProposalLookupStatus.Invalid)]
    [InlineData(HttpStatusCode.NotFound, WatchlistProposalLookupStatus.NotProposal)]
    [InlineData(HttpStatusCode.Conflict, WatchlistProposalLookupStatus.NotConfirmed)]
    [InlineData(HttpStatusCode.Forbidden, WatchlistProposalLookupStatus.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, WatchlistProposalLookupStatus.Unavailable)]
    public async Task 照会の応答の状態を台帳の判定と届かないに分ける(HttpStatusCode status, WatchlistProposalLookupStatus expected)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        (await Source(handler).FindAsync("daily-2026-09-28", 3, default)).Status.Should().Be(expected);
        handler.Requests.Should().ContainSingle().Which.PathAndQuery
            .Should().Be("/reports/policy-revisions/watchlist-proposal/binding?periodKey=daily-2026-09-28&version=3");
    }

    // 報告書の JSON 設定（Web の既定＋列挙は名前。ReportService の Program と同じ。送り手の固定は報告書の試験が持つ）。
    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    // IADR-0420: 結び付きの照会の応答は、報告書の本物の応答型（WatchlistProposalBindingView）を報告書の設定で直列化したものから読める。
    [Fact]
    public async Task 照会の応答は報告書の本物の型から読め読めなければ届かないとする()
    {
        ReportWatchlist.WatchlistProposalBindingView sent = new(
            new ReportWatchlist.WatchlistProposalView(
                Guid.NewGuid(), "daily-2026-09-28", 3,
                [new ReportRevise.WatchlistChangeView("add", "NVDA", "AI 需要")],
                [new ReportWatchlist.WatchlistSnapshotEntryView("AAPL", "UnitedStates")],
                ApplyRecorded: false),
            SecondFactorVerified: true,
            ApplyReserved: false);
        var ok = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(sent, ReportWire), System.Text.Encoding.UTF8, "application/json"),
        });

        var found = await Source(ok).FindAsync("daily-2026-09-28", 3, default);

        found.Status.Should().Be(WatchlistProposalLookupStatus.Found);
        found.Proposal!.Changes.Should().Equal(new BoundWatchlistChange("add", "NVDA", "AI 需要"));
        found.Proposal.Snapshot.Should().Equal(new BoundWatchlistSnapshotEntry("AAPL", "UnitedStates"));
        found.Proposal.ApplyRecorded.Should().BeFalse();
        found.Proposal.SecondFactorVerified.Should().BeTrue();
        found.Proposal.ApplyReserved.Should().BeFalse();

        var unknown = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(sent with { Proposal = sent.Proposal with { Snapshot = null } }, ReportWire)),
        });
        (await Source(unknown).FindAsync("k", 1, default)).Proposal!.Snapshot.Should().BeNull("照会できなかった監視銘柄を空に化けさせない");

        var broken = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"proposal\":null}") });
        (await Source(broken).FindAsync("k", 1, default)).Status.Should().Be(WatchlistProposalLookupStatus.Unavailable);
        var thrown = new StubHandler(_ => throw new HttpRequestException("down"));
        (await Source(thrown).FindAsync("k", 1, default)).Status.Should().Be(WatchlistProposalLookupStatus.Unavailable);
    }

    // ---- 本番の組み立て（Program.cs）を通す ----

    private static WebApplicationFactory<Program> Configured(
        MonitorWorkerWebApplicationFactory baseFactory, string mode, IWatchlistProposalSource? source) =>
        baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Monitor:DelegatedActor:TrustedClientIds", "ai-stock-trading-owner");
            b.UseSetting("Watchlist:ProposalBinding:Mode", mode);
            if (source is not null)
            {
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<WatchlistProposalBindingGate>();
                    s.AddSingleton(_ => new WatchlistProposalBindingGate(
                        new WatchlistProposalBindingOptions { Mode = Enum.Parse<WatchlistProposalBindingMode>(mode) }, source,
                        NullLogger<WatchlistProposalBindingGate>.Instance));
                });
            }
        });

    private static HttpClient Bot(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, "ai-stock-trading-owner");
        return client;
    }

    private static HttpClient Owner(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static async Task<List<string>> SymbolsAsync(WebApplicationFactory<Program> factory) =>
        [.. (await Owner(factory).GetFromJsonAsync<JsonElement>("/monitor/watchlist")).EnumerateArray().Select(e => e.GetProperty("symbol").GetString()!)];

    // 台帳の案（期待値＝いまの監視銘柄・入れ替え＝NVDA の追加）を、立てたホストの監視銘柄から作る。
    private static async Task<List<string>> LedgerOfAsync(WebApplicationFactory<Program> factory, FakeSource source)
    {
        var before = await SymbolsAsync(factory);
        source.Result = Found(new BoundWatchlistProposal(
            [new BoundWatchlistChange("add", "NVDA", "台帳の理由")],
            [.. before.Select(s => new BoundWatchlistSnapshotEntry(s, "UnitedStates"))],
            ApplyRecorded: false,
            SecondFactorVerified: true));
        return before;
    }

    private static object Body(IEnumerable<string> expected, string symbol) => new
    {
        ExpectedWatchlist = expected.Select(s => new { Symbol = s, Market = Market.UnitedStates }).ToArray(),
        Changes = new[] { new { Action = "add", Symbol = symbol, Reason = "r" } },
        ProposalRef,
        OnBehalfOf = "developer",
    };

    [Fact]
    public async Task Enforceは台帳の案と一致する要求だけを適用し違えば403で何も適用しない()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        var source = new FakeSource(new(WatchlistProposalLookupStatus.Unavailable, null));
        await using var factory = Configured(baseFactory, "Enforce", source);
        var before = await LedgerOfAsync(factory, source);

        var forged = await Bot(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(before, "TSLA"));
        forged.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SymbolsAsync(factory)).Should().NotContain("TSLA");

        var bound = await Bot(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(before, "NVDA"));
        bound.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SymbolsAsync(factory)).Should().Contain("NVDA");
        // 変更履歴の理由は台帳の確定済みの値（要求の理由 "r" ではない）。
        var history = await Owner(factory).GetFromJsonAsync<JsonElement>("/monitor/watchlist/history");
        history.EnumerateArray().Should().Contain(h => h.GetProperty("reason").GetString()!.StartsWith("台帳の理由"))
            .And.NotContain(h => h.GetProperty("reason").GetString()!.StartsWith("r（") || h.GetProperty("reason").GetString() == "r");

        // 🔴 同じ確定済みの案の 2 度目は、台帳の予約が 1 回だけなので 403（同じ要求を送り直しても適用しない）。
        (await Bot(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(before, "NVDA")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // 宛先（Reports:BaseUrl）が無い本番の組み立てで Enforce なら、照会できないので拒む（閉じる向き）。Audit は記録だけで適用する。
    [Theory]
    [InlineData("Enforce", HttpStatusCode.Forbidden)]
    [InlineData("Audit", HttpStatusCode.OK)]
    public async Task 台帳へ届かなければEnforceは拒みAuditは適用する(string mode, HttpStatusCode expected)
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        await using var factory = Configured(baseFactory, mode, null);
        var before = await SymbolsAsync(factory);

        var response = await Bot(factory).PostAsJsonAsync("/monitor/watchlist/proposal-apply", Body(before, "NVDA"));

        response.StatusCode.Should().Be(expected);
    }

    // gRPC 面も同じ処理関数を通る（403 → PERMISSION_DENIED。1 件も適用しない）。
    [Fact]
    public async Task gRPC面も結び付かなければPERMISSION_DENIED()
    {
        await using var baseFactory = new MonitorWorkerWebApplicationFactory();
        var source = new FakeSource(new(WatchlistProposalLookupStatus.Unavailable, null));
        await using var factory = Configured(baseFactory, "Enforce", source);
        var before = await LedgerOfAsync(factory, source);
        var request = new Proto.WatchlistProposalApplicationRequest
        {
            ExpectedWatchlist = new Proto.WatchlistItems(),
            Changes = new Proto.WatchlistChangeInstructions(),
            ProposalRef = ProposalRef,
            OnBehalfOf = "developer",
        };
        request.ExpectedWatchlist.Items.AddRange(before.Select(s => new Proto.WatchlistItem { Symbol = s, Market = Proto.Market.UnitedStates }));
        request.Changes.Items.Add(new Proto.WatchlistChangeInstruction { Action = "add", Symbol = "TSLA", Reason = "r" });
        var client = new Proto.WatchlistOwnerWrite.WatchlistOwnerWriteClient(GrpcChannel.ForAddress(
            factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = new BotHeaders(factory.Server.CreateHandler()) }));

        var act = async () => await client.ApplyWatchlistProposalAsync(request);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        (await SymbolsAsync(factory)).Should().NotContain("TSLA");
    }

    private sealed class BotHeaders(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, "trading-owner");
            request.Headers.TryAddWithoutValidation(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
            request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, "ai-stock-trading-owner");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
