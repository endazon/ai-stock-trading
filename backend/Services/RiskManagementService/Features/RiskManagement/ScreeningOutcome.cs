using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-11: スクリーニング結果。承認なら OrderApproved、拒否なら OrderRejected（理由列挙つき）を保持する。
// ホスト（Slice B）はこの結果に応じて対応するイベントをバスへ発行する。
public record ScreeningOutcome
{
    public required bool IsApproved { get; init; }

    /// <summary>承認時の発行イベント。拒否時は null。</summary>
    public OrderApproved? Approved { get; init; }

    /// <summary>拒否時の発行イベント。承認時は null。</summary>
    public OrderRejected? Rejected { get; init; }

    /// <summary>
    /// FR-20, FR-11, #387, IADR-0148 決定3: この審査 1 回ぶんの観測（段階ゲートの統制違反件数の供給元）。
    /// <para>
    /// <b>承認・拒否のどちらでも必ず伴う</b>（<c>required</c>）。拒否だけを観測すると
    /// 「違反 0 件」を主張する根拠が無くなり、未供給と区別できない。
    /// </para>
    /// </summary>
    public required OrderScreeningObservation Observation { get; init; }

    public static ScreeningOutcome Approve(OrderApproved approved, OrderScreeningObservation observation) =>
        new() { IsApproved = true, Approved = approved, Observation = observation };

    public static ScreeningOutcome Reject(OrderRejected rejected, OrderScreeningObservation observation) =>
        new() { IsApproved = false, Rejected = rejected, Observation = observation };

    /// <summary>
    /// FR-10, #832, IADR-0407: <b>承認済みの新規建ての判断の再配送</b>であり、再審査しなかった（第 3 の形）。
    /// <para>
    /// 🔴 <b>このとき <see cref="Approved"/> も <see cref="Rejected"/> も <c>null</c> である。</b> 呼び出し側は
    /// <see cref="IsApproved"/> より<b>先に</b>本プロパティを見て、何も発行せずに戻る（承認を発行し直さない・拒否へ反転させない）。
    /// 見落として <see cref="IsApproved"/> の偽側へ進むと <c>Rejected!</c> の参照で例外になる（黙って拒否を発行する形にはならない）。
    /// </para>
    /// <para>
    /// <see cref="Observation"/> は最初の審査と同じ「承認」（理由 0 件）を表すが、呼び出し側は<b>記録しない</b>
    /// （最初の審査が発行より先に記録済み・IADR-0148）。
    /// </para>
    /// </summary>
    public bool IsApprovedReplay { get; init; }

    /// <summary>FR-10, #832, IADR-0407: 承認済みの判断の再配送を再審査せずに終える結果。</summary>
    public static ScreeningOutcome ApprovedReplay(OrderScreeningObservation observation) =>
        new() { IsApproved = false, IsApprovedReplay = true, Observation = observation };
}
