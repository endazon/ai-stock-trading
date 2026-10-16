extern alias RiskManagementWorker;

using AwesomeAssertions;
using ReportService.Domain;
using Xunit;
using RiskApprovalSource = RiskManagementWorker::RiskManagementService.Features.RiskManagement.ApprovalSource;

namespace ReportService.Tests;

// FR-06, FR-16, FR-10, #1357, IADR-0540 決定 1・2: 承認行の由来 → 決済の種別の分類表と、由来の序数（送り手と同じ）を固定する。
public class SettlementKindsTests
{
    public static TheoryData<FillApprovalSource?, bool?, SettlementKind?> Table { get; } = new()
    {
        // 損切り（S0 の約定・S1 の成行決済）は判断の記録の有無に依らない。
        { FillApprovalSource.ProtectiveStopS0, null, SettlementKind.StopLoss },
        { FillApprovalSource.SoftwareStopS1, false, SettlementKind.StopLoss },
        { FillApprovalSource.TradeDecision, null, SettlementKind.DecisionExit },
        { FillApprovalSource.ProtectionLostClose, true, SettlementKind.ProtectionLostClose },
        // 🔴 #1176 より前の判断由来も OrderApproved。判断の記録の有無で分け、記録が未供給なら分けない。
        { FillApprovalSource.OrderApproved, true, SettlementKind.DecisionExit },
        { FillApprovalSource.OrderApproved, false, SettlementKind.NonDecisionClose },
        { FillApprovalSource.OrderApproved, null, null },
        // 🔴 由来が記録されていない（列追加前・旧版の応答）は種別未供給。推定で埋めない。
        { null, true, null },
        { null, null, null },
    };

    // #1357
    [Theory]
    [MemberData(nameof(Table))]
    public void 承認行の由来と判断の記録の有無から決済の種別を決める(
        FillApprovalSource? source, bool? decisionRecord, SettlementKind? expected) =>
        SettlementKinds.Of(source, decisionRecord).Should().Be(expected);

    // #1357
    [Fact]
    public void 種別未供給は種別未供給と書く() =>
        SettlementKinds.Label(null).Should().Be("種別未供給");

    // #1357: REST は列挙を数値で往復する。報告書の写しの序数が送り手と名前ごとに一致すること（片側だけ足す・並べ替えると赤）。
    [Fact]
    public void 由来の写しの序数は送り手と名前ごとに一致する()
    {
        Enum.GetNames<FillApprovalSource>().Should().BeEquivalentTo(Enum.GetNames<RiskApprovalSource>());
        foreach (var name in Enum.GetNames<RiskApprovalSource>())
        {
            ((int)Enum.Parse<FillApprovalSource>(name)).Should().Be(
                (int)Enum.Parse<RiskApprovalSource>(name), "{0} の序数", name);
        }
    }
}
