using RiskManagementService.Domain;

namespace RiskManagementService.Application.Ports;

// FR-20, FR-12, #386, 06_daytrading-review §4.1 条件3 / §4.3, IADR-0149: 段階ゲートの「最小取引件数 100 件」を
// 数えるための約定の観測ログ。
//
// **0 は fail-safe である。** 統制違反件数（#387・IADR-0148）と違い、取引件数の 0 は「条件未充足＝昇格しない」に
// 倒れる。よって「未供給」と「0 件」を型で区別する仕組みは設けない——同じ形を機械的に真似すると、
// 意味の無い区別が判定を複雑にし、複雑さそのものが次の欠陥を隠す。
public interface IStage1FillObservationStore
{
    /// <summary>
    /// 約定 1 件ぶんの観測を記録する。<b><c>DecisionId</c> で冪等</b>（分割約定の続報・再送で二重計上しない）。
    /// </summary>
    void Record(Stage1FillObservation observation);

    /// <summary>
    /// 現在の観測窓の取引件数。算入対象（moomoo <c>SIMULATE</c> の新規建て）だけを数える。
    /// <b>記録が無ければ 0</b>＝条件未充足＝昇格しない。
    /// </summary>
    int GetTradeCount();

    /// <summary>
    /// 観測窓を区切る（現在窓の観測を破棄する）。
    /// <para>
    /// 計画は Stage 1 の起算点を「<b>Stage 1 遷移日</b>」と定める（§4.2）。段階遷移が受理された時点で窓を区切ると、
    /// Stage 1 に居る間の窓は Stage 1 へ入った時刻から始まる＝計画の期間と一致する。
    /// 区切った直後は 0（＝昇格しない）に戻る（fail-safe）。
    /// </para>
    /// </summary>
    void ResetWindow();
}
