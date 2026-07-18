using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// ADR-0001, #22 受け入れ基準①: イベント契約の後方互換の CI 契約テスト。
//
// platform 上流仕様（10_composability-design §3）は「イベントは共通エンベロープ＋ペイロードで標準化し、
// 後方互換の追加のみ許可、互換性は CI の契約テストで検証する」と定める。共通エンベロープ型自体は platform でも
// 繰延中（microservices-platform IADR-0049）で、具体エンベロープ型は存在しない。エンベロープ標準は ADR-0018 の
// 固定部＝platform 所有のため、拡張側（ai-stock-trading）が勝手に定義しない。
//
// 本テストは §3 のうち**今 actionable な部分**＝「後方互換の追加のみ許可」を機械化する契約テストである
// （IADR-0079）。既存イベントのフィールド削除・改名・型変更を破壊的変更として検出し、追加のみを許す。
// 共通エンベロープ型の導入は上流確定時（IADR-0049 の繰延解除）に本テストの検証対象へ拡張する。
public class EventBackwardCompatibilityTests
{
    // ドメインイベント名 → (プロパティ名 → 型表示)。追加のみ許可の基準（committed snapshot）。
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void 既存イベント契約は後方互換を保つ_フィールド削除_改名_型変更を禁止する()
    {
        var current = ComputeSchema();
        var baselinePath = BaselinePath();

        // ブートストラップ/意図的更新: UPDATE_EVENT_BASELINE=1 のときは現行スキーマを基準として書き出す
        // （新イベント・新フィールドの追加を承認する運用手順。差分は PR レビューで確認する）。
        if (Environment.GetEnvironmentVariable("UPDATE_EVENT_BASELINE") == "1")
        {
            File.WriteAllText(baselinePath, JsonSerializer.Serialize(current, JsonOptions));
            return;
        }

        File.Exists(baselinePath).Should().BeTrue(
            $"イベント契約の基準ファイルが必要です（UPDATE_EVENT_BASELINE=1 で生成）: {baselinePath}");

        var baseline = JsonSerializer.Deserialize<SortedDictionary<string, SortedDictionary<string, string>>>(
            File.ReadAllText(baselinePath))!;

        var violations = new List<string>();
        foreach (var (evt, props) in baseline)
        {
            // 既存イベントの削除は破壊的。
            if (!current.TryGetValue(evt, out var currentProps))
            {
                violations.Add($"イベント '{evt}' が削除されています（後方互換違反）");
                continue;
            }

            foreach (var (prop, type) in props)
            {
                // フィールド削除/改名は破壊的。
                if (!currentProps.TryGetValue(prop, out var currentType))
                {
                    violations.Add($"'{evt}.{prop}' が削除/改名されています（後方互換違反）");
                    continue;
                }

                // 型変更は破壊的。
                if (currentType != type)
                {
                    violations.Add($"'{evt}.{prop}' の型が '{type}' → '{currentType}' へ変更されています（後方互換違反）");
                }
            }
        }

        // 新イベント・新フィールドの「追加」は後方互換として許容する（違反にしない）。
        violations.Should().BeEmpty(
            "イベント契約は後方互換の追加のみ許可（platform 10_composability-design §3）。"
            + "破壊的変更が必要なら新 ADR を起こし、UPDATE_EVENT_BASELINE=1 で基準を更新すること。"
            + "\n" + string.Join("\n", violations));
    }

    // Shared.Contracts.Events 名前空間の全 record 型のスキーマを算出する（AuditConsumerCoverage と同じ母集合）。
    private static SortedDictionary<string, SortedDictionary<string, string>> ComputeSchema()
    {
        var eventTypes = typeof(InformationCollected).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract
                && t.Namespace == "AiStockTrading.Shared.Contracts.Events" && IsRecord(t));

        var schema = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var t in eventTypes)
        {
            var props = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                // record が生成する EqualityContract（protected）は Public 検索に出ない。公開プロパティのみ対象。
                props[p.Name] = FormatType(p.PropertyType);
            }
            schema[t.Name] = props;
        }
        return schema;
    }

    // 安定した型表示（Nullable は "T?"、ジェネリックは "Name<Arg,...>"、それ以外は型名）。
    private static string FormatType(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying is not null) return FormatType(underlying) + "?";

        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            var args = string.Join(",", t.GetGenericArguments().Select(FormatType));
            return $"{name}<{args}>";
        }
        return t.Name;
    }

    private static bool IsRecord(Type t) =>
        t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static string BaselinePath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "event-schemas.baseline.json");
}
