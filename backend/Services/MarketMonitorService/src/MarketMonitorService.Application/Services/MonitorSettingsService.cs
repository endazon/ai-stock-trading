using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.MarketMonitor.Application.State;
using AiStockTrading.MarketMonitor.Domain;

namespace AiStockTrading.MarketMonitor.Application.Services;

// FR-03, FR-11, FR-13, UC-06, SC-01 §2, #340, IADR-0155: 収集パラメータ（変動閾値・クールダウン）の
// 閲覧・変更。変更は利用者のみ（ホスト層 OwnerOnly）・**理由必須**・**値域検証**・**変更履歴に記録**する。
//
// 既存の `PUT /monitor/settings` は**監視設定の全置換**である。画面から使うと、変動閾値だけを変えたい
// 場面でも監視銘柄とクールダウンを送り直す必要があり、**送り漏らした瞬間に監視銘柄が消える**
// （`GuardUpdateRequest.ConfiguredAccountType` が同じ理由で nullable にされたのと同型の危険）。
// したがって画面が使う経路は**項目単位の部分更新**とし、他の項目を巻き込まない。
//
// 変更履歴は MonitorWatchlistService と同じ `IMonitorSettingsChangeLog` へ追記する（監視設定の履歴は
// 1 本の台帳であり、監視銘柄と収集パラメータで別の台帳に分けない）。
public sealed class MonitorSettingsService(
    IMonitoredSymbolStore store,
    IMonitorSettingsChangeLog changeLog,
    IClock clock)
{
    public MarketMonitorSettings GetSettings() => store.GetSettings();

    public IReadOnlyList<MonitorSettingsChangeEntry> GetHistory() => changeLog.GetHistory();

    /// <summary>
    /// FR-03, FR-13, SC-01 §2: 変動閾値だけを更新する（クールダウン・監視銘柄は保持する）。
    /// 値域外・理由の欠如は <see cref="ArgumentException"/>（ホスト層で 400 に写像）。
    /// </summary>
    public MarketMonitorSettings UpdateMovementThreshold(decimal ratio, string actor, string reason)
    {
        RequireActorAndReason(actor, reason);
        if (MonitorSettingsBounds.ValidateMovementThresholdRatio(ratio) is { } message)
        {
            throw new ArgumentException(message, nameof(ratio));
        }

        var current = store.GetSettings();
        var updated = current with { MovementThresholdRatio = ratio };
        Save(
            updated,
            MonitorSettingsChangeType.MovementThresholdChanged,
            actor,
            reason,
            before: Render(current.MovementThresholdRatio),
            after: Render(ratio));
        return store.GetSettings();
    }

    /// <summary>
    /// FR-03, FR-13, SC-01 §2: クールダウンだけを更新する（変動閾値・監視銘柄は保持する）。
    /// </summary>
    public MarketMonitorSettings UpdateCooldown(TimeSpan cooldown, string actor, string reason)
    {
        RequireActorAndReason(actor, reason);
        if (MonitorSettingsBounds.ValidateCooldown(cooldown) is { } message)
        {
            throw new ArgumentException(message, nameof(cooldown));
        }

        var current = store.GetSettings();
        var updated = current with { Cooldown = cooldown };
        Save(
            updated,
            MonitorSettingsChangeType.CooldownChanged,
            actor,
            reason,
            before: current.Cooldown.ToString(),
            after: cooldown.ToString());
        return store.GetSettings();
    }

    /// <summary>
    /// FR-03, FR-11, FR-13, SC-02, #423, IADR-0164 決定3: 監視設定を**全置換**する
    /// （<c>PUT /monitor/settings</c> の実体）。
    /// <para>
    /// 部分更新（<see cref="UpdateMovementThreshold"/> / <see cref="UpdateCooldown"/>）と<b>同じ規律</b>
    /// —— 理由必須・<see cref="MonitorSettingsBounds"/> による値域検証・変更履歴への記録 —— を課す。
    /// </para>
    /// <para>
    /// <b>画面から使わない経路だからこそ塞ぐ。</b> 本メソッドの導入前、全置換 PUT は理由も履歴も持たず、
    /// 値域も「比率が正・クールダウンが非負」だけを見ていた（上限が無かった）。その状態では
    /// 「画面は 0.6 を弾くが API を直接叩けば保存できる」——**統制を画面の親切心に依存させることになる**
    /// （IADR-0141 決定1 / IADR-0155 残余リスク4）。
    /// </para>
    /// <para>
    /// 履歴は<b>実際に変わった項目だけ</b>を記録する（変わっていない項目の行を積むと、監査で
    /// 「いつ変わったのか」が読めなくなる）。
    /// </para>
    /// </summary>
    public MarketMonitorSettings Replace(MarketMonitorSettings settings, string actor, string reason)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RequireActorAndReason(actor, reason);
        if (MonitorSettingsBounds.ValidateMovementThresholdRatio(settings.MovementThresholdRatio) is { } thresholdError)
        {
            throw new ArgumentException(thresholdError, nameof(settings));
        }

        if (MonitorSettingsBounds.ValidateCooldown(settings.Cooldown) is { } cooldownError)
        {
            throw new ArgumentException(cooldownError, nameof(settings));
        }

        var current = store.GetSettings();
        // 永続化を先に確定させる（fail-safe）。競合はここで送出され、履歴は 1 件も残らない。
        store.Save(settings);

        var now = clock.UtcNow;
        if (current.MovementThresholdRatio != settings.MovementThresholdRatio)
        {
            changeLog.Record(new MonitorSettingsChangeEntry(
                actor, MonitorSettingsChangeType.MovementThresholdChanged, reason, now,
                Render(current.MovementThresholdRatio), Render(settings.MovementThresholdRatio)));
        }

        if (current.Cooldown != settings.Cooldown)
        {
            changeLog.Record(new MonitorSettingsChangeEntry(
                actor, MonitorSettingsChangeType.CooldownChanged, reason, now,
                current.Cooldown.ToString(), settings.Cooldown.ToString()));
        }

        RecordWatchlistDelta(current, settings, actor, reason, now);
        return store.GetSettings();
    }

    // 全置換で監視銘柄が増減した場合の履歴。増分・減分をそれぞれ 1 件で記録する
    // （前後値は監視銘柄サービス（MonitorWatchlistService）と同じ「一覧の前後」表現に揃える）。
    private void RecordWatchlistDelta(
        MarketMonitorSettings current,
        MarketMonitorSettings updated,
        string actor,
        string reason,
        DateTimeOffset now)
    {
        var before = current.MonitoredSymbols;
        var after = updated.MonitoredSymbols;
        var added = after.Any(s => !before.Any(b => Same(b, s)));
        var removed = before.Any(s => !after.Any(a => Same(a, s)));
        if (added)
        {
            changeLog.Record(new MonitorSettingsChangeEntry(
                actor, MonitorSettingsChangeType.WatchlistSymbolAdded, reason, now,
                RenderSymbols(before), RenderSymbols(after)));
        }

        if (removed)
        {
            changeLog.Record(new MonitorSettingsChangeEntry(
                actor, MonitorSettingsChangeType.WatchlistSymbolRemoved, reason, now,
                RenderSymbols(before), RenderSymbols(after)));
        }
    }

    // (Symbol, Market) の一致判定（MonitorWatchlistService と同じ規則。大文字小文字を無視する）。
    private static bool Same(MonitoredSymbol a, MonitoredSymbol b) =>
        a.Market == b.Market && string.Equals(a.Symbol, b.Symbol, StringComparison.OrdinalIgnoreCase);

    private static string RenderSymbols(IReadOnlyCollection<MonitoredSymbol> symbols) =>
        symbols.Count == 0 ? "(なし)" : string.Join(", ", symbols.Select(s => $"{s.Symbol}@{s.Market}"));

    private void Save(
        MarketMonitorSettings updated,
        MonitorSettingsChangeType changeType,
        string actor,
        string reason,
        string before,
        string after)
    {
        // 永続化を先に確定させる（fail-safe）。Version 楽観排他競合はここで DbUpdateConcurrencyException として
        // 送出され、ホスト層で 409 に写像される（履歴は記録されない＝台帳と履歴が食い違わない）。
        store.Save(updated);
        changeLog.Record(new MonitorSettingsChangeEntry(actor, changeType, reason, clock.UtcNow, before, after));
    }

    // 履歴の前後値レンダリング。比率は画面表示（百分率）ではなく**保存された比率のまま**残す
    // （監査は保存値を追うためであり、表示単位を混ぜると後から単位を取り違える）。
    private static string Render(decimal ratio) => ratio.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void RequireActorAndReason(string actor, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }
}
