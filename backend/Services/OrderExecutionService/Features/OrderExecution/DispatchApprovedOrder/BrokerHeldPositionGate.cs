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
//   - 🔴 **方向ごとに数える。ネット（符号付き合算）で数えない**（#873 の監査 N1）。同一 (銘柄, 市場) に
//     ロングとショートが同時にある（両建て）と、ネットは正当な決済を誤って止める —— 実測: ロング +300 と
//     ショート −100 を同時に持つとき、ネットは +200 であり、**ショート 100 株の買い戻し（正当な決済）が
//     「建玉なし」になり**、ロング 300 株の売り決済も 200 株へ縮められる。**どちらも FR-10 に反する側の誤り**である。
//     空売りは既定で無効だが、計画 ADR-0016 の下で有効化でき、維持率割れの自動縮小はショート建玉の縮小を出す。
//   - ただし**報告する乖離の数量はネット**である（定期突合 IADR-0118 と同じ物差しにする。判定と報告で
//     数え方が違うのは意図的であり、片方向しか無い通常の建玉では両者は一致する）。
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

        // 同一銘柄・同一市場の行だけを見る（口座・建玉が分かれて返るブローカーがあっても、
        // 突合の単位は (銘柄, 市場) である。IADR-0118）。
        var rows = snapshot
            .Where(p => p.Symbol == intent.Symbol && p.Market == intent.Market)
            .ToList();

        // 報告用のネット（+ ロング / − ショート）。**判定には使わない**（上の規律を参照）。
        var net = rows.Sum(p => p.Quantity);

        // 判定用: 決済方向の建玉だけを、**方向ごとに**数える（両建てでも正当な決済を止めない）。
        var closable = rows
            .Where(p => intent.Side == TradeSide.Sell ? p.Quantity > 0 : p.Quantity < 0)
            .Sum(p => Math.Abs(p.Quantity));

        if (closable == 0)
            return new BrokerHeldPositionVerdict(BrokerHeldPositionOutcome.NoPosition, net, 0);

        // 🔴 #873 の監査（3 巡目）1: **`Proceed` でも「実際に決済できる株数」を入れる**（`intent.Quantity` で
        // 上限を切らない）。読み手は現在 `NoPosition` / `Reduce` しか見ないので害は出ていないが、
        // **分岐によって同じ項目の意味が変わる**のは N1（判定にネットを使った）→ NB1（分類にネットを使った）と
        // **同じ系統の罠**である。送る数量は `Math.Min(closable, intent.Quantity)` を呼び出し側が決める
        //（`Reduce` のときだけ縮める）のであって、この値が上限を兼ねてはならない。
        return closable < intent.Quantity
            ? new BrokerHeldPositionVerdict(BrokerHeldPositionOutcome.Reduce, net, closable)
            : new BrokerHeldPositionVerdict(BrokerHeldPositionOutcome.Proceed, net, closable);
    }

    /// <summary>
    /// 乖離 1 件を既存の突合（IADR-0118）と同じ表現で作る。台帳側は<b>この決済が消そうとした数量</b>を
    /// 建玉の向きで符号化した値であり、台帳の建玉そのものではない（発注執行は台帳を持たない）。
    /// ブローカー側は<b>ネット</b>を載せる（定期突合と同じ物差し。判定の「方向ごと」とは別である）。
    /// <paramref name="closableQuantity"/> は<b>分類にだけ</b>使う（下のコメント）。
    /// </summary>
    public static PositionDriftItem DriftOf(OrderIntent intent, int brokerNetQuantity, int closableQuantity)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var ledgerQuantity = intent.Side == TradeSide.Sell ? intent.Quantity : -intent.Quantity;

        // 🔴 #873 の監査 NB1: **ネットが 0 でも「ブローカーは何も持っていない」とは限らない。**
        // 方向ごとに数えるようにした（N1）ことで `closable` と `net` は独立した —— 両建て（ロング +100 /
        // ショート −100）ではネットが 0 でも決済方向の建玉は 100 株あり、実際に 100 株を縮めて送る。
        // ここで `LedgerOnly`（台帳にだけある建玉）と分類すると、**送った直後に「ブローカーには無い」と
        // 報告する**ことになる（通知の文面と監査台帳の両方が誤る）。
        // したがって **`closable > 0` なら数量不一致へ倒す**。`LedgerOnly` は「決済方向にも 1 株も無く、
        // ネットも 0」＝本当に何も持っていないときだけである。
        //
        // 🔴 #873 の監査 N7: 定期突合の `BrokerOnly`（台帳側が 0）には**ここでは分岐しない**。
        // 台帳側は必ず ±intent.Quantity であり、決済の数量は保有数（1 以上。IADR-0119 決定1）だからである。
        // 仮に 0 が来ても「台帳だけが持っている」側へ倒す —— 発注執行が根拠にできるのは台帳側の数量だけであり、
        // 「ブローカーにだけある建玉」を本経路が発見することは構造的に無い（決済の相手方しか見ていない）。
        var kind = brokerNetQuantity == 0 && closableQuantity == 0
            ? PositionDriftKind.LedgerOnly
            : PositionDriftKind.QuantityMismatch;

        return new PositionDriftItem(intent.Symbol, intent.Market, ledgerQuantity, brokerNetQuantity, kind);
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
/// #864, IADR-0355: 突き合わせの結果。
/// <para>
/// <paramref name="BrokerNetQuantity"/> は符号付きのネット建玉で、**報告（乖離イベント）専用**である
/// （不明のときは 0 だが、<see cref="BrokerHeldPositionOutcome.Indeterminate"/> のときは**意味を持たない**）。
/// <paramref name="ClosableQuantity"/> は**決済方向だけを数えた実際の建玉数**であり、**判定はこちらで行う**
/// （両建てでネットを使うと正当な決済を止める。#873 の監査 N1）。
/// **注文数量で切っていない**（`Proceed` でも実際の建玉数が入る。#873 の監査〔3 巡目〕1）——
/// 送る数量は呼び出し側が `Reduce` のときだけ縮めて決める。
/// </para>
/// </summary>
public readonly record struct BrokerHeldPositionVerdict(
    BrokerHeldPositionOutcome Outcome,
    int BrokerNetQuantity,
    int ClosableQuantity);
