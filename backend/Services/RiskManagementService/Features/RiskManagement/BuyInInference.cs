using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159: 強制買戻し（buy-in）の**事後推定**（純関数）。
//
// **これは検知ではなく推定である。** イベント検知の供給元が無い（SIMULATE では原理的に発生せず、専用の通知 API も
// 見当たらない）ため、決定4 の改訂は「**建玉の消失を約定履歴と突合し、事後に推定する**」代替を定めた。
//
// 突合の式（IADR-0159 決定1）:
//
//     未説明の消失 = max(0, 台帳の空売り数量 − ブローカの空売り数量 − 処理中の決済数量)
//
//   - **台帳**は自らの約定履歴の畳み込み（PortfolioProjection.ProjectOpenPositions）である。自分で手仕舞い・
//     損切りをすればその約定が台帳に入り、台帳側の数量が減る。**これが正常な決済と強制買戻しを区別する唯一の手段**
//     である（決定4 改訂・#419 の設計上の注意）。
//   - **処理中の決済**は「承認済みだが約定が台帳へまだ届いていない決済」＝**自らの決済指示そのもの**である。
//     これを引かないと、約定の反映遅れという**通常運行の事象**が毎回強制買戻しとして推定される。
//
// **どちらへ倒すか（非対称）**: 決定4 の目的は「**同じ銘柄で繰り返さない**」ことである。
//   - 過剰推定の代価 … 不要な 30 日禁止（**機会損失**。資金は失わない）
//   - 過少推定の代価 … **同じ銘柄で強制買戻しを繰り返す**（損失に上限の無い取引での強制決済の反復）
// 同じ重さではない。**過剰側（fail-closed）へ倒す**。具体的には (a) ブローカ応答に現れない銘柄は数量 0＝全量消失と
// して扱い、(b) 「建玉が 0 になった時点」を待たず**数量差**で突合する。
public static class BuyInInference
{
    /// <summary>
    /// 突合の結果 1 銘柄ぶん。<see cref="NewlyInferredQuantity"/> が正なら**新たな推定**（禁止を登録する）、
    /// 0 なら**リセット**（乖離が解消したので帰属数量を戻す。禁止期限は戻さない）。
    /// </summary>
    public sealed record Outcome(
        string Symbol,
        Market Market,
        int LedgerShortQuantity,
        int BrokerShortQuantity,
        int InFlightCloseQuantity,
        int UnexplainedQuantity,
        int NewlyInferredQuantity,
        IReadOnlyList<BuyInCoveringFill> CoveringFills);

    /// <summary>
    /// 建玉の消失を自らの決済指示と突合し、強制買戻しを推定する。
    /// </summary>
    /// <param name="ledgerPositions">取引台帳の建玉射影（＝自らの約定履歴）。</param>
    /// <param name="brokerPositions">
    /// ブローカが返した建玉の観測。**照会できなかった場合は本関数を呼ばないこと**——空列を渡すと
    /// 「全建玉が消失した」と読まれ、全銘柄が 30 日禁止になる（IADR-0159 決定4。統制ではなく事故になる）。
    /// </param>
    /// <param name="fills">台帳の約定列。突合の根拠（監査ログ）として決済約定を抜き出すために用いる。</param>
    /// <param name="inFlightCloseQuantities">
    /// 銘柄ごとの「処理中の決済数量」（承認済み・未約定）。**自らの決済指示**であり、未説明の消失から差し引く。
    /// </param>
    /// <param name="latestPerSymbol">銘柄ごとの最新の推定記録（帰属数量の突き合わせに用いる）。</param>
    public static IReadOnlyList<Outcome> Infer(
        IReadOnlyList<OpenPosition> ledgerPositions,
        IReadOnlyList<BrokerPositionSnapshot> brokerPositions,
        IReadOnlyList<LedgerFill> fills,
        IReadOnlyDictionary<(string Symbol, Market Market), int> inFlightCloseQuantities,
        IReadOnlyList<BuyInInferenceRecord> latestPerSymbol)
    {
        ArgumentNullException.ThrowIfNull(ledgerPositions);
        ArgumentNullException.ThrowIfNull(brokerPositions);
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(inFlightCloseQuantities);
        ArgumentNullException.ThrowIfNull(latestPerSymbol);

        // 台帳・ブローカとも**空売り建玉だけ**を見る（強制買戻しはショートにしか起こらない）。数量は正の値へ揃える。
        var ledgerShorts = new Dictionary<(string Symbol, Market Market), int>();
        foreach (var p in ledgerPositions.Where(p => p.Side == TradeSide.Sell && p.Quantity > 0))
        {
            var key = (p.Symbol, p.Market);
            ledgerShorts[key] = ledgerShorts.GetValueOrDefault(key) + p.Quantity;
        }

        var brokerShorts = new Dictionary<(string Symbol, Market Market), int>();
        foreach (var p in brokerPositions.Where(p => p.Quantity < 0))
        {
            var key = (p.Symbol, p.Market);
            brokerShorts[key] = brokerShorts.GetValueOrDefault(key) + -p.Quantity;
        }

        var attributed = latestPerSymbol.ToDictionary(r => (r.Symbol, r.Market), r => r.UnexplainedQuantity);

        // 対象は「台帳に空売り建玉がある銘柄」と「既に帰属数量を持つ銘柄」の和集合。後者を落とすと、
        // 台帳から建玉が消えた（＝人が台帳を是正した）ときにリセット行を書けず、帰属数量が残り続ける。
        var keys = ledgerShorts.Keys
            .Union(attributed.Where(kv => kv.Value > 0).Select(kv => kv.Key))
            .OrderBy(k => k.Symbol, StringComparer.Ordinal)
            .ThenBy(k => k.Market);

        var outcomes = new List<Outcome>();
        foreach (var key in keys)
        {
            var ledgerShort = ledgerShorts.GetValueOrDefault(key);
            var brokerShort = brokerShorts.GetValueOrDefault(key);
            var inFlightClose = Math.Max(0, inFlightCloseQuantities.GetValueOrDefault(key));

            // ブローカのほうが空売り建玉が**多い**場合（台帳に無い建玉）は本推定の対象外である
            // （強制買戻しは建玉が減る事象であり、増える乖離は IADR-0118 の建玉突合が別途報告する）。
            var unexplained = Math.Max(0, ledgerShort - brokerShort - inFlightClose);
            var already = Math.Max(0, attributed.GetValueOrDefault(key));

            if (unexplained > already)
            {
                // **新たな推定**。増分だけを数える——同じ乖離を毎巡回で観測しても推定は 1 回であり、
                // 禁止期間が巡回ごとに 30 日ずつ延びない（IADR-0159 決定2）。
                outcomes.Add(new Outcome(
                    key.Symbol, key.Market, ledgerShort, brokerShort, inFlightClose,
                    UnexplainedQuantity: unexplained,
                    NewlyInferredQuantity: unexplained - already,
                    CoveringFills: CoveringFillsFor(fills, key.Symbol, key.Market)));
            }
            else if (unexplained < already)
            {
                // **リセット**。乖離が解消した（台帳が是正された・建玉が建て直された）。帰属数量を現在値へ戻す。
                // **禁止期限は戻さない**（30 日は経過でしか解けない）。
                outcomes.Add(new Outcome(
                    key.Symbol, key.Market, ledgerShort, brokerShort, inFlightClose,
                    UnexplainedQuantity: unexplained,
                    NewlyInferredQuantity: 0,
                    CoveringFills: CoveringFillsFor(fills, key.Symbol, key.Market)));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// FR-11, ADR-0016 決定4: 推定の**根拠**として監査ログへ残す「自らの決済約定」。
    /// <para>
    /// 現在の建玉に対応する範囲だけを返す——約定を時系列に畳み、**建玉が最後に 0 になった時点より後**の約定が
    /// 現在の建玉を作っている。それ以前の決済は別の建玉のものであり、根拠として並べると誤読を招く。
    /// </para>
    /// </summary>
    public static IReadOnlyList<BuyInCoveringFill> CoveringFillsFor(
        IReadOnlyList<LedgerFill> fills, string symbol, Market market)
    {
        ArgumentNullException.ThrowIfNull(fills);

        var ordered = fills
            .Where(f => f.Symbol == symbol && f.Market == market)
            .OrderBy(f => f.ExecutedAt)
            .ToList();

        var startIndex = 0;
        var net = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            net += ordered[i].Side == TradeSide.Buy ? ordered[i].Quantity : -ordered[i].Quantity;
            if (net == 0)
            {
                // ここで建玉が消滅した。次の約定から現在の建玉が始まる。
                startIndex = i + 1;
            }
        }

        return ordered
            .Skip(startIndex)
            .Where(f => f.Side == TradeSide.Buy) // 空売り建玉に対する自らの決済指示＝買い戻し。
            .Select(f => new BuyInCoveringFill(f.Side, f.Quantity, f.Price, f.ExecutedAt))
            .ToList();
    }
}
