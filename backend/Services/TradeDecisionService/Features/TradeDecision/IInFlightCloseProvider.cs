using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// 🔴 FR-04, FR-10, #1338, IADR-0535 決定2: 「今この方向の決済を送れば、処理中の決済が建玉を覆うため発注執行が見送るか」を照会するポート。
// 実体は発注執行の GET /order-execution/in-flight-closes で、**決済のゲートと同じ判定**（発注執行の InFlightCloses）を返す。
// 判断側は結果を読むだけで、処理中の決済の数え方を持たない。安全既定は NoOpInFlightCloseProvider（常に不明）。
// 🔴 不明なら判断は LLM を呼ぶ（見送らない）——決済（損切りを含む）の判断を止める向きへは倒さない（IADR-0535 決定3）。
public interface IInFlightCloseProvider
{
    /// <summary>
    /// (銘柄, 市場) の <paramref name="closeSide"/> 方向の処理中の決済が建玉を覆うか。照会できない・応答を解釈できない・発注執行が
    /// 「分からない」と答えた場合は <b>null（＝不明）</b>。
    /// </summary>
    Task<InFlightCloseCoverage?> GetAsync(
        string symbol, Market market, TradeSide closeSide, CancellationToken cancellationToken = default);
}

/// <summary>
/// #1338, IADR-0535: 処理中の決済の照会結果。<see cref="Covered"/> が true のときだけ、決済の結論は発注にならない
/// （発注執行が InFlightCloseCoversPosition で見送る）。<see cref="OrderIds"/> は数えた処理中の決済の注文 ID（ログ用）。
/// <para>
/// 🔴 #1434, IADR-0572 決定2: <see cref="BrokerClosableQuantity"/> は発注執行がブローカーから照会した決済方向の建玉（照会したときだけ。null＝不明）。
/// 0 は「ブローカーは決済方向の建玉を持っていない」という観測であり、決済の結論は発注執行の決済のゲート（建玉なし）が見送る。
/// 台帳（判断の保有の読み元）は約定の射影が届くまで遅れるので、決済の約定の直後はこちらが先に 0 になる。
/// </para>
/// </summary>
public sealed record InFlightCloseCoverage(
    bool Covered, int InFlightQuantity, IReadOnlyList<string> OrderIds, int? BrokerClosableQuantity = null);
