using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

/// <summary>
/// FR-10, FR-12, ADR-0040 決定1・決定3, #819, IADR-0342 決定2: 損切りの実行機構の変更要求を受理してよいかの判定（純関数）。
/// <para>
/// 受理条件の単一情報源であり、エンドポイント・サービス・テストはここだけを参照する（画面とサーバで規則を二重に書かない。
/// <see cref="BrokerProviderChange"/> と同じ形）。拒否理由は最初の 1 件で打ち切らず全件列挙する。
/// </para>
/// </summary>
public static class StopLossMethodChange
{
    /// <summary>計画の 4 手法（S0〜S3）のいずれかか。<b>「どの値が既知か」の単一情報源</b>。</summary>
    public static bool IsKnown(StopLossExecutionMethod method) => method switch
    {
        StopLossExecutionMethod.BrokerStopOrder => true,
        StopLossExecutionMethod.SoftwareStop => true,
        StopLossExecutionMethod.NoProtectiveStop => true,
        StopLossExecutionMethod.AlternativeBrokerOrderType => true,
        _ => false,
    };

    /// <summary>
    /// 永続行から読んだ値を解決する（allow-list）。<b>本項目を持たない旧行（null）と未知の序数は S0</b>
    /// ——読めない行が「逆指値なしの建玉を許容」へ倒れると、利用者が選んでいない免除が黙って効く。
    /// </summary>
    public static StopLossExecutionMethod Resolve(StopLossExecutionMethod? value) =>
        value is { } method && IsKnown(method) ? method : StopLossExecutionMethod.BrokerStopOrder;

    /// <summary>
    /// ADR-0040 決定1: 発注先が実弾（moomoo REAL）の間は S0 以外を有効にできない。
    /// 実弾かどうかの判定は <see cref="BrokerProviderChange.IsLive"/> だけを通す。
    /// </summary>
    public static bool IsPermittedOn(StopLossExecutionMethod method, BrokerProvider currentProvider) =>
        method == StopLossExecutionMethod.BrokerStopOrder || !BrokerProviderChange.IsLive(currentProvider);

    public static IReadOnlyList<StopLossMethodChangeRejection> Evaluate(
        StopLossExecutionMethod target, string? reason, BrokerProvider currentProvider)
    {
        var rejections = new List<StopLossMethodChangeRejection>();

        if (!IsKnown(target))
        {
            rejections.Add(StopLossMethodChangeRejection.UnknownMethod);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            rejections.Add(StopLossMethodChangeRejection.ReasonRequired);
        }

        if (!IsPermittedOn(target, currentProvider))
        {
            rejections.Add(StopLossMethodChangeRejection.NotPermittedOnLive);
        }

        return rejections;
    }
}

/// <summary>#819, IADR-0342 決定2: 損切りの実行機構の変更を受理しない理由。</summary>
public enum StopLossMethodChangeRejection
{
    /// <summary>変更理由が空（監査のため必須）。</summary>
    ReasonRequired = 0,

    /// <summary>S0〜S3 のいずれでもない。</summary>
    UnknownMethod = 1,

    /// <summary>発注先が実弾（moomoo REAL）の間に S0 以外を選ぼうとした（ADR-0040 決定1）。</summary>
    NotPermittedOnLive = 2,
}
