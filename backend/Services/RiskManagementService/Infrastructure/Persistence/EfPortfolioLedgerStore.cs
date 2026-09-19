using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, FR-05, IADR-0018: 取引台帳の EF 実装（追記専用・専有 DB）。承認は DecisionId、約定は OrderId で冪等。
public sealed class EfPortfolioLedgerStore(RiskManagementDbContext db) : IPortfolioLedgerStore
{
    public void AppendApproval(
        Guid decisionId,
        OrderIntent intent,
        DateTimeOffset approvedAt,
        decimal? fxRateBaseToDisplay = null)
    {
        ArgumentNullException.ThrowIfNull(intent);

        // 冪等: 既に承認済みの DecisionId は無視する（ブローカ再送・IADR-0129 決定 10 の根拠のひとつ）。
        if (db.ApprovedOrders.Find(decisionId) is not null)
            return;

        db.ApprovedOrders.Add(new ApprovedOrderRow
        {
            DecisionId = decisionId,
            Symbol = intent.Symbol,
            Market = intent.Market,
            Side = intent.Side,
            ProductType = intent.ProductType,
            PositionEffect = intent.PositionEffect,
            Mode = intent.Mode,
            Quantity = intent.Quantity,
            Price = intent.Price,
            StopLossPrice = intent.StopLossPrice,
            // IADR-0107: 承認時点の換算レート（＝約定時レートの近似）を台帳に固定する。後から引き直さない。
            FxRateToBase = intent.FxRateToBase,
            // #611, IADR-0286 決定1: 認識時レート（1 USD あたりの円）を承認時点で固定する。null＝未記録（推定で埋めない）。
            FxRateBaseToDisplay = fxRateBaseToDisplay,
            ApprovedAt = approvedAt,
        });
        db.SaveChanges();
    }

    // FR-20, #386, IADR-0149 決定2: 承認済み注文の建玉効果を DecisionId で引く（未承認は null＝不明）。
    public PositionEffect? FindApprovedPositionEffect(Guid decisionId) =>
        db.ApprovedOrders.Find(decisionId)?.PositionEffect;

    // FR-19, #425, IADR-0165: 承認 Intent を DecisionId で引く（未承認は null＝不明）。
    // IADR-0107: 列追加前の既存行（FxRateToBase が null）はレート 1＝基準通貨建てとして扱う（GetFills と同じ）。
    public OrderIntent? FindApprovedIntent(Guid decisionId) =>
        db.ApprovedOrders.Find(decisionId) is { } a
            ? new OrderIntent(
                a.Symbol, a.Market, a.Side, a.ProductType, a.Mode, a.Quantity, a.Price,
                a.PositionEffect, a.StopLossPrice, a.FxRateToBase ?? 1m)
            : null;

    public bool AppendFill(
        Guid decisionId,
        string orderId,
        int filledQuantity,
        decimal averagePrice,
        DateTimeOffset executedAt,
        BrokerProvider? provider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        // 相関する承認 Intent が無ければ記録しない（銘柄・方向を補完できないため）。
        if (db.ApprovedOrders.Find(decisionId) is null)
            return false;

        // #270, IADR-0113: 単調 upsert。ブローカの約定数量は**累積値**であり差分ではないため、1 注文 = 1 行に
        // 最新の累積を保持する。累積が増えたときだけ更新し、それ以外（同数・少ない数量の後追い）は無視する。
        // これにより (1) 部分約定 → 全量約定の進捗が台帳に届き、(2) 再送・巡回重複・順序前後でも二重計上せず、
        // (3) 数量が巻き戻ることもない。差分行として追記すると OrderId 一意の冪等キー（IADR-0018）が壊れる。
        var existing = db.TradeFills.Find(orderId);
        if (existing is not null)
        {
            if (filledQuantity <= existing.FilledQuantity)
                return true;

            existing.FilledQuantity = filledQuantity;
            existing.AveragePrice = averagePrice;
            existing.ExecutedAt = executedAt;
            // #569, IADR-0271: 発注先は**分かったときだけ上書きする**。続報が発注先を運ばない
            // （旧版・不明）場合に既知の値を null へ戻さない。
            existing.Provider = provider ?? existing.Provider;
            db.SaveChanges();
            return true;
        }

        db.TradeFills.Add(new TradeFillRow
        {
            OrderId = orderId,
            DecisionId = decisionId,
            FilledQuantity = filledQuantity,
            AveragePrice = averagePrice,
            ExecutedAt = executedAt,
            Provider = provider,
        });
        db.SaveChanges();
        return true;
    }

    // FR-10, UC-06, #848, IADR-0117: 承認が終端になったことを記録する（InMemoryPortfolioLedgerStore と同一の意味論）。
    public void MarkTerminal(Guid decisionId, OrderStatus terminalStatus, DateTimeOffset terminalAt)
    {
        // 終端を捏造しない（Accepted / PartiallyFilled は「まだ動く」）。
        // #848 改定 2: 門は AbandonsUnfilledRemainder（取消・失効・拒否）であって IsTerminal ではない。
        // **全量約定（Filled）は書かない**——集計が自然に 0 にするので得が無く、約定の記録より先に
        // commit されると建玉が丸ごと空いて見える区間を作る（同じ株数を二度売れる）。
        if (!OrderStatusLifecycle.AbandonsUnfilledRemainder(terminalStatus))
            return;

        // 相関する承認が無ければ書かない（AppendFill と同じ。知らない注文の終端は台帳の語彙に無い）。
        if (db.ApprovedOrders.Find(decisionId) is not { } approval)
            return;

        // 単調・冪等: 最初の終端が真。後着の終端で時刻も状態も動かさない。
        if (approval.TerminalAt is not null)
            return;

        approval.TerminalAt = terminalAt;
        approval.TerminalStatus = terminalStatus;
        db.SaveChanges();
    }

    // #292, IADR-0117: 処理中の決済数量（InMemoryPortfolioLedgerStore と同一の意味論）。
    public int GetInFlightCloseQuantity(string symbol, Market market, DateTimeOffset approvedAtOrAfter)
    {
        var approvals = db.ApprovedOrders
            .Where(a => a.Symbol == symbol
                     && a.Market == market
                     && a.PositionEffect == PositionEffect.Close
                     && a.ApprovedAt >= approvedAtOrAfter
                     // #848: 終端になったと**確認できた**承認は数えない（残りは二度と約定しない）。
                     // null＝未確認は従来どおり処理中として数える（fail-safe。除外し過ぎるとショート化する）。
                     && a.TerminalAt == null)
            .Select(a => new { a.DecisionId, a.Quantity })
            .ToList();

        if (approvals.Count == 0)
            return 0;

        // DecisionId ごとの約定累計。1 承認に複数の注文行が対応し得る形（リコンサイル経路）でも取りこぼさない。
        var decisionIds = approvals.Select(a => a.DecisionId).ToList();
        var filledByDecision = db.TradeFills
            .Where(f => decisionIds.Contains(f.DecisionId))
            .GroupBy(f => f.DecisionId)
            .Select(g => new { DecisionId = g.Key, Filled = g.Sum(x => x.FilledQuantity) })
            .ToDictionary(x => x.DecisionId, x => x.Filled);

        // 未約定 = 承認数量 − 約定累計。約定が承認を超えた場合（部分列挙・訂正）も負に振れさせない。
        return approvals.Sum(a => Math.Max(0, a.Quantity - filledByDecision.GetValueOrDefault(a.DecisionId)));
    }

    public IReadOnlyList<LedgerFill> GetFills()
    {
        // 約定 × 承認 Intent を DecisionId で結合し、銘柄・方向・建玉効果を補完して射影入力を返す。
        var query =
            from f in db.TradeFills
            join a in db.ApprovedOrders on f.DecisionId equals a.DecisionId
            // IADR-0107: 列追加前の既存行（FxRateToBase が null）はレート 1＝基準通貨建てとして扱う（当時の暗黙の前提）。
            select new LedgerFill(
                a.Symbol, a.Market, a.Side, a.PositionEffect,
                f.FilledQuantity, f.AveragePrice, f.ExecutedAt, a.StopLossPrice, a.FxRateToBase ?? 1m,
                // #563, IADR-0269: 判断記録（監査台帳の TradeDecisionMade）と突き合わせる相関キー。
                f.DecisionId,
                // #569, IADR-0271: **実際に発注したアダプタの発注先**（列追加前の行は null＝不明）。
                // 承認 Intent の Mode（a.Mode）へフォールバックしない——段が食い違う。
                f.Provider,
                // #611, IADR-0286 決定1: 認識時レート（1 USD あたりの円）。**列追加前の行・未解決の行は null のまま**
                // （FxRateToBase の `?? 1m` とは違い、既定へ倒さない——1 円/ドルは事実ではなく、推定でもない誤りである）。
                a.FxRateBaseToDisplay,
                // #849, #870, IADR-0350, IADR-0360 決定 1: 約定行の由来はシステムである
                // （式ツリーは省略可能引数を許さないため明示する）。
                TradeOrigin.System);

        var fills = query.ToList();

        // FR-10, FR-11, #849, IADR-0350 決定 2: 利用者が承認した乖離の取り込み行を合流させる。
        // **約定ではない**ため由来を ManualAdoption にし、射影が数量だけで畳めるようにする（Price は参考の取得単価）。
        // 承認行を持たないため StopLossPrice・DecisionId・Provider・認識時レートは既定（無し）のままにする。
        fills.AddRange(db.PositionDriftAdoptions.AsNoTracking().AsEnumerable().Select(ToLedgerFill));
        return fills;
    }

    // #849, IADR-0350 決定 2: 追記専用。冪等キーの一意制約で並行の二重投入を 1 件に絞る。
    public bool AppendDriftAdoption(LedgerDriftAdoption adoption)
    {
        ArgumentNullException.ThrowIfNull(adoption);

        var key = adoption.IdempotencyKey;
        if (db.PositionDriftAdoptions.AsNoTracking().Any(r => r.IdempotencyKey == key))
            return false;

        var row = new PositionDriftAdoptionRow
        {
            Id = adoption.Id,
            IdempotencyKey = key,
            Symbol = adoption.Symbol,
            Market = adoption.Market,
            Side = adoption.Side,
            Quantity = adoption.Quantity,
            CostBasisPrice = adoption.CostBasisPrice,
            FxRateToBase = adoption.FxRateToBase,
            LedgerQuantityBefore = adoption.LedgerQuantityBefore,
            BrokerQuantity = adoption.BrokerQuantity,
            ObservedAtUtc = adoption.ObservedAt,
            Actor = adoption.Actor,
            Reason = adoption.Reason,
            AdoptedAtUtc = adoption.AdoptedAt,
        };
        var entry = db.PositionDriftAdoptions.Add(row);

        try
        {
            db.SaveChanges();
            return true;
        }
        // #714, IADR-0317: 競合の判定は例外の型ではなく**行の実在**で行う。上の存在確認から保存までの間に
        // 他方が同じ冪等キーで先に書いた場合だけを「既に取り込み済み」として false に倒す。
        catch (DbUpdateException)
        {
            entry.State = EntityState.Detached;
            if (db.PositionDriftAdoptions.AsNoTracking().Any(r => r.IdempotencyKey == key))
                return false;

            // 冪等キーの行が無い＝競合ではなく本物の保存失敗である。**握り潰さない**
            // （「取り込み済み」に化けると、台帳が変わっていないのに利用者へ成功に見える応答が返る）。
            throw;
        }
    }

    // #870, IADR-0360 決定 2: 取り込みそのものを読む口（操作者・理由・取り込み前後の数量・観測時刻を落とさない）。
    public IReadOnlyList<LedgerDriftAdoption> GetDriftAdoptions() =>
    [
        .. db.PositionDriftAdoptions.AsNoTracking()
            .OrderBy(r => r.AdoptedAtUtc).ThenBy(r => r.Id)
            .AsEnumerable()
            .Select(ToAdoption),
    ];

    private static LedgerDriftAdoption ToAdoption(PositionDriftAdoptionRow r) => new(
        r.Id, r.Symbol, r.Market, r.Side, r.Quantity, r.CostBasisPrice, r.FxRateToBase,
        r.LedgerQuantityBefore, r.BrokerQuantity, r.ObservedAtUtc, r.Actor, r.Reason, r.AdoptedAtUtc);

    private static LedgerFill ToLedgerFill(PositionDriftAdoptionRow r) => new(
        r.Symbol, r.Market, r.Side, PositionEffect.Close, r.Quantity, r.CostBasisPrice, r.AdoptedAtUtc,
        StopLossPrice: null, FxRateToBase: r.FxRateToBase, Origin: TradeOrigin.ManualAdoption);
}
