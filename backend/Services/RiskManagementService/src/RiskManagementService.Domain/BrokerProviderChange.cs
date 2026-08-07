using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

/// <summary>
/// FR-20, FR-13, SC-02, #334, IADR-0141: 発注先（Broker Provider）の変更要求を受理してよいかの判定（純関数）。
/// <para>
/// 計画（FR-20 (1)・05_screens SC-02）は実弾（moomoo <c>REAL</c>）への切替に
/// <b>チェックボックスの同意と「REAL」の文字入力の両方</b>を求め、
/// <b>「既定の『OK』ボタン 1 押しで切り替えられてはならない」</b>と明記する。
/// </para>
/// <para>
/// 判定を純関数へ集約するのは、<b>画面とサーバが別々の条件式を持つと食い違うから</b>である
/// （画面は警告を出さないのにサーバは通す、あるいはその逆）。画面・API・テストは本クラスの
/// 定数と判定だけを参照する（IADR-0141 決定3）。
/// </para>
/// </summary>
public static class BrokerProviderChange
{
    /// <summary>
    /// FR-20 (2), IADR-0141 決定2, #422: 実弾切替の確認に打ち込ませる文字列。
    /// <para>
    /// 照合は前後空白のみを除いた<b>完全一致（大文字小文字を区別する）</b>である。
    /// <b>これは計画が 2026-08-07 に確定させた規則である</b>（FR-20 追記 (2)。
    /// 「確認文字列「REAL」の照合は、前後空白のみを除いた完全一致とし大文字小文字を区別する。
    /// <c>real</c> は受理しない。明記しないと後から緩める変更が『利便性の改善』として入り込む」）。
    /// 実装が先に同じ規則へ到達しており（IADR-0141 決定2）、裁定はそれを追認したものである。
    /// <b>「利便性の改善」を理由に緩めてはならない</b>——この入力は「打つ手間」自体が統制である。
    /// </para>
    /// </summary>
    public const string LiveAcknowledgementPhrase = "REAL";

    /// <summary>実弾（実資金で執行される発注先）か。「実資金かどうか」の判定はここだけを通す。</summary>
    public static bool IsLive(BrokerProvider provider) => provider == BrokerProvider.MoomooReal;

    /// <summary>
    /// 切替先が明示的な確認操作を要するか。<b>実弾へ向かうときだけ要求する。</b>
    /// 安全な方向（内蔵 <c>paper</c> / moomoo <c>SIMULATE</c>）へ摩擦を足すと、
    /// 危険なときだけ止まるという統制の意味が薄れる（IADR-0086 の非対称と同じ）。
    /// </summary>
    public static bool RequiresLiveConfirmation(BrokerProvider target) => IsLive(target);

    /// <summary>
    /// FR-20, 05_screens SC-02 ②: 段階ゲートを飛ばす組み合わせか（例: Stage 1 のまま実弾）。
    /// <para>
    /// <b>これは拒否理由ではなく警告である。</b>計画は「運用段階との組み合わせは保存を妨げないが、
    /// 段階が想定する発注先と異なる場合は警告を表示する」と定める（IADR-0141 決定4）。
    /// </para>
    /// </summary>
    public static bool SkipsStageGate(BrokerProvider target, StageSettings stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return IsLive(target) && !IsLive(stage.Mode);
    }

    /// <summary>
    /// 変更要求の判定。拒否理由は最初の 1 件で打ち切らず全件列挙する（FR-11 監査・RiskEvaluator と同じ流儀）。
    /// </summary>
    /// <param name="request">変更要求（切替先・理由・確認操作）。</param>
    /// <param name="stage">現在の段階設定（②の警告判定に用いる）。</param>
    public static BrokerProviderChangeAssessment Evaluate(
        BrokerProviderChangeRequest request,
        StageSettings stage)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stage);

        var rejections = new List<BrokerProviderChangeRejection>();

        // 未定義の enum 値（範囲外の整数を直接投げられた場合）は受理しない。既定値 0 へ暗黙に倒すと
        // 「不正な値を送ったら内蔵 paper になった」という説明のつかない状態遷移が生まれる。
        //
        // FR-20 (3), #422, IADR-0161 決定3: 既知の 3 値の定義は BrokerProviderResolution が単一情報源である。
        // **読み取り（既に書かれた行）と書き込み（これから書く値）で扱いが違うのは意図的である**——
        // 読み取りは黙って内蔵 paper へ落とし（読めない行を実弾にしないため）、書き込みは拒否する
        // （利用者が選んだ値と保存された値が食い違う状態を作らないため）。
        if (!BrokerProviderResolution.IsKnown(request.Target))
        {
            rejections.Add(BrokerProviderChangeRejection.UnknownProvider);
        }

        // FR-13, 05_screens:「変更理由を必須（1 文字以上）とし、監査ログに記録し、版の対象に含める」。
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            rejections.Add(BrokerProviderChangeRejection.ReasonRequired);
        }

        if (RequiresLiveConfirmation(request.Target))
        {
            if (!request.AcknowledgedLiveTrading)
            {
                rejections.Add(BrokerProviderChangeRejection.LiveAcknowledgementMissing);
            }

            // 前後空白のみを除いた完全一致。null・空文字・大文字小文字違いはいずれも不一致である。
            if (!string.Equals(request.Acknowledgement?.Trim(), LiveAcknowledgementPhrase, StringComparison.Ordinal))
            {
                rejections.Add(BrokerProviderChangeRejection.LivePhraseMismatch);
            }
        }

        return new BrokerProviderChangeAssessment(
            Accepted: rejections.Count == 0,
            Rejections: rejections,
            RequiresLiveConfirmation: RequiresLiveConfirmation(request.Target),
            SkipsStageGate: SkipsStageGate(request.Target, stage));
    }
}

/// <summary>
/// FR-13, FR-20, SC-02, #334: 発注先の変更要求。
/// </summary>
/// <param name="Target">切替先の発注先。</param>
/// <param name="Reason">変更理由（1 文字以上・監査のため必須）。</param>
/// <param name="AcknowledgedLiveTrading">計画の「チェックボックスの同意」に対応する明示的な同意。</param>
/// <param name="Acknowledgement">
/// 計画の「『REAL』の文字入力」に対応する確認文字列。実弾以外への切替では用いない。
/// </param>
public sealed record BrokerProviderChangeRequest(
    BrokerProvider Target,
    string Reason,
    bool AcknowledgedLiveTrading = false,
    string? Acknowledgement = null);

/// <summary>FR-20, IADR-0141: 発注先の変更を受理しない理由。</summary>
public enum BrokerProviderChangeRejection
{
    /// <summary>変更理由が空（FR-13「1 文字以上」）。</summary>
    ReasonRequired = 0,

    /// <summary>実弾への切替で、同意（チェックボックス）が無い。</summary>
    LiveAcknowledgementMissing = 1,

    /// <summary>実弾への切替で、確認文字列（<c>REAL</c>）が一致しない。</summary>
    LivePhraseMismatch = 2,

    /// <summary>発注先が 3 値のいずれでもない。</summary>
    UnknownProvider = 3,
}

/// <summary>
/// FR-20, SC-02, #334: 発注先の変更要求の判定結果。
/// </summary>
/// <param name="Accepted">受理してよいか（拒否理由が 0 件）。</param>
/// <param name="Rejections">受理しない理由の全件。</param>
/// <param name="RequiresLiveConfirmation">明示的な確認操作を要する切替か（＝実弾へ向かうか）。</param>
/// <param name="SkipsStageGate">
/// 段階ゲートを飛ばす組み合わせか（例: Stage 1 のまま実弾）。<b>警告であり拒否理由ではない。</b>
/// </param>
public sealed record BrokerProviderChangeAssessment(
    bool Accepted,
    IReadOnlyList<BrokerProviderChangeRejection> Rejections,
    bool RequiresLiveConfirmation,
    bool SkipsStageGate);
