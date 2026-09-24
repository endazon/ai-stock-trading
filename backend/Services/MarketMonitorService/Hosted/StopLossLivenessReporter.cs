using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using MarketMonitorService.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketMonitorService.Hosted;

// FR-03, FR-10, UC-02, ADR-0040 決定1（S1）, #902, IADR-0365: 損切り評価の**生存**を低頻度のログで示す。
//
// 損切りラインと現在値の比較（S1 ソフトウェア逆指値の発動源）は本サービスの巡回が行うが、巡回は到達するまで何も出さず、
// 価格が取れない銘柄も黙って飛ばす。稼働中の PoC で「保有中に評価が回っているか」を 1.5 時間判定できなかった（#902）。
//
//   決定2: 保有を評価している間、Information の要約を**間隔に 1 回まで**出す（初回は即時）。保有 0 件では何も出さず、状態を捨てる。
//   決定3: 保有銘柄の価格がしきい値を**超えて**取れないとき Warning（連続中は要約間隔に 1 回まで）。回復したら Information を 1 回。
//
// 🔴 **観測のみ。到達の判定・発行・決済には一切関与しない**（価格欠落を理由に建玉を閉じない。fail-loud であって fail-close ではない）。
// 時刻は呼び出し側が注入する（IClock 由来）。巡回は単一のループから呼ばれるが、念のため状態はロックで守る。
public sealed class StopLossLivenessReporter(
    IOptions<MonitorOptions> options,
    ILogger<StopLossLivenessReporter> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Symbol, Market Market, TradeSide Side), SymbolState> _states = [];

    // #909, IADR-0380 決定3: 保護の空白を既に声に出した市場（閉場のたびに 1 回だけ出す）。
    private readonly HashSet<Market> _closedReported = [];

    // IADR-0380［2026-09-24 追記 / PR #929 監査］F3: 「閉場と読んでいる」ことを閉場期間ごとに市場ごと 1 回出した印
    // （OnMarketOpen で解く）。保有を知らなくても出す —— 再起動直後・誤って閉場と読んだ日を無音にしない。
    private readonly HashSet<Market> _closedAnnounced = [];
    private DateTimeOffset? _lastSummaryAt;

    private TimeSpan SummaryInterval => TimeSpan.FromSeconds(Math.Max(1, options.Value.StopLossSummaryIntervalSeconds));

    private TimeSpan MissingThreshold => TimeSpan.FromSeconds(Math.Max(1, options.Value.QuoteMissingWarningSeconds));

    /// <summary>
    /// 🔴 FR-03, FR-10, #909, IADR-0380 決定3: **市場が閉場したことと、そのあいだ保護が働かないことを声に出す。**
    /// <para>
    /// S1 は開場中しか保護しない（閉場中は到達を検知せず、出した成行も翌寄りまで約定しない）。
    /// **建玉は次の開場まで無保護である** —— これを黙って通り過ぎさせないのが本メソッドの唯一の仕事である。
    /// 最終観測値が既に損切りラインを越えていた保有は **Critical**（到達したまま閉場した＝翌寄りで滑る）、
    /// 越えていなければ Warning で出す。**閉場のたびに市場ごとに 1 回だけ**出す（60 秒ごとの巡回で重ねない）。
    /// </para>
    /// <para>
    /// あわせて、その市場の欠落の起点・要約の間隔を捨て、次の開場の最初の巡回から数え直す（#904 監査 N2）。
    /// 捨てないと、閉場をまたいだ最初の欠落が「引け前の最終取得からの経過」として即座に警告される。
    /// </para>
    /// <para>
    /// 何も知らない（保有を照会していない・再起動直後）ときは**保有の報告を出さない**。「保有 0 件」と
    /// 「照会していない」を混同させないためであり、次に保有を知った巡回で出し直せるよう既出にも数えない。
    /// ただし「閉場と判定している・次の開場はいつか」の Information は閉場期間ごとに 1 回出す
    /// （IADR-0380［2026-09-24 追記 / PR #929 監査］F3。無音だと誤って閉場と読んだ日が見えない）。
    /// </para>
    /// </summary>
    /// <param name="market">閉場している市場。</param>
    /// <param name="heldPositions">その市場で保有している建玉（価格は照会していないので常に <c>null</c>）。</param>
    /// <param name="now">現在時刻（IClock 由来）。</param>
    /// <param name="nextOpen">次の開場時刻。見通せなければ <c>null</c>。</param>
    public void OnMarketClosed(
        Market market, IReadOnlyList<StopLossEvaluation> heldPositions, DateTimeOffset now, DateTimeOffset? nextOpen)
    {
        ArgumentNullException.ThrowIfNull(heldPositions);

        lock (_gate)
        {
            // 最後に観測できた価格（引け際の値）を持っているのは自分だけなので、報告の前に取り出す。
            var lines = heldPositions.Count > 0
                ? heldPositions.Select(e => Unprotected(e, _states.GetValueOrDefault(Key(e)))).ToList()
                : _states.Values
                    .Where(s => s.Last is not null && s.Last.Market == market)
                    .Select(s => Unprotected(s.Last!, s))
                    .ToList();

            ForgetMarket(market);

            var until = nextOpen is { } open
                ? open.ToString("O", CultureInfo.InvariantCulture)
                : "不明（カレンダーが次の開場を見通せません）";

            if (lines.Count == 0)
            {
                // 保有は知らないので書かない（既出にも数えない）。閉場と読んでいることだけを 1 回出す（監査 F3）。
                if (_closedAnnounced.Add(market))
                {
                    logger.LogInformation(
                        "市場を閉場と判定しています（{Market}）。次の開場は {NextOpen} です。"
                            + "閉場中は損切り（S1）の到達を評価しません。",
                        market, until);
                }

                return;
            }

            _closedAnnounced.Add(market);
            if (!_closedReported.Add(market))
                return; // 同じ閉場で二度目は出さない
            var breached = lines.Count(l => l.Breached);
            var details = string.Join(" / ", lines.Select(l => l.Text));

            if (breached > 0)
            {
                logger.LogCritical(
                    "🔴 市場が閉場しました（{Market}）。ソフトウェア逆指値（S1）は**開場中しか保護しません**:"
                        + " 保有 {Count} 件は次の開場（{NextOpen}）まで**無保護**です。"
                        + "閉場中は到達を検知せず、成行を出しても翌寄りまで約定しません: {Details}"
                        + " **うち {Breached} 件は最終観測値が既に損切りラインを越えたまま閉場しました。"
                        + "翌寄りで大きく滑る可能性があります。手当てが要るかを確認してください。**",
                    market, lines.Count, until, details, breached);
                return;
            }

            logger.LogWarning(
                "🔴 市場が閉場しました（{Market}）。ソフトウェア逆指値（S1）は**開場中しか保護しません**:"
                    + " 保有 {Count} 件は次の開場（{NextOpen}）まで**無保護**です。"
                    + "閉場中は到達を検知せず、成行を出しても翌寄りまで約定しません: {Details}",
                market, lines.Count, until, details);
        }
    }

    /// <summary>
    /// IADR-0380［2026-09-24 追記 / PR #929 監査］F3: その市場を開場と読んだ巡回で呼ぶ。「閉場と判定しています」の印を解き、
    /// 次の閉場期間でまた 1 回出せるようにする（保有が無く <see cref="Observe"/> に評価が来ない市場でも解けるように分けてある）。
    /// </summary>
    public void OnMarketOpen(Market market)
    {
        lock (_gate)
            _closedAnnounced.Remove(market);
    }

    /// <summary>1 巡回の評価記録を受け取り、必要なら要約・欠落の Warning・回復を記録する。</summary>
    public void Observe(IReadOnlyList<StopLossEvaluation> evaluations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evaluations);

        lock (_gate)
        {
            if (evaluations.Count == 0)
            {
                // 保有なし: 何も出さない。次に保有が現れたら即時に要約する（決定2）。
                _states.Clear();
                _lastSummaryAt = null;
                return;
            }

            var seen = new HashSet<(string, Market, TradeSide)>();
            foreach (var evaluation in evaluations)
            {
                var key = Key(evaluation);
                seen.Add(key);

                // #909, IADR-0380 決定3: 評価できた＝その市場は開場している。保護の再開を 1 回だけ声に出し、
                // 次の閉場でまた報告できるようにする（市場ごとに数える。片方だけ閉場している巡回で重ねない）。
                if (_closedReported.Remove(evaluation.Market))
                {
                    logger.LogInformation(
                        "市場が開場し、損切り評価を再開しました（{Market}）。閉場中は保護が働いていません。",
                        evaluation.Market);
                }

                if (!_states.TryGetValue(key, out var state))
                {
                    state = new SymbolState();
                    _states[key] = state;
                }

                state.Last = evaluation;
                if (evaluation.Price is { } price)
                    OnPriced(state, evaluation, price, now);
                else
                    OnMissing(state, evaluation, now);
            }

            foreach (var gone in _states.Keys.Where(k => !seen.Contains(k)).ToList())
                _states.Remove(gone);

            if (_lastSummaryAt is null || now - _lastSummaryAt.Value >= SummaryInterval)
            {
                _lastSummaryAt = now;
                logger.LogInformation(
                    "損切り評価は稼働中: 保有 {Count} 件を評価（要約は {Interval} に 1 回）: {Details}",
                    evaluations.Count, SummaryInterval, string.Join(" / ", _states.Values.Select(Describe)));
            }
        }
    }

    private void OnPriced(SymbolState state, StopLossEvaluation evaluation, decimal price, DateTimeOffset now)
    {
        if (state.MissingWarnedAt is not null)
        {
            logger.LogInformation(
                "損切り評価の価格取得が回復: {Symbol}/{Market} 現在値={Price} ライン={StopLoss}（欠落の開始 {MissingSince:O}）",
                evaluation.Symbol, evaluation.Market, price, evaluation.StopLossPrice, state.MissingSince);
        }

        state.LastPrice = price;
        state.LastPricedAt = now;
        state.MissingSince = null;
        state.MissingWarnedAt = null;
    }

    private void OnMissing(SymbolState state, StopLossEvaluation evaluation, DateTimeOffset now)
    {
        state.MissingSince ??= state.LastPricedAt ?? now;
        var missingFor = now - state.MissingSince.Value;
        if (missingFor <= MissingThreshold)
            return;
        if (state.MissingWarnedAt is { } warnedAt && now - warnedAt < SummaryInterval)
            return;

        state.MissingWarnedAt = now;
        logger.LogWarning(
            "損切り評価で価格を取得できていません: {Symbol}/{Market} {Side} {Quantity} 株 ライン={StopLoss}"
                + " 欠落 {MissingFor}（最終取得 {LastPricedAt}・最終価格 {LastPrice}）。損切りラインへの到達を検知できません。"
                + " 建玉は自動では決済しません（市況データ源を確認してください）。",
            evaluation.Symbol, evaluation.Market, evaluation.Side, evaluation.Quantity, evaluation.StopLossPrice,
            missingFor, state.LastPricedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "なし",
            state.LastPrice?.ToString(CultureInfo.InvariantCulture) ?? "なし");
    }

    private static (string Symbol, Market Market, TradeSide Side) Key(StopLossEvaluation e) =>
        (e.Symbol, e.Market, e.Side);

    /// <summary>#909, IADR-0380 決定3: 閉場時の保護の空白 1 行分（最終観測値とラインの関係つき）。</summary>
    private static (string Text, bool Breached) Unprotected(StopLossEvaluation e, SymbolState? state)
    {
        var lastPrice = state?.LastPrice;
        var breached = lastPrice is { } price && StopLossEvaluator.IsTriggered(e.Side, e.StopLossPrice, price);
        var observed = lastPrice is { } p
            ? $"最終観測値={p.ToString(CultureInfo.InvariantCulture)}"
                + $" @ {state?.LastPricedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "不明"}"
            : "最終観測値=なし（この保有の価格を一度も取れていません）";
        var verdict = breached ? "🔴 **ラインを越えたまま閉場**" : "ライン未到達のまま閉場";
        return (
            string.Create(
                CultureInfo.InvariantCulture,
                $"{e.Symbol}/{e.Market} {e.Side} {e.Quantity}株 ライン={e.StopLossPrice} {observed} {verdict}"),
            breached);
    }

    /// <summary>その市場の観測状態だけを捨てる（他の市場は開場しているかもしれない）。</summary>
    private void ForgetMarket(Market market)
    {
        foreach (var key in _states.Keys.Where(k => k.Market == market).ToList())
            _states.Remove(key);

        // 要約の間隔は全市場で共有しているため、閉場をまたいだら数え直す（#904 監査 N2 と同じ理由）。
        if (_states.Count == 0)
            _lastSummaryAt = null;
    }

    private static string Describe(SymbolState state)
    {
        var e = state.Last!;
        var price = e.Price is { } p
            ? $"現在値={p.ToString(CultureInfo.InvariantCulture)}"
            : $"現在値=取得できず（最終 {state.LastPrice?.ToString(CultureInfo.InvariantCulture) ?? "なし"}"
                + $" @ {state.LastPricedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "なし"}）";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{e.Symbol}/{e.Market} {e.Side} {e.Quantity}株 {price} ライン={e.StopLossPrice} 評価={e.EvaluatedAt:O}");
    }

    private sealed class SymbolState
    {
        public StopLossEvaluation? Last { get; set; }

        public decimal? LastPrice { get; set; }

        public DateTimeOffset? LastPricedAt { get; set; }

        public DateTimeOffset? MissingSince { get; set; }

        public DateTimeOffset? MissingWarnedAt { get; set; }
    }
}
