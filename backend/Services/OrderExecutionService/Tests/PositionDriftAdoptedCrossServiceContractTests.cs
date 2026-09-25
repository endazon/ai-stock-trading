extern alias RiskManagementWorker;

using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Runtime;
using Xunit;
using RmAdoptionCommand = RiskManagementWorker::RiskManagementService.Features.RiskManagement.AdoptPositionDrift.PositionDriftAdoptionCommand;
using RmAdoptionRejection = RiskManagementWorker::RiskManagementService.Features.RiskManagement.AdoptPositionDrift.PositionDriftAdoptionRejection;
using RmAdoptionService = RiskManagementWorker::RiskManagementService.Features.RiskManagement.AdoptPositionDrift.PositionDriftAdoptionService;
using RmClock = RiskManagementWorker::RiskManagementService.Common.Abstractions.IClock;
using RmDriftDetector = RiskManagementWorker::RiskManagementService.Features.RiskManagement.PositionDriftDetector;
using RmDriftStateStore = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.InMemoryPositionDriftStateStore;
using RmDriftTracker = RiskManagementWorker::RiskManagementService.Features.RiskManagement.PositionDriftTracker;
using RmLedger = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.InMemoryPortfolioLedgerStore;
using RmObservations = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.InMemoryBrokerPositionObservationStore;
using RmOpenPosition = RiskManagementWorker::RiskManagementService.Features.RiskManagement.OpenPosition;
using RmPriceSource = RiskManagementWorker::RiskManagementService.Features.RiskManagement.ICurrentPriceSource;
using RmProjection = RiskManagementWorker::RiskManagementService.Features.RiskManagement.PortfolioProjection;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1010, FR-10, FR-11, UC-06, #879, #858, IADR-0424 決定3, IADR-0420 決定1, IADR-0370:
// **取り込み（PositionDriftAdopted）の越境の契約**。送り手（リスク管理）の**本物の PositionDriftAdoptionService** が作ったイベントを、
// 受け手（発注執行）の**本番の Program.cs が組んだ Wolverine の既定シリアライザ**で書いて読み、**本番のホストのバス**へ流して、
// 受け手の業務クラスが保護記録を追随させることを固定する。
//
// あわせて、**同じ送り手が建玉を増やす取り込みを発行しない**ことをここでも表明する —— 発注執行が「取り込みで生じた建玉」へ
// 保護を作らない（IADR-0424 決定2）根拠は、送り手のこの挙動（計画 ADR-0041 決定3・UnsupportedDirection）にある。送り手が
// 増加を発行するよう変わったら、受け手の扱いを見直すためにこの試験が赤になる。
public class PositionDriftAdoptedCrossServiceContractTests
{
    private const string Symbol = "AAPL";

    private sealed class Clock(DateTimeOffset now) : RmClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);
    }

    private sealed class NoPrices : RmPriceSource
    {
        public IReadOnlyDictionary<(string Symbol, Market Market), decimal> GetCurrentPrices(
            IReadOnlyList<RmOpenPosition> positions) => new Dictionary<(string Symbol, Market Market), decimal>();
    }

    // 送り手の世界: 台帳にロング ledgerQuantity 株、ブローカーの観測は brokerQuantity 株（報告済みの乖離にするため 2 回観測する）。
    private static RmAdoptionService Sender(DateTimeOffset now, int ledgerQuantity, int brokerQuantity)
    {
        var ledger = new RmLedger();
        var decisionId = Guid.NewGuid();
        var openedAt = now.AddDays(-1);
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(Symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                ledgerQuantity, 200m, PositionEffect.Open, StopLossPrice: 190m),
            openedAt);
        ledger.AppendFill(decisionId, $"open-{decisionId:N}", ledgerQuantity, 200m, openedAt);

        var observations = new RmObservations();
        var tracker = new RmDriftTracker(new RmDriftStateStore(), NullLogger<RmDriftTracker>.Instance);
        var snapshot = new[] { new BrokerPositionSnapshot(Symbol, Market.UnitedStates, brokerQuantity, 200m) };
        for (var i = 0; i < 2; i++)
        {
            observations.Record(snapshot, now.AddMinutes(-5).AddSeconds(i));
            tracker.ShouldReport(RmDriftDetector.Detect(RmProjection.ProjectOpenPositions(ledger.GetFills()), snapshot));
        }

        return new RmAdoptionService(ledger, observations, tracker, new NoPrices(), new Clock(now));
    }

    [Fact]
    public async Task 送り手が作った減らす取り込みは通信路を越えて受け手の本番のハンドラが保護記録を追随させる()
    {
        var now = DateTimeOffset.UtcNow;
        var outcome = Sender(now, ledgerQuantity: 100, brokerQuantity: 40)
            .Adopt(new RmAdoptionCommand(Symbol, Market.UnitedStates, "証券会社のアプリで 60 株を売却"), "owner");
        outcome.Rejection.Should().Be(RmAdoptionRejection.None, "前提: 送り手が減らす取り込みを受理した");
        var sent = outcome.Adopted!;

        // 受け手: 本番の Program.cs（moomoo 構成・建玉照会は 40 株を返す）。S1 の行が 100 株を主張している。
        var broker = new ForgoneCloseProtectionCompositionTests.IndeterminateBroker(
            positions: [new BrokerPositionSnapshot(Symbol, Market.UnitedStates, 40, 200m)]);
        await using var factory = new ForgoneCloseProtectionCompositionTests.ProgramFactory(broker);
        var entry = Guid.NewGuid();
        using (var seed = factory.Services.CreateScope())
        {
            seed.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Save(new ProtectiveStopOrder(
                entry, Guid.NewGuid(), string.Empty, Symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
                BrokerProvider.MoomooSimulate, 100, 190m, 1m, 0, ProtectiveStopState.Active, now.AddDays(-1), now.AddDays(-1),
                Mechanism: StopLossExecutionMethod.SoftwareStop, RemainingProtected: 100));
        }

        // 通信路: 受け手の本番の Wolverine の既定シリアライザで書き、同じシリアライザで読む。
        var serializer = factory.Services.GetRequiredService<IWolverineRuntime>().Options.DefaultSerializer;
        var received = (PositionDriftAdopted)serializer.ReadFromData(
            typeof(PositionDriftAdopted), new Envelope { Data = serializer.WriteMessage(sent) });
        received.Should().Be(sent, "送り手が載せた項目（数量の符号・前後・観測・操作者）がすべて通信路を越える");
        received.LedgerQuantityBefore.Should().Be(100);
        received.LedgerQuantityAfter.Should().Be(40);

        await factory.Services.GetRequiredService<IMessageBus>().InvokeAsync(received);

        using var check = factory.Services.CreateScope();
        var row = check.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(entry)!;
        row.RemainingProtected.Should().Be(40, "取り込みで消えた 60 株ぶんの主張だけを減らし、残る 40 株の保護は残す");
        row.State.Should().Be(ProtectiveStopState.Active);
    }

    // 🔴 T-10-1010（続き）: 同じ本物の送り手は、建玉を生む・増やす・反転させる取り込みを**発行しない**（台帳も書かない）。
    [Theory]
    [InlineData(100, 150)]   // 増加
    [InlineData(100, -30)]   // 方向の反転
    public void 送り手は建玉を増やす取り込みを発行しない(int ledgerQuantity, int brokerQuantity)
    {
        var outcome = Sender(DateTimeOffset.UtcNow, ledgerQuantity, brokerQuantity)
            .Adopt(new RmAdoptionCommand(Symbol, Market.UnitedStates, "テスト"), "owner");

        outcome.Rejection.Should().Be(RmAdoptionRejection.UnsupportedDirection);
        outcome.Adopted.Should().BeNull("発行されるイベントが無い＝受け手へ建玉を生む取り込みは届かない");
    }
}
