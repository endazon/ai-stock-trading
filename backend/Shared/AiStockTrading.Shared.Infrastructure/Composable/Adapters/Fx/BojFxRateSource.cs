using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.RateLimiting;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;

// FR-10, FR-17, #381, ADR-0022 決定1, IADR-0194: 日本銀行 時系列統計データ API（無料・認証不要）から
// 「外国為替市況（日次）」の**仲値**を取得する。為替レートの**第一の情報源**である（FRED はフォールバック）。
//
// ADR-0022 決定1 が FRED の罠（DEXJPUS は系列こそ営業日次だが公表は H.10 週次リリース）を避けるために
// 選んだ源である。取得できない場合はすべて null（＝レート無し）へ縮退し、FallbackFxRateSource が FRED を試す。
public sealed class BojFxRateSource(
    HttpClient httpClient,
    string seriesCode,
    IRateLimiter rateLimiter,
    ILogger<BojFxRateSource> logger,
    string baseUrl = BojFxRateSource.DefaultBaseUrl,
    string db = BojFxRateSource.DefaultDb)
    : IFxRateSource
{
    public const string DefaultBaseUrl = "https://www.stat-search.boj.or.jp/api/v1/getDataCode";

    /// <summary>
    /// DB 名。系列コードとは**別のパラメータ**として渡す（ADR-0022 決定1 の落とし穴 1）。
    /// </summary>
    public const string DefaultDb = "fm08";

    /// <summary>
    /// 東京市場 ドル・円 スポット **17 時時点**（オファーとビッドの**仲値**）。
    /// <para>
    /// 🔴 <b>渡すのは系列コード <c>FXERD04</c> であり、画面で使うデータコード <c>FM08'FXERD04</c> ではない</b>
    /// （後者を渡すと API がエラーを返す。ADR-0022 決定1 の落とし穴 1）。DB 名は <see cref="DefaultDb"/> として別に渡す。
    /// </para>
    /// <para>
    /// 🔴 <b>中心相場（<c>FXERD05</c>）は別系列であり、本決定が指す仲値ではない。</b> 中心相場は「その日に最も
    /// 取引の多かった出来値」であって、仲値なのは 17 時時点だけである（ADR-0022 決定1 の落とし穴 2）。
    /// <b>取り違えても動作は正常に見える</b>——もっともらしい値が返るためである（実測 2026-08-15: 同じ 2026-08-12 で
    /// <c>FXERD04</c>=159.38・<c>FXERD05</c>=159.35）。<b>テストでしか捕まらない</b>ため否定形テストで固定する。
    /// </para>
    /// </summary>
    public const string DefaultSeriesCode = "FXERD04";

    /// <summary>
    /// 利用条件で必須のクレジット表記。報告書の為替欄へ伝播させる（ADR-0022 決定1）。
    /// <para>
    /// 🔴 <b>実体は <see cref="FxSourceCredits.Boj"/> にある。ここで文字列を持たない。</b>
    /// 表示側（ReportService）も同じ文言を要するため、両側が literal を持つと
    /// <b>片方だけ古くなっても誰も気づかない</b>（IADR-0196 決定4）。
    /// </para>
    /// </summary>
    public const string Credit = FxSourceCredits.Boj;

    // 日銀の統計は日本時間で公表される。日本標準時は夏時間を持たないため固定オフセットで扱う。
    private static readonly TimeSpan JapanOffset = TimeSpan.FromHours(9);

    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
    {
        // ポート契約: 基準通貨は外部へ問い合わせず必ずレート 1（IFxRateSource）。
        if (quote == MarketCurrency.Base)
            return FxRate.Identity(quote);

        // 系列は USD/JPY 固定であり、基準通貨（USD）以外に解決できるのは JPY だけである。
        // 対応しない通貨を推測で換算しない（誤ったレートでの発注を作らない）。
        if (quote != Currency.Jpy)
        {
            logger.LogWarning("日銀の FX 系列は USD/JPY のみです（要求通貨 {Quote} は解決しません）。", quote);
            return null;
        }

        // 「短時間における高頻度のアクセス」を禁止行為とする規約のため、送信前に自制する（IADR-0064 と同じ規律）。
        await rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        // 期間は指定しない。日次・週次も**月次形式 YYYYMM** で指定する仕様であり、推測すると静かに欠測する
        // （BojInformationSource・IADR-0064 と同じ判断）。全期間を受けて末尾＝最新の観測のみを採る。
        //
        // format=json&lang=en を使う。#381 の受け入れ基準は「CP932 デコード」と書いているが、
        // **CP932 は format=csv&lang=jp の話であり、JSON/en では現れない**（実測 2026-08-15・純 ASCII）。
        // 不要な文字コード依存を持ち込まないため JSON/en を採る（IADR-0194 決定2）。
        var url = $"{_baseUrl}?format=json&lang=en" +
                  $"&db={Uri.EscapeDataString(db)}&code={Uri.EscapeDataString(seriesCode)}";

        BojResponse? payload;
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "日銀 FX 取得失敗（系列 {SeriesCode}）: {Status}。レート無しとして扱います。",
                    seriesCode, (int)response.StatusCode);
                return null;
            }

            payload = await response.Content.ReadFromJsonAsync<BojResponse>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // 通信・解析の失敗はレート無しへ縮退する（安全側＝フォールバックが試され、それも駄目なら新規建てが止まる）。
            logger.LogWarning(ex, "日銀 FX の取得に失敗しました（系列 {SeriesCode}）。レート無しとして扱います。", seriesCode);
            return null;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // #1331, IADR-0533 決定3: HttpClient の**タイムアウト**は OperationCanceledException（TaskCanceledException）で
            // 届く。呼び出し側が取り消していないのに伝播させると、FallbackFxRateSource が「取り消し」と読んで
            // **FRED を試さないまま**上位へ例外が抜ける。取り消しではないので、通信の失敗と同じくレート無しへ縮退する。
            // 呼び出し側の取り消し（cancellationToken が立っている）は従来どおり伝播させる。
            logger.LogWarning(
                ex, "日銀 FX の取得がタイムアウトしました（系列 {SeriesCode}）。レート無しとして扱います。", seriesCode);
            return null;
        }

        return Map(quote, payload);
    }

    // 要求した系列コードの結果だけを採る。**複数系列が返っても取り違えない**ための照合である
    // （FXERD04 と FXERD05 は同日で値が異なり、取り違えても正常に見える）。
    private FxRate? Map(Currency quote, BojResponse? payload)
    {
        if (payload?.ResultSet is not { Count: > 0 } series)
        {
            // #1331: API は HTTP 200 のまま本文の STATUS / MESSAGEID でエラーを返すことがある（パラメータ誤り・
            // 該当なし等）。理由を残さないと「形式が変わったのか、要求が誤っているのか」をログから切り分けられない。
            logger.LogWarning(
                "日銀 FX 応答に系列がありません（系列 {SeriesCode} / STATUS {Status} / {MessageId} {Message}）。" +
                "レート無しとして扱います。",
                seriesCode, payload?.Status, payload?.MessageId, payload?.Message);
            return null;
        }

        var matched = series.FirstOrDefault(s => string.Equals(s.SeriesCode, seriesCode, StringComparison.Ordinal));
        if (matched is null)
        {
            logger.LogWarning(
                "日銀 FX 応答に要求した系列 {SeriesCode} が含まれません。レート無しとして扱います。", seriesCode);
            return null;
        }

        if (Latest(matched.Values) is not { } latest)
        {
            logger.LogWarning("日銀 FX に有効な観測がありません（系列 {SeriesCode}）。レート無しとして扱います。", seriesCode);
            return null;
        }

        var (surveyDate, jpyPerUsd) = latest;

        // #1331, IADR-0533 決定2: **取得の成功を残す。** 従来は成功時に何も出さず、鮮度の警告しか見えなかった——
        // 「日銀から取れているが最新の観測が古い（収録待ち）」と「取れていない」をログから区別できなかった。
        // LAST_UPDATE（日銀が系列を最後に更新した日）を並べれば、収録の遅れか取得の失敗かを 1 行で判別できる。
        // 呼ばれるのはキャッシュ TTL（既定 6 時間）ごとであり、判断サイクルごとには出ない。
        logger.LogInformation(
            "日銀 FX を取得しました（系列 {SeriesCode}: 観測日 {SurveyDate:yyyy-MM-dd} / 値 {JpyPerUsd} 円 / " +
            "系列の最終更新 {LastUpdate}）。",
            seriesCode, surveyDate, jpyPerUsd, matched.LastUpdate);

        // #364, IADR-0152 決定2: ポート契約は「quote 通貨 1 単位あたりの基準通貨額」である。
        // 本系列は「1 USD あたりの円」のため、JPY → USD の換算には**逆数**が要る。
        // 逆数は丸めない——丸めると往復換算の誤差が片側へ偏り、統制の実効上限が系統的にずれる。
        // 0 以下の観測は Latest が弾いているため、ゼロ除算は構造的に起こり得ない。
        return new FxRate(quote, MarketCurrency.Base, 1m / jpyPerUsd, new DateTimeOffset(surveyDate, JapanOffset));
    }

    // 末尾（＝最新の期）から、値のある直近の観測を採る。
    //
    // 🔴 ADR-0022 決定1 の落とし穴 3: **収録は「翌々営業日 8:50 頃」である。** 本体 PDF は毎営業日公表だが、
    // 実装が読む経路への収録は 2 営業日遅れる。**当日・前営業日が null であることを異常として扱ってはならない。**
    // API は欠測・休場・未収録をいずれも同じ null で返すため、区別せず「値のある直近」を採る。
    private static (DateTime SurveyDate, decimal Value)? Latest(BojValues? values)
    {
        if (values?.Values is not { Count: > 0 } observations || values.SurveyDates is not { Count: > 0 } dates)
            return null;

        for (var i = Math.Min(observations.Count, dates.Count) - 1; i >= 0; i--)
        {
            var observation = observations[i];
            if (observation.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            if (!decimal.TryParse(
                    observation.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                || value <= 0m)
            {
                continue;
            }

            // SURVEY_DATES は日次系列では yyyyMMdd の整数で返る。読めない形は採らない（推測で日付を作らない）。
            if (!DateTime.TryParseExact(
                    dates[i].ToString(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            return (date, value);
        }

        return null;
    }

    // 日銀 時系列統計データ API の応答（必要な項目のみ）。フィールド名は大文字・アンダースコア区切り。
    //
    // IADR-0194 決定1: InformationCollectionService の BojInformationSource が同型の DTO を持つが、
    // **共通化しない**（契約もサービスも違い、抽出は Shared.Infrastructure への移動を伴って本 issue の
    // 受け入れ基準と 1 対 1 で対応しない diff になる）。3 つ目の利用者が現れたら抽出する。
    private sealed record BojResponse(
        // STATUS は数値で返るが、型の違いで解析ごと失敗させないため JsonElement で受ける（ログに出すだけ）。
        [property: JsonPropertyName("STATUS")] JsonElement? Status,
        [property: JsonPropertyName("MESSAGEID")] string? MessageId,
        [property: JsonPropertyName("MESSAGE")] string? Message,
        [property: JsonPropertyName("RESULTSET")] IReadOnlyList<BojSeries>? ResultSet);

    private sealed record BojSeries(
        [property: JsonPropertyName("SERIES_CODE")] string? SeriesCode,
        [property: JsonPropertyName("NAME_OF_TIME_SERIES")] string? NameOfTimeSeries,
        [property: JsonPropertyName("LAST_UPDATE")] int? LastUpdate,
        [property: JsonPropertyName("VALUES")] BojValues? Values);

    private sealed record BojValues(
        [property: JsonPropertyName("SURVEY_DATES")] IReadOnlyList<JsonElement>? SurveyDates,
        [property: JsonPropertyName("VALUES")] IReadOnlyList<JsonElement>? Values);
}
