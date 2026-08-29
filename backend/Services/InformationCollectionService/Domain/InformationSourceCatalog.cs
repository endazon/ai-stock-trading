namespace InformationCollectionService.Domain;

// FR-01, ADR-0020 決定1: 情報源の 4 区分。区分は「無いと動かないもの」と「有れば良いもの」を分けるためにある。
public enum SourceTier
{
    /// <summary>必須。構成として必ず有効化する。欠測は検知・記録・通知の対象で、欠測時の振る舞いを定義する。</summary>
    Required,

    /// <summary>推奨。既定で有効化する。欠測はログのみで、取引サイクルは通常どおり進む。</summary>
    Recommended,

    /// <summary>任意。既定では無効。必要が生じた時点で有効化する。</summary>
    Optional,

    /// <summary>検証用途。バックテスト・学習にのみ用いる。<b>ライブの取引判断の入力にしてはならない。</b></summary>
    VerificationOnly,
}

// FR-01, ADR-0020 決定3: 必須ソースが欠測したときの振る舞い。**3 種に限る。**
//
// 🔴 「必須」は「欠測したら止める」と同義ではない。止めるべきものだけを止める——区別しないと、
// 補助的な情報源の一時的な欠測で取引全体が停止する（ADR-0020 決定3 の但し書き）。
public enum MissingSourceBehavior
{
    /// <summary>サイクル中止（フェイルセーフ）。適用先は moomoo OpenAPI（発注経路そのもの）。</summary>
    AbortCycle,

    /// <summary>
    /// 限定縮退。<b>当該機能の新規建てのみ停止する。手仕舞い・損切りは継続する。</b>
    /// 適用先はニュース系の全滅と FINRA 空売りデータ（空売りの新規建てのみ）。
    /// </summary>
    LimitedDegradation,

    /// <summary>記録・通知のみ（サイクルは継続）。適用先は Finnhub の市況面・SEC EDGAR・FRED。</summary>
    RecordAndNotifyOnly,
}

// FR-01, ADR-0020: 情報源 1 件の区分定義。
//
// Category は「同じ役割を担う源のまとまり」である。ニュース系の「いずれか 1 つ以上が生きていること」は
// この単位で判定する（特定の 1 提供元を必須にしない・ADR-0020 決定2）。
public sealed record InformationSourceDefinition(
    string Name,
    string Category,
    SourceTier Tier,
    MissingSourceBehavior MissingBehavior,
    bool EnabledByDefault)
{
    /// <summary>
    /// 限定縮退のうち<b>空売りの新規建てだけ</b>を止めるもの（FINRA。ADR-0020 決定3）。
    /// 通常の限定縮退（ニュース系）は新規建て全体を止める。
    /// </summary>
    public bool LimitsShortEntriesOnly { get; init; }

    /// <summary>ライブの取引判断の入力に用いてよいか。<b>検証用途は不可</b>（ADR-0020 決定1）。</summary>
    public bool UsableForLiveDecision => Tier != SourceTier.VerificationOnly;
}

// FR-01, ADR-0004, ADR-0020, ADR-0005 決定5: 情報源の区分表。
//
// 初期値は計画 `06_technical/02_datasource-candidates.md`「情報源の区分」の割当表を写像したものである
// （**割当の正は計画表**であり、ADR-0020 決定 も「表を正とする」と定める）。
public sealed class InformationSourceCatalog
{
    /// <summary>ニュース系のカテゴリ名。ADR-0020 決定2 の「いずれか 1 つ以上」判定はこのカテゴリで行う。</summary>
    public const string NewsCategory = "news";

    /// <summary>ライブ市況・発注のカテゴリ名。</summary>
    public const string MarketLiveCategory = "market-live";

    private readonly Dictionary<string, InformationSourceDefinition> _byName;

    public InformationSourceCatalog(IEnumerable<InformationSourceDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _byName = definitions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<InformationSourceDefinition> Definitions => _byName.Values;

    public InformationSourceDefinition? Find(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name, out var d) ? d : null;

    /// <summary>
    /// ライブの取引判断の入力に用いてよいソースか。<b>カタログに無い名前は不可</b>（fail-closed）——
    /// 「知らない源だから通す」は統制にならない。
    /// </summary>
    public bool IsUsableForLiveDecision(string? name) => Find(name)?.UsableForLiveDecision == true;

    public IReadOnlyList<InformationSourceDefinition> InCategory(string category) =>
        [.. _byName.Values.Where(d => string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// ADR-0005 決定5 / ADR-0020 決定5: 必須ソースが有料化（または無料枠が実質使用不能な水準へ縮小）した場合の
    /// <b>推奨への一時降格</b>。欠測時の扱いも「記録のみ」へ切り替える。
    /// <para>
    /// 🔴 <b>必須のまま放置しない。</b> 支払いの判断が下りるまで取引が止まるのを避けるための降格であり、
    /// 有料採用そのものの判断は ADR-0005 のプロセス（利用者承認・月次費用上限・月報での費用対効果）に乗せる。
    /// </para>
    /// </summary>
    public InformationSourceCatalog DemoteToRecommended(string name)
    {
        var target = Find(name)
            ?? throw new ArgumentException($"カタログに存在しない情報源は降格できない: {name}", nameof(name));

        if (target.Tier != SourceTier.Required)
            return this;

        return new InformationSourceCatalog(_byName.Values.Select(d => d.Name == target.Name
            ? d with { Tier = SourceTier.Recommended, MissingBehavior = MissingSourceBehavior.RecordAndNotifyOnly }
            : d));
    }

    /// <summary>計画の割当表どおりの初期カタログ。</summary>
    public static InformationSourceCatalog Default { get; } = new(
    [
        // --- 必須 ---
        // 発注経路そのもの。代替が無いため欠測はサイクル中止（フェイルセーフ）。
        // 本サービスはコネクタを持たない（可用性の観測は BrokerAvailabilityObserved 経路。#337 で結線）。
        new("moomoo", MarketLiveCategory, SourceTier.Required, MissingSourceBehavior.AbortCycle, true),
        // 米国株のライブ系の冗長化。moomoo が生きていればサイクルは継続する。
        new("finnhub", MarketLiveCategory, SourceTier.Required, MissingSourceBehavior.RecordAndNotifyOnly, true),
        // ニュース系（第一）。銘柄との紐付けが提供側で済んでいる唯一の無料候補。
        new("finnhub-news", NewsCategory, SourceTier.Required, MissingSourceBehavior.LimitedDegradation, true),
        // ニュース系（代替）。**どちらか 1 つを必須にすると、その 1 つの都合で取引が止まる**（ADR-0020 §理由）。
        new("google-news", NewsCategory, SourceTier.Required, MissingSourceBehavior.LimitedDegradation, true),
        // 米国開示。24 時間以上継続した場合に通知を上げる（継続時間は復帰イベントが持つ）。
        new("sec-edgar", "disclosure-us", SourceTier.Required, MissingSourceBehavior.RecordAndNotifyOnly, true),
        // 為替・米マクロ。直近取得値を用い、報告書に取得時刻を明記する（鮮度の統制は TradeDecision 側）。
        new("fred", "macro", SourceTier.Required, MissingSourceBehavior.RecordAndNotifyOnly, true),
        // FINRA 空売りデータ（ADR-0016 決定12）。**空売りの新規建てだけ**を止める。買戻し・手仕舞いは止めない。
        new("finra-short", "supply-us", SourceTier.Required, MissingSourceBehavior.LimitedDegradation, true)
        {
            LimitsShortEntriesOnly = true,
        },

        // --- 推奨（既定で有効。欠測はログのみ） ---
        new("gdelt", "news-tone", SourceTier.Recommended, MissingSourceBehavior.RecordAndNotifyOnly, true),
        new("edinet", "disclosure-jp", SourceTier.Recommended, MissingSourceBehavior.RecordAndNotifyOnly, true),
        // 日銀 時系列統計 API のうち**為替以外**（マクロ）。為替の第一／フォールバックは TradeDecision の
        // FxRateSource が担う（IADR-0194 / IADR-0196）。同じ統制を 2 か所に持たない。
        new("boj", "macro-jp", SourceTier.Recommended, MissingSourceBehavior.RecordAndNotifyOnly, true),

        // --- 任意（既定で無効） ---
        new("tdnet-yanoshin", "disclosure-jp-timely", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly, false),
        new("jpx-supply", "supply-jp", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly, false),
        new("e-stat", "macro-jp", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly, false),
        new("sec-edgar-13f", "supply-us-long", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly, false),
        new("reddit", "sentiment", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly, false),
        new("investing-rss", "sentiment", SourceTier.Optional, MissingSourceBehavior.RecordAndNotifyOnly, false),

        // --- 検証用途（ライブ判断の入力にしてはならない） ---
        new("jquants", "verification", SourceTier.VerificationOnly, MissingSourceBehavior.RecordAndNotifyOnly, false),
        new("stooq", "verification", SourceTier.VerificationOnly, MissingSourceBehavior.RecordAndNotifyOnly, false),
    ]);
}
