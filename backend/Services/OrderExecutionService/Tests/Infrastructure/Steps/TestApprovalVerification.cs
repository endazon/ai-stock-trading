using AiStockTrading.Shared.Infrastructure.Composable.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Infrastructure.Steps;

namespace OrderExecutionService.Tests;

// NFR, #1527, IADR-0610: 試験のホストが OrderApprovedHandler を組むための検証器（段 1・鍵なし＝従前どおり通す）。
// 本番の登録（AddOrderApprovalVerifier）と同じ既定の段である。
internal static class TestApprovalVerification
{
    public static OrderApprovalVerifier ReportOnly() =>
        new(OrderApprovalSigningMode.Report, signature: null, NullLogger<OrderApprovalVerifier>.Instance);
}
