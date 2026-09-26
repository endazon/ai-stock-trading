using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AiStockTrading.Shared.Contracts.Backtest;

// FR-15, FR-20, ADR-0033 決定2, IADR-0281 決定3, IADR-0318: 記録・再生戦略の**同一性**を導出する純関数。
//
// `BacktestEvaluated.StrategyId` は verdict の無効化契機「戦略の変更」を機械判定する唯一の鍵である
// （IADR-0281 決定3）。記録・再生方式では **戦略の中身＝記録そのもの**であるため、
// 戦略 ID は記録の内容から導く。同じ記録を読み直せば同じ ID になり、1 件でも判断が変われば ID が変わる。
//
// 🔴 **作成時刻（CreatedAt）はハッシュに含めない。** 含めると、同じ記録を保存し直しただけで別戦略に見え、
// 受け手（Risk）が有効な verdict を「戦略が変わった」として捨てる。
public static class Stage0StrategyIdentity
{
    /// <summary>戦略 ID の接頭辞（`ai-decision-replay/&lt;model&gt;/&lt;recordset-hash&gt;`）。</summary>
    public const string Prefix = "ai-decision-replay";

    /// <summary>ハッシュの短縮長（16 進 16 文字＝64 ビット。記録の取り違えを検知するには十分で、ID が読める長さに収まる）。</summary>
    private const int ShortHashLength = 16;

    /// <summary>戦略 ID を組む。</summary>
    public static string StrategyIdFor(string modelId, string contentHash) =>
        $"{Prefix}/{Normalize(modelId)}/{Normalize(contentHash)}";

    /// <summary>
    /// 記録の**内容**から短縮ハッシュを導く。期間・銘柄・カットオフ日・モデル・各判断（生の判断を含む）を
    /// 決定的な正規形へ並べて SHA-256 を採る。
    /// </summary>
    public static string ComputeContentHash(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<Stage0RecordedSymbol> symbols,
        DateOnly llmTrainingCutoff,
        string modelId,
        IReadOnlyList<Stage0DecisionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(records);

        var sb = new StringBuilder();
        sb.Append(from.ToString("O", CultureInfo.InvariantCulture)).Append('|')
          .Append(to.ToString("O", CultureInfo.InvariantCulture)).Append('|')
          .Append(llmTrainingCutoff.ToString("O", CultureInfo.InvariantCulture)).Append('|')
          .Append(Normalize(modelId)).Append('\n');

        // 銘柄は並び順の揺れを吸収して正規化する（構成の記述順で ID が変わらないようにする）。
        foreach (var s in symbols
            .OrderBy(s => s.Symbol, StringComparer.Ordinal)
            .ThenBy(s => s.Market))
        {
            sb.Append(s.Symbol).Append('@').Append(s.Market).Append('\n');
        }

        // 判断は AsOf → 銘柄 → 市場の安定順（記録器の出力順に依存させない）。
        foreach (var r in records
            .OrderBy(r => r.AsOf)
            .ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .ThenBy(r => r.Market))
        {
            sb.Append(r.AsOf.ToString("O", CultureInfo.InvariantCulture)).Append('|')
              .Append(r.Symbol).Append('@').Append(r.Market).Append('|')
              .Append(r.MajorityAction).Append('|')
              .Append(r.SignedQuantity.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(r.VoteCount.ToString(CultureInfo.InvariantCulture)).Append('|')
              .Append(r.InputFingerprint).Append('|');

            // 生の判断も同一性に含める。多数決結果が同じでも票の割れ方が違えば別の記録である
            // （ADR-0033 決定4 が生の判断を残させた理由＝成績のばらつきの説明が変わる）。
            foreach (var raw in r.RawDecisions.OrderBy(v => v.Attempt))
            {
                sb.Append(raw.Attempt.ToString(CultureInfo.InvariantCulture)).Append(':')
                  .Append(raw.Action).Append(':')
                  .Append(raw.Unparseable ? '1' : '0').Append(';');
            }

            // FR-15, ADR-0036 決定1, #749, IADR-0387: **as-of 入力の再構成可否も同一性に含める。**
            // 同じ判断列でも「何を判定母集団から外すか」が違えば、**評価したものが違う** ——
            // 戦略 ID は verdict の無効化契機「戦略の変更」を機械判定する唯一の鍵であり（IADR-0281 決定3）、
            // 除外の集合が変わったのに ID が同じままだと、**別の母集団で採った合格が生き残る**。
            // 未申告（null）は `-` として畳む（申告の有無そのものも同一性の一部である）。
            sb.Append('#');
            if (r.AsOfInputs is null)
            {
                sb.Append('-');
            }
            else
            {
                foreach (var kind in Stage0AsOfInputs.RequiredKinds)
                {
                    var status = r.AsOfInputs.FirstOrDefault(s => s.Kind == kind);
                    sb.Append(kind).Append('=')
                      .Append(status is null ? "?" : status.Availability.ToString()).Append(';');
                }

                // FR-04, ADR-0044 決定 3, #1034, IADR-0440 決定 7: 必須でない種別（(e) 当時の監視銘柄）は**申告があるときだけ**含める。
                // 申告の無い記録（ADR-0044 より前の記録）の戦略 ID を変えないためである。申告があれば、その可否も同一性の一部である。
                foreach (var kind in Stage0AsOfInputs.DeclarableKinds.Except(Stage0AsOfInputs.RequiredKinds))
                {
                    var status = r.AsOfInputs.FirstOrDefault(s => s.Kind == kind);
                    if (status is not null)
                        sb.Append(kind).Append('=').Append(status.Availability.ToString()).Append(';');
                }
            }

            sb.Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(digest)[..ShortHashLength];
    }

    // ID に空白・区切り文字を混ぜない（StrategyId は文字列比較で同一性を判定する鍵である）。
    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().Replace('/', '_').Replace(' ', '-');
}
