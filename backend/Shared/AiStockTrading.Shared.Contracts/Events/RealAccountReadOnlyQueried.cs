using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, UC-06, ADR-0016 決定3（2026-08-06 追記）, #1000, IADR-0482 決定4:
// **実弾口座（moomoo の Real × Margin）のヘッダで読み取り専用の照会を 1 回送った**という事実。
//
// 本系は発注を SIMULATE に固定している（実弾は閂で止まっている）。借株可否と維持率の束の照会（`TrdGetMarginRatio`）だけは
// SIMULATE 口座では成功しないため、裁定（#1000・2026-10-02）により実弾口座のヘッダで照会する経路を足した。
// 🔴 **実弾のヘッダが本系から OpenD へ出た回数と時刻を、後から台帳だけで数えられるようにする**のが本イベントの役目である
// （「実弾には触れていない」を記録で示せる状態を保つ）。照会を送ったら答えの成否に関わらず 1 件出す。
//
// 🔴 **口座 ID の全桁を運ばない。** <see cref="MaskedTail"/> は末尾 2 桁以外を伏せた形（`****08`）だけである
// （発注執行のログ・検証口と同じ伏せ方。IADR-0473 / IADR-0476）。
/// <param name="TradingEnvironment">照会に使った取引環境。常に <see cref="RealTradingEnvironment"/>（＝実弾口座のヘッダ）。</param>
/// <param name="Operation">照会の種類（OpenD のプロトコル名。現状は <c>GetMarginRatio</c> だけ）。</param>
/// <param name="MaskedTail">照会に使った口座 ID の末尾 2 桁以外を伏せた形。全桁は運ばない。</param>
/// <param name="Symbol">照会した銘柄。</param>
/// <param name="Market">照会した銘柄の市場。</param>
/// <param name="Outcome">照会の結果（<see cref="RealAccountReadOnlyQueryOutcomes"/> の語彙）。</param>
/// <param name="QueriedAt">照会を送った時刻（UTC）。</param>
public record RealAccountReadOnlyQueried(
    string TradingEnvironment,
    string Operation,
    string MaskedTail,
    string Symbol,
    Market Market,
    string Outcome,
    DateTimeOffset QueriedAt)
{
    /// <summary>実弾口座のヘッダ（moomoo <c>TrdEnv_Real</c>）で照会したことを表す値。</summary>
    public const string RealTradingEnvironment = "Real";
}

/// <summary>#1000, IADR-0482 決定4: <see cref="RealAccountReadOnlyQueried.Outcome"/> の語彙。</summary>
public static class RealAccountReadOnlyQueryOutcomes
{
    /// <summary>借株できると答えた。</summary>
    public const string Permitted = "permitted";

    /// <summary>借株できないと答えた。</summary>
    public const string NotPermitted = "not-permitted";

    /// <summary>応答に当該銘柄の行、または借株可否の欄が無かった（分からない）。</summary>
    public const string FieldMissing = "field-missing";

    /// <summary>照会が失敗した（非成功の応答・不達・打ち切り）。</summary>
    public const string Failed = "failed";
}
