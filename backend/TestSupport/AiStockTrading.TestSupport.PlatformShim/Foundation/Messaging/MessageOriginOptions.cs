namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>NFR, IADR-0627 決定 4・IADR-0633 決定 4: 発行元の確かめの段。</summary>
public enum MessageOriginMode
{
    /// <summary>段 1（既定）: 署名を付け、受け手は確かめて記録するだけで通す。旧い HMAC の印も（記録して）受ける。</summary>
    Report = 0,

    /// <summary>段 2: 新しい署名で確かめられないメッセージは処理しない。要る鍵が無い・旧い共有の鍵があれば起動で止まる。</summary>
    Enforce = 1,
}

/// <summary>
/// NFR, IADR-0633 決定 3: 構成 <c>MessageOrigin</c>。鍵は Secret から env で渡す。
/// <list type="bullet">
/// <item><c>MessageOrigin__SigningKey</c>: 自分の秘密の鍵（PKCS#8 DER の base64・P-256）。発行元のサービスだけが持つ。</item>
/// <item><c>MessageOrigin__VerifyKeys__&lt;発行元&gt;</c>: 確かめる発行元の公開の鍵（SubjectPublicKeyInfo DER の base64・P-256）。</item>
/// <item><c>MessageOrigin__Keys__&lt;発行元&gt;</c>: 旧い共有の鍵（HMAC。IADR-0627）。段 1 の移行の間だけ。段 2 では起動で止まる。</item>
/// </list>
/// env の名前はシェルの識別子に限るので、発行元の識別子の <c>-</c> は <c>_</c> で書いてよい（読むときに <c>-</c> へ戻す）。
/// </summary>
public sealed class MessageOriginOptions
{
    public const string SectionName = "MessageOrigin";

    /// <summary>段（既定 Report）。</summary>
    public MessageOriginMode Mode { get; set; } = MessageOriginMode.Report;

    /// <summary>自分の秘密の鍵（発行元のサービスだけ）。</summary>
    public string? SigningKey { get; set; }

    /// <summary>確かめる発行元ごとの公開の鍵。</summary>
    public Dictionary<string, string?> VerifyKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>入れ替えの間だけ与える前の公開の鍵（確かめにだけ使う）。</summary>
    public Dictionary<string, string?> PreviousVerifyKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>旧い共有の鍵（HMAC。段 1 の移行の間だけ）。</summary>
    public Dictionary<string, string?> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>旧い共有の鍵の入れ替えの間だけの前の鍵。</summary>
    public Dictionary<string, string?> PreviousKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 受け入れる古さの上限（既定 24 時間）。受け手が止まっている間に溜まったメッセージを落とさない長さにする
    /// （約定・取消の取りこぼしは台帳を壊す）。再送の記録もこの長さだけ持つ。
    /// </summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>発行の時刻が受け手の時計より先にあってよい幅（既定 5 分）。</summary>
    public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromMinutes(5);
}
