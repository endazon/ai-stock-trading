extern alias RiskManagementWorker;

using System.Globalization;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using Microsoft.Extensions.Logging;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;

namespace TradeDecisionService.Features.TradeDecision.FlattenBeforeClose;

// 🔴 FR-10, FR-19, #1396, IADR-0561 決定 3・4: **閉場前の手仕舞い**（利用者裁定 2026-10-20・planning#808）。閉場の M 分前（既定 15 分・市場ごと）以降の
// 場中に、保有中の建玉を**成行で全量**手仕舞う判断（TradeDecisionMade・Close・MarketOrder）を作る。LLM は呼ばない（統制であり判断ではない）。
//
//   - 既定は**無効**。設定（リスク管理の SC-02 の保存値か構成 PreCloseFlatten:Enabled。#1407・IADR-0564 決定 1 でサイジング文脈から受け取る）か
//     方針の決まった書式の行（「閉場前の手仕舞い: 有効」）のどちらかで有効になる。M（市場ごと）も同じ設定から受け取る。
//   - 有効時の既定は**全建玉**。方針の「持ち越し可: …」行に明記した銘柄だけを残す（自由文は読まない。PolicyPreCloseLines）。
//   - 🔴 **決済の経路は既存のものを使う**（新しい発注の経路を作らない）: 判断の決済と同じく TradeDecisionMade を発行し、リスク管理の審査
//     （決済は統制で止めない＝ADR-0009）→ OrderApproved（出どころ TradeDecision）→ 発注執行の成行の口（IADR-0357）へ流れる。
//     処理中の決済が建玉を覆えば発注執行のゲート（IADR-0535）が見送るため、巡回ごとに出しても二重に決済しない。覆うと分かっていれば出さない。
//   - 方針が読めない（未確定・照会の失敗）ときは、構成で有効なら全建玉を手仕舞う（持ち越しの指定を確かめられない＝持ち越さない側へ倒す）。
//   - 閉場の時刻は市場カレンダー（共有カーネルの MarketHours ＋ 構成の臨時休場・臨時の半日）。休場日・閉場後は何もしない。
//
// ソフトウェア逆指値（S1）との関係: S1 は判断の手仕舞い（出どころ TradeDecision）を損切りの前に取り消して自分の決済を出す（IADR-0515）。
// 本決済もその対象であり、損切りが優先する（同じ建玉を二重に決済しない）。夜間は S1 が保護しない（IADR-0380）ため、本統制は持ち越しを減らす側にだけ働く。
public sealed class PreCloseFlattener(
    IMarketCalendar calendar,
    IDailyPolicyProvider policyProvider,
    IHeldSymbolsProvider heldSymbols,
    IHeldPositionProvider heldPositions,
    IInFlightCloseProvider inFlightCloses,
    IEntryBlockersProvider entryBlockers,
    IFxRateProvider fxRate,
    ICurrentPriceProvider currentPrice,
    ISizingContextProvider sizingProvider,
    IClock clock,
    ILogger<PreCloseFlattener> logger,
    PreCloseFlattenLastKnown? lastKnown = null)
{
    // 省略は単体試験の直接組み立て用（巡回ごとに新しい記憶＝直近の値を持たない）。本番の配線で singleton が渡ることは PreCloseFlattenWiringTests が固定する。
    private readonly PreCloseFlattenLastKnown _lastKnown = lastKnown ?? new PreCloseFlattenLastKnown();

    /// <summary>手仕舞いの対象の市場（日本株は無効だが〔ADR-0062〕、再有効化に備えて見る。審査が市場の無効で止める前に AnyOrder で見送る）。</summary>
    private static readonly Market[] Markets = [Market.UnitedStates, Market.Japan];

    /// <summary>手仕舞いの判断の根拠文の頭（監査・通知で本統制の決済を見分ける）。</summary>
    public const string RationalePrefix = "閉場前の手仕舞い（統制）";

    /// <summary>DecisionId の区切りの幅（分）。同じ区切りの中の重複は下流の冪等が止め、区切りが変われば出し直せる。</summary>
    public const int DecisionIdBucketMinutes = 5;

    /// <summary>今この瞬間に出すべき閉場前の手仕舞いの判断（0 件以上）。</summary>
    public async Task<IReadOnlyList<TradeDecisionMade>> FlattenAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        // 手元の値だけで決まる「閉場の 60 分前（M の上限）以降の場中か」を先に見る（その外では設定も方針も保有も照会しない）。
        var candidates = new Dictionary<Market, DateTimeOffset>();
        foreach (var market in Markets)
        {
            if (!calendar.IsOpen(market, now))
                continue;
            if (calendar.SessionClose(market, now) is { } close && PreCloseWindow.IsAtOrAfter(now, close, PreCloseFlattenSettings.MaxMinutes))
                candidates[market] = close;
        }

        if (candidates.Count == 0)
            return [];

        // #1407, IADR-0564 決定 1: 効いている有効・無効と M（リスク管理の保存値か構成の値）。
        // 🔴 PR #1408 のレビュー: 照会の失敗（安全既定）・旧応答で値が無いときは、既定へ倒さず直近に取れた値で判定する（無効・短い M へ倒すと統制が緩む）。
        var context = await sizingProvider.GetContextAsync(cancellationToken).ConfigureAwait(false);
        _lastKnown.Remember(context.PreCloseFlatten);
        var settings = context.PreCloseFlatten ?? _lastKnown.Value ?? TradingDefaults.CreatePreCloseFlatten();
        if (context.PreCloseFlatten is null)
        {
            logger.LogWarning(
                _lastKnown.Value is null
                    ? "閉場前の手仕舞い: 設定（有効・無効と M）をサイジング文脈から受け取れず、起動から一度も受け取っていないため既定（無効・15 分）で判定します。"
                    : "閉場前の手仕舞い: 設定（有効・無効と M）をサイジング文脈から受け取れないため、直近に受け取った値で判定します。");
        }

        var closes = candidates
            .Where(kv => PreCloseWindow.IsAtOrAfter(now, kv.Value, settings.MinutesFor(kv.Key)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (closes.Count == 0)
            return [];

        var policy = await GetPolicySafeAsync(cancellationToken).ConfigureAwait(false);
        var enabledByPolicy = PolicyPreCloseLines.FlattenEnabled(policy?.Summary);
        if (!settings.Enabled && !enabledByPolicy)
            return [];

        var keep = PolicyPreCloseLines.OvernightAllowed(policy?.Summary);
        var held = await heldSymbols.GetHeldSymbolsAsync(cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            logger.LogWarning("閉場前の手仕舞い: 保有銘柄を照会できないため、この巡回では手仕舞いを出せません（次の巡回で再試行）。");
            return [];
        }

        var decisions = new List<TradeDecisionMade>();
        foreach (var target in held)
        {
            if (!closes.TryGetValue(target.Market, out var close))
                continue;

            if (keep.Contains(target.Symbol))
            {
                logger.LogInformation(
                    "閉場前の手仕舞い: 方針の「持ち越し可」に明記された銘柄のため手仕舞いません: {Symbol} market={Market}", target.Symbol, target.Market);
                continue;
            }

            try
            {
                if (await FlattenOneAsync(target, close, now, policy, context, settings.MinutesFor(target.Market), cancellationToken).ConfigureAwait(false) is { } decision)
                    decisions.Add(decision);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "閉場前の手仕舞いの組み立てでエラー: {Symbol}。次の巡回で再試行します。", target.Symbol);
            }
        }

        return decisions;
    }

    private async Task<TradeDecisionMade?> FlattenOneAsync(
        WatchedSymbol target, DateTimeOffset close, DateTimeOffset now, DailyPolicy? policy, SizingContext context, int minutes,
        CancellationToken cancellationToken)
    {
        var position = await heldPositions.GetPositionAsync(target.Symbol, target.Market, cancellationToken).ConfigureAwait(false);
        if (position is null)
        {
            logger.LogWarning("閉場前の手仕舞い: 保有状況を照会できないため手仕舞いを出せません: {Symbol}（次の巡回で再試行）", target.Symbol);
            return null;
        }

        if (!position.IsHeld)
            return null;

        var closeSide = position.IsLong ? TradeSide.Sell : TradeSide.Buy;

        // 全注文が審査で必ず拒否される銘柄（市場の無効・禁止銘柄）は出さない（巡回ごとに拒否を積まない。IADR-0521 決定 4 と同じ口）。
        if ((await entryBlockers.GetAsync(target.Symbol, target.Market, cancellationToken).ConfigureAwait(false))?.AnyOrder is { Count: > 0 } any)
        {
            logger.LogWarning(
                "閉場前の手仕舞い: 全注文が審査で拒否される銘柄のため手仕舞いを出せません: {Symbol} reasons={Reasons}",
                target.Symbol, string.Join(",", any));
            return null;
        }

        // 処理中の決済が建玉を覆うなら出さない（発注執行のゲートが見送る＝出しても発注にならない）。不明なら出す（ゲートが止める）。
        if (await inFlightCloses.GetAsync(target.Symbol, target.Market, closeSide, cancellationToken).ConfigureAwait(false) is { Covered: true } coverage)
        {
            logger.LogInformation(
                "閉場前の手仕舞い: 処理中の決済が建玉を覆うため出しません: {Symbol} inFlight={InFlight} orders={Orders}",
                target.Symbol, coverage.InFlightQuantity, string.Join(",", coverage.OrderIds));
            return null;
        }

        // 換算レートが無ければ出さない（既定の 1 を載せると、監査台帳へ JPY を USD として記録する。#506 と同じ）。
        if (await fxRate.GetRateToBaseAsync(target.Market, cancellationToken).ConfigureAwait(false) is not { } rate)
        {
            logger.LogWarning("閉場前の手仕舞い: 基準通貨への換算レートが解決できないため手仕舞いを出せません: {Symbol} market={Market}", target.Symbol, target.Market);
            return null;
        }

        // 成行は価格をブローカーへ送らない。ここの価格は参照価格（台帳・監査・内蔵 paper の約定価格）であり、現在値 → 平均取得単価の順に使う
        // （現在値が読めないことで手仕舞いを止めない。0 を載せない。PositionCloseService と同じ規律）。
        var price = await GetReferencePriceAsync(target, cancellationToken).ConfigureAwait(false) ?? position.AverageEntryPrice;
        if (price is not > 0m)
        {
            logger.LogWarning("閉場前の手仕舞い: 参照価格（現在値・平均取得単価）が無いため手仕舞いを出せません: {Symbol}", target.Symbol);
            return null;
        }

        var quantity = Math.Abs(position.SignedQuantity);
        var intent = new OrderIntent(
            target.Symbol,
            target.Market,
            closeSide,
            // PR #1399 のレビュー: 決済の ProductType は判断の決済（TradeDecisionAppService）・利用者の手仕舞い（PositionCloseService）と同じ Cash。
            // 決済では種別を読まない —— 審査の種別の判定（ProductTypeResolver.IsEnabled・差金決済防止・空売り統制）は新規建て（isEntry）だけ、
            // 発注執行の種別の分岐（StopLossMethodPolicy）も新規建てだけで、moomoo へは売買の向きだけを送る（種別は送らない）。台帳は向きと数量で建玉を減らす。
            ProductType.Cash,
            context.Mode,
            quantity,
            price.Value,
            PositionEffect.Close,
            StopLossPrice: null,
            FxRateToBase: rate,
            MarketOrder: true);

        var localClose = TimeZoneInfo.ConvertTime(close, MarketHours.ZoneOf(target.Market));
        var rationale = string.Create(
            CultureInfo.InvariantCulture,
            $"{RationalePrefix}: 閉場 {localClose:HH:mm}（現地）の {minutes} 分前以降のため、保有の全量 {quantity} 株を成行で手仕舞う（方針の「持ち越し可」に無い銘柄）。");
        logger.LogWarning(
            "閉場前の手仕舞い: {Symbol} {Side} 数量={Quantity} 成行（閉場 {Close:O}）", target.Symbol, closeSide, quantity, close);

        // 🔴 PR #1399 のレビュー: DecisionId は (現地の日付, 市場, 銘柄, 方向, 5 分の区切り) から決定的に導く（ScheduledDecisionIds.ForPreCloseFlatten）。
        // 入れ替え中に新旧の Pod の常駐が同じ区切りで出しても、発注執行の DecisionId の一意予約が 2 本目を止める。
        var localNow = TimeZoneInfo.ConvertTime(now, MarketHours.ZoneOf(target.Market));
        var bucket = (localNow.Hour * 60 + localNow.Minute) / DecisionIdBucketMinutes;
        var decisionId = ScheduledDecisionIds.ForPreCloseFlatten(
            DateOnly.FromDateTime(localNow.DateTime), target.Market, target.Symbol, closeSide, bucket);

        return new TradeDecisionMade(decisionId, intent, rationale, now, Policy: policy?.Reference);
    }

    private async Task<decimal?> GetReferencePriceAsync(WatchedSymbol target, CancellationToken cancellationToken)
    {
        if (!currentPrice.IsEnabled)
            return null;

        try
        {
            var reading = await currentPrice
                .GetCurrentPriceAsync(DecisionTrigger.Scheduled(target.Symbol, target.Market), cancellationToken)
                .ConfigureAwait(false);
            return reading is { Price: > 0m } r ? r.Price : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "閉場前の手仕舞い: 現在値の取得で例外。平均取得単価を参照価格にします: {Symbol}", target.Symbol);
            return null;
        }
    }

    private async Task<DailyPolicy?> GetPolicySafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await policyProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "閉場前の手仕舞い: 方針の照会で例外。方針の指定（有効化・持ち越し可）は無いものとして扱います。");
            return null;
        }
    }
}
