using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement;

// FR-20, ADR-0016 決定14, #388, IADR-0281 決定2: **verdict の「情報源」を名乗る目印**と、その列挙。
//
// 裁定（2026-08-07）は無効化の契機に「情報源の変更（借株料の照会経路・維持率の供給）」を挙げた。
// 「経路が変わった」を機械的に判定するために、供給アダプタ自身に識別子を名乗らせ、**登録されているものを列挙**して
// フィンガープリントを作る（純関数は Domain の ShortSellReleaseSources）。

/// <summary>
/// FR-20, ADR-0016 決定14: 空売り解禁の前提となる供給元（借株照会・維持率）が自らを名乗る目印。
/// <para>
/// 🔴 **借株照会・維持率の供給アダプタ（#417 / #419）は、実装時に本インターフェースを実装して DI へ登録すること。**
/// 登録しないと情報源フィンガープリントが変わらず、**経路が変わったのに verdict が生き残る**
/// （裁定が名指しした「照会経路が変わった翌日に古い verdict で解禁できてしまう」穴がそのまま残る）。
/// </para>
/// </summary>
public interface IShortSellReleaseSource
{
    /// <summary>供給元の種別（借株照会か維持率か）。</summary>
    ShortSellReleaseSourceKind Kind { get; }

    /// <summary>
    /// 供給元の識別子。**経路が変われば変わる値**にすること（実装名＋接続先の種別など）。
    /// 料率や維持率の**値そのものを入れてはならない**——値は毎日変わるため、経路の変更ではなく
    /// 値の変動で verdict が失効し、30 日の有効期限が意味を失う。
    /// </summary>
    string SourceId { get; }
}

/// <summary>
/// FR-20, ADR-0016 決定14, IADR-0281 決定2: 登録済みの供給元を列挙し、**評価時点の情報源フィンガープリント**を返す。
/// <para>
/// **今日は登録が 1 件も無く、値は <c>borrow=none;margin=none</c> である**（借株照会・維持率の供給は未実装）。
/// これは死んだ経路ではない——供給が結線された瞬間に文字列が変わり、既存の verdict は自動で失効する。
/// </para>
/// </summary>
public sealed class ShortSellReleaseSourceInventory(IEnumerable<IShortSellReleaseSource> sources)
{
    public string CurrentFingerprint() =>
        ShortSellReleaseSources.Fingerprint(
            IdsOf(ShortSellReleaseSourceKind.BorrowLookup),
            IdsOf(ShortSellReleaseSourceKind.MaintenanceMargin));

    private IEnumerable<string> IdsOf(ShortSellReleaseSourceKind kind) =>
        sources.Where(s => s.Kind == kind).Select(s => s.SourceId);
}
