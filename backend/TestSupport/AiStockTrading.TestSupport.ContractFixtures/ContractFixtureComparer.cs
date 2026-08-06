using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiStockTrading.TestSupport.ContractFixtures;

// FR-10, FR-20, SC-01, SC-02, SC-03, #389, #340, IADR-0146: フロントとの契約フィクスチャ（実 HTTP 応答の JSON）の
// 正規化と突合。**バックエンドのプロパティ名を変えたらここが赤くなる**ことが本比較器の唯一の存在理由である。
//
// IADR-0146 決定4: 正規化は**値の形**で行い、キー名の登録簿を作らない。`DateTimeOffset` は実行のたびに
// 変わるためフィクスチャに固定できないが、「`changedAt` と `occurredAtUtc` を正規化する」というキー名の
// 列挙にすると、新しい日時キーが増えたときに登録漏れでフィクスチャが不安定化し、再生成の常用を招く
// （＝機構が無効化される）。よって「`T` を含む ISO 日時として解釈できる文字列」という値の形で判定する。
// `DateOnly`（`T` を含まない）は既定値由来の定数のため正規化しない。
public static class ContractFixtureComparer
{
    /// <summary>正規化後の日時（フィクスチャに固定される値）。</summary>
    public const string NormalizedTimestamp = "2026-08-05T00:00:00.0000000+00:00";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 応答に含まれる日本語（禁止銘柄の理由等）を \uXXXX へ落とさない（フィクスチャを人が読めるようにする）。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 応答 JSON を突合可能な正準形へ整える。空白・改行・インデントの差は吸収し、
    /// **キー名・キーの有無・値の型の差は吸収しない**（吸収したら検査が意味を失う）。
    /// </summary>
    public static string Normalize(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true })
            ?? throw new InvalidOperationException("契約フィクスチャの JSON が null です。");
        var normalized = NormalizeNode(node);
        return normalized?.ToJsonString(WriteOptions) ?? "null";
    }

    /// <summary>
    /// 正規化したうえで期待値と実応答を突き合わせる。一致しなければ <paramref name="difference"/> に
    /// **最初に食い違った行**（行番号つき）を返す（改名がどこで起きたかを読み取れるようにする）。
    /// </summary>
    public static bool Matches(string expected, string actual, out string difference)
    {
        var e = Normalize(expected);
        var a = Normalize(actual);
        if (string.Equals(e, a, StringComparison.Ordinal))
        {
            difference = string.Empty;
            return true;
        }

        difference = DescribeFirstDifference(e, a);
        return false;
    }

    private static string DescribeFirstDifference(string expected, string actual)
    {
        var expectedLines = expected.ReplaceLineEndings("\n").Split('\n');
        var actualLines = actual.ReplaceLineEndings("\n").Split('\n');
        var max = Math.Max(expectedLines.Length, actualLines.Length);
        for (var i = 0; i < max; i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "(行なし)";
            var a = i < actualLines.Length ? actualLines[i] : "(行なし)";
            if (!string.Equals(e, a, StringComparison.Ordinal))
            {
                return $"{i + 1} 行目で相違: 期待(フィクスチャ)=`{e.Trim()}` / 実応答=`{a.Trim()}`";
            }
        }

        return "行単位の相違は見つかりませんでした（行数のみ相違）。";
    }

    private static JsonNode? NormalizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                {
                    var result = new JsonObject();
                    foreach (var (key, value) in obj.ToList())
                    {
                        result[key] = NormalizeNode(value?.DeepClone());
                    }

                    return result;
                }

            case JsonArray array:
                {
                    var result = new JsonArray();
                    foreach (var item in array.ToList())
                    {
                        result.Add(NormalizeNode(item?.DeepClone()));
                    }

                    return result;
                }

            case JsonValue value when value.TryGetValue<string>(out var text) && IsIsoTimestamp(text):
                return JsonValue.Create(NormalizedTimestamp);

            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// 値の形だけで「ISO 8601 の日時」を判定する（キー名を見ない・IADR-0146 決定4）。
    /// `T` を含むことを要件にして `DateOnly`（`2026-08-05`）を除外する——日付は既定値由来の定数であり、
    /// 正規化すると契約の情報を無意味に捨てる。
    /// </summary>
    public static bool IsIsoTimestamp(string? text) =>
        text is not null
        && text.Contains('T', StringComparison.Ordinal)
        && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _);
}
