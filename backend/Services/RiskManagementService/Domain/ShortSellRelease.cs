namespace RiskManagementService.Domain;

// FR-20, UC-06, ADR-0016 決定14（2026-08-07 確定・verdict の形式）, #388, IADR-0281:
// **空売り実弾解禁の verdict**（実弾解禁前の確認が「済んだ」という判定）の型と、その有効性を判定する純関数。
//
// 裁定（利用者・質問票 第13回 補問 Q14。環流 planning#222）が定めたのは 3 点である。
//   ① 記録の主体と場所 = 利用者承認・段階ゲートの承認記録と**同じ経路**（別記録にしない）
//   ② 有効期限 = **30 日**
//   ③ 無効化の契機 = **情報源の変更 / 戦略の変更 / 期限切れ**（3 つとも）
//
// ① は台帳（StageGateLedger）の側が担う（承認種別 StageTransitionKind.ShortSellReleaseVerdict）。
// ②③ を判定するのが本ファイルである。

/// <summary>
/// FR-20, ADR-0016 決定14: verdict の供給元の種別。裁定が名指しした 2 つだけを持つ。
/// </summary>
public enum ShortSellReleaseSourceKind
{
    /// <summary>借株料の照会経路（moomoo <c>TrdGetMarginRatio</c> ほか）。</summary>
    BorrowLookup = 0,

    /// <summary>維持率の供給（<c>MaintenanceMarginSnapshot</c> の供給元）。</summary>
    MaintenanceMargin = 1,
}

/// <summary>
/// FR-20, ADR-0016 決定14, IADR-0281 決定2: **情報源フィンガープリント**（純関数）。
/// <para>
/// 「情報源の変更」を機械的に判定するための識別子であり、**登録アダプタ名の列挙**から作る。
/// 経路が変わる＝別のアダプタが登録される／登録が消えることであり、意味と機構が一致する。
/// </para>
/// <para>
/// **ハッシュにしない。** 監査で「何が変わって無効になったのか」が読めることに実益があり、値は十分短い。
/// **値そのもの（料率・維持率）をハッシュにもしない**——値は毎日変わるため、「経路の変更」ではなく
/// 「値の変動」で失効し、30 日の有効期限が意味を失う。
/// </para>
/// </summary>
public static class ShortSellReleaseSources
{
    /// <summary>供給アダプタが 1 つも登録されていないことの表現。**空文字にしない**（空は「未算出」と紛れる）。</summary>
    public const string Unsupplied = "none";

    /// <summary>
    /// 借株照会・維持率供給それぞれの登録アダプタ名から、決定的なフィンガープリントを組み立てる。
    /// 正規化（trim・空除去・重複除去・序数順ソート）するため、**登録順や重複には依存しない**——
    /// DI の登録順が変わっただけで verdict が失効するのは、裁定が意図した「情報源の変更」ではない。
    /// </summary>
    public static string Fingerprint(
        IEnumerable<string>? borrowLookupIds, IEnumerable<string>? maintenanceMarginIds) =>
        $"borrow={Format(borrowLookupIds)};margin={Format(maintenanceMarginIds)}";

    private static string Format(IEnumerable<string>? ids)
    {
        var normalized = (ids ?? [])
            .Select(id => (id ?? string.Empty).Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return normalized.Count == 0 ? Unsupplied : string.Join(",", normalized);
    }
}

/// <summary>
/// FR-20, ADR-0016 決定14, IADR-0281 決定1: verdict の行が**何を確認したか**を写し取った添付
/// （段階ゲートの承認記録＝<see cref="StageTransition"/> に相乗りする）。
/// 承認者・発行時刻・承認記録 ID は承認記録そのものが持つため、ここには重複して持たない。
/// </summary>
/// <param name="SourceFingerprint">発行時点の情報源フィンガープリント（<see cref="ShortSellReleaseSources"/>）。</param>
/// <param name="StrategyId">発行時点の戦略識別子（バックテスト verdict が名乗る戦略 ID）。</param>
public sealed record ShortSellReleaseAttestation(string SourceFingerprint, string StrategyId);

/// <summary>
/// FR-20, ADR-0016 決定14: 承認記録に載った verdict 1 件（台帳から復元した読み取り用の形）。
/// </summary>
/// <param name="ApprovalSequence">承認記録 ID＝段階ゲート台帳の連番（監査で 1 件を特定できる）。</param>
/// <param name="ApprovedBy">承認者（認証済み利用者名）。</param>
/// <param name="IssuedAtUtc">発行時刻。有効期限 30 日の起点。</param>
/// <param name="SourceFingerprint">発行時点の情報源フィンガープリント。</param>
/// <param name="StrategyId">発行時点の戦略識別子。</param>
public sealed record ShortSellReleaseVerdict(
    int ApprovalSequence,
    string ApprovedBy,
    DateTimeOffset IssuedAtUtc,
    string SourceFingerprint,
    string StrategyId);

/// <summary>
/// FR-20, ADR-0016 決定14: verdict の有効性。**Valid 以外はすべて「解禁しない」**（フェイルクローズ）。
/// <para>
/// 序数は HTTP 経路（<c>GET /risk-controls/stage-gate</c>）で整数として往来するため、
/// **値を明示し、追加は末尾へ行う**（<see cref="StageGateCriterion"/> と同じ規律）。
/// </para>
/// </summary>
public enum ShortSellReleaseVerdictStatus
{
    /// <summary>有効（30 日以内・情報源も戦略も発行時と同一）。</summary>
    Valid = 0,

    /// <summary>承認記録に verdict が無い（**最重要のフェイルクローズ**）。</summary>
    Missing = 1,

    /// <summary>期限切れ（発行から 30 日超。または発行時刻が未来＝台帳の時刻が壊れている）。</summary>
    Expired = 2,

    /// <summary>情報源が変わった（借株料の照会経路・維持率の供給）。</summary>
    SourceChanged = 3,

    /// <summary>戦略が変わった（または戦略の同一性を名乗れない）。</summary>
    StrategyChanged = 4,
}

/// <summary>
/// FR-20, ADR-0016 決定14, IADR-0281 決定4: verdict の有効性を判定する純関数。
/// </summary>
public static class ShortSellReleasePolicy
{
    /// <summary>
    /// FR-20, ADR-0016 決定14: **有効期限 30 日。**
    /// <para>
    /// 裁定が 30 日とした理由は「無期限は半年前の確認で解禁できる」「Stage 3 到達は自己資金 $5,000 が条件であり、
    /// 確認から解禁まで数か月空くのが標準的なシナリオである」「30 日は決定4 の強制買戻し禁止期間と同じ長さであり、
    /// 計画 ADR 内に新しい時間単位を増やさない」である。
    /// </para>
    /// </summary>
    public static readonly TimeSpan ValidityPeriod = TimeSpan.FromDays(30);

    /// <summary>
    /// verdict の有効性を判定する。**3 つの無効化契機（期限切れ・情報源の変更・戦略の変更）はいずれか 1 つで無効**。
    /// </summary>
    /// <param name="verdict">承認記録から復元した最新の verdict。<c>null</c>＝未承認（フェイルクローズ）。</param>
    /// <param name="currentSourceFingerprint">**評価時点**の情報源フィンガープリント。</param>
    /// <param name="currentStrategyId">**評価時点**の戦略識別子。</param>
    /// <param name="now">評価時刻（UTC）。</param>
    public static ShortSellReleaseVerdictStatus Evaluate(
        ShortSellReleaseVerdict? verdict,
        string? currentSourceFingerprint,
        string? currentStrategyId,
        DateTimeOffset now)
    {
        // ① 未承認。equity を満たしていても解禁しない（裁定「verdict が無ければ解禁されない」）。
        if (verdict is null)
        {
            return ShortSellReleaseVerdictStatus.Missing;
        }

        // ② 期限切れ（30 日ちょうどは有効＝`<=`）。
        // **経過が負（発行時刻が未来）も期限切れへ倒す**——台帳の時刻が壊れている状態を「有効」と読まない。
        var elapsed = now - verdict.IssuedAtUtc;
        if (elapsed < TimeSpan.Zero || elapsed > ValidityPeriod)
        {
            return ShortSellReleaseVerdictStatus.Expired;
        }

        // ③ 情報源の変更。決定3 は「発注前に借株料を照会できない場合は空売り自体を行わない」と定めており、
        // 照会経路の変更は verdict の前提そのものを崩す（裁定が名指しした穴）。
        if (!string.Equals(verdict.SourceFingerprint, currentSourceFingerprint, StringComparison.Ordinal))
        {
            return ShortSellReleaseVerdictStatus.SourceChanged;
        }

        // ④ 戦略の変更。**どちらかが空なら不一致として扱う**——同一性を名乗れないものを「同じ」と読まない。
        if (string.IsNullOrWhiteSpace(currentStrategyId)
            || string.IsNullOrWhiteSpace(verdict.StrategyId)
            || !string.Equals(verdict.StrategyId, currentStrategyId, StringComparison.Ordinal))
        {
            return ShortSellReleaseVerdictStatus.StrategyChanged;
        }

        return ShortSellReleaseVerdictStatus.Valid;
    }

    /// <summary>verdict の失効時刻（発行 + 30 日）。表示・監査のための導出値。</summary>
    public static DateTimeOffset ExpiresAt(ShortSellReleaseVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return verdict.IssuedAtUtc + ValidityPeriod;
    }
}
