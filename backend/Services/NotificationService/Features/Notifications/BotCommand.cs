namespace NotificationService.Features.Notifications;

// FR-14, UC-06, ADR-0009: 受け付けるコマンド種別。詳細設計07 のコマンド体系のうち kill switch と
// 一時停止（pause/resume）・稼働状態照会（status）を扱う。
public enum BotCommandKind
{
    // 未知・解析不能。呼び出し側は必ず拒否する（暗黙実行しない）。
    Unknown,

    // /killswitch: 全停止の起動（確認ボタン＋確認フレーズを要する）。
    KillSwitchEngage,

    // /killswitch off: 全停止の解除（確認ステップを要する）。
    KillSwitchDisengage,

    // /pause: 新規建ての一時停止（確認ボタンのみ・フレーズ不要。kill switch より軽い統制）。
    Pause,

    // /resume: 一時停止の解除（確認ボタンのみ）。pause のみ解除する（kill switch・ロックアウトは解除しない）。
    Resume,

    // /status: 稼働状態の参照（表示専用・副作用なし）。
    Status,

    // FR-19, FR-11, UC-06, #464, ADR-0028 決定2/決定3: /gfv clear。
    // GFV 違反による**停止の解除**（破壊的・確認ボタン＋確認フレーズを要する）。
    // **解除の窓口は Discord Bot に一元化される**（決定3。画面からは解除できない）。
    GoodFaithViolationClear,

    // FR-20, UC-06: /stage status。段階ゲートの現況（現段階・設定・昇格可否・撤退評価・直近履歴）の参照（表示専用）。
    StageStatus,

    // /stage promote <n>: 段階の昇格（実弾方向・破壊的）。確認ボタンを要する（Gateway が担う）。TargetStage に遷移先。
    StagePromote,

    // /stage demote <n>: 段階の差し戻し（安全方向）。手動の段階変更のため確認ボタンを要する。TargetStage に遷移先。
    StageDemote,

    // /stage withdrawal: 撤退基準の評価（安全側＝HaltNewEntries 成立時に Risk が kill switch を自動起動）。確認不要。
    StageWithdrawal,

    // FR-14, FR-07, UC-03〜05, IADR-0240: /report show <periodKey>。
    // レビュー局面（版番号）の参照（表示専用・副作用なし）。
    ReportShow,

    // /report approve <periodKey> [<version>]: 報告書の確定（破壊的＝確定した方針が取引に適用される）。
    // 版番号を伴わない要求は**確認ボタンを出す前段**であり、確定そのものは版番号つきの要求でのみ行う
    // （詳細設計07「確定要求は 対象ID＋版番号 を必須とする」）。
    ReportApprove,

    // /report request-changes <periodKey> <version>: 差し戻し（修正指示）。安全方向・可逆。
    ReportRequestChanges,
}

// FR-14: 解析済みコマンド。TargetStage は段階遷移（StagePromote/StageDemote）の遷移先（0〜3）。
// それ以外の種別では null（既定）。
//
// FR-07, UC-03〜05, IADR-0240: PeriodKey / Version は報告書レビュー系（ReportShow / ReportApprove /
// ReportRequestChanges）でのみ意味を持つ。**Version が null の ReportApprove は「確認前」**であり、
// 確定を実行してよいのは版番号が確定している要求だけである。
public sealed record BotCommand(
    BotCommandKind Kind,
    int? TargetStage = null,
    string? PeriodKey = null,
    int? Version = null)
{
    public static readonly BotCommand Unknown = new(BotCommandKind.Unknown);
}
