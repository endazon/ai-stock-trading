using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-01, ADR-0031（計画）決定3, IADR-0292: Finnhub の日次上限。
//
// 🔴 ADR-0043（計画）決定 1, #1030, IADR-0437: 暫定の前提値「約 300 回/日」（第三者観測）は**撤回された**。
// 日次上限は「公式に記載が無く、未実測」であり、**既定は未設定（null）＝何とも比べない**。日次上限を実測したら
// その値を設定する（そのときだけ見積りと比べて警告する。ADR-0031 決定 3 の「統制（確定）」の文は有効なまま）。
// 推測値を実測として焼き込まない（IADR-0224）。構成キー名 `Finnhub:ProvisionalDailyLimit` は chart の 5 サービスが
// 既に持つため据え置く（名前の「暫定」は歴史的な呼び名であり、値は実測したものだけを入れる）。
//
// 情報収集（Collection:Source:Finnhub）・実市況 4 サービス（MarketData:Finnhub）の双方が、
// 同じ構成セクション "Finnhub"（トップレベル。両者と別枠）を読む——日次上限は用途に依らず 1 つの値である。
public sealed class FinnhubDailyVolumeGuardOptions
{
    public const string SectionName = "Finnhub";

    /// <summary>
    /// 日次上限（回/日）。<b>既定 null＝未実測・比べない</b>（ADR-0043 決定 1）。日次上限を実測したら、その値だけを設定する。
    /// </summary>
    public int? ProvisionalDailyLimit { get; set; }

    /// <summary>
    /// 構成から読む。**空文字は「既定に委ねる」であって型変換の失敗ではない**（NFR, #679）。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>IConfiguration.Get&lt;T&gt;()</c> を使ってはならない。chart の設定点は「キーは書くが値は空」という
    /// 形で既定へ委ねる規約（<c>values.yaml</c> の多数のキーが <c>value: ""</c>）であり、<c>Get&lt;T&gt;()</c> は
    /// 空文字を数値へ変換できず <c>InvalidOperationException</c> を投げる。実測（2026-09-03）では
    /// <c>Finnhub__ProvisionalDailyLimit: ""</c> を与えた RiskManagementService が起動時に CrashLoopBackOff へ
    /// 落ちた。ここでは <c>DecisionOptionsLoader</c> と同じ TryParse ＋ 安全側フォールバックへ揃える。
    /// </remarks>
    public static FinnhubDailyVolumeGuardOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new FinnhubDailyVolumeGuardOptions();
        var raw = configuration[$"{SectionName}:{nameof(ProvisionalDailyLimit)}"];

        // 空・未設定・不正値・非正値はすべて未設定（比べない）へ倒す。**上限を 0 や負にしない**
        // （0 だと「常に超過」になり警告が常時鳴り、統制の信号が雑音に埋もれる）。
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            options.ProvisionalDailyLimit = parsed;
        }

        return options;
    }
}
