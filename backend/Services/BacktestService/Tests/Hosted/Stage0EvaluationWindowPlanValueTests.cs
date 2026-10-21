using System.Text.RegularExpressions;
using BacktestService.Domain;
using BacktestService.Hosted;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BacktestService.Tests;

// FR-15, ADR-0069 決定1・決定2・決定5, #1307, IADR-0563: Stage 0 の評価の窓（365 暦日）と DSR の閾値（0.95）は計画の値である。
// コードの既定と、判定用の構成（開発用の appsettings・本番の Helm の構成）を計画の値と突き合わせる。
//
// 🔴 **PoC の経路の確認用の構成は対象外である**（ADR-0069 決定5）。values-local.yaml の `LookbackDays = 24` は
// 記録・再生の経路を確かめるための値で、判定用ではない。対象外は <see cref="PocOnlyValues"/> に名前で挙げ、
// それ以外の values*.yaml・appsettings*.json は**増えても自動で検査の対象に入る**（母集合はファイルの列挙で取る）。
public class Stage0EvaluationWindowPlanValueTests
{
    // ADR-0069 決定1: 評価の窓の長さ（暦日）。ADR-0069 決定2: DSR の合格の閾値。
    private const int PlanLookbackDays = 365;
    private const double PlanMinDeflatedSharpe = 0.95;

    private const string HelmDir = "deploy/helm/ai-stock-trading";
    private const string ServiceDir = "backend/Services/BacktestService";
    private const string LookbackEnv = "Backtest__Stage0__LookbackDays";

    // ADR-0069 決定5: 判定用でない（経路の確認用の）構成。理由を書かずに足さない。
    private static readonly Dictionary<string, string> PocOnlyValues = new(StringComparer.Ordinal)
    {
        ["values-local.yaml"] = "PoC（経路B）の記録・再生の経路の確認用。利用者裁定 2026-10-13 の記録の窓（2026-09-15〜10-09）に合わせた 24 日（ADR-0069 決定5）",
    };

    [Fact]
    public void 評価の窓の長さは計画の値365暦日でありコードの既定と公開定数が同値である()
    {
        Stage0EvaluationOptions.LookbackDaysPlanValue.Should().Be(PlanLookbackDays, "ADR-0069 決定1: 値の正本は計画である");
        new Stage0EvaluationOptions().LookbackDays.Should().Be(
            Stage0EvaluationOptions.LookbackDaysPlanValue,
            "ADR-0069 決定1: 窓の長さを変えるときは計画を改める（実装の構成だけで変えない）");
    }

    [Fact]
    public void DSRの合格の閾値は計画の値095でありDefaultと公開定数が同値である()
    {
        Stage0GateCriteria.MinDeflatedSharpeDefault.Should().Be(PlanMinDeflatedSharpe, "ADR-0069 決定2: 値の正本は計画である");
        Stage0GateCriteria.Default.MinDeflatedSharpe.Should().Be(
            Stage0GateCriteria.MinDeflatedSharpeDefault,
            "ADR-0069 決定2: 変更が要るなら計画へ環流する（実装で動かさない）");
    }

    // ADR-0069 決定1・決定3: カットオフ 2026-06-30 のもとで 365 暦日の窓が届くのは終端 2027-07-01（起点 2026-07-01）である。
    // その前日の終端では窓の起点がカットオフ当日になり、検証条件①（窓のバーはすべてカットオフより後）を満たさない。
    [Fact]
    public void 既定の窓はカットオフ2026_06_30のもとで2027_07_01に初めてカットオフより後から始まる()
    {
        var cutoff = new DateOnly(2026, 6, 30);
        var options = new Stage0EvaluationOptions();

        options.EvaluationWindow(new DateOnly(2027, 7, 1)).Should().Be((new DateOnly(2026, 7, 1), new DateOnly(2027, 7, 1)));
        options.EvaluationWindow(new DateOnly(2027, 7, 1)).From.Should().BeAfter(cutoff);
        options.EvaluationWindow(new DateOnly(2027, 6, 30)).From.Should().NotBeAfter(cutoff, "届く前の窓はカットオフ以前を含む（ADR-0069 決定3）");
    }

    // 開発用の構成（appsettings.json に重ねる appsettings.Development.json）。appsettings*.json を列挙して束縛する。
    [Fact]
    public void appsettingsの構成は評価の窓を計画の値で束縛する()
    {
        var dir = Path.Combine([RepoRoot(), .. ServiceDir.Split('/')]);
        var files = Directory.GetFiles(dir, "appsettings*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        files.Should().Contain(f => Path.GetFileName(f) == "appsettings.json");

        var baseFile = files.Single(f => Path.GetFileName(f) == "appsettings.json");
        foreach (var file in files)
        {
            var builder = new ConfigurationBuilder().AddJsonFile(baseFile, optional: false);
            if (file != baseFile) builder.AddJsonFile(file, optional: false);
            var options = new Stage0EvaluationOptions();
            builder.Build().GetSection(Stage0EvaluationOptions.SectionName).Bind(options);

            options.LookbackDays.Should().Be(PlanLookbackDays, $"{Path.GetFileName(file)} は判定用の構成である（ADR-0069 決定1）");
        }
    }

    // 本番と追加の構成（values*.yaml）。backtest の extraEnv に LookbackDays を書かない（既定 365 が効く）か、365 を書く。
    [Fact]
    public void Helmの判定用の構成は評価の窓を計画の値から外さない()
    {
        var dir = Path.Combine([RepoRoot(), .. HelmDir.Split('/')]);
        var files = Directory.GetFiles(dir, "values*.yaml", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        files.Select(Path.GetFileName).Should().Contain("values.yaml");
        // 対象外の名前が実在しない（改名・削除された）なら、対象外の表が古い。黙って残さない。
        files.Select(Path.GetFileName).Should().Contain(PocOnlyValues.Keys);

        var checkedCount = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (PocOnlyValues.ContainsKey(name)) continue;

            var env = BacktestEnv(File.ReadAllText(file));
            if (env.TryGetValue(LookbackEnv, out var value))
                value.Should().Be(PlanLookbackDays.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{name} は判定用の構成である（ADR-0069 決定1）");
            checkedCount++;
        }

        checkedCount.Should().BeGreaterThan(0, "少なくとも本番の values.yaml を検査している");
    }

    // 否定形: 検査器が「書いていない」を全部素通しにしていないこと。PoC の構成は実際に 365 と違う値を書いており、
    // 同じ読み方で 365 以外の値が読める（＝対象外にしなければ赤になる）。
    [Fact]
    public void PoCの構成は365と違う値を書いており対象外の表が無ければ検知される()
    {
        var env = BacktestEnv(File.ReadAllText(Path.Combine([RepoRoot(), .. HelmDir.Split('/'), "values-local.yaml"])));

        env.Should().ContainKey(LookbackEnv);
        env[LookbackEnv].Should().NotBe(PlanLookbackDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ADR-0069 決定2: DSR の閾値は構成の口を持たない（Default だけが効く）。構成のどこにも閾値を書かせない。
    [Fact]
    public void DSRの閾値はどの構成からも変えられない()
    {
        var root = RepoRoot();
        var configs = Directory.GetFiles(Path.Combine([root, .. HelmDir.Split('/')]), "values*.yaml")
            .Concat(Directory.GetFiles(Path.Combine([root, .. ServiceDir.Split('/')]), "appsettings*.json"))
            .Append(Path.Combine(root, "docker-compose.yml"));

        foreach (var file in configs)
            File.ReadAllText(file).Should().NotContain("MinDeflatedSharpe", $"{Path.GetFileName(file)} に DSR の閾値の構成を書かない");
    }

    // values の `services.backtest.extraEnv` のうち `- { name: X, value: "Y" }` の行（コメント行と secretKeyRef は除く）。
    private static Dictionary<string, string> BacktestEnv(string text)
    {
        var block = ServiceBlock(text, "backtest");
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(block, @"(?m)^\s*-\s*\{\s*name:\s*([A-Za-z0-9_]+),\s*value:\s*""([^""]*)""\s*\}"))
            env[m.Groups[1].Value] = m.Groups[2].Value;
        return env;
    }

    // `  <name>:` から次の同じ深さのキーまで。
    private static string ServiceBlock(string text, string name)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, $"  {name}:");
        if (start < 0) return string.Empty;
        var end = start + 1;
        var sibling = new Regex(@"^  [A-Za-z0-9_-]+:\s*$|^[A-Za-z]");
        while (end < lines.Length && !sibling.IsMatch(lines[end])) end++;
        return string.Join('\n', lines[start..end]);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }
}
