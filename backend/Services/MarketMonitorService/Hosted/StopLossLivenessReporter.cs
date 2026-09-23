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
    private DateTimeOffset? _lastSummaryAt;

    private TimeSpan SummaryInterval => TimeSpan.FromSeconds(Math.Max(1, options.Value.StopLossSummaryIntervalSeconds));

    private TimeSpan MissingThreshold => TimeSpan.FromSeconds(Math.Max(1, options.Value.QuoteMissingWarningSeconds));

    /// <summary>
    /// 閉場を知らせる。欠落の起点・要約の間隔を捨て、次の開場の最初の巡回から数え直す（#904 監査 N2）。
    /// 捨てないと、週末をまたいだ最初の欠落が「金曜の最終取得からの 48 時間」として即座に警告される。
    /// </summary>
    public void OnMarketClosed()
    {
        lock (_gate)
        {
            _states.Clear();
            _lastSummaryAt = null;
        }
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
                var key = (evaluation.Symbol, evaluation.Market, evaluation.Side);
                seen.Add(key);
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
