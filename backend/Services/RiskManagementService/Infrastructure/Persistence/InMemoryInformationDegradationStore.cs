using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-01, FR-02, FR-10, ADR-0020, #337, #564, IADR-0249, IADR-0267: 縮退状態のプロセス内保持（カテゴリ集合＋鮮度）。
//
// **永続化しない。** 縮退は「いま収集サービスが観測している事実」であり、プロセスをまたいで引き継ぐべき
// 値ではない。**再起動で観測が消えれば新規建ては止まり（フェイルクローズ）、次の巡回の現況観測で復帰する**
// （`InMemoryBrokerAccountObservationStore`・IADR-0153 決定3 と同じ形）。
//
// 🔴 **「集合が空」を「健全」と読まない。** 空は「観測して止めるものが無かった」と「まだ何も聞いていない」の
// 両方であり得る。両者を分けるのが**観測の鮮度**である —— 有効な観測が無いあいだは止める側へ倒す。
// これが #564（縮退継続中の再起動で停止が黙って解ける fail-open）の是正の中核であり、
// **復元経路（毎巡回の現況観測）だけを足しても、既定が「不明なら通す」のままでは是正にならない。**
public sealed class InMemoryInformationDegradationStore(TimeProvider timeProvider) : IInformationDegradationStore
{
    /// <summary>
    /// 観測の有効期間の下限。発行側が 0・負値を宣言しても、これ未満には縮まない
    /// （縮むと健全でも常時停止に落ち、統制が「常に赤」になって外される）。
    /// </summary>
    public static readonly TimeSpan MinValidity = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 観測の有効期間の上限。<b>発行側の宣言をそのまま信じない</b>——構成を誤って極端に長い値を
    /// 宣言されると<b>鮮度の要求が事実上消え、#564 の fail-open がそのまま戻る</b>
    /// （受け手側でクランプする作法は IADR-0150 決定2 と同じ）。
    /// </summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromHours(2);

    private readonly Lock _gate = new();
    private readonly HashSet<string> _degraded = new(StringComparer.Ordinal);
    private DateTimeOffset? _observedAt;
    private TimeSpan _validFor;

    public void MarkDegraded(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        lock (_gate)
        {
            _degraded.Add(category);
        }
    }

    public void MarkRecovered(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        lock (_gate)
        {
            _degraded.Remove(category);
        }
    }

    public void ApplyObservation(
        IReadOnlyCollection<string> blockingCategories, TimeSpan validFor, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(blockingCategories);

        lock (_gate)
        {
            // 逆行する観測（再配送・順序の入れ替わり）は無視する。古い現況で新しい状態を上書きしない。
            if (_observedAt is not null && observedAt <= _observedAt)
            {
                return;
            }

            _degraded.Clear();
            foreach (var category in blockingCategories)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(category);
                _degraded.Add(category);
            }

            _observedAt = observedAt;
            _validFor = Clamp(validFor);
        }
    }

    public bool BlocksNewEntries
    {
        get
        {
            lock (_gate)
            {
                // ① 止めるべき縮退が残っている ② まだ現況を 1 件も受け取っていない ③ 最後の現況が失効した
                // —— いずれも新規建ては止める。②③ が「不明なら止める」（#564）である。
                return _degraded.Count > 0
                    || _observedAt is not { } observedAt
                    || timeProvider.GetUtcNow() - observedAt > _validFor;
            }
        }
    }

    /// <summary>発行側が宣言した有効期間を上下限へ収める（純関数・境界テスト用）。</summary>
    public static TimeSpan Clamp(TimeSpan validFor) =>
        validFor < MinValidity ? MinValidity : validFor > MaxValidity ? MaxValidity : validFor;
}
