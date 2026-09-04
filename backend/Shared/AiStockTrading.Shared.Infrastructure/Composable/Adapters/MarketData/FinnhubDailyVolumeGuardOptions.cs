using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, ADR-0031（計画）決定3, IADR-0292: 日次上限は未実測のため、暫定手段として第三者観測
// 「約 300 回/日」を計画上の前提値として扱う。**これは実測値ではない**（推測値を実測として焼き込まない
// という IADR-0224 の原則を維持）。この暫定値を超える規模へ監視銘柄数・巡回頻度を上げる前に、
// 日次上限の実測を先行条件とする（ADR-0031 決定3）。
//
// 情報収集（Collection:Source:Finnhub）・実市況 4 サービス（MarketData:Finnhub）の双方が、
// 同じ構成セクション "Finnhub"（トップレベル。両者と別枠）を読む——暫定上限は用途に依らず 1 つの値である。
public sealed class FinnhubDailyVolumeGuardOptions
{
    public const string SectionName = "Finnhub";

    /// <summary>
    /// 暫定日次上限（回/日）。既定 300（第三者観測。ADR-0031 決定3）。日次上限が実測されたら、
    /// 実測値で本設定を上書きする（推測値の既定を残したまま「実測済み」の顔をさせない）。
    /// </summary>
    public int ProvisionalDailyLimit { get; set; } = 300;

    /// <summary>
    /// 構成から読む。**空文字は「既定に委ねる」であって型変換の失敗ではない**（NFR, #679）。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>IConfiguration.Get&lt;T&gt;()</c> を使ってはならない。chart の設定点は「キーは書くが値は空」という
    /// 形で既定へ委ねる規約（<c>values.yaml</c> の多数のキーが <c>value: ""</c>）であり、<c>Get&lt;T&gt;()</c> は
    /// 空文字を <c>int</c> へ変換できず <c>InvalidOperationException</c> を投げる。実測（2026-09-03）では
    /// <c>Finnhub__ProvisionalDailyLimit: ""</c> を与えた RiskManagementService が起動時に CrashLoopBackOff へ
    /// 落ちた。**同じ空文字を <c>services.Configure&lt;T&gt;()</c>（内部は Bind）は黙って読み飛ばす**ため、
    /// 姉妹の <c>MarketDataOptions.EstimatedSymbolCount</c> は同じ空文字でも落ちない —— 落ち方が
    /// 呼び出し方で変わる。ここでは <c>DecisionOptionsLoader</c> と同じ TryParse ＋ 安全側フォールバックへ揃える。
    /// </remarks>
    public static FinnhubDailyVolumeGuardOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FinnhubDailyVolumeGuardOptions();
        var raw = configuration[$"{SectionName}:{nameof(ProvisionalDailyLimit)}"];

        // 空・未設定・不正値・非正値はすべて既定（300）へ倒す。**上限を 0 や負にしない**
        // （0 だと「常に超過」になり警告が常時鳴り、統制の信号が雑音に埋もれる）。
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            options.ProvisionalDailyLimit = parsed;
        }

        return options;
    }
}
