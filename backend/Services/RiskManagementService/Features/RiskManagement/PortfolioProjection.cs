using RiskManagementService.Common.Abstractions;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-05, IADR-0018, #337（#249 吸収）, IADR-0246: 取引台帳（LedgerFill 列）から判定入力 PortfolioState を
// 組み立てる純関数。符号付き在庫・平均取得単価法で Open/Close を統一処理し、実現損益は在庫が減少する約定で計上する。
// DB・Clock 非依存（now と initialCapital は呼び出し側＝プロバイダが与える）。
public static class PortfolioProjection
{
    // 🔴 #249 / IADR-0246: 取引日境界は**約定の市場の現地取引日**で解釈する（従来の JST 固定 +9 を廃止）。
    // JST 固定では米国市場の日次境界が ET 10–11 時（セッション中）に走り、当日損益・日次発注枠・
    // 同日再エントリーが同一セッションの途中でリセットされていた。導出は TradingDay.Of（単一情報源）。
    public static DateOnly TradeDate(DateTimeOffset instant, Market market) => TradingDay.Of(instant, market);

    // IADR-0036: currentPrices（(Symbol,Market)→現在値）と equityHighWaterMark（資金ピーク）を与えると含み損益・DD を
    // 時価で算出する。既定（null）では従来どおり UnrealizedPnl=0/DrawdownRatio=0（実供給の結線は #22/#82 の後続）。
    // #249 / IADR-0246: 「当日」は市場ごとに異なる（同一瞬間でも JST と ET で日付が違う）ため、
    // DateOnly ではなく判定時点（now）を受け、約定ごとに**その市場の現地取引日**同士で比較する。
    //
    // FR-10, #829, IADR-0346 決定2: workingEntries（承認済みで終端でない新規建て注文）を与えると、当日分の残数量を
    // 日次発注累計・段階資金・保有建玉数へ算入する。既定（null）は従来どおり約定だけ。
    public static PortfolioState Project(
        IReadOnlyList<LedgerFill> fills,
        DateTimeOffset now,
        decimal initialCapital,
        IReadOnlyDictionary<(string Symbol, Market Market), decimal>? currentPrices = null,
        decimal? equityHighWaterMark = null,
        IReadOnlyList<WorkingEntryOrder>? workingEntries = null)
    {
        ArgumentNullException.ThrowIfNull(fills);

        // 銘柄（銘柄コード, 市場）ごとの符号付き在庫（+ ロング / − ショート）と平均取得単価。
        // FR-10, FR-17, #257, IADR-0107 決定1/4: 建玉はローカル通貨のまま畳み込み（市場監視の損切り検知が現在値と
        // 同一通貨で比較するため）、金額集計・実現損益は同じ畳み込みを基準通貨（USD）の単価でもう一度行って積む。
        // 基準通貨側の平均取得単価は建玉の加重平均約定時レートを内包するため、実現損益には為替の影響も自然に入る。
        var positions = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();
        var positionsInBase = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();

        decimal realizedBeforeToday = 0m;
        decimal realizedToday = 0m;
        decimal orderedToday = 0m;
        var symbolsTradedToday = new HashSet<(string Symbol, Market Market)>();
        var consecutiveLosses = 0;

        // 約定は時系列順に畳み込む（実現損益・連敗・当日境界の判定に順序が必要）。
        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            var key = (fill.Symbol, fill.Market);
            var signedQ = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            positions.TryGetValue(key, out var pos);

            // IADR-0033: 平均取得単価法の畳み込みは共有の純関数（SignedInventory）を単一情報源とする。
            // #849, IADR-0350 決定 3: 取り込み行は ApplyToLot が平均取得単価で在庫だけを減らす（実現損益 0）。
            var applied = ApplyToLot(new InventoryLot(pos.Qty, pos.AvgCost), signedQ, fill.Price, fill.IsDriftAdoption);
            positions[key] = (applied.Lot.Quantity, applied.Lot.AverageCost);

            // IADR-0107: 同じ畳み込みを基準通貨の単価で行う。実現損益・取得額・エクイティはこちらを採る。
            positionsInBase.TryGetValue(key, out var posInBase);
            var appliedInBase = ApplyToLot(
                new InventoryLot(posInBase.Qty, posInBase.AvgCost), signedQ, fill.PriceInBase, fill.IsDriftAdoption);
            positionsInBase[key] = (appliedInBase.Lot.Quantity, appliedInBase.Lot.AverageCost);
            var realized = appliedInBase.RealizedPnl;

            // FR-10, FR-11, #849, IADR-0350 決定 3: **取り込み行は約定ではない。** 在庫（＝段階資金・保有建玉数・
            // 含み損益の対象）だけを観測へ合わせ、当日発注累計・同日売買銘柄・当日実現損益・連敗には触れない。
            // システム外の売買の価格も時刻も分からないため、これらを動かせば「分からない値」を確定値として
            // 統制へ流し込むことになる（実現損益は上の ApplyToLot により構造的に 0 である）。
            if (fill.IsDriftAdoption)
                continue;

            // #249 / IADR-0246: 当日判定は約定の市場の現地取引日で行う（JST 固定だと ET 夜間で日付がずれる）。
            var date = TradeDate(fill.ExecutedAt, fill.Market);
            var today = TradeDate(now, fill.Market);
            var isToday = date == today;
            if (isToday)
            {
                // FR-10, #302, #329, IADR-0130 決定4: 日次発注枠は「**新規建ての発注代金の合計**で判定し、
                // 手仕舞い（決済）注文は算入しない」（計画 05_trading-assumptions §5）。
                // ゲート（RiskEvaluator の isEntry）と同じ区別をカウンタ側にも入れる。片側だけだと
                // 「拒否はされないが枠は減る」状態が残り、大口決済が当日の新規建て枠を枯渇させて
                // 手仕舞いをためらわせる（ADR-0009 と逆向きの誘因）。
                if (fill.PositionEffect == PositionEffect.Open)
                {
                    orderedToday += fill.Quantity * fill.PriceInBase;
                }

                symbolsTradedToday.Add(key);
                realizedToday += realized;
            }
            else if (date < today)
            {
                realizedBeforeToday += realized;
            }

            // 連敗: 在庫を減少させた（実現が発生した）約定のみを 1 取引として数える。
            // 損失で +1、利益で 0 にリセット、損益ゼロ（建て増しや損益なし）は据え置き。
            if (realized < 0m)
                consecutiveLosses++;
            else if (realized > 0m)
                consecutiveLosses = 0;
        }

        var invested = 0m;
        var openPositions = new List<OpenPosition>();
        foreach (var (key, pos) in positions)
        {
            if (pos.Qty == 0)
                continue;
            var avgCostInBase = positionsInBase[key].AvgCost;
            // IADR-0107: 取得額（段階資金上限の累計判定・IADR-0005）は基準通貨で積む。
            invested += Math.Abs(pos.Qty) * avgCostInBase;
            var side = pos.Qty > 0 ? TradeSide.Buy : TradeSide.Sell;
            var impliedRate = ImpliedRateToBase(pos.AvgCost, avgCostInBase);
            openPositions.Add(new OpenPosition(
                key.Symbol, key.Market, side, Math.Abs(pos.Qty), pos.AvgCost, StopLossPrice: null, impliedRate));
        }

        // FR-10, #829, IADR-0346 決定2: 承認済みで生きている新規建て注文の**残数量**を算入する。
        // 計画 FR-10 は日次枠を「新規建ての発注代金の合計」で定め、§5 は Stage の上限を「発注可能額」と呼ぶ。
        // 約定だけを数えると、指値が溜まっている間は上限を超えて承認し続けられる（#27 と同型の累計超過の穴）。
        var openPositionCount = openPositions.Count;
        if (workingEntries is { Count: > 0 })
        {
            // 残数量＝承認数量 − 同じ DecisionId の約定累計。約定は**同じ fills** から数える——約定が届くと
            // 約定側が増えて残数量が同じだけ減るため、合計は変わらない（二重計上も取りこぼしもしない）。
            var filledByDecision = new Dictionary<Guid, int>();
            foreach (var fill in fills)
            {
                if (fill.DecisionId == Guid.Empty)
                    continue; // 相関できないレガシー行は注文へ帰属させない
                filledByDecision[fill.DecisionId] = filledByDecision.GetValueOrDefault(fill.DecisionId) + fill.Quantity;
            }

            var heldKeys = openPositions.Select(p => (p.Symbol, p.Market)).ToHashSet();
            var pendingNewKeys = new HashSet<(string Symbol, Market Market)>();
            foreach (var order in workingEntries)
            {
                // 当日は承認時刻の**市場の現地取引日**で判定する（約定と同じ規則・IADR-0246）。
                // 終端イベントが届かない行（実測で翌日も Accepted のまま）が翌日以降の枠を食い続けないため。
                if (TradeDate(order.ApprovedAt, order.Market) != TradeDate(now, order.Market))
                    continue;

                var remaining = Math.Max(0, order.Quantity - filledByDecision.GetValueOrDefault(order.DecisionId));
                if (remaining == 0)
                    continue;

                var notionalInBase = remaining * order.PriceInBase;
                orderedToday += notionalInBase;
                invested += notionalInBase;

                // 保有建玉数は建玉の無い（銘柄, 市場）だけ増やす（建て増しは数を増やさない。建玉キーの粒度は従来のまま）。
                var key = (order.Symbol, order.Market);
                if (!heldKeys.Contains(key))
                    pendingNewKeys.Add(key);
            }

            openPositionCount += pendingNewKeys.Count;
            // IADR-0346 決定4: SymbolsTradedToday（同日再エントリーの入力）には算入しない。
        }

        // IADR-0036: 含み損益は現在値入力から時価算出（現在値欠損は 0）。当日開始運用資金（固定基準）= 初期資金 + 当日より前の実現損益。
        var capital = initialCapital + realizedBeforeToday;
        var unrealized = PortfolioValuation.UnrealizedPnl(openPositions, currentPrices);
        // 現在エクイティ = 固定基準 + 当日実現 + 含み。DD はピーク入力から算出（ピーク追跡は #22/#82 の後続）。
        var currentEquity = capital + realizedToday + unrealized;

        return new PortfolioState
        {
            Capital = capital,
            OpenPositionCount = openPositionCount,
            InvestedCapital = invested,
            DailyOrderedAmount = orderedToday,
            DailyRealizedPnl = realizedToday,
            UnrealizedPnl = unrealized,
            DrawdownRatio = PortfolioValuation.DrawdownRatio(equityHighWaterMark, currentEquity),
            ConsecutiveLosses = consecutiveLosses,
            SymbolsTradedToday = symbolsTradedToday,
        };
    }

    // FR-03, FR-10, IADR-0030: 約定列から銘柄別ネット建玉（数量>0）を射影する純関数。損切りライン検知（市場監視）へ
    // 供給する保有ポジションの一次射影。実現損益・当日境界は不要のため、符号付き在庫・平均取得単価のみを畳み込む
    // （Project と同一の Apply を再利用）。数量 0（全決済）は除外する。
    // IADR-0107: 建玉に紐づく加重平均の約定時レート＝基準通貨の平均取得単価 ÷ ローカル通貨の平均取得単価。
    // 含み損益の換算（Project）と損切り決済への引き継ぎ（ProjectOpenPositions）で同じ導出を使う。
    // 単価 0（理論上のみ）はレート 1 に倒す（0 除算を作らない）。
    // FR-10, FR-11, #849, IADR-0350 決定 3: 1 行を在庫へ適用する**単一の入口**（Project・ProjectOpenPositions・
    // PortfolioValuation.EquityHighWaterMark が共有する）。
    //
    // 約定は従来どおり約定単価で畳む。**乖離の取り込み行は、その時点の平均取得単価で畳む** ——
    // SignedInventory.Apply の実現損益は (決済単価 − 取得単価) × 数量 なので、決済単価に取得単価そのものを
    // 渡せば**丸め誤差なしに 0** になる（行に保存した単価を使うと、為替換算の端数で ±1e-27 の「損益」が生まれ、
    // 連敗カウンタを動かし得る）。取得単価は不変のため、部分的な取り込みの後も残りの建玉の評価は変わらない。
    // 取り込みは在庫を**減らす方向に限る**（サービス層が保証する）ため、在庫 0 への適用・建て増し・反転は
    // ここへ来ない。来た場合も SignedInventory の通常の規則で畳まれ、例外にはしない（射影は読み取り経路であり、
    // 1 行の異常で統制の全判定を落とさない）。
    internal static InventoryFillResult ApplyToLot(InventoryLot lot, int signedQuantity, decimal price, bool isDriftAdoption) =>
        SignedInventory.Apply(lot, signedQuantity, isDriftAdoption ? lot.AverageCost : price);

    private static decimal ImpliedRateToBase(decimal averageCost, decimal averageCostInBase) =>
        averageCost > 0m ? averageCostInBase / averageCost : 1m;

    // IADR-0035: 損切り価格は最新の同方向エントリー（新規/建て増し/反転）を採る（一部決済では保持・全決済で消滅）。
    // IADR-0107: 価格（平均取得単価・損切り価格）はローカル通貨のまま返す（損切り検知は現在値と同一通貨で比較する）。
    // 併せて建玉の加重平均約定時レート（FxRateToBase）を載せる。機械執行の決済（維持率割れの自動縮小等）が
    // 決済注文へ引き継ぎ、決済レグの台帳集計が基準通貨で揃うようにするため（Project と同じ二重畳み込み）。
    public static IReadOnlyList<OpenPosition> ProjectOpenPositions(IReadOnlyList<LedgerFill> fills)
    {
        ArgumentNullException.ThrowIfNull(fills);

        var positions = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();
        var positionsInBase = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();
        var stops = new Dictionary<(string Symbol, Market Market), decimal?>();
        foreach (var fill in fills.OrderBy(f => f.ExecutedAt))
        {
            var key = (fill.Symbol, fill.Market);
            var signedQ = fill.Side == TradeSide.Buy ? fill.Quantity : -fill.Quantity;
            positions.TryGetValue(key, out var pos);
            // IADR-0033: 共有の畳み込み純関数を用いる（実現損益はここでは不要）。
            // #849, IADR-0350 決定 3: 取り込み行は平均取得単価で在庫だけを減らす（Project と同じ規則）。
            var applied = ApplyToLot(new InventoryLot(pos.Qty, pos.AvgCost), signedQ, fill.Price, fill.IsDriftAdoption);
            positions[key] = (applied.Lot.Quantity, applied.Lot.AverageCost);

            // IADR-0107: 基準通貨側の平均取得単価（＝加重平均約定時レートを内包）を並行して畳み込む。
            positionsInBase.TryGetValue(key, out var posInBase);
            var appliedInBase = ApplyToLot(
                new InventoryLot(posInBase.Qty, posInBase.AvgCost), signedQ, fill.PriceInBase, fill.IsDriftAdoption);
            positionsInBase[key] = (appliedInBase.Lot.Quantity, appliedInBase.Lot.AverageCost);

            // IADR-0035: 建玉が消滅したら損切りも消滅。約定が建玉と同方向（新規/建て増し/反転）なら最新エントリーの損切りに更新。
            // 一部決済（反対方向で符号は不変）は既存の損切りを保持する。
            if (applied.Lot.Quantity == 0)
                stops.Remove(key);
            else if (Math.Sign(applied.Lot.Quantity) == Math.Sign(signedQ))
                stops[key] = fill.StopLossPrice;
        }

        var result = new List<OpenPosition>();
        foreach (var (key, pos) in positions)
        {
            if (pos.Qty == 0)
                continue; // 全決済済みは保有なし
            var side = pos.Qty > 0 ? TradeSide.Buy : TradeSide.Sell;
            var impliedRate = ImpliedRateToBase(pos.AvgCost, positionsInBase[key].AvgCost);
            result.Add(new OpenPosition(
                key.Symbol, key.Market, side, Math.Abs(pos.Qty), pos.AvgCost,
                stops.GetValueOrDefault(key), impliedRate));
        }

        return result;
    }
}
