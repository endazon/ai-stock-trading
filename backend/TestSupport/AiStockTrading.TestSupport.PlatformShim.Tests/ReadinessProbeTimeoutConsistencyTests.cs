using System.Globalization;
using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, IADR-0468, #1137: readinessProbe の timeoutSeconds（chart の values.yaml）と、アプリの DB 疎通チェックの打ち切り
// （HealthCheckExtensions.NpgSqlReadinessTimeout）の大小を固定する。
// 🔴 probe の打ち切り ＞ チェックの打ち切り（厳密に）。逆だと kubelet が先に切り、チェックは途中で取り消されて
// 「結果の無い失敗」になる（2026-09-30 17:49 UTC の "completed after 1002ms … The operation was canceled" の形）。
// 描画結果（11 Worker 全部に timeoutSeconds が出ること）は helm.yml の assert が固定する。
public class ReadinessProbeTimeoutConsistencyTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    private static string ChartFile(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "helm", "ai-stock-trading", name));

    /// <summary>values.yaml のトップレベル <c>probes.readiness.&lt;key&gt;</c> の整数値（無ければ null）。</summary>
    private static int? ReadinessValue(string key)
    {
        var m = Regex.Match(
            ChartFile("values.yaml"),
            @"^probes:\r?\n(?:[ \t]+.*\r?\n)*?  readiness:\r?\n(?:    .*\r?\n)*?    " + Regex.Escape(key) + @":[ \t]*(\d+)[ \t]*\r?$",
            RegexOptions.Multiline);
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    [Fact]
    public void DB疎通チェックの打ち切りは3秒()
    {
        HealthCheckExtensions.NpgSqlReadinessTimeout.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void readinessProbeのtimeoutSecondsはDB疎通チェックの打ち切りより厳密に長い()
    {
        var probeTimeout = ReadinessValue("timeoutSeconds");

        probeTimeout.Should().NotBeNull("values.yaml の probes.readiness.timeoutSeconds を明示する（未指定は Kubernetes 既定 1 秒）");
        TimeSpan.FromSeconds(probeTimeout!.Value).Should().BeGreaterThan(HealthCheckExtensions.NpgSqlReadinessTimeout);
        probeTimeout.Should().Be(5);
    }

    [Fact]
    public void readinessProbeの回数と間隔は従来の値のまま()
    {
        // 変えるのは timeoutSeconds だけ（#1137 の射程）。起動時の migration を待つ 30 回 × 10 秒は据え置く。
        ReadinessValue("initialDelaySeconds").Should().Be(10);
        ReadinessValue("periodSeconds").Should().Be(10);
        ReadinessValue("failureThreshold").Should().Be(30);
    }

    [Fact]
    public void 共通テンプレートのreadinessProbeはvaluesのprobes_readinessを読む()
    {
        var template = ChartFile(Path.Combine("templates", "deployment.yaml"));

        template.Should().Contain("timeoutSeconds: {{ $ready.timeoutSeconds }}");
        template.Should().Contain("{{- $ready := $.Values.probes.readiness }}");
    }

    [Fact]
    public void AddNpgSqlを呼ぶ全サービスが共通の打ち切りを渡している()
    {
        // 母集合は実ツリーの走査（一覧を手で書かない）。新しいサービスが打ち切り無しで足されたら落ちる。
        var programs = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "backend", "Services"), "Program.cs", SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal)
                && !p.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal))
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .Where(f => f.Text.Contains(".AddNpgSql(", StringComparison.Ordinal))
            .ToList();

        // 着手時点の実測は 7（audit / configuration / cost-control / market-monitor / order-execution / report / risk-management）。
        programs.Should().HaveCount(7);
        foreach (var (path, text) in programs)
        {
            var calls = Regex.Matches(text, @"\.AddNpgSql\([^;]*;");
            calls.Should().NotBeEmpty();
            foreach (Match call in calls)
            {
                call.Value.Should().Contain(
                    "timeout: HealthCheckExtensions.NpgSqlReadinessTimeout",
                    $"{Path.GetRelativePath(RepoRoot(), path)} の AddNpgSql は共通の打ち切りを渡す");
            }
        }
    }
}
