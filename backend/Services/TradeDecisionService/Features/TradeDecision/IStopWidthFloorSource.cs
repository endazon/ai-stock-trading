using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-10, ADR-0049 決定2, #1120, IADR-0465 決定1: 損切り幅の下限（1.0 × ATR(14, 日足)）の供給口。
// 🔴 #1122, IADR-0486 決定1・決定3: **既定は供給しない**（NoAtrStopWidthFloorSource＝IsEnabled=false・常に null）。
// `StopWidthFloor:Atr14:Enabled=true`（既定 false）かつ日足の口があるときだけ、Atr14StopWidthFloorSource が日足（#1118 の
// IDailyBarsProvider。出来高と同じ singleton・同じキャッシュ）から ATR(14) を計算して返す。
// null のとき、取引判断は参照価格（アンカー後の現在値）の 2% を下限とする（`StopWidthFloorPolicy.Resolve`）。
// 🔴 ATR は価格に依らない（1 株あたりの値幅）。そのため問い合わせは参照価格を受け取らず、判断ごとに**プロンプトの前に 1 回だけ**読み、
// 同じ値をプロンプト・下限の適用・監査へ使う（IADR-0486 決定2）。
public interface IStopWidthFloorSource
{
    /// <summary>
    /// ATR の下限を供給する構成か（false＝供給しない既定。要求を 1 回も出さず、プロンプトの下限の行も出さない）。
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// (銘柄, 市場) の損切り幅の下限（1 株あたり・銘柄のローカル通貨）。判断時点の前営業日までの確定足から求める。
    /// 得られない（足が足りない・前営業日の足が無い・取得失敗・経路が無い）ときは <b>null</b>。呼び出し側が退避の 2% を使う。
    /// </summary>
    ValueTask<StopWidthFloor?> GetFloorAsync(string symbol, Market market, CancellationToken cancellationToken = default);

    /// <summary>
    /// FR-15, ADR-0049 決定2, #1122, IADR-0486 決定4: <b>過去の判断時点（Stage 0 の AsOf）</b>の前営業日までの確定足から、
    /// 本番と同じ計算で下限を求める（<c>IDailyBarsProvider.GetConfirmedBarsAsOfAsync</c>。先読みしない）。得られなければ null。
    /// </summary>
    ValueTask<StopWidthFloor?> GetFloorAsOfAsync(
        string symbol, Market market, DateOnly tradingDay, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-10, ADR-0049 決定2, #1120: 損切り幅の下限（1 株あたり・ローカル通貨）とその出所。
/// #1122, IADR-0486 決定3: <paramref name="Atr"/> は出所が ATR のときの ATR(14) の値（下限 ＝ 1.0 × ATR。プロンプトと観測ログに出す）。
/// </summary>
public sealed record StopWidthFloor(decimal PerShare, StopWidthFloorSource Source, decimal? Atr = null);

/// <summary>
/// FR-10, FR-04, ADR-0049 決定2, #1122, IADR-0486 決定2: 判断ごとに 1 回読んだ下限の供給の結果（ATR の下限が有効な構成のときだけ作る）。
/// <para>
/// <b>null（このオブジェクト自体が無い）＝ ATR の下限が無効</b>（既定）。プロンプトは従来のまま（下限の行を出さない）。
/// <see cref="Supplied"/> が null ＝ 有効だが ATR が得られない（プロンプトに「未提供・参照価格の 2%」と書く）。
/// </para>
/// </summary>
public sealed record StopWidthFloorContext(StopWidthFloor? Supplied)
{
    /// <summary>有効な構成で ATR が得られなかった（退避の 2% が効く）。</summary>
    public static readonly StopWidthFloorContext Unavailable = new((StopWidthFloor?)null);

    /// <summary>供給された ATR の下限（正の値・出所が ATR）。それ以外は null（プロンプトは「未提供」と書く）。</summary>
    public StopWidthFloor? Atr14 =>
        Supplied is { PerShare: > 0m, Source: StopWidthFloorSource.Atr14 } floor ? floor : null;
}
