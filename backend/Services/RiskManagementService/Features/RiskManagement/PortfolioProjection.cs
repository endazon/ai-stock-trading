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
    //
    // 🔴 FR-10, #869, ADR-0041 決定2, IADR-0354: **本射影は統制上限の基準資金（equity）を作らない。**
    // 基準資金の供給元はブローカーの口座照会（ICapitalBaselineStore）に確定しており、台帳から導くのをやめた
    // （従前の「初期資金 ＋ 当日より前の実現損益」は含み損益を含まず、計画〔FR-10 本文・05_trading-assumptions
    // §5 注記〕の「前営業日終値時点の USD 評価額」と食い違っていた）。
    // <paramref name="initialCapital"/> が残るのは**ドローダウンのエクイティ系列の起点**としてだけである
    // （IADR-0066。DD は比率上限の分母ではなく「ピークと現在の比」であり、ADR-0041 決定2 の射程外）。
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
            // FR-04, #934, IADR-0390 決定1: 残数量の導出は ProjectWorkingEntries に切り出した（判断の入力も同じ定義を読む）。
            var heldKeys = openPositions.Select(p => (p.Symbol, p.Market)).ToHashSet();
            var pendingNewKeys = new HashSet<(string Symbol, Market Market)>();
            foreach (var (order, remaining) in ProjectWorkingEntries(fills, now, workingEntries))
            {
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

        // IADR-0036: 含み損益は現在値入力から時価算出（現在値欠損は 0）。
        var unrealized = PortfolioValuation.UnrealizedPnl(openPositions, currentPrices);
        // 台帳由来の現在エクイティ = 初期資金 + 累計実現損益 + 含み。DD はピーク入力から算出（IADR-0066）。
        // 🔴 #869 / ADR-0041 決定2: **これは統制上限の基準資金ではない。** 基準資金はブローカーの口座照会に
        // 由来し（ICapitalBaselineStore）、本射影は 1 度も触れない。ここで積むのは DD の入力だけである。
        var currentEquity = initialCapital + realizedBeforeToday + realizedToday + unrealized;

        return new PortfolioState
        {
            LedgerEquity = currentEquity,
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

    // FR-10, #829, IADR-0346 決定2 / FR-04, #934, IADR-0390 決定1: 承認済みで生きている新規建て注文のうち、
    // **当日（承認時刻の市場の現地取引日）**かつ**残数量 > 0** のものを残数量つきで返す純関数。
    // 統制（Project の日次発注累計・段階資金・保有建玉数）と判断の入力（GET /risk-controls/working-entry-orders）が
    // **同じ定義**を読むために 1 か所に置く。定義が 2 か所に分かれると「統制は数えるが判断は知らない」（#934）が再発する。
    //
    // - 残数量＝承認数量 − 同じ DecisionId の約定累計。約定は**同じ fills** から数える——約定が届くと
    //   約定側が増えて残数量が同じだけ減るため、合計は変わらない（二重計上も取りこぼしもしない）。
    // - 当日の判定: 終端イベントが届かない行（実測で翌日も Accepted のまま）が翌日以降に生き続けないため（IADR-0246）。
    public static IReadOnlyList<(WorkingEntryOrder Order, int Remaining)> ProjectWorkingEntries(
        IReadOnlyList<LedgerFill> fills,
        DateTimeOffset now,
        IReadOnlyList<WorkingEntryOrder> workingEntries)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(workingEntries);

        var result = new List<(WorkingEntryOrder Order, int Remaining)>();
        if (workingEntries.Count == 0)
            return result;

        var filledByDecision = new Dictionary<Guid, int>();
        foreach (var fill in fills)
        {
            if (fill.DecisionId == Guid.Empty)
                continue; // 相関できないレガシー行は注文へ帰属させない
            filledByDecision[fill.DecisionId] = filledByDecision.GetValueOrDefault(fill.DecisionId) + fill.Quantity;
        }

        foreach (var order in workingEntries)
        {
            if (TradeDate(order.ApprovedAt, order.Market) != TradeDate(now, order.Market))
                continue;

            var remaining = Math.Max(0, order.Quantity - filledByDecision.GetValueOrDefault(order.DecisionId));
            if (remaining == 0)
                continue;

            result.Add((order, remaining));
        }

        return result;
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

    // FR-10, FR-03, #936, IADR-0393（IADR-0035 の「最新の同方向エントリー」を改める）: 損切り価格は、保有中の建玉を
    // **エントリーごとのロット**（数量・その約定の損切り価格）として持ち、**残っているロットのうち最も保護的なライン**
    // （ロング: 最も高い／ショート: 最も低い）を採る。減少（一部決済・取り込み）は**古いロットから**削る（発注の順。下の追記）。
    // 反転は反転後の残りを 1 ロットにし、全決済でロットを捨てる。
    // 🔴 最新エントリーのラインを採ると、同じ銘柄に後から低いラインで建て増したとき、先に建てた記録のラインで
    // 市場監視が到達を出さない（稼働 PoC の AAPL 713 株 331.67 と 715 株 330.88。損切りが 0.79 遅れた）。
    // 市場監視はこのラインで到達を 1 件出し、発注執行は S1 の行ごとに自分のラインで判定する（IADR-0344 決定4）ので、
    // ここが最も保護的なラインであれば、どの行も自分のラインより遅れて判定されない。
    // 🔴 ロットの帰属は推定である（台帳はどの決済がどのエントリーを閉じたかを持たない）。古い順に削るのは、
    // 発注執行が外部要因の減少を古い行から割り当てる規則と同じ向きにするためで、取り違えたときは
    // 「ラインが実際より保護的」側（発注執行の行が 1 件も達していない到達が出る）へだけ倒れる
    // （ロットの並びと発注執行の行の並びが一致する限り。並びが食い違う入力は IADR-0393 の残余リスク）。
    // ラインを持たないロット（不明）の有無は StopLossUnknown で返す（OpenPositionsService が近似で見積もる）。
    // 🔴 ［2026-09-25 追記 / #936 の監査］「古い」は**約定時刻ではなく発注の順**である。ロットは
    // (EntryOrderedAt＝承認時刻 ?? 約定時刻, DecisionId) の順に並べる —— 発注執行の割り当て (CreatedAt, EntryDecisionId)
    // と同じ鍵の形である。稼働環境では先に出した 715 株（330.88）の指値が板に残り、後に出した 713 株（331.67）が
    // 先に約定した。約定時刻の順で削ると、外部要因の減少で発注執行に残る 713 株の 331.67 を台帳が先に捨てる（T-10-769）。
    // 在庫・平均取得単価の畳み込みは従来どおり約定時刻の順のまま（ここで変えるのはロットの並びだけ）。
    // IADR-0107: 価格（平均取得単価・損切り価格）はローカル通貨のまま返す（損切り検知は現在値と同一通貨で比較する）。
    // 併せて建玉の加重平均約定時レート（FxRateToBase）を載せる。機械執行の決済（維持率割れの自動縮小等）が
    // 決済注文へ引き継ぎ、決済レグの台帳集計が基準通貨で揃うようにするため（Project と同じ二重畳み込み）。
    public static IReadOnlyList<OpenPosition> ProjectOpenPositions(IReadOnlyList<LedgerFill> fills)
    {
        ArgumentNullException.ThrowIfNull(fills);

        var positions = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();
        var positionsInBase = new Dictionary<(string Symbol, Market Market), (int Qty, decimal AvgCost)>();
        var lots = new Dictionary<(string Symbol, Market Market), List<StopLossLot>>();
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

            // #936, IADR-0393: 建玉が消滅したらロットも消滅。新規・反転は残りを 1 ロットに、建て増しはロットを足し、
            // 一部決済・取り込み（反対方向で符号は不変）は古いロットから削る。
            ApplyToStopLossLots(lots, key, pos.Qty, applied.Lot.Quantity, fill);
        }

        var result = new List<OpenPosition>();
        foreach (var (key, pos) in positions)
        {
            if (pos.Qty == 0)
                continue; // 全決済済みは保有なし
            var side = pos.Qty > 0 ? TradeSide.Buy : TradeSide.Sell;
            var impliedRate = ImpliedRateToBase(pos.AvgCost, positionsInBase[key].AvgCost);
            var held = lots.GetValueOrDefault(key) ?? [];
            result.Add(new OpenPosition(
                key.Symbol, key.Market, side, Math.Abs(pos.Qty), pos.AvgCost,
                MostProtective(side, held.Select(l => l.StopLossPrice).OfType<decimal>()), impliedRate)
            {
                // 🔴 ロットが無い（≠ 0 の建玉に対して起こらないはずの状態）も「不明」に倒す。無いと読むと近似も入らない。
                StopLossUnknown = held.Count == 0 || held.Any(l => l.StopLossPrice is null),
            });
        }

        return result;
    }

    /// <summary>
    /// #936, IADR-0393: 損切りラインのうち最も保護的な値（ロング: 最も高い／ショート: 最も低い）。候補が無ければ null。
    /// 市場監視は建玉 1 件をこの値と比べ（到達はロング: 現在値 ≦ ライン）、発注執行が行ごとに自分のラインで判定する。
    /// </summary>
    public static decimal? MostProtective(TradeSide side, IEnumerable<decimal> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        decimal? best = null;
        foreach (var line in lines)
        {
            if (best is not { } current || (side == TradeSide.Buy ? line > current : line < current))
                best = line;
        }

        return best;
    }

    // #936, IADR-0393: 保有中の建玉を構成するエントリー 1 件ぶん（数量・その約定の損切り価格。null＝記録なし）。
    // OrderedAt・DecisionId はロットの並び（発注の順）の鍵である（2026-09-25 追記）。
    private sealed class StopLossLot(int quantity, decimal? stopLossPrice, DateTimeOffset orderedAt, Guid decisionId)
    {
        public int Quantity { get; set; } = quantity;

        public decimal? StopLossPrice { get; } = stopLossPrice;

        public DateTimeOffset OrderedAt { get; } = orderedAt;

        public Guid DecisionId { get; } = decisionId;

        // 発注執行の ProtectiveStopNetting.Allocate の並び（CreatedAt → EntryDecisionId）と同じ比べ方。
        public bool IsOrderedAfter(StopLossLot other) =>
            OrderedAt != other.OrderedAt ? OrderedAt > other.OrderedAt : DecisionId.CompareTo(other.DecisionId) > 0;
    }

    private static void ApplyToStopLossLots(
        Dictionary<(string Symbol, Market Market), List<StopLossLot>> lots,
        (string Symbol, Market Market) key,
        int previousQuantity,
        int newQuantity,
        LedgerFill fill)
    {
        // 🔴 承認時刻が分からない行（取り込み行・承認時刻を持たない入力）は約定時刻で代える。推定で埋めない。
        var orderedAt = fill.EntryOrderedAt ?? fill.ExecutedAt;

        if (newQuantity == 0)
        {
            lots.Remove(key); // 全決済: 損切りも消滅
            return;
        }

        if (previousQuantity == 0 || Math.Sign(previousQuantity) != Math.Sign(newQuantity))
        {
            // 新規・反転: 残りはこの約定だけのロットである（反転前の建玉のラインは引き継がない）。
            lots[key] = [new StopLossLot(Math.Abs(newQuantity), fill.StopLossPrice, orderedAt, fill.DecisionId)];
            return;
        }

        var held = lots.TryGetValue(key, out var existing) ? existing : lots[key] = [];
        if (Math.Abs(newQuantity) > Math.Abs(previousQuantity))
        {
            // 建て増し: 発注の順の位置へ差し込む（後から約定した先の発注は、先に約定した後の発注より前に入る）。
            // 同じ鍵（同じ承認の分割約定）は後ろへ付ける。
            var lot = new StopLossLot(fill.Quantity, fill.StopLossPrice, orderedAt, fill.DecisionId);
            var index = held.Count;
            while (index > 0 && held[index - 1].IsOrderedAfter(lot))
                index--;
            held.Insert(index, lot);
            return;
        }

        // 一部決済・取り込み: 古いロット（発注の順）から削る（発注執行が外部要因の減少を古い行から割り当てるのと同じ向き）。
        var toRemove = Math.Abs(previousQuantity) - Math.Abs(newQuantity);
        while (toRemove > 0 && held.Count > 0)
        {
            var oldest = held[0];
            var taken = Math.Min(oldest.Quantity, toRemove);
            oldest.Quantity -= taken;
            toRemove -= taken;
            if (oldest.Quantity == 0)
                held.RemoveAt(0);
        }
    }
}
