using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

// 🔴 FR-10, FR-05, ADR-0016, UC-06, #864, IADR-0355: 決済（Close）を送る前に、ブローカーの実建玉と
// 突き合わせる純関数。**副作用を持たない**（照会も発注も発行も呼び出し側が行う）。
//
// なぜ要るか: 決済の数量の出所は**台帳の射影**であってブローカーの事実ではない（IADR-0119 決定1 /
// IADR-0351 決定6）。台帳が乖離していると（#849。台帳 3,381 株 / ブローカー 0 株を実測）、
// 決済注文は**ブローカー上では保有 0 からの売り＝裸の新規ショート**になる。空売りは方針で禁止であり、
// 注文が「決済」として通るためショート建玉の規律（ADR-0016）も空売り固有の統制も効かない。
//
// 判定の規律（IBrokerPositionSource の契約をそのまま引き継ぐ）:
//   - **空列（建玉ゼロ）と null（不明）を取り違えない。** 前者は「ブローカーは持っていない」という観測事実、
//     後者は「分からない」であり、倒す先も理由も違う（IADR-0355 決定2 / 決定3）。
//   - 決済方向の建玉だけを数える。`Sell` の決済が消せるのはロング（正の数量）、`Buy` の決済が消せるのは
//     ショート（負の数量）である。**反対方向の建玉は 0 として扱う**——反対方向へ送れば建玉が増える。
public static class BrokerHeldPositionGate
{
    /// <summary>
    /// 決済の注文意図とブローカーの建玉スナップショット（null＝照会不能）から、送ってよい数量を決める。
    /// </summary>
    public static BrokerHeldPositionVerdict Evaluate(
        OrderIntent intent, IReadOnlyList<BrokerPositionSnapshot>? snapshot)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (snapshot is null)
            return new BrokerHeldPositionVerdict(BrokerHeldPositionOutcome.Indeterminate, 0, 0);

        // ブローカーの符号付き数量（+ ロング / − ショート）。同一銘柄・同一市場の行は合算する
        // （口座・建玉が分かれて返るブローカーがあっても、突合の単位は (銘柄, 市場) である。IADR-0118）。
        var net = snapshot
            .Where(p => p.Symbol == intent.Symbol && p.Market == intent.Market)
            .Sum(p => p.Quantity);

        var closable = intent.Side == TradeSide.Sell ? Math.Max(0, net) : Math.Max(0, -net);

        if (closable == 0)
            return new BrokerHeldPositionVerdict(BrokerHeldPositionOutcome.NoPosition, net, 0);

        return closable < intent.Quantity
            ? new BrokerHeldPositionVerdict(BrokerHeldPositionOutcome.Reduce, net, closable)
            : new BrokerHeldPositionVerdict(BrokerHeldPositionOutcome.Proceed, net, intent.Quantity);
    }

    /// <summary>
    /// 乖離 1 件を既存の突合（IADR-0118）と同じ表現で作る。台帳側は<b>この決済が消そうとした数量</b>を
    /// 建玉の向きで符号化した値であり、台帳の建玉そのものではない（発注執行は台帳を持たない）。
    /// </summary>
    public static PositionDriftItem DriftOf(OrderIntent intent, int brokerQuantity)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var ledgerQuantity = intent.Side == TradeSide.Sell ? intent.Quantity : -intent.Quantity;
        var kind = ledgerQuantity == 0 ? PositionDriftKind.BrokerOnly
            : brokerQuantity == 0 ? PositionDriftKind.LedgerOnly
            : PositionDriftKind.QuantityMismatch;

        return new PositionDriftItem(intent.Symbol, intent.Market, ledgerQuantity, brokerQuantity, kind);
    }
}

/// <summary>#864, IADR-0355: 突き合わせの結論。</summary>
public enum BrokerHeldPositionOutcome
{
    /// <summary>実建玉が注文数量を満たす（従来どおりそのまま送る）。</summary>
    Proceed = 0,

    /// <summary>実建玉はあるが足りない（実建玉の範囲へ縮めて送る。監査・通知に残す）。</summary>
    Reduce,

    /// <summary>決済方向の実建玉が 0（送らない。送れば裸のショートになる）。</summary>
    NoPosition,

    /// <summary>建玉を照会できない（不明。送らない＝IADR-0355 決定3）。</summary>
    Indeterminate,
}

/// <summary>
/// #864, IADR-0355: 突き合わせの結果。<paramref name="BrokerQuantity"/> は符号付きのネット建玉
/// （不明のときは 0 だが、<see cref="BrokerHeldPositionOutcome.Indeterminate"/> のときは**意味を持たない**）。
/// </summary>
public readonly record struct BrokerHeldPositionVerdict(
    BrokerHeldPositionOutcome Outcome,
    int BrokerQuantity,
    int ClosableQuantity);
