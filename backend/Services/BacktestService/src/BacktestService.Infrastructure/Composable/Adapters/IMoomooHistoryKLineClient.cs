namespace AiStockTrading.Backtest.Infrastructure.Composable.Adapters;

// FR-15, ADR-0023 決定5, IADR-0157, #382: moomoo OpenAPI の履歴 K 線（QotRequestHistoryKL）取得の抽象。
//
// **SDK（moomoo-api / protobuf）依存を実装 1 クラス（MMApiMoomooHistoryKLineClient）に閉じるためのポートである。**
// ページング・前復権の指定・欠測の記録といった「壊れると黙って結果が歪む」判断は、本ポートの**利用側**
// （MoomooHistoricalBarSource）に置き、実 OpenD 無しで単体検証できるようにする。
//
// **本ポートは米国株の日足に限る**（ADR-0023 決定5 が定めたのは米国株日足 OHLC の履歴源である）。
// 日本株を足す場合は計画側の決定が要る（本実装は写像せず欠測として残す）。
public interface IMoomooHistoryKLineClient
{
    /// <summary>
    /// 米国株の日足 K 線を 1 リクエスト分（最大 <see cref="MoomooHistoryKLineRequest.MaxCount"/> 件）取得する。
    /// 続きがある場合は応答の <see cref="MoomooHistoryKLinePage.NextRequestKey"/> が非 null になり、
    /// 呼び出し側はそれを次の要求へ渡して継続する（ADR-0023 決定5: 1 リクエスト 1,000 件上限）。
    /// </summary>
    /// <exception cref="MoomooHistoryKLineException">OpenD が非成功（retType != 0）を返した場合。</exception>
    Task<MoomooHistoryKLinePage> RequestUsDailyKLinesAsync(
        MoomooHistoryKLineRequest request,
        CancellationToken cancellationToken = default);
}

// FR-15, ADR-0023 決定5: 履歴 K 線の 1 リクエスト。NextRequestKey が null なら先頭ページ。
public sealed record MoomooHistoryKLineRequest(
    string Symbol,
    DateOnly From,
    DateOnly To,
    MoomooKLineAdjustment Adjustment,
    int MaxCount,
    byte[]? NextRequestKey);

// FR-15, ADR-0023 決定5: 1 リクエスト分の応答。NextRequestKey が非 null なら続きがある。
public sealed record MoomooHistoryKLinePage(IReadOnlyList<MoomooDailyKLine> KLines, byte[]? NextRequestKey);

// FR-15, ADR-0023 決定5: SDK 非依存の日足 K 線（OHLCV）。
public readonly record struct MoomooDailyKLine(
    DateOnly Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);

// FR-15, ADR-0023 決定5: 復権（株式分割・配当による過去価格の調整）方式。
//
// **バックテストで用いるのは <see cref="ForwardAdjusted"/>（前復権 / RehabType_Forward）である**（決定5）。
// 無指定（<see cref="None"/>）にすると分割を跨いで価格が不連続になり、バックテストの結果が壊れる。
//
// ⚠️ **前復権の調整方式が ADR-0016 決定14 の費用モデル（借株料・配当相当額）と整合するかは未確認である**
// （ADR-0023 決定5 の「実装側で確認を要する 2 点」の 2。実 OpenD のデータでしか確認できない）。
public enum MoomooKLineAdjustment
{
    /// <summary>復権なし（RehabType_None）。**バックテストでは使わない**（分割で価格が不連続になる）。</summary>
    None,

    /// <summary>前復権（RehabType_Forward）。ADR-0023 決定5 が定めた方式。</summary>
    ForwardAdjusted,

    /// <summary>後復権（RehabType_Backward）。本システムでは使わない。</summary>
    BackwardAdjusted,
}

// FR-15, ADR-0023 決定5: OpenD が非成功（retType != 0）を返したことを表す。
// 呼び出し側（MoomooHistoricalBarSource）はこれを**その銘柄の欠測**として記録し、他銘柄の取得を続ける。
// 通信の失敗（タイムアウト・切断）は本例外ではなく元の例外のまま送出する（握りつぶさない・Stooq と同方針）。
public sealed class MoomooHistoryKLineException(string message) : Exception(message);
