namespace AiStockTrading.InformationCollection.Application.Ports;

// FR-01, ADR-0004, IADR-0061: 情報源へ要求を送る前の自制。各コネクタは HTTP 要求の直前に WaitAsync し、
// 情報源の公表レート上限（保守側の既定）を超えないようにする（「レート制限違反がない」を構造で満たす）。
public interface IRateLimiter
{
    Task WaitAsync(CancellationToken cancellationToken = default);
}
