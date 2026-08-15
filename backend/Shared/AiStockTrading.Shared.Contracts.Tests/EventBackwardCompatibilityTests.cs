using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
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
//
// 既知の限界（IADR-0079 に明記）: プロパティの**型名**単位で比較するため、enum メンバー
// （OrderStatus/Market/TradeSide/RejectionReason 等）の削除・改名は検出しない（型名自体は不変のため）。
public class EventBackwardCompatibilityTests
{
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

        var violations = FindViolations(baseline, current);

        // 新イベント・新フィールドの「追加」は後方互換として許容する（違反にしない）。
        violations.Should().BeEmpty(
            "イベント契約は後方互換の追加のみ許可（platform 10_composability-design §3）。"
            + "破壊的変更が必要なら新 ADR を起こし、UPDATE_EVENT_BASELINE=1 で基準を更新すること。"
            + "\n" + string.Join("\n", violations));
    }

    // 🔴 #381 停止側 / IADR-0198: **追加を許容することと、追加を記録しないことは違う。**
    //
    // 上のテストは「基準に載っている型」しか見ない（追加は後方互換なので違反にしない）。
    // その結果、**新イベントは誰かが基準を再生成するまで一切保護されない** —— 追加した次の PR で
    // フィールドを削除・改名しても、基準に無いのだから緑のまま通る。
    //
    // 実測: #509 で追加した 3 型（FxRateSourceFellBack / FxRateSourcePrimaryRestored / FxRateStale）が
    // 基準に載らないまま develop に入っていた。**本 PR は現にその FxRateStale を書き換えている** ——
    // 削除していたら誰も気づかなかった。
    //
    // よってここで「基準への登録漏れ」を別立てで落とす。承認は従来どおり UPDATE_EVENT_BASELINE=1 の
    // 再生成＋PR レビューであり、**手順は増えない。忘れたときに気づけるようになるだけである。**
    [Fact]
    public void 全イベントが基準に登録されている_追加は許容するが記録漏れは許容しない()
    {
        var baselinePath = BaselinePath();
        File.Exists(baselinePath).Should().BeTrue(
            $"イベント契約の基準ファイルが必要です（UPDATE_EVENT_BASELINE=1 で生成）: {baselinePath}");

        var baseline = JsonSerializer.Deserialize<SortedDictionary<string, SortedDictionary<string, string>>>(
            File.ReadAllText(baselinePath))!;

        var unpinned = ComputeSchema().Keys.Where(evt => !baseline.ContainsKey(evt)).ToArray();

        unpinned.Should().BeEmpty(
            "基準に無いイベントは後方互換の検査対象外＝**追加した瞬間から無保護**になります。"
            + "UPDATE_EVENT_BASELINE=1 で基準を再生成し、差分を PR レビューで確認してください。"
            + "\n未登録: " + string.Join(", ", unpinned));
    }

    // --- 比較ロジック自体の回帰テスト（削除/改名/型変更を検出し、追加は許容する） -----------------

    [Fact]
    public void FindViolations_フィールド削除を検出する()
    {
        var baseline = Schema(("Evt", Props(("A", "Int32"), ("B", "String"))));
        var current = Schema(("Evt", Props(("A", "Int32")))); // B が消えた
        FindViolations(baseline, current).Should().ContainSingle().Which.Should().Contain("Evt.B");
    }

    [Fact]
    public void FindViolations_イベント削除を検出する()
    {
        var baseline = Schema(("Evt", Props(("A", "Int32"))));
        var current = new SortedDictionary<string, SortedDictionary<string, string>>(); // Evt ごと消えた
        FindViolations(baseline, current).Should().ContainSingle().Which.Should().Contain("Evt");
    }

    [Fact]
    public void FindViolations_型変更を検出する()
    {
        var baseline = Schema(("Evt", Props(("A", "Int32"))));
        var current = Schema(("Evt", Props(("A", "Int64")))); // 型が変わった
        FindViolations(baseline, current).Should().ContainSingle().Which.Should().Contain("Int32");
    }

    [Fact]
    public void FindViolations_フィールド_イベントの追加は許容する()
    {
        var baseline = Schema(("Evt", Props(("A", "Int32"))));
        var current = Schema(
            ("Evt", Props(("A", "Int32"), ("B", "String"))), // フィールド追加
            ("NewEvt", Props(("X", "Guid"))));               // イベント追加
        FindViolations(baseline, current).Should().BeEmpty();
    }

    // 後方互換違反（削除・改名・型変更）を列挙する純関数。追加は違反にしない。
    internal static IReadOnlyList<string> FindViolations(
        IReadOnlyDictionary<string, SortedDictionary<string, string>> baseline,
        IReadOnlyDictionary<string, SortedDictionary<string, string>> current)
    {
        var violations = new List<string>();
        foreach (var (evt, props) in baseline)
        {
            if (!current.TryGetValue(evt, out var currentProps))
            {
                violations.Add($"イベント '{evt}' が削除されています（後方互換違反）");
                continue;
            }

            foreach (var (prop, type) in props)
            {
                if (!currentProps.TryGetValue(prop, out var currentType))
                {
                    violations.Add($"'{evt}.{prop}' が削除/改名されています（後方互換違反）");
                    continue;
                }

                if (currentType != type)
                {
                    violations.Add($"'{evt}.{prop}' の型が '{type}' → '{currentType}' へ変更されています（後方互換違反）");
                }
            }
        }
        return violations;
    }

    // Shared.Contracts.Events の全 record 型のスキーマを算出する（母集合は EventTypeDiscovery で単一化）。
    private static SortedDictionary<string, SortedDictionary<string, string>> ComputeSchema()
    {
        var schema = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var t in EventTypeDiscovery.GetEventTypes())
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

    // 基準 snapshot の場所。まずコンパイル時のソース隣接パスを使い、無ければ出力ディレクトリから上方探索する
    // （build と test を別ジョブ/別ランナーへ分離しても解決できるようにするフォールバック）。
    private static string BaselinePath([CallerFilePath] string thisFile = "")
    {
        const string fileName = "event-schemas.baseline.json";
        var beside = Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", fileName);
        if (File.Exists(beside)) return beside;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return beside; // 見つからなければ元のパスを返す（存在アサートで失敗させる/書き出し先に使う）。
    }

    // テスト用の合成スキーマ/プロパティ集合を組み立てる補助。
    private static SortedDictionary<string, string> Props(params (string Prop, string Type)[] props)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (prop, type) in props) map[prop] = type;
        return map;
    }

    private static SortedDictionary<string, SortedDictionary<string, string>> Schema(
        params (string Event, SortedDictionary<string, string> Props)[] events)
    {
        var schema = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (evt, props) in events) schema[evt] = props;
        return schema;
    }
}
