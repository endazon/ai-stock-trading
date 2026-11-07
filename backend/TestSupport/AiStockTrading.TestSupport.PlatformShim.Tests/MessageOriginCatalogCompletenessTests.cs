using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, FR-10, IADR-0627 決定 1・5: 発行元を確かめる型の表（MessageOriginCatalog）を、ハンドラの受ける型から引いた母集合と突き合わせる。
// 記憶や一覧の転記に頼らない —— 各サービスのハンドラ（*Handler / *Consumer の Handle 系のメソッド）が受ける型をソースから走査し、
// 「表の受け手」＋「理由つきの対象外」と一致しなければ赤にする（受け手に型を足して表を忘れると落ちる）。
public partial class MessageOriginCatalogCompletenessTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    [GeneratedRegex(@"\bpublic\s+(?:(?:sealed|static|partial)\s+)*(?:class|record)\s+(\w+(?:Handler|Consumer))\b[^{;]*\{")]
    private static partial Regex HandlerClass();

    [GeneratedRegex(@"\bpublic\s+(?:static\s+)?(?:async\s+)?[\w<>\[\]?,.\s()]*?\b(?:Handle|HandleAsync|Consume|ConsumeAsync)\s*\(\s*([\w.]+)\??\s+\w+")]
    private static partial Regex HandlerMethod();

    [GeneratedRegex(@"\bconst\s+string\s+ServiceName\s*=\s*""([^""]+)""")]
    private static partial Regex ServiceNameConst();

    // サービスの識別子 → そのサービスのハンドラが受ける型の短い名前。
    private static Dictionary<string, SortedSet<string>> HandledTypes()
    {
        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var dir in Directory.GetDirectories(Path.Combine(RepoRoot(), "backend", "Services")))
        {
            var program = Path.Combine(dir, "Program.cs");
            if (!File.Exists(program)) continue;
            var name = ServiceNameConst().Match(File.ReadAllText(program));
            if (!name.Success) continue;
            var issuer = MessageOriginCatalog.IssuerIdFor(name.Groups[1].Value);
            var types = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (rel.StartsWith("Tests/", StringComparison.Ordinal) || rel.Contains("/bin/") || rel.Contains("/obj/")
                    || rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal))
                    continue;
                var code = File.ReadAllText(file);
                foreach (Match cls in HandlerClass().Matches(code))
                {
                    var body = Body(code, cls.Index + cls.Length - 1);
                    foreach (Match m in HandlerMethod().Matches(body))
                        types.Add(m.Groups[1].Value.Split('.')[^1]);
                }
            }

            result[issuer] = types;
        }

        return result;
    }

    private static string Body(string code, int openAt)
    {
        var depth = 0;
        for (var i = openAt; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[openAt..(i + 1)];
        }

        return code[openAt..];
    }

    [Fact]
    public void 走査の母集合は空でない()
    {
        var handled = HandledTypes();

        handled.Keys.Should().Contain([MessageOriginCatalog.RiskManagement, MessageOriginCatalog.OrderExecution, "audit", "notification"]);
        handled[MessageOriginCatalog.RiskManagement].Should().Contain(nameof(TradeDecisionMade));
        handled[MessageOriginCatalog.OrderExecution].Should().Contain(nameof(StopLossTriggered));
    }

    [Fact]
    public void 統制に使う受け手の受ける型は表か理由つきの対象外のどちらかにある()
    {
        // 対象外のサービスと、記録だけの受け手（表の型をすべて確かめる。IADR-0633 決定 5）は突き合わせの外。
        var excludedServices = MessageOriginCatalog.ExcludedServices.Select(e => e.Service)
            .Concat(MessageOriginCatalog.RecordOnlyServices.Select(e => e.Service)).ToHashSet(StringComparer.Ordinal);
        var catalogTypes = MessageOriginCatalog.Rules.Select(r => r.MessageType.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (service, types) in HandledTypes())
        {
            if (excludedServices.Contains(service)) continue;

            var expected = new SortedSet<string>(
                MessageOriginCatalog.VerifiedBy(service).Select(r => r.MessageType.Name)
                    .Concat(MessageOriginCatalog.ExcludedReceivers.Where(e => e.Service == service).Select(e => e.MessageType.Name)),
                StringComparer.Ordinal);
            // 表の外の型（サービス内だけで回すローカルの型など）は、表と対象外のどちらにも無く、契約のイベントでもないものに限る。
            var contractEvents = types.Where(t => typeof(TradeDecisionMade).Assembly.GetType($"AiStockTrading.Shared.Contracts.Events.{t}") is not null);
            // IADR-0638 決定 2: 表の外の型をまとめて確かめないサービス（通知）は、受ける表の型だけを突き合わせる。
            if (MessageOriginCatalog.ExternalTypesUnverifiedServices.Any(e => e.Service == service))
                contractEvents = contractEvents.Where(catalogTypes.Contains);

            new SortedSet<string>(contractEvents, StringComparer.Ordinal).Should().BeEquivalentTo(
                expected, $"{service} のハンドラが受ける契約の型は、表の受け手（確かめる）か理由つきの対象外のどちらかに載せる");
            catalogTypes.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void 表の発行元と受け手は実在するサービスで受け手は実際にその型を受ける()
    {
        var handled = HandledTypes();
        foreach (var rule in MessageOriginCatalog.Rules)
        {
            rule.Issuers.Should().NotBeEmpty();
            rule.Receivers.Should().NotBeEmpty();
            foreach (var issuer in rule.Issuers)
                handled.Keys.Should().Contain(issuer, $"{rule.MessageType.Name} の発行元");
            foreach (var receiver in rule.Receivers)
                handled[receiver].Should().Contain(rule.MessageType.Name, $"{receiver} は {rule.MessageType.Name} を受ける");
        }
    }

    // 発行元は、その型を実際に組み立てる（new）サービスである。組み立てるのに発行元でない統制側のサービスがあれば、印の無い発行になる。
    [Fact]
    public void 表の型を組み立てるサービスは表の発行元である()
    {
        var root = Path.Combine(RepoRoot(), "backend", "Services");
        // 組み立てても発行しないもの（理由）: 発注執行は保護レグの承認を自分の中で組み直して発注に使う（発行しない）。
        var notPublished = new HashSet<(string, string)> { (MessageOriginCatalog.OrderExecution, nameof(OrderApproved)) };
        foreach (var dir in Directory.GetDirectories(root))
        {
            var program = Path.Combine(dir, "Program.cs");
            if (!File.Exists(program)) continue;
            var name = ServiceNameConst().Match(File.ReadAllText(program));
            if (!name.Success) continue;
            var service = MessageOriginCatalog.IssuerIdFor(name.Groups[1].Value);
            var code = string.Join('\n', Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(dir, f).Replace('\\', '/').StartsWith("Tests/", StringComparison.Ordinal))
                .Select(File.ReadAllText));
            foreach (var rule in MessageOriginCatalog.Rules)
            {
                var builds = Regex.IsMatch(code, $@"\bnew\s+{rule.MessageType.Name}\s*\(");
                if (builds && !notPublished.Contains((service, rule.MessageType.Name)))
                    rule.Issuers.Should().Contain(service, $"{service} は {rule.MessageType.Name} を組み立てる（発行するなら表の発行元に載せる）");
            }
        }
    }

    // 全サービスの Program.cs は、共通の配線の直後に発行元の確かめの配線を呼ぶ（呼び忘れると印も確かめも無い）。
    [Fact]
    public void ブローカへ繋ぐ全サービスは発行元の確かめを配線する()
    {
        var programs = Directory.GetDirectories(Path.Combine(RepoRoot(), "backend", "Services"))
            .Select(d => Path.Combine(d, "Program.cs"))
            .Where(File.Exists)
            .Where(p => File.ReadAllText(p).Contains("UseAiStockTradingRabbitMq(", StringComparison.Ordinal))
            .ToList();

        programs.Should().HaveCountGreaterThanOrEqualTo(11);
        programs.Should().AllSatisfy(p => File.ReadAllText(p).Should().Contain(
            "opts.UseAiStockTradingMessageOrigin(ServiceName, builder.Configuration);", Path.GetRelativePath(RepoRoot(), p)));
    }

    [Fact]
    public void 対象外には理由がある()
    {
        MessageOriginCatalog.ExcludedReceivers.Should().AllSatisfy(e => e.Reason.Should().NotBeNullOrWhiteSpace());
        MessageOriginCatalog.ExcludedServices.Should().AllSatisfy(e => e.Reason.Should().NotBeNullOrWhiteSpace());
        MessageOriginCatalog.RecordOnlyServices.Should().AllSatisfy(e => e.Reason.Should().NotBeNullOrWhiteSpace());
        MessageOriginCatalog.ExternalTypesUnverifiedServices.Should().AllSatisfy(e => e.Reason.Should().NotBeNullOrWhiteSpace());
        MessageOriginCatalog.Rules.Select(r => r.MessageType).Should().OnlyHaveUniqueItems();
    }

    // 🔴 IADR-0633 決定 5: 監査は記録だけの受け手で、受ける表の型があること（走査の母集合が空なら意味が無い）。
    [Fact]
    public void 記録だけの受け手は表の型を受ける()
    {
        var handled = HandledTypes();
        var catalog = MessageOriginCatalog.Rules.Select(r => r.MessageType.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (service, _) in MessageOriginCatalog.RecordOnlyServices)
        {
            MessageOriginCatalog.ExcludedServices.Select(e => e.Service).Should().NotContain(service);
            handled[service].Where(catalog.Contains).Should().NotBeEmpty($"{service} は表の型を受ける");
        }
    }

    // 🔴 NFR, FR-09, IADR-0638 決定 2: 通知は受ける表の型をすべて確かめる（偽の約定・損切り到達・保護レグの事象を利用者へ知らせない）。
    // 陰性対照: 表の行から Notification を 1 つ外すと、上の突き合わせとここが赤になる。
    [Fact]
    public void 通知は受ける表の型をすべて確かめる()
    {
        var handled = HandledTypes()["notification"];
        var catalog = MessageOriginCatalog.Rules.Select(r => r.MessageType.Name).ToHashSet(StringComparer.Ordinal);
        var received = handled.Where(catalog.Contains).ToList();

        received.Should().Contain([nameof(OrderExecuted), nameof(StopLossTriggered), nameof(PositionDriftAdopted)]);
        MessageOriginCatalog.VerifiedBy(MessageOriginCatalog.Notification).Select(r => r.MessageType.Name)
            .Should().BeEquivalentTo(received);
        MessageOriginCatalog.ExcludedServices.Select(e => e.Service).Should().NotContain(MessageOriginCatalog.Notification);
        MessageOriginCatalog.IsRecordOnly(MessageOriginCatalog.Notification).Should().BeFalse("通知は段 2 で確かめられないものを知らせない");
    }
}
