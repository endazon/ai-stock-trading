using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Tests;

// 🔴 NFR-06, FR-14, #1554, IADR-0622: ボットの許可表（OwnerBotRoutes）の母集合は、ボットの REST アダプタが呼ぶ経路そのものである。
// アダプタ（Infrastructure/ExternalServices/Http*Controller.cs）の源泉から「/ で始まる文字列リテラル」を全部引き、許可表の経路と**集合で一致**を求める。
// - ボットの経路を足して許可表に足さない → 赤（段 2 でボットの操作が 403 になる前に止める）。
// - 許可表に残った古い経路 → 赤（ボットが使わない経路をボットに開けたままにしない）。
// 提供側の印（AllowOwnerBot）と許可表の一致は、各提供側の試験（OwnerBotRoutesMarkedTests）が確かめる。
public partial class OwnerBotRouteCoverageTests
{
    private static string AdaptersDirectory() =>
        Path.Combine(BotReadGrpcWiringTests.RepoRoot(), "backend", "Services", "NotificationService", "Infrastructure", "ExternalServices");

    // 通常の文字列リテラルと補間の文字列リテラルのうち、/ で始まるもの（補間の穴の中身は経路の引数として扱う）。
    [GeneratedRegex(@"\$?""(/[A-Za-z0-9_\-/{}().?=&:]*)""")]
    private static partial Regex PathLiteral();

    private static IReadOnlySet<string> AdapterPaths()
    {
        var files = Directory.GetFiles(AdaptersDirectory(), "Http*Controller.cs");
        files.Should().NotBeEmpty("ボットの REST アダプタが見つかる（空どうしの一致は何も証明しない）");

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;
                foreach (Match m in PathLiteral().Matches(line))
                    paths.Add(OwnerBotRoutes.Normalize(m.Groups[1].Value));
            }
        }

        return paths;
    }

    [Fact]
    public void ボットのアダプタが呼ぶ経路と許可表は集合で一致する()
    {
        var adapter = AdapterPaths();
        var table = OwnerBotRoutes.All.Select(r => r.NormalizedTemplate).ToHashSet(StringComparer.Ordinal);

        adapter.Except(table).Should().BeEmpty("ボットのアダプタが呼ぶ経路はすべて許可表に在る（漏れは段 2 で 403 になる）");
        table.Except(adapter).Should().BeEmpty("許可表の経路はすべてボットのアダプタが呼ぶ（使わない経路を開けたままにしない）");
    }

    // 引き方そのものの確かめ（既知の経路が引けている）。
    [Fact]
    public void 引き方は補間の経路と定数の経路の両方を引く()
    {
        var adapter = AdapterPaths();

        adapter.Should().Contain("/reports/{}/confirm", "補間の経路（期間キー）");
        adapter.Should().Contain("/reports/{}/regenerate", "補間の穴に式がある経路");
        adapter.Should().Contain("/monitor/watchlist", "定数で持つ経路");
        adapter.Should().Contain("/risk-controls/kill-switch/engage");
    }

    // 印を付けてよいのは許可表の経路を持つ 3 つの提供側だけ（各提供側の試験が「印＝許可表の分」を確かめる。それ以外のサービスに印が
    // 紛れ込むと、どの試験もそれを許可表と比べないため、ここで源泉から止める）。
    [Fact]
    public void 印は許可表の経路を持つ提供側の源泉にだけ現れ_数は許可表と同じ()
    {
        var services = Path.Combine(BotReadGrpcWiringTests.RepoRoot(), "backend", "Services");
        var sep = Path.DirectorySeparatorChar;
        var marks = Directory.GetFiles(services, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}Tests{sep}", StringComparison.Ordinal)
                && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f)
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && l.Contains(".AllowOwnerBot()", StringComparison.Ordinal))
                .Select(_ => Path.GetRelativePath(services, f).Split(sep)[0]))
            .ToList();

        marks.Distinct().Should().BeEquivalentTo(["RiskManagementService", "ReportService", "MarketMonitorService"]);
        marks.Should().HaveCount(OwnerBotRoutes.All.Count);
    }
}
