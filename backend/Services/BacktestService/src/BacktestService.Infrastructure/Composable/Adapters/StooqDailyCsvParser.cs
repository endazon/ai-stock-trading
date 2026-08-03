using System.Globalization;
using AiStockTrading.Backtest.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Backtest.Infrastructure.Composable.Adapters;

// FR-15, ADR-0004, #208, IADR-0105: Stooq 日足 CSV（Date,Open,High,Low,Close,Volume）の解析（純関数）。
//
// 解析はロケール非依存（InvariantCulture）で行う。実行環境のカルチャで小数点が変わると価格が黙って壊れるため。
//
// **部分採用しない**: 1 行でも壊れていれば銘柄丸ごと解析失敗として扱う。壊れた行だけを捨てると偽の価格ギャップが
// でき、約定不能（UnfilledOrderCount）や過小な DD として Stage 0 判定へ静かに混入する。欠測は欠測として扱い、
// 「取れなかった」ことを呼び出し元（HistoricalBarGap）に残すほうが監査可能である。
public static class StooqDailyCsvParser
{
    private static readonly string[] ExpectedHeader = ["date", "open", "high", "low", "close", "volume"];

    public static bool TryParse(
        string? csv, string symbol, Market market, out IReadOnlyList<PriceBar> bars, out string failureReason)
    {
        bars = [];

        if (string.IsNullOrWhiteSpace(csv))
        {
            failureReason = "応答が空";
            return false;
        }

        var lines = csv
            .Split('\n')
            .Select(l => l.Trim('\r', ' ', '\t'))
            .Where(l => l.Length > 0)
            .ToList();

        // Stooq は該当データが無いとき CSV ではなく "No data" 等の平文を返す。ヘッダ不一致はすべて解析失敗。
        var header = lines[0].Split(',').Select(c => c.Trim().ToLowerInvariant()).ToList();
        if (header.Count < ExpectedHeader.Length || !header.Take(ExpectedHeader.Length).SequenceEqual(ExpectedHeader))
        {
            failureReason = $"想定外の応答形式（先頭行: {Truncate(lines[0])}）";
            return false;
        }

        if (lines.Count == 1)
        {
            failureReason = "データ行なし";
            return false;
        }

        var parsed = new List<PriceBar>(lines.Count - 1);
        foreach (var line in lines.Skip(1))
        {
            if (!TryParseRow(line, symbol, market, out var bar))
            {
                failureReason = $"解析できない行（{Truncate(line)}）";
                return false;
            }
            parsed.Add(bar);
        }

        bars = [.. parsed.OrderBy(b => b.Date)];
        failureReason = "";
        return true;
    }

    private static bool TryParseRow(string line, string symbol, Market market, out PriceBar bar)
    {
        bar = default;

        var columns = line.Split(',');
        if (columns.Length < ExpectedHeader.Length)
            return false;

        if (!DateOnly.TryParseExact(columns[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return false;

        if (!TryParsePrice(columns[1], out var open) ||
            !TryParsePrice(columns[2], out var high) ||
            !TryParsePrice(columns[3], out var low) ||
            !TryParsePrice(columns[4], out var close))
            return false;

        // OHLC の整合が壊れた行は破損データ。素通しすると約定価格（翌営業日始値）・時価評価（終値）・DD が壊れる。
        // 高値 < 安値、および始値・終値が [安値, 高値] の外はいずれも実在し得ないため弾く（境界一致＝寄り天/寄り底は正常）。
        if (high < low || open < low || open > high || close < low || close > high)
            return false;

        // 出来高は指数等で空になり得る（価格の破損とは別扱い）。欠損は 0、負値は破損として弾く。
        var volumeText = columns[5].Trim();
        long volume = 0;
        if (volumeText.Length > 0 && volumeText != "-" &&
            !long.TryParse(volumeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out volume))
            return false;
        if (volume < 0)
            return false;

        bar = new PriceBar(symbol, market, date, open, high, low, close, volume);
        return true;
    }

    // 価格は正の有限値のみ受け付ける（0 以下は取引価格として成立しない＝破損）。
    private static bool TryParsePrice(string text, out decimal value) =>
        decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0m;

    // 失敗理由に応答本文をそのまま載せない（HTML エラーページ等で診断ログが膨らむのを防ぐ）。
    private static string Truncate(string text) =>
        text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 60), "…");
}
