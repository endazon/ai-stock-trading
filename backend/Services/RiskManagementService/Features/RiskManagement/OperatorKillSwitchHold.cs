namespace RiskManagementService.Features.RiskManagement;

/// <summary>
/// NFR, FR-10, UC-06, ADR-0009, #1530, IADR-0613: Discord を経ない停止の「保持」（operator hold）の構成。
/// <para>
/// ノードへ SSH で入ったオーナーが <c>scripts/ast-trading-stop.sh</c> で入れる（名前空間の Secret <c>ast-operator-hold</c> を作り、
/// 本サービスの Pod を作り直す。chart は optional の secretKeyRef で読むので、Secret が無ければ env も無く既定の振る舞いのまま）。
/// </para>
/// <list type="bullet">
///   <item>入っていれば、起動時に既存の kill switch を起動する（<see cref="KillSwitchService.ApplyOperatorHold"/>。変更履歴に残る）。</item>
///   <item>入っている間は解除を拒む（<see cref="KillSwitchHeldByOperatorException"/> → 409）。<b>止める窓口を Discord と独立させる</b>ため。</item>
///   <item>外しても kill switch は起動したまま残る（DB の状態）。再開は従来どおり Discord・画面の解除（確認ボタン＋確認フレーズ）で行う。</item>
/// </list>
/// </summary>
public sealed class OperatorKillSwitchHoldOptions
{
    public const string SectionName = "RiskManagement:OperatorKillSwitchHold";

    /// <summary>保持で起動したときの actor（変更履歴・状態に残る）。人の利用者名と取り違えない固定の値。</summary>
    public const string Actor = "operator-hold";

    /// <summary>理由が空のときの理由（止める側を理由の欠けで失敗させない）。</summary>
    public const string DefaultReason = "SSH の経路から停止（理由の指定なし）";

    public bool Enabled { get; set; }

    public string? Reason { get; set; }
}

/// <summary>NFR, #1530, IADR-0613: 起動時の保持の適用の結果（ログの印に使う）。</summary>
public enum OperatorHoldOutcome
{
    /// <summary>保持は入っていない（何もしない）。</summary>
    Inactive,

    /// <summary>保持により kill switch を起動した。</summary>
    Engaged,

    /// <summary>保持は入っているが、kill switch は既に起動していた（先の起動の主体・理由を保つ）。</summary>
    AlreadyEngaged,
}

/// <summary>
/// NFR, #1530, IADR-0613: 保持の間の解除の拒否。REST・gRPC の解除は同じ処理関数を通るので、どちらも 409（gRPC は ABORTED）になる。
/// </summary>
public sealed class KillSwitchHeldByOperatorException()
    : InvalidOperationException("kill switch は SSH の経路で保持されているため解除できません。保持を外してから解除してください。");
