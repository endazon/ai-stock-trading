namespace OpendAuthGateway.Common;

/// <summary>
/// #722, IADR-0322: OpenD 認証サイドカーの構成。
/// <para>
/// 実体はすべて <see cref="RuntimeDirectory"/>（OpenD コンテナと共有する <c>emptyDir</c>）配下にあり、
/// <b>ファイル名は定数で固定する</b>。構成で名前まで動かせるようにすると、
/// 「クライアントが渡したパスを読む／書く」形へ一歩近づくためである（IADR-0322 決定 2）。
/// </para>
/// <para>
/// 🔴 <b>PVC は絶対にマウントしない。</b> 画像 CAPTCHA の実体は PVC 上（<c>$HOME/.com.moomoo.OpenD/</c>）に
/// あるが、そこにはデバイス信頼の実体（<c>Device.dat</c>）と <c>OpenD.xml</c>（ログイン資格情報の MD5）が
/// 同居する。サイドカーはそれらに触れてはならないので、<b>OpenD 本体が emptyDir へ複製した写し</b>だけを読む。
/// </para>
/// </summary>
public sealed class OpendAuthOptions
{
    /// <summary>構成セクション名（<c>appsettings</c> / 環境変数 <c>OpendAuth__*</c>）。</summary>
    public const string SectionName = "OpendAuth";

    /// <summary>OpenD の標準入力 FIFO のファイル名（<c>entrypoint.sh</c> の <c>OPEND_STDIN_FIFO</c> と対）。</summary>
    public const string StdinFifoFileName = "stdin";

    /// <summary>OpenD のコンソール複製のファイル名（<c>entrypoint.sh</c> の <c>script -q -f -a</c> の出力先）。</summary>
    public const string ConsoleLogFileName = "console.log";

    /// <summary>画像 CAPTCHA の写しのファイル名（<c>entrypoint.sh</c> の複写ループが置く）。</summary>
    public const string CaptchaFileName = "captcha.png";

    /// <summary>OpenD コンテナと共有する <c>emptyDir</c> のマウント先。</summary>
    public string RuntimeDirectory { get; set; } = "/run/opend";

    /// <summary>
    /// <c>GET /opend-auth/state</c> が返すコンソール末尾の上限バイト数。
    /// 週単位で常駐する OpenD のログ全量を返さないための境界である。
    /// </summary>
    public int ConsoleTailBytes { get; set; } = 8 * 1024;

    /// <summary>
    /// <c>POST /opend-auth/verify</c> の要求本文の上限バイト数。
    /// 想定する本文は <c>{"kind":"phone","code":"123456"}</c> 程度であり、余裕を見ても 512 で足りる。
    /// </summary>
    public int MaxRequestBodyBytes { get; set; } = 512;

    /// <summary>画像 CAPTCHA として返す上限バイト数（これを超える写しは壊れているとみなして 404 にする）。</summary>
    public int MaxCaptchaBytes { get; set; } = 1024 * 1024;

    /// <summary>投入の流量制限の窓（秒）。</summary>
    public int RateLimitWindowSeconds { get; set; } = 60;

    /// <summary>
    /// 流量制限の窓あたり上限件数。
    /// <b>呼び出し元ごとではなくサービス全体で数える</b> —— 守る対象（moomoo の SMS 送信枠と
    /// OpenD のコンソール）が全体で 1 つしかないためである（IADR-0322 決定 4）。
    /// </summary>
    public int RateLimitMaxSubmissions { get; set; } = 5;

    /// <summary>OpenD の標準入力 FIFO の絶対パス。</summary>
    public string StdinFifoPath => Path.Combine(RuntimeDirectory, StdinFifoFileName);

    /// <summary>コンソール複製の絶対パス。</summary>
    public string ConsoleLogPath => Path.Combine(RuntimeDirectory, ConsoleLogFileName);

    /// <summary>画像 CAPTCHA の写しの絶対パス。</summary>
    public string CaptchaPath => Path.Combine(RuntimeDirectory, CaptchaFileName);
}
