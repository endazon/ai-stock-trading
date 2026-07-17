namespace AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;

// FR-01, ADR-0004, IADR-0064: 情報源へ要求を送る前の自制。各コネクタは HTTP 要求の直前に WaitAsync し、
// 情報源の公表レート上限（保守側の既定）を超えないようにする（「レート制限違反がない」を構造で満たす）。
// IADR-0068: 情報収集・市況の両系統から使うため共有物へ移した（元は InformationCollection.Application.Ports）。
// プロセスをまたぐ協調は行わない（枠はサービスごとの予算配分で守る。IADR-0068 決定 4）。
public interface IRateLimiter
{
    Task WaitAsync(CancellationToken cancellationToken = default);
}
