namespace NotificationService.Features.Notifications;

// FR-20, FR-14, UC-06, ADR-0008, IADR-0070/0081: 段階ゲート（#20）の OwnerOnly エンドポイントの抽象。
// 通知サービスは段階ゲートの状態を持たず、既存の Risk エンドポイント（GET /risk-controls/stage-gate,
// POST /risk-controls/stage-gate/transition, POST /risk-controls/stage-gate/withdrawal/evaluate）を呼ぶだけ
// （権威は Risk 側）。kill switch / pause（IKillSwitchController / IPauseController）と同型。
//
// いずれも OwnerOnly（trading-owner）のため、実装は Bot 専用の owner マップ機密クライアントの
// client_credentials トークンを付与する（trading-service トークンでは 403）。
//
// 返り値はすべて**整形済み表示テキスト**（Message）を持つ。数値 enum（段階・拒否基準）の射影と整形は実装
// アダプタ（Worker）に隔離し、Application 層は Risk の JSON 表現に結合しない（IADR-0081 決定1・pause と同型）。
public interface IStageGateController
{
    // 段階ゲートの現況（現段階・モード・資金上限・昇格可否＋未充足基準・撤退評価・直近履歴）を取得する（表示専用）。
    Task<StageGateStatusResult> GetStatusAsync(CancellationToken cancellationToken = default);

    // 段階遷移（昇格・差し戻し）を要求する。承認者は Risk 側が認証済みトークンから取る（要求本文は targetStage のみ）。
    // 受理（200）と拒否（422・未充足基準／飛び級／現段階指定）を整形して返す。
    Task<StageTransitionCommandResult> RequestTransitionAsync(int targetStage, CancellationToken cancellationToken = default);

    // 撤退基準の評価（安全側＝HaltNewEntries 成立時に Risk が kill switch を自動起動）。実降格は行わず提案のみ返る。
    Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default);
}

// FR-20: 段階ゲート照会・撤退評価の結果。Message は利用者へ表示する整形済みテキスト。
// Succeeded=false は Risk 呼び出しが失敗（HTTP エラー・タイムアウト・解釈不能）したことを意味する
// （失敗を成功に見せない）。
//
// #466, IADR-0180: <see cref="Stage1Warning"/> は Message に含まれる引き下げ警告を**単独でも取り出せる**形で
// 併記したものである（出ていなければ null）。**確認ボタンを出す前**に警告だけを添えるために要る——
// 現況照会の全文を確認プロンプトへ貼ると長すぎて警告が埋もれる。
public sealed record StageGateStatusResult(bool Succeeded, string Message, string? Stage1Warning = null);

// FR-20, UC-06: 段階遷移要求の結果。Accepted は Risk が遷移を受理したか（422 拒否時は false）。
// Succeeded=false は Risk 呼び出し自体が失敗したこと（HTTP エラー等）を意味し、Accepted とは区別する。
//
// FR-20, FR-11, SC-02, #466, 06_daytrading-review §4.1 追補3（質問票 第15回 Q13-a）, IADR-0180:
// <see cref="Stage1Warning"/> は**最小取引件数が引き下げられている場合の警告文言**（整形済み・出ていなければ null）。
//
// **Message へ混ぜず別項目で返す。** アダプタは `targetStage` しか知らず現段階を持たないため、
// 昇格か差し戻しかを判定できない。裁定が名指ししたのは**昇格承認**（`/stage promote`）であり、
// 差し戻しへ同じ警告を出すと「読まれない警告」化する。よって**付加の可否はコマンド種別を知る
// `StageGateCommandHandler` が決める**（整形はアダプタ・付加判断は Application 層）。
public sealed record StageTransitionCommandResult(
    bool Succeeded, bool Accepted, string Message, string? Stage1Warning = null);
