using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using Wolverine;

namespace RiskManagementService.Infrastructure.ExternalServices;

// NFR-06, FR-14, FR-11, FR-09, IADR-0626 決定 3: 第 2 要素を確かめた結果（OwnerSecondFactorChecked）を発行する。
// 監査が全件を記録し、通知が失敗・停止・成功をオーナーへ知らせる（既存の Discord 通知の経路）。
// 門は同期の callback から呼ばれるので、ここで発行を待つ（呼ぶたびにスコープを作る＝要求のスコープと混ぜない）。
// 発行の失敗は門（TotpOwnerSecondFactorVerifier）がログに残し、確かめの結果は変えない。
public sealed class MessageBusOwnerSecondFactorObserver(IServiceScopeFactory scopes) : IOwnerSecondFactorObserver
{
    public void OnChecked(OwnerSecondFactorChecked check)
    {
        using var scope = scopes.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        bus.PublishAsync(check).AsTask().GetAwaiter().GetResult();
    }
}
