using System.Collections.Concurrent;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-19, IADR-0040/0067: 注文アクティビティ源のインメモリ実装（プロセス内リングバッファ）。
// 事前構築した OrderActivityRecord を Record で流し込んで検知アルゴリズムを検証するためのテスト・ローカル用供給。
// 本番は注文系イベントの射影を読む EfOrderActivitySource が供給し、射影＋読み取りのインメモリ版は
// InMemoryOrderActivityStore（#154）が担う。実ホスト結線・実 E2E は後続（#82）。
// （銘柄, 市場）別に直近の OrderActivityRecord を保持し、窓外を刈り込んで返す。
public sealed class InMemoryOrderActivitySource : IOrderActivitySource
{
    // 1 キーあたりの保持上限。窓長を大きく超える履歴は不要（相場操縦検知は直近窓のみを見る）。
    // キー（銘柄, 市場）自体は明示削除しないが、基数は取引対象銘柄数で実質有界。恒久運用の刈り込みは
    // 本番の射影ストア（EfOrderActivityStore・#154）側の保持方針で扱う（本実装はテスト・ローカル用）。
    private const int MaxRecordsPerKey = 512;

    private readonly ConcurrentDictionary<(string Symbol, Market Market), List<OrderActivityRecord>> _records = new();

    /// <summary>注文アクティビティを 1 件記録する（発注→終端まで確定した要約を追記する）。</summary>
    public void Record(string symbol, Market market, OrderActivityRecord record)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        ArgumentNullException.ThrowIfNull(record);

        var bucket = _records.GetOrAdd((symbol, market), _ => []);
        lock (bucket)
        {
            bucket.Add(record);
            // 保持上限を超えたら古い順に落とす（発注時刻でリングバッファ的に切り詰める）。
            if (bucket.Count > MaxRecordsPerKey)
            {
                bucket.Sort((a, b) => a.PlacedAt.CompareTo(b.PlacedAt));
                bucket.RemoveRange(0, bucket.Count - MaxRecordsPerKey);
            }
        }
    }

    public OrderActivityWindow GetRecentActivity(
        string symbol, Market market, DateTimeOffset asOf, TimeSpan lookback)
    {
        var from = asOf - lookback;

        if (!_records.TryGetValue((symbol, market), out var bucket))
        {
            return OrderActivityWindow.Empty(symbol, market, asOf);
        }

        List<OrderActivityRecord> inWindow;
        lock (bucket)
        {
            // 窓内＝発注時刻が [asOf - lookback, asOf] の記録。窓外は刈り込む。
            inWindow = bucket
                .Where(r => r.PlacedAt >= from && r.PlacedAt <= asOf)
                .ToList();
        }

        return new OrderActivityWindow
        {
            Symbol = symbol,
            Market = market,
            AsOf = asOf,
            Records = inWindow,
        };
    }
}
