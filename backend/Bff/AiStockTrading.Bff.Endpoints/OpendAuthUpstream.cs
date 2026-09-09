using System.Text.Json.Serialization;

namespace AiStockTrading.Bff.Endpoints;

// SC-04, FR-09, FR-11, UC-06（代替フロー「ゲートウェイの有人認証」）, ADR-0002 前提条件1, ADR-0024 決定1・2,
// IADR-0321: OpenD 認証サイドカーとの**写像を閉じ込める 1 ファイル**。
//
// 🔴 **上流（OpenD 常駐 Pod 内のサイドカー・#722）は本 PR と並行して実装中である。** 着地時に欄名が
// 動くことが分かっているため、**調停が 1 ファイルの編集で済む形**にしてある——`OpendAuthBffEndpoints`
// は写像後の型（`OpendAuthStateView`）しか知らず、上流の生の形（`UpstreamState`）を見ない。
//
// **本ファイルの外に上流のパス・欄名・コマンド名を書かないこと。** 書いた瞬間に「1 ファイルで済む」が壊れる。
internal static class OpendAuthUpstream
{
    // ---- 構成キー ----

    /// <summary>
    /// 上流サイドカーの基底 URL（クラスタ内。例 <c>http://opend-auth:8080</c>）。
    /// 🔴 **既定は空であり、空は「未構成 → 供給が無い」を意味する**（fail-safe）。
    /// 未構成のときに 0 や「—」ではなく**供給が無い**と宣言するのが本画面の要である
    /// ——取り違えると、壊れたゲートウェイが「正常にログインできている」ように見える。
    /// </summary>
    internal const string BaseUrlKey = "OpendAuth:BaseUrl";

    /// <summary>
    /// デバイス信頼の永続化（ADR-0024 決定1 の条件 (1)）。**配備構成（PVC）から静的に決まる**ため
    /// サイドカーではなく構成から読む（計画 05_screens SC-04「表示項目の供給元」の表）。未設定は供給なし。
    /// </summary>
    internal const string DeviceTrustPersistedKey = "OpendAuth:DeviceTrustPersisted";

    /// <summary>
    /// egress IP の安定性（ADR-0024 決定1 の条件 (2)）。**配備構成（固定 NAT）から静的に決まる。**
    /// 🔴 Pod IP は無関係である。未設定は供給なし。
    /// </summary>
    internal const string EgressStableKey = "OpendAuth:EgressStable";

    // ---- 上流のパス ----

    internal const string StatePath = "/opend-auth/state";
    internal const string CaptchaPath = "/opend-auth/captcha";
    internal const string VerifyPath = "/opend-auth/verify";
    internal const string ResendPath = "/opend-auth/resend";

    // ---- 供給可否（MetricAvailability の序数）----
    //
    // RiskManagementService の `MetricAvailability` と**同じ 3 値・同じ序数**である。型を参照しないのは、
    // 本 BFF ライブラリが ASP.NET Core の FrameworkReference だけで自己完結する契約だからである
    // （platform → 可変ユニット参照は禁止・IADR-0057）。**新しい形を発明していない**——フロントは
    // 既存の `isNotSupplied` / `METRIC_NOT_SUPPLIED_TEXT` をそのまま再利用する。

    /// <summary>値がある。</summary>
    internal const int Available = 0;

    /// <summary>供給元が無い・取得できなかった。画面は「取得できていません（供給元がありません）」と明示する。</summary>
    internal const int NotSupplied = 1;

    /// <summary>概念が成立しない（いま入力を待っていない＝ログイン済みで正常）。異常ではない。</summary>
    internal const int NotApplicable = 2;

    // ---- 待機中プロンプトの種別（BFF の対外表現）----

    internal const string PromptPhone = "phone";
    internal const string PromptPic = "pic";

    // ---- 接続状態（BFF の対外表現）----

    internal const string ConnectionWaiting = "waiting";
    internal const string ConnectionIdle = "idle";
    internal const string ConnectionUnavailable = "unavailable";

    // ---- 上流の生の形（着地時に動き得る面。**ここだけ**）----

    /// <summary><c>GET {base}/opend-auth/state</c> の応答。</summary>
    internal sealed record UpstreamState(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("prompt")] string? Prompt,
        [property: JsonPropertyName("captchaAvailable")] bool CaptchaAvailable,
        [property: JsonPropertyName("lastLoginAt")] string? LastLoginAt,
        [property: JsonPropertyName("detail")] string? Detail);

    /// <summary>
    /// <c>POST {base}/opend-auth/verify</c> の要求本文。
    /// 🔴 <b>コードだけである。</b> コマンド名・種別を載せる欄が**型として存在しない** ——
    /// 利用者はもちろん、この BFF の呼び出し側コードからもコマンドを指定できない。
    /// </summary>
    internal sealed record UpstreamVerifyRequest(
        [property: JsonPropertyName("code")] string Code);

    // ---- 送ってよい OpenD コンソールコマンド（許可リスト。拒否リストではない）----
    //
    // 🔴 計画 05_screens SC-04 が名指しした 3 つだけである。**未知のコマンドが増えても既定は拒否**である。
    // 送れないもの（参考。**本 BFF に経路が無い**ので定数としても持たない）:
    //   relogin -login_pwd= / exit / close_api_conn / set_log_level
    //     … 統制の外からゲートウェイを止められてしまう
    //   show_delay_report -detail_report_path= / show_sub_info -sub_info_path=
    //     … 🔴 root 権限で任意パスへ書ける（デバイス信頼の実体や OpenD.xml を潰せる）

    /// <summary>SMS 検証コードの入力。待機中プロンプトが SMS のときだけ。</summary>
    internal const string CommandInputPhoneVerifyCode = "input_phone_verify_code";

    /// <summary>画像 CAPTCHA の入力。待機中プロンプトが画像のときだけ。</summary>
    internal const string CommandInputPicVerifyCode = "input_pic_verify_code";

    /// <summary>SMS の再送要求。レート制限（暫定 60 秒に 1 回・実測待ち）は上流が課す。</summary>
    internal const string CommandRequestPhoneVerifyCode = "req_phone_verify_code";

    /// <summary>
    /// **待機中のプロンプトから送信コマンドを決める。** これがサーバ側でコマンドを決めるということである。
    /// 待機中でない・未知のプロンプトなら <c>null</c>（＝送らない・409）。
    /// </summary>
    internal static string? CommandForPrompt(string? prompt) => prompt switch
    {
        PromptPhone => CommandInputPhoneVerifyCode,
        PromptPic => CommandInputPicVerifyCode,
        _ => null,
    };

    // ---- 写像 ----

    /// <summary>
    /// 上流の状態を画面向けの宣言へ写す。
    /// <para>
    /// 🔴 <b>3 状態を取り違えない。</b> 「いま入力を待っていない」（<see cref="NotApplicable"/>）と
    /// 「状態を取得できていない」（<see cref="NotSupplied"/>）は別である。取り違えると、
    /// <b>ゲートウェイへ到達できていないのに「正常にログインできている」ように見える。</b>
    /// </para>
    /// <para>
    /// 判らないものはすべて <see cref="NotSupplied"/> へ倒す（未知の <c>status</c>、<c>waiting</c> なのに
    /// プロンプト種別が読めない場合を含む）。**安全側は「取れていない」であって「対象なし」ではない。**
    /// </para>
    /// </summary>
    internal static OpendAuthStateView ToView(UpstreamState? upstream, bool? deviceTrust, bool? egressStable)
    {
        if (upstream is null)
        {
            return Unsupplied("ゲートウェイの状態を取得できませんでした。", deviceTrust, egressStable);
        }

        var prompt = upstream.Prompt is PromptPhone or PromptPic ? upstream.Prompt : null;

        // status と prompt の整合が取れているときだけ「値がある」と宣言する。
        var (availability, connection) = upstream.Status switch
        {
            // 待機中だがプロンプト種別が読めない＝何を入力すべきか判らない。**対象なしではない。**
            "waiting" when prompt is null => (NotSupplied, ConnectionUnavailable),
            "waiting" => (Available, ConnectionWaiting),
            // ログイン済みで入力待ちではない（概念は成立するが該当が無い）。
            "idle" => (NotApplicable, ConnectionIdle),
            // 上流が「取れていない」と言っている／未知の値。
            _ => (NotSupplied, ConnectionUnavailable),
        };

        // 待機中でないなら、プロンプト種別は載せない（残っていると画面が入力欄を開けてしまう）。
        var effectivePrompt = availability == Available ? prompt : null;

        return new OpendAuthStateView(
            PromptAvailability: availability,
            Prompt: effectivePrompt,
            Connection: connection,
            // 供給が無いときに CAPTCHA が「ある」と言わない（画像取得の導線を開けない）。
            CaptchaAvailable: availability != NotSupplied && upstream.CaptchaAvailable,
            // 🔴 null を「まだ一度もログインしていない（対象なし）」と読み替えない。上流の契約は
            // 両者を区別しないため、**安全側の「取得できていません」へ倒す**。
            LastLoginAtAvailability: string.IsNullOrWhiteSpace(upstream.LastLoginAt) ? NotSupplied : Available,
            LastLoginAt: string.IsNullOrWhiteSpace(upstream.LastLoginAt) ? null : upstream.LastLoginAt,
            DeviceTrustAvailability: deviceTrust is null ? NotSupplied : Available,
            DeviceTrustPersisted: deviceTrust,
            EgressStabilityAvailability: egressStable is null ? NotSupplied : Available,
            EgressStable: egressStable,
            Detail: upstream.Detail);
    }

    /// <summary>
    /// 供給が無いときの宣言（未構成・上流不達・応答が読めない）。
    /// **200 で「正常」に見せない**ためにこの形を返す（画面は宣言に従って警告を出す）。
    /// </summary>
    internal static OpendAuthStateView Unsupplied(string detail, bool? deviceTrust, bool? egressStable) =>
        new(
            PromptAvailability: NotSupplied,
            Prompt: null,
            Connection: ConnectionUnavailable,
            CaptchaAvailable: false,
            LastLoginAtAvailability: NotSupplied,
            LastLoginAt: null,
            // デバイス信頼・egress は**配備構成**由来であり、上流の到達性とは独立に供給され得る。
            DeviceTrustAvailability: deviceTrust is null ? NotSupplied : Available,
            DeviceTrustPersisted: deviceTrust,
            EgressStabilityAvailability: egressStable is null ? NotSupplied : Available,
            EgressStable: egressStable,
            Detail: detail);
}

/// <summary>
/// SC-04: 画面が読むゲートウェイ状態の宣言。
/// <para>
/// **供給可否はサーバが宣言し、画面は従う**（05_screens「供給が無い値の表示規約」）。
/// クライアントが値の有無から供給可否を推測する形にすると、正当な値と未供給が区別できなくなる。
/// </para>
/// </summary>
/// <param name="PromptAvailability">待機中プロンプトの供給可否（0=値あり / 1=供給が無い / 2=対象なし）。</param>
/// <param name="Prompt"><c>phone</c> / <c>pic</c>。待機中でなければ <c>null</c>。</param>
/// <param name="Connection"><c>waiting</c> / <c>idle</c> / <c>unavailable</c>。</param>
/// <param name="CaptchaAvailable">画像 CAPTCHA を取得できるか。</param>
/// <param name="LastLoginAtAvailability">最終ログイン成功日時の供給可否。</param>
/// <param name="LastLoginAt">最終ログイン成功日時（ISO 8601）。</param>
/// <param name="DeviceTrustAvailability">デバイス信頼の永続化の供給可否。</param>
/// <param name="DeviceTrustPersisted">デバイス信頼が永続化されているか（ADR-0024 決定1 の条件 (1)）。</param>
/// <param name="EgressStabilityAvailability">egress IP 安定性の供給可否。</param>
/// <param name="EgressStable">egress IP が安定しているか（ADR-0024 決定1 の条件 (2)）。</param>
/// <param name="Detail">補足（利用者向けの説明。**検証コードは決して載せない**）。</param>
public sealed record OpendAuthStateView(
    int PromptAvailability,
    string? Prompt,
    string Connection,
    bool CaptchaAvailable,
    int LastLoginAtAvailability,
    string? LastLoginAt,
    int DeviceTrustAvailability,
    bool? DeviceTrustPersisted,
    int EgressStabilityAvailability,
    bool? EgressStable,
    string? Detail);
