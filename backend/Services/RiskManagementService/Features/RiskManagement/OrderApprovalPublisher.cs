using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace RiskManagementService.Features.RiskManagement;

// 🔴 NFR, FR-10, #1527, IADR-0610 決定1: 注文の承認（OrderApproved）を発行する**唯一の口**。発行元の印（HMAC）をヘッダーへ載せる。
// 書き手 3 つ（審査＝TradeDecisionMadeHandler・利用者の手仕舞い＝ClosePositionEndpoint・維持率の自動縮小＝
// MaintenanceMarginEvaluationService）はすべてここを通す。素の bus.PublishAsync(承認) に戻すと印の無い承認が出る
// （OrderApprovalPublisherGuardTests が本サービスのソースを走査して止める）。
//
// 鍵が無い構成（段 1 の既定）では印を付けずに発行し、警告を残す（従前と同じ発行。受け手は段 1 なら通す）。
public sealed class OrderApprovalPublisher(OrderApprovalSignature? signature, ILogger<OrderApprovalPublisher> logger)
{
    public ValueTask PublishAsync(IMessageBus bus, OrderApproved approved)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(approved);

        if (signature is null)
        {
            logger.LogWarning(
                "注文の承認の印を付ける鍵が構成されていないため、印なしで発行します（OrderApprovalSigning:Key）: DecisionId={DecisionId}",
                approved.DecisionId);
            return bus.PublishAsync(approved);
        }

        return bus.PublishAsync(
            approved,
            new DeliveryOptions().WithHeader(OrderApprovalSignature.HeaderName, signature.Sign(approved)));
    }
}

// NFR, #1527, IADR-0610 決定3: 発行の口の登録。構成 OrderApprovalSigning を起動時に読み、段 2 で鍵が無ければここで例外（起動で止める）。
public static class OrderApprovalPublisherRegistration
{
    public static IServiceCollection AddOrderApprovalPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(OrderApprovalSigningOptions.SectionName).Get<OrderApprovalSigningOptions>()
            ?? new OrderApprovalSigningOptions();
        var signature = options.Resolve();
        return services.AddSingleton(sp =>
            new OrderApprovalPublisher(signature, sp.GetRequiredService<ILogger<OrderApprovalPublisher>>()));
    }
}
