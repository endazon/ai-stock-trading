namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>NFR, IADR-0627 決定 4: 発行元の確かめの段。</summary>
public enum MessageOriginMode
{
    /// <summary>段 1（既定）: 印を付け、受け手は確かめて記録するだけで通す。</summary>
    Report = 0,

    /// <summary>段 2: 確かめられないメッセージは処理しない。要る鍵が無ければ起動で止まる。</summary>
    Enforce = 1,
}

/// <summary>
/// NFR, IADR-0627 決定 3・4: 構成 <c>MessageOrigin</c>。鍵は Secret から env
/// <c>MessageOrigin__Keys__&lt;発行元&gt;</c>（base64・32 バイト以上）で渡す。env の名前はシェルの識別子に限るので、
/// 発行元の識別子の <c>-</c> は <c>_</c> で書いてよい（読むときに <c>-</c> へ戻す。例 <c>MessageOrigin__Keys__trade_decision</c>）。
/// </summary>
public sealed class MessageOriginOptions
{
    public const string SectionName = "MessageOrigin";

    /// <summary>段（既定 Report）。</summary>
    public MessageOriginMode Mode { get; set; } = MessageOriginMode.Report;

    /// <summary>
    /// 発行元ごとの鍵。発行元は自分の鍵で印を作り、受け手は確かめる型の発行元の鍵を持つ。
    /// </summary>
    public Dictionary<string, string?> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>入れ替えの間だけ与える前の鍵（確かめにだけ使う）。</summary>
    public Dictionary<string, string?> PreviousKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 受け入れる古さの上限（既定 24 時間）。受け手が止まっている間に溜まったメッセージを落とさない長さにする
    /// （約定・取消の取りこぼしは台帳を壊す）。再送の記録もこの長さだけ持つ。
    /// </summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>発行の時刻が受け手の時計より先にあってよい幅（既定 5 分）。</summary>
    public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromMinutes(5);
}
