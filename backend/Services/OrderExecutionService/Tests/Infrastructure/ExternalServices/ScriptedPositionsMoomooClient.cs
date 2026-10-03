using OrderExecutionService.Infrastructure.ExternalServices;

namespace OrderExecutionService.Tests;

// FR-10, NFR, #1164, IADR-0487: 建玉照会だけを台本どおりに失敗させる moomoo のクライアントの偽物。
// **本物のアダプタ（MoomooBrokerAdapter）の前に置き**、分類器への入力（例外）を経路ごとに同じ形で与えるために使う。
// 台本が尽きたら最後の 1 件を繰り返す（null は成功＝空の建玉）。建玉照会以外は呼ばれない前提（呼ばれたら落とす）。
internal sealed class ScriptedPositionsMoomooClient(params Func<Exception>?[] script) : IMoomooTradeClient
{
    private int _calls;

    /// <summary>建玉照会（GetPositionsAsync）が呼ばれた回数。</summary>
    public int PositionCalls => _calls;

    public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var index = Math.Min(Interlocked.Increment(ref _calls) - 1, script.Length - 1);
        var failure = script.Length == 0 ? null : script[index];
        if (failure is not null)
            throw failure();
        return Task.FromResult<IReadOnlyList<MoomooPositionSnapshot>>([]);
    }

    public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("建玉照会の試験は発注しない");

    public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("建玉照会の試験は注文を照会しない");

    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("建玉照会の試験は取り消さない");

    public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
        string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("建玉照会の試験は突合しない");

    public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("建玉照会の試験は口座を照会しない");

    public Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("建玉照会の試験は口座を照会しない");
}
