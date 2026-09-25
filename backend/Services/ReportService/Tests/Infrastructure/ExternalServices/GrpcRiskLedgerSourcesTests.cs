extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetDriftAdoptions;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetOpenPositions;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;
using RiskBuyInInferenceRecord = RiskManagementWorker::RiskManagementService.Features.RiskManagement.BuyInInferenceRecord;
using RiskLedgerFill = RiskManagementWorker::RiskManagementService.Features.RiskManagement.LedgerFill;
using RiskReadWireMapping = RiskManagementWorker::RiskManagementService.Features.RiskManagement.RiskReadWireMapping;

namespace ReportService.Tests;

// T-10-1055, T-10-1056, T-10-1058, T-10-1059, NFR, FR-06, FR-10, FR-20, FR-21, IADR-0352, IADR-0427, #997 (#753):
// 報告書が gRPC で読む取引台帳の 6 つの供給元（約定・取り込み・強制買戻し・建玉・稼働率・段階）の**原則 A**・**契約**・
// **timeout / retry**・**依存先の門と観測（#840）**。
//
// 🔴 実 Kestrel の h2c で本当に往復させる（RiskReadStubHost）。欠落は proto3 の既定値で線に乗るので、受け手の写しが
// 「無い」を「未供給」「不明」として扱っているかは、実際に符号化・復号された message でしか確かめられない。
public class GrpcRiskLedgerSourcesTests
{
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 30);

    private static (ServiceProvider Sp, RiskManagementGrpcTransport Transport) Compose(
        string address, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["RiskManagement:Grpc"] = address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ReportDependencyProbe>();
        services.AddAiStockTradingRiskManagementGrpc(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<RiskManagementGrpcTransport>());
    }

    private static ILogger<T> Log<T>() => NullLogger<T>.Instance;

    // ---- T-10-1055: 約定（倒す向きは空列） ----

    private static Proto.PeriodFill Fill(Action<Proto.PeriodFill>? tweak = null)
    {
        var f = new Proto.PeriodFill
        {
            Symbol = "AAPL",
            Market = Proto.Market.UnitedStates,
            Side = Proto.TradeSide.Buy,
            PositionEffect = Proto.PositionEffect.Open,
            Quantity = 10,
            Price = "200",
            ExecutedAt = new DateTimeOffset(2026, 9, 10, 14, 0, 0, TimeSpan.Zero).ToString("O"),
            FxRateToBase = "1",
            DecisionId = Guid.NewGuid().ToString("D"),
            Provider = Proto.BrokerProvider.MoomooSimulate,
            FxRateBaseToDisplay = "147.25",
        };
        tweak?.Invoke(f);
        return f;
    }

    private static async Task<IReadOnlyList<PeriodTradeFill>> ReadFillsAsync(params Proto.PeriodFill[] fills)
    {
        var response = new Proto.GetFillsResponse();
        response.Fills.AddRange(fills);
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior { Fills = RiskReadStubBehavior.Returns(response) });
        var (sp, t) = Compose(host.Address);
        await using (sp)
        {
            var result = await new GrpcPeriodFillSource(t, Log<GrpcPeriodFillSource>()).GetFillsAsync(From, To);
            host.Behavior.LastPeriod.Should().Be(("2026-09-01", "2026-09-30"), "期間は yyyy-MM-dd で運ぶ");
            return result;
        }
    }

    // 🔴 発注先の未指定は null（どちらの段にも算入しない）、認識時レートの欠落は null（未記録）。既定値で埋めない。
    [Fact]
    public async Task T_10_1055_約定の発注先の未指定と認識時レートの欠落は不明のまま()
    {
        var fills = await ReadFillsAsync(Fill(f =>
        {
            f.Provider = Proto.BrokerProvider.Unspecified;
            f.ClearFxRateBaseToDisplay();
            f.ClearFxRateToBase();
            f.ClearDecisionId();
        }));

        var fill = fills.Should().ContainSingle().Subject;
        fill.Provider.Should().BeNull();
        fill.FxRateBaseToDisplay.Should().BeNull();
        fill.Price.Should().Be(200m, "同伴レートの欠落は REST と同じく 1");
        fill.DecisionId.Should().Be(Guid.Empty, "判断 ID の欠落は REST と同じく空（相関できない）");
    }

    // 🔴 必須の項目が欠けた行を既定値（日本・買い・新規・0）で「作り話の約定」にしない —— 応答全体を空列（数値 0 の報告書）へ倒す。
    [Theory]
    [InlineData("市場が未指定")]
    [InlineData("方向が未指定")]
    [InlineData("建て／決済が未指定")]
    [InlineData("数量なし")]
    [InlineData("単価なし")]
    [InlineData("約定時刻なし")]
    public async Task T_10_1055_約定の必須の欠落は既定値で作らず空列(string how)
    {
        var fills = await ReadFillsAsync(Fill(), Fill(f =>
        {
            switch (how)
            {
                case "市場が未指定": f.Market = Proto.Market.Unspecified; break;
                case "方向が未指定": f.Side = Proto.TradeSide.Unspecified; break;
                case "建て／決済が未指定": f.PositionEffect = Proto.PositionEffect.Unspecified; break;
                case "数量なし": f.ClearQuantity(); break;
                case "単価なし": f.ClearPrice(); break;
                default: f.ClearExecutedAt(); break;
            }
        }));

        fills.Should().BeEmpty(how);
    }

    [Fact]
    public async Task T_10_1055_銘柄の無い約定は_REST_と同じく落とす()
    {
        var fills = await ReadFillsAsync(Fill(), Fill(f => f.ClearSymbol()));

        fills.Should().ContainSingle().Which.Symbol.Should().Be("AAPL");
    }

    // ---- T-10-1055: 強制買戻し・取り込み・稼働率・段階・建玉（倒す向きは null） ----

    // 🔴 `period_covered` の欠落を「覆っている」と読まない（REST の旧版応答と同じ）。false も未供給。true なら正当な 0 件。
    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task T_10_1055_強制買戻しの期間の被覆の欠落は覆っていない扱い(bool? covered, bool supplied)
    {
        var response = new Proto.GetBuyInInferencesResponse();
        if (covered is { } c)
            response.PeriodCovered = c;
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            BuyInInferences = RiskReadStubBehavior.Returns(response),
        });
        var (sp, t) = Compose(host.Address);
        await using (sp)
        {
            var result = await new GrpcBuyInInferenceRecordSource(t, Log<GrpcBuyInInferenceRecordSource>())
                .GetInferencesAsync(From, To);

            if (supplied)
                result.Should().NotBeNull().And.BeEmpty("覆われた期間の 0 件は正当な 0");
            else
                result.Should().BeNull("0 件と表示しない（ADR-0016 決定15）");
        }
    }

    [Fact]
    public async Task T_10_1055_稼働率の入れ物の欠落は未供給_空の入れ物は行なし_累計の欠落は未供給のまま()
    {
        var absent = new Proto.GetSessionUptimeResponse { Stage1CumulativeCountedDays = 5 };
        var empty = new Proto.GetSessionUptimeResponse { Days = new Proto.SessionUptimeDays() };
        await using var hostAbsent = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            SessionUptime = RiskReadStubBehavior.Returns(absent),
        });
        await using var hostEmpty = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            SessionUptime = RiskReadStubBehavior.Returns(empty),
        });
        var (sp1, t1) = Compose(hostAbsent.Address);
        var (sp2, t2) = Compose(hostEmpty.Address);
        await using (sp1)
        await using (sp2)
        {
            (await new GrpcOpenDUptimeSource(t1, Log<GrpcOpenDUptimeSource>()).GetUptimeAsync(From, To))
                .Should().BeNull("日次の一覧が無い＝未供給（稼働率 0% とは書かない）");

            var record = await new GrpcOpenDUptimeSource(t2, Log<GrpcOpenDUptimeSource>()).GetUptimeAsync(From, To);
            record.Should().NotBeNull();
            record!.Days.Should().BeEmpty("空の入れ物は観測された取引日が無かった");
            record.Stage1CumulativeCountedDays.Should().BeNull("累計の欠落は 0 ではなく未供給");
        }
    }

    [Theory]
    [InlineData(0, null)]      // 未指定
    [InlineData(1, 0)]         // Stage 0（C# の 0 は線上で 1）
    [InlineData(2, 1)]
    [InlineData(99, null)]     // 未知の番号（提供側が段階を増やした）
    public async Task T_10_1055_段階の未指定と未知は未供給_Stage_0_は_Stage_0(int wire, int? expected)
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            StageGate = RiskReadStubBehavior.Returns(new Proto.GetStageGateResponse { CurrentStage = (Proto.TradingStage)wire }),
        });
        var (sp, t) = Compose(host.Address);
        await using (sp)
        {
            var stage = await new GrpcStageProgressSource(t, Log<GrpcStageProgressSource>()).GetCurrentStageAsync();

            stage.Should().Be(expected is { } e ? (TradingStage)e : null);
        }
    }

    [Theory]
    [InlineData("識別子なし")]
    [InlineData("市場が未指定")]
    [InlineData("数量なし")]
    [InlineData("取り込み時刻なし")]
    public async Task T_10_1055_取り込みの必須の欠落は未供給(string how)
    {
        var row = RiskReadWireMapping.ToProto(new DriftAdoptionView(
            Guid.NewGuid(), "NVDA", Market.UnitedStates, TradeSide.Sell, 5, 5, 0,
            DateTimeOffset.UtcNow.AddMinutes(-5), "owner", "手動で売った", DateTimeOffset.UtcNow));
        switch (how)
        {
            case "識別子なし": row.ClearAdoptionId(); break;
            case "市場が未指定": row.Market = Proto.Market.Unspecified; break;
            case "数量なし": row.ClearQuantity(); break;
            default: row.ClearAdoptedAt(); break;
        }

        var response = new Proto.GetDriftAdoptionsResponse();
        response.Adoptions.Add(row);
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            DriftAdoptions = RiskReadStubBehavior.Returns(response),
        });
        var (sp, t) = Compose(host.Address);
        await using (sp)
        {
            (await new GrpcPeriodDriftAdoptionSource(t, Log<GrpcPeriodDriftAdoptionSource>()).GetDriftAdoptionsAsync(From, To))
                .Should().BeNull(how);
        }
    }

    [Fact]
    public async Task T_10_1055_建玉の識別の欠落は_1_行でも未供給_失敗も未供給()
    {
        var response = new Proto.GetOpenPositionsResponse();
        response.Positions.Add(RiskReadWireMapping.ToProto(
            new OpenPositionView("AAPL", Market.UnitedStates, TradeSide.Buy, 1, 190.5m, 180m)));
        var broken = RiskReadWireMapping.ToProto(new OpenPositionView("MSFT", Market.UnitedStates, TradeSide.Buy, 1, 400m, 380m));
        broken.ClearStopLossPrice();
        response.Positions.Add(broken);
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = RiskReadStubBehavior.Returns(response),
        });
        await using var failing = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            OpenPositions = RiskReadStubBehavior.Fails<Proto.GetOpenPositionsResponse>(StatusCode.Internal),
        });
        var (sp1, t1) = Compose(host.Address);
        var (sp2, t2) = Compose(failing.Address);
        await using (sp1)
        await using (sp2)
        {
            (await new GrpcOpenPositionSource(t1, Log<GrpcOpenPositionSource>()).GetOpenPositionsAsync())
                .Should().BeNull("価格の無い行を落として「建玉なし」とは書かない（#957）");
            (await new GrpcOpenPositionSource(t2, Log<GrpcOpenPositionSource>()).GetOpenPositionsAsync())
                .Should().BeNull();
        }
    }

    // ---- T-10-1056: 契約（送り手の本物の型 → 提供側の写し → 線 → 受け手） ----

    [Fact]
    public async Task T_10_1056_送り手の型の約定と取り込みと強制買戻しと建玉を受け手が同じ値で読む()
    {
        var decisionId = Guid.NewGuid();
        var executedAt = new DateTimeOffset(2026, 9, 10, 9, 30, 0, TimeSpan.FromHours(9));
        var senderFill = new RiskLedgerFill(
            "7203", Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 2500.5m, executedAt,
            StopLossPrice: 2400m, FxRateToBase: 0.0068m, DecisionId: decisionId, Provider: BrokerProvider.InternalPaper,
            FxRateBaseToDisplay: 147.25m);
        var adoptionId = Guid.NewGuid();
        var senderDrift = new DriftAdoptionView(
            adoptionId, "NVDA", Market.UnitedStates, TradeSide.Sell, 5, 5, 0,
            executedAt.AddMinutes(-5), "owner", "手動で売った", executedAt);
        var inferredOn = new DateOnly(2026, 9, 10);
        var senderInference = new RiskBuyInInferenceRecord(
            Guid.NewGuid(), "TSLA", Market.UnitedStates, 10, 4, 1, 5, 5, BanUntil: null, inferredOn, executedAt, executedAt);

        var fills = new Proto.GetFillsResponse();
        fills.Fills.Add(RiskReadWireMapping.ToProto(senderFill));
        var drifts = new Proto.GetDriftAdoptionsResponse();
        drifts.Adoptions.Add(RiskReadWireMapping.ToProto(senderDrift));
        var buyIn = new Proto.GetBuyInInferencesResponse { PeriodCovered = true };
        buyIn.ObservedTradingDays.Add("2026-09-10");
        buyIn.Inferences.Add(RiskReadWireMapping.ToProto(senderInference));
        var positions = new Proto.GetOpenPositionsResponse();
        positions.Positions.Add(RiskReadWireMapping.ToProto(
            new OpenPositionView("7203", Market.Japan, TradeSide.Sell, 100, 2500m, 2575m)));

        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            Fills = RiskReadStubBehavior.Returns(fills),
            DriftAdoptions = RiskReadStubBehavior.Returns(drifts),
            BuyInInferences = RiskReadStubBehavior.Returns(buyIn),
            OpenPositions = RiskReadStubBehavior.Returns(positions),
        });
        var (sp, t) = Compose(host.Address);
        await using (sp)
        {
            var fill = (await new GrpcPeriodFillSource(t, Log<GrpcPeriodFillSource>()).GetFillsAsync(From, To)).Single();
            fill.Should().Be(new PeriodTradeFill(
                "7203", Market.Japan, TradeSide.Buy, PositionEffect.Open, 100, 2500.5m * 0.0068m, executedAt,
                decisionId, BrokerProvider.InternalPaper, 147.25m));
            fill.ExecutedAt.Offset.Should().Be(TimeSpan.FromHours(9), "時刻はオフセットごと運ぶ");

            var drift = (await new GrpcPeriodDriftAdoptionSource(t, Log<GrpcPeriodDriftAdoptionSource>())
                .GetDriftAdoptionsAsync(From, To))!.Single();
            drift.AdoptionId.Should().Be(adoptionId);
            drift.Side.Should().Be(TradeSide.Sell);
            drift.LedgerQuantityBefore.Should().Be(5);
            drift.BrokerQuantity.Should().Be(0, "在る 0 は 0");

            var inference = (await new GrpcBuyInInferenceRecordSource(t, Log<GrpcBuyInInferenceRecordSource>())
                .GetInferencesAsync(From, To))!.Single();
            inference.EventId.Should().Be(senderInference.Id);
            inference.NewlyInferredQuantity.Should().Be(5);
            inference.BanUntil.Should().Be(inferredOn, "禁止期限の欠落は REST と同じく推定日で代える");

            var position = (await new GrpcOpenPositionSource(t, Log<GrpcOpenPositionSource>()).GetOpenPositionsAsync())!.Single();
            position.Market.Should().Be(Market.Japan, "日本（C# の 0）が線上で未指定に化けない");
            position.Side.Should().Be(TradeSide.Sell);
            position.StopLossPrice.Should().Be(2575m);
        }
    }

    // ---- T-10-1058: timeout / retry（既定の deadline は REST の risk-ledger と同じ 10 秒） ----

    [Fact]
    public async Task T_10_1058_提供側が黙れば構成した_deadline_で未供給へ倒れ再試行しない()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            StageGate = RiskReadStubBehavior.RespondsOnceThenHangs(
                new Proto.GetStageGateResponse { CurrentStage = Proto.TradingStage.Stage1Simulate }),
        });
        var (sp, t) = Compose(host.Address, new() { ["RiskManagement:GrpcTimeoutSeconds"] = "3" });
        await using (sp)
        {
            var source = new GrpcStageProgressSource(t, Log<GrpcStageProgressSource>());
            (await source.GetCurrentStageAsync()).Should().Be(TradingStage.Stage1Simulate, "暖機（#885）");

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var stage = await source.GetCurrentStageAsync();
            elapsed.Stop();

            stage.Should().BeNull();
            host.Behavior.Calls.Should().Be(2, "既定は再試行しない");
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "構成した 3 秒の deadline で打ち切られること");
        }
    }

    [Fact]
    public async Task T_10_1058_一時的な_UNAVAILABLE_は構成した回数だけ再試行し恒久的な失敗は再試行しない()
    {
        var ok = new Proto.GetStageGateResponse { CurrentStage = Proto.TradingStage.Stage2MinimalLive };
        await using var transient = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            StageGate = RiskReadStubBehavior.FailsThenSucceeds(StatusCode.Unavailable, 1, ok),
        });
        await using var permanent = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            StageGate = RiskReadStubBehavior.Fails<Proto.GetStageGateResponse>(StatusCode.PermissionDenied),
        });
        var (sp1, t1) = Compose(transient.Address, new() { ["RiskManagement:GrpcMaxAttempts"] = "2" });
        var (sp2, t2) = Compose(permanent.Address, new() { ["RiskManagement:GrpcMaxAttempts"] = "3" });
        await using (sp1)
        await using (sp2)
        {
            (await new GrpcStageProgressSource(t1, Log<GrpcStageProgressSource>()).GetCurrentStageAsync())
                .Should().Be(TradingStage.Stage2MinimalLive);
            (await new GrpcStageProgressSource(t2, Log<GrpcStageProgressSource>()).GetCurrentStageAsync())
                .Should().BeNull();
            transient.Behavior.Calls.Should().Be(2);
            permanent.Behavior.Calls.Should().Be(1);
        }
    }

    // ---- T-10-1059: 依存先の門と観測（#840 / IADR-0352）を gRPC でも失わない ----

    // 資格情報は整っているのにトークンを取れない供給元（Keycloak が起動していない等）。
    private sealed class FailingTokenProvider : IServiceAccessTokenProvider
    {
        public int Calls { get; private set; }

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<string?>(null);
        }
    }

    private static RiskManagementGrpcTransport GatedTransport(
        string address, ReportDependencyProbe probe, IServiceAccessTokenProvider? tokenProvider) =>
        new(
            GrpcClientExtensions.CreateAiStockTradingChannel(address, tokenProvider ?? NoServiceAccessTokenProvider.Instance),
            TimeSpan.FromSeconds(10), 1, probe, tokenProvider, NullLogger<RiskManagementGrpcTransport>.Instance);

    // 🔴 トークンを取れないなら**送信しない**（提供側は 1 度も呼ばれない）。未供給へ倒し、一過性として記録する。
    [Fact]
    public async Task T_10_1059_トークンを取れなければ送信せず未供給へ倒し一過性として記録する()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            StageGate = RiskReadStubBehavior.Returns(new Proto.GetStageGateResponse { CurrentStage = Proto.TradingStage.Stage1Simulate }),
        });
        var probe = new ReportDependencyProbe();
        var tokens = new FailingTokenProvider();
        using var transport = GatedTransport(host.Address, probe, tokens);
        using var observation = probe.Begin();

        var stage = await new GrpcStageProgressSource(transport, Log<GrpcStageProgressSource>()).GetCurrentStageAsync();

        stage.Should().BeNull();
        host.Behavior.Calls.Should().Be(0, "トークンの無い要求を上流へ投げない（REST の門と同じ）");
        observation.Failures.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Dependency = "risk-ledger",
            Kind = ReportDependencyFailureKind.ServiceTokenUnavailable,
            Transient = true,
        });
    }

    // 陰性対照: 資格情報が未整備の構成（no-op）では門は素通しする（dev・単体実行。REST と同じ）。
    [Fact]
    public async Task T_10_1059_資格情報が未整備なら門は素通しする()
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            StageGate = RiskReadStubBehavior.Returns(new Proto.GetStageGateResponse { CurrentStage = Proto.TradingStage.Stage1Simulate }),
        });
        var probe = new ReportDependencyProbe();
        using var transport = GatedTransport(host.Address, probe, NoServiceAccessTokenProvider.Instance);
        using var observation = probe.Begin();

        (await new GrpcStageProgressSource(transport, Log<GrpcStageProgressSource>()).GetCurrentStageAsync())
            .Should().Be(TradingStage.Stage1Simulate);
        observation.Failures.Should().BeEmpty();
    }

    // 🔴 失敗の分類は REST と同じ判定（HTTP 相当へ写して ReportDependencyHandler.IsTransient）。
    // 401 相当は一過性（#866）、403 相当は恒常、接続できない・deadline は一過性。
    [Theory]
    [InlineData("Unauthenticated", "GrpcStatus", true)]
    [InlineData("PermissionDenied", "GrpcStatus", false)]
    [InlineData("Internal", "GrpcStatus", true)]
    [InlineData("InvalidArgument", "GrpcStatus", false)]
    [InlineData("Unavailable", "Unreachable", true)]
    [InlineData("DeadlineExceeded", "Timeout", true)]
    public async Task T_10_1059_失敗を_REST_と同じ判定で一過性と恒常に分けて記録する(string status, string kind, bool transient)
    {
        await using var host = await RiskReadStubHost.StartAsync(new RiskReadStubBehavior
        {
            StageGate = RiskReadStubBehavior.Fails<Proto.GetStageGateResponse>(Enum.Parse<StatusCode>(status)),
        });
        var probe = new ReportDependencyProbe();
        using var transport = GatedTransport(host.Address, probe, NoServiceAccessTokenProvider.Instance);
        using var observation = probe.Begin();

        (await new GrpcStageProgressSource(transport, Log<GrpcStageProgressSource>()).GetCurrentStageAsync()).Should().BeNull();

        observation.Failures.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Dependency = "risk-ledger",
            Kind = Enum.Parse<ReportDependencyFailureKind>(kind),
            Transient = transient,
        });
    }
}
