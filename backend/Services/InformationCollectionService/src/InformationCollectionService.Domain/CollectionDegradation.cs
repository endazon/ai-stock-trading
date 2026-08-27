namespace AiStockTrading.InformationCollection.Domain;

// FR-01, ADR-0020 決定2/決定3: 1 巡回におけるソース単位の取得結果。**成否だけを持つ**——
// 0 件（そのソースに新着が無い）と失敗（取れなかった）は別の事実であり、混同すると
// 「ニュースが無い日」を欠測として扱って新規建てを止めることになる。
public sealed record SourceOutcome(string Name, bool Succeeded)
{
    public static SourceOutcome Ok(string name) => new(name, true);

    public static SourceOutcome Failed(string name) => new(name, false);
}

// FR-01, FR-09, ADR-0020 決定2/決定3: 欠測の判定結果（1 巡回ぶん）。
//
// 🔴 **「手仕舞い・損切りを止める」を型として持たない。** 本型が表現できる停止は
// <see cref="BlocksNewEntries"/>（新規建て）と <see cref="BlocksShortEntries"/>（空売りの新規建て）だけであり、
// 決済側は定数 <c>true</c> である。フラグの組み合わせ次第で出口が塞がる状態を作らないためである
// （為替の鮮度切れで「入口と出口が同じゲートで塞がれていた」事故と同じ形を作らない・IADR-0197）。
public sealed record CollectionDegradation(
    bool AbortCycle,
    bool BlocksNewEntries,
    bool BlocksShortEntries,
    bool NewsOutage,
    IReadOnlyList<string> MissingRequired,
    IReadOnlyList<string> UnconfiguredRequired,
    IReadOnlyList<string> Notifications)
{
    /// <summary>縮退も中止も無い状態。</summary>
    public static CollectionDegradation None { get; } =
        new(false, false, false, false, [], [], []);

    /// <summary>
    /// 🔴 <b>手仕舞い（Close）は常に許可される。</b> ADR-0020 決定2/決定3 は限定縮退でも
    /// 「手仕舞い・損切りは止めない」と定める。<b>止められないより閉じられないほうが危険である。</b>
    /// </summary>
    public bool ClosesAllowed => true;

    /// <summary>
    /// 🔴 <b>損切りは常に許可される。</b> 実行機構はブローカー側の逆指値であり（NFR-04）、
    /// 系が止まっても効く。本サービスの縮退がこれを打ち消すことはない。
    /// </summary>
    public bool StopLossAllowed => true;

    /// <summary>何らかの記録・通知を要する状態か。</summary>
    public bool IsDegraded =>
        AbortCycle || BlocksNewEntries || BlocksShortEntries || NewsOutage || MissingRequired.Count > 0;
}

// FR-01, ADR-0020 決定2/決定3: 区分 × 欠測 → 振る舞いの判定（純関数）。
//
// 判定の順序と根拠:
//   1. 未構成の必須ソースは**欠測に数えない**。数えると、外部接続しない安全既定（IADR-0022）のままで
//      毎サイクルが中止になる。未構成は警告として別に持つ（UnconfiguredRequired）。
//   2. ニュース系は**カテゴリ単位**で見る（「いずれか 1 つ以上が生きていること」・決定2）。
//      1 つでも成功していれば欠測ではない。**試行が 1 つも無い**ときも縮退させない（未構成と同じ扱い）。
//   3. 必須ソースの欠測は、その定義が持つ振る舞い 3 種のいずれかへ落とす。
//   4. 推奨・任意の欠測は**記録のみ**（決定3 の「3 種」は必須ソースに対する規定である）。
public static class DegradationEvaluator
{
    public static CollectionDegradation Evaluate(
        InformationSourceCatalog catalog,
        IReadOnlyList<SourceOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(outcomes);

        var attempted = outcomes.ToDictionary(o => o.Name, StringComparer.OrdinalIgnoreCase);

        var abortCycle = false;
        var blocksNewEntries = false;
        var blocksShortEntries = false;
        var missingRequired = new List<string>();
        var unconfiguredRequired = new List<string>();
        var notifications = new List<string>();

        // 2. ニュース系（カテゴリ単位の「いずれか 1 つ以上」）。
        var newsSources = catalog.InCategory(InformationSourceCatalog.NewsCategory);
        var attemptedNews = newsSources.Where(n => attempted.ContainsKey(n.Name)).ToList();
        var newsOutage = attemptedNews.Count > 0 && attemptedNews.TrueForAll(n => !attempted[n.Name].Succeeded);

        if (newsOutage)
        {
            blocksNewEntries = true;
            missingRequired.AddRange(attemptedNews.Select(n => n.Name));
            notifications.Add(
                "ニュース系が全滅した（Finnhub 企業ニュース・Google News RSS のいずれも取得できていない）。"
                + "単一ソース由来の急シグナルに基づく新規建てを行わない。**手仕舞い・損切りは止めない。**");
        }

        // 3. ニュース系以外の必須ソース。
        foreach (var definition in catalog.Definitions.Where(d => d.Tier == SourceTier.Required))
        {
            if (string.Equals(definition.Category, InformationSourceCatalog.NewsCategory, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!attempted.TryGetValue(definition.Name, out var outcome))
            {
                // 1. 未構成（この巡回で試行されていない）。記録はするが止めない。
                unconfiguredRequired.Add(definition.Name);
                continue;
            }

            if (outcome.Succeeded)
                continue;

            missingRequired.Add(definition.Name);

            switch (definition.MissingBehavior)
            {
                case MissingSourceBehavior.AbortCycle:
                    abortCycle = true;
                    notifications.Add($"必須情報源 {definition.Name} が欠測したため当該サイクルを中止する（フェイルセーフ）。");
                    break;

                case MissingSourceBehavior.LimitedDegradation when definition.LimitsShortEntriesOnly:
                    blocksShortEntries = true;
                    notifications.Add(
                        $"必須情報源 {definition.Name} が欠測したため空売りの新規建てを停止する。"
                        + "**買戻し・手仕舞いは止めない。**");
                    break;

                case MissingSourceBehavior.LimitedDegradation:
                    blocksNewEntries = true;
                    notifications.Add(
                        $"必須情報源 {definition.Name} が欠測したため新規建てを停止する。**手仕舞い・損切りは止めない。**");
                    break;

                default:
                    notifications.Add($"必須情報源 {definition.Name} が欠測した（記録・通知のみ。サイクルは継続する）。");
                    break;
            }
        }

        return new CollectionDegradation(
            abortCycle,
            blocksNewEntries,
            blocksShortEntries,
            newsOutage,
            missingRequired,
            unconfiguredRequired,
            notifications);
    }
}
