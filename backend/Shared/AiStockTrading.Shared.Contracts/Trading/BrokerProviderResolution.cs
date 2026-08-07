namespace AiStockTrading.Shared.Contracts.Trading;

/// <summary>
/// FR-20 (3), #422, IADR-0161: <b>発注先（<see cref="BrokerProvider"/>）の allow-list による解決。</b>
/// <para>
/// 計画（FR-20 の 2026-08-07 追記 (3)）は「発注先の初期値は内蔵 <c>paper</c> とする。
/// <b>設定ストアの旧行（本項目を持たない場合）も同じ既定へ落とす</b>（「読めない行は実弾」に
/// 倒れないようにするため）」と定める。
/// </para>
/// <para>
/// <b>本クラスは allow-list である。</b>既知の 3 値の<b>明示一致</b>だけを受理し、それ以外
/// （<c>null</c>・欠落・未知の序数・未知の文字列・大文字小文字違い・別の型）は<b>すべて</b>
/// <see cref="Default"/>（内蔵 <c>paper</c>＝外部へ一度も発注しない唯一の値）へ落とす。
/// deny-list（「危険な値だけを弾く」）で書くと、<b>未知の値が実弾側へ落ちる</b>——それが計画の
/// 「読めない行は実弾」が名指ししている失敗である。同型の先例は
/// <c>HistoricalBarSourceFactory.ResolveProvider</c>（未知の provider はすべて no-op）。
/// </para>
/// <para>
/// <b><c>Enum.IsDefined</c> で置き換えてはならない。</b>それは「enum に書いてあるか」を見るものであり、
/// 「本規則が承認したか」ではない。4 値目（例: 別ブローカの <c>REAL</c>）を末尾へ足した瞬間に、
/// 未検討の値が黙って allow-list を素通りする。
/// </para>
/// </summary>
public static class BrokerProviderResolution
{
    /// <summary>
    /// FR-20 (3): 解決できない入力の落とし先＝<b>内蔵 <c>paper</c></b>。
    /// 外部へ一度も発注しない唯一の値であり、初期段階が Stage 0 であることとも整合する。
    /// </summary>
    public const BrokerProvider Default = BrokerProvider.InternalPaper;

    /// <summary>
    /// 計画が定める 3 値のいずれかか。<b>「どの値が既知か」の単一情報源</b>であり、
    /// 書き込み側（<c>BrokerProviderChange</c> の未定義値検査）もここを通す。
    /// </summary>
    public static bool IsKnown(BrokerProvider provider) => provider switch
    {
        BrokerProvider.InternalPaper => true,
        BrokerProvider.MoomooReal => true,
        BrokerProvider.MoomooSimulate => true,
        _ => false,
    };

    /// <summary>
    /// 永続行・イベント本文から読んだ値を解決する。<b>3 値の明示一致のみ</b>を受理し、
    /// <c>null</c>（＝本項目を持たない旧行）と範囲外の序数は <see cref="Default"/> へ落とす。
    /// </summary>
    public static BrokerProvider Resolve(BrokerProvider? value) => value switch
    {
        BrokerProvider.InternalPaper => BrokerProvider.InternalPaper,
        BrokerProvider.MoomooReal => BrokerProvider.MoomooReal,
        BrokerProvider.MoomooSimulate => BrokerProvider.MoomooSimulate,
        _ => Default, // null・未知の序数はすべて内蔵 paper（allow-list）
    };

    /// <summary>
    /// 文字列表現（手編集された行・外部ツールが書いた行）を解決する。
    /// <para>
    /// 照合は<b>前後空白のみを除いた完全一致</b>で<b>大文字小文字を区別する</b>
    /// （FR-20 (2) の「REAL」照合規則と同じ規律。緩めると <c>"MOOMOO REAL"</c> のような綴りが
    /// 実弾として通り始める）。受理するのは序数の 10 進表記と enum の正準名だけである。
    /// </para>
    /// </summary>
    public static BrokerProvider Resolve(string? value) => (value ?? string.Empty).Trim() switch
    {
        "0" or nameof(BrokerProvider.InternalPaper) => BrokerProvider.InternalPaper,
        "1" or nameof(BrokerProvider.MoomooReal) => BrokerProvider.MoomooReal,
        "2" or nameof(BrokerProvider.MoomooSimulate) => BrokerProvider.MoomooSimulate,
        _ => Default, // 空・未知の文字列・大小文字違いはすべて内蔵 paper（allow-list）
    };
}
