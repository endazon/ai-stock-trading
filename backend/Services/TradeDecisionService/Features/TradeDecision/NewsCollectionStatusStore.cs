using AiStockTrading.Shared.Contracts.Events;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-01, ADR-0020 決定2, #1081, IADR-0455: 情報収集から届いた**ニュースの状態の最新値**を有効期限つきで保持する。
//
// 🔴 **欠測の明示を RAG に頼らない。** 収集側の欠測文書（KB の collection-status）は判断側の RAG が効かないと届かず、
// ニュース源が未構成のときはそもそも作られない（IADR-0220）。本ストアは InformationCollected の追加項目から
// 状態を受け取り、判断のプロンプトへ「ニュース: 取得済み／欠測／未提供（未構成）／不明」を明示させる。
//
// 🔴 **不明を既定にする。** 未受信・期限切れ・旧イベント（null）・範囲外の値はすべて「不明」（null）を返す
// ——最後に聞いた値を信じ続けない（期限切れの「取得済み」は、いまも取れている保証にならない）。
// 永続化しない（プロセス内。再起動で不明に戻り、次の巡回で復元する。InMemoryInformationDegradationStore と同じ形）。
public sealed class NewsCollectionStatusStore
{
    /// <summary>有効期間の下限。発行側が宣言しない（旧発行側）・0・負値のときもこれで数える。</summary>
    public static readonly TimeSpan MinValidity = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 有効期間の上限。<b>発行側の宣言をそのまま信じない</b>（極端に長い宣言で古い状態を信じ続けない）。
    /// リスク管理の現況観測の受け手（<c>InMemoryInformationDegradationStore.MaxValidity</c>）と同じ値。
    /// </summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromHours(2);

    private readonly Lock _gate = new();
    private NewsCollectionStatus? _status;
    private DateTimeOffset? _observedAt;
    private TimeSpan _validFor;

    /// <summary>
    /// 1 巡回ぶんの観測を記録する。<b>status が null（旧発行側）や範囲外の値でも記録する</b>
    /// ——それが最新の観測であり、前の値を残すと「いま不明」が「前回の値」にすり替わる。
    /// 観測時刻が記録済みより古い（再配送・順序の入れ替わり）ときは無視する。
    /// </summary>
    public void Record(NewsCollectionStatus? status, TimeSpan? validFor, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            if (_observedAt is { } last && observedAt < last)
            {
                return;
            }

            _status = status is { } s && Enum.IsDefined(s) ? s : null;
            _observedAt = observedAt;
            _validFor = Clamp(validFor ?? MinValidity);
        }
    }

    /// <summary>いま有効なニュースの状態。未受信・期限切れ・不明な値は null（＝不明）。</summary>
    public NewsCollectionStatus? Current(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_observedAt is not { } observedAt || now - observedAt > _validFor)
            {
                return null;
            }

            return _status;
        }
    }

    /// <summary>発行側が宣言した有効期間を上下限へ収める（純関数・境界テスト用）。</summary>
    public static TimeSpan Clamp(TimeSpan validFor) =>
        validFor < MinValidity ? MinValidity : validFor > MaxValidity ? MaxValidity : validFor;
}
