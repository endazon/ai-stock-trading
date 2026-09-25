using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-07, FR-14, UC-03, ADR-0003, #1016, IADR-0431 決定 2: 利用者の自由文の指示から方針の改訂案を作るポート。
// 実体は platform LLM ゲートウェイ（散文ドラフトと同じ輸送・purpose・費用計上）。
//
// 🔴 **失敗は「案なし」で返し、散文ドラフトのようなプレースホルダへは倒さない。** 散文は報告書の一節であり定型文で
// 埋めてよいが、方針は確定すると取引に効く。「AI が作れなかった」を定型の方針文に化けさせると、利用者が
// それを案と読んで確定し得る（原則 A: 案が無いことと、案があることを混同しない）。
public interface IReportPolicyReviser
{
    Task<PolicyRevisionOutcome> ReviseAsync(PolicyRevisionContext context, CancellationToken cancellationToken = default);
}

// 改訂の文脈。Instruction は利用者の自由文（検証済み・制御文字を落とした原文）。CurrentPolicy は土台の方針
// （空＝土台なし）。ParentPolicy は上位方針（散文ドラフトと同じ扱い。null＝未確定）。
// FR-13, ADR-0042 決定 1, #1025: CurrentUsWatchlist は現在の監視銘柄のうち米国の銘柄（入れ替え案の土台）。null＝照会できなかった。
public sealed record PolicyRevisionContext(
    ReportKind Kind,
    string PeriodKey,
    string CurrentPolicy,
    ParentPolicyReference? ParentPolicy,
    string Instruction,
    IReadOnlyList<string>? CurrentUsWatchlist = null);

// 失敗の種別。利用者へ見せる理由（Message）と対にして返す。
public enum PolicyRevisionFailure
{
    /// <summary>LLM が構成されていない（改訂は使えない）。</summary>
    Unavailable,

    /// <summary>呼び出しの失敗（非 2xx・認可の拒否・例外）。</summary>
    CallFailed,

    /// <summary>上限時間内に応答が無かった。</summary>
    TimedOut,

    /// <summary>送信拒否（機密区分による縮退）・安全性分類器の拒否・禁止モデル。</summary>
    Refused,

    /// <summary>応答はあったが、案のスキーマに合わなかった（空・JSON でない・検証違反）。</summary>
    InvalidOutput,
}

// 改訂の結果。Proposal が非 null なら案がある。null なら Failure と Message が理由。
public sealed record PolicyRevisionOutcome(
    PolicyRevisionProposal? Proposal,
    PolicyRevisionFailure? Failure,
    string Message,
    LlmModelUsage? ModelUsage = null)
{
    public bool Succeeded => Proposal is not null;

    public static PolicyRevisionOutcome Proposed(PolicyRevisionProposal proposal, LlmModelUsage? modelUsage) =>
        new(proposal, null, "案を作成しました", modelUsage);

    public static PolicyRevisionOutcome Failed(PolicyRevisionFailure failure, string message, LlmModelUsage? modelUsage = null) =>
        new(null, failure, message, modelUsage);
}
