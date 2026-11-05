using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Tests;

// NFR, #1527, IADR-0610: 試験のホストが承認を発行するハンドラを組むための発行口（鍵なし＝印なしで従前どおり発行する）。
// 本番の登録（AddOrderApprovalPublisher）の段 1・鍵なしと同じ振る舞いである。
internal static class TestApprovalPublishing
{
    public static OrderApprovalPublisher Unsigned() => new(null, NullLogger<OrderApprovalPublisher>.Instance);
}
