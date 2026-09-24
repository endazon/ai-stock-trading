using RiskManagementService.Features.RiskManagement;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #935, IADR-0394 決定6: 承認行の由来（approved_orders.Source）は**整数として永続化**される。
// 既存メンバの間へ挿入すると過去の行の意味が変わる（S1 の損切りが「OrderApproved」と読まれる等）。
// 追加は常に末尾へ——本表がその規律を機械的に固定する（RejectionReasonOrdinalStabilityTests と同じ作法）。
public class ApprovalSourceTests
{
    public static TheoryData<ApprovalSource, int> FixedOrdinals { get; } = new()
    {
        { ApprovalSource.OrderApproved, 0 },
        { ApprovalSource.ProtectiveStopS0, 1 },
        { ApprovalSource.SoftwareStopS1, 2 },
        { ApprovalSource.ProtectionLostClose, 3 },
    };

    [Theory]
    [MemberData(nameof(FixedOrdinals))]
    public void 由来の序数は不変である(ApprovalSource source, int expectedOrdinal) =>
        ((int)source).Should().Be(expectedOrdinal, "由来は整数として永続化される。新設は末尾へ追加する");

    [Fact]
    public void 序数表はすべての由来を網羅する() =>
        Enum.GetValues<ApprovalSource>().Should().BeEquivalentTo(
            FixedOrdinals.Select(row => row.Data.Item1).ToArray(),
            "新しい由来を追加したら本表へ 1 行足す");
}
