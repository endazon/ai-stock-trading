using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Xunit;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// FR-15, FR-04, NFR（費用）, ADR-0033 決定5, ADR-0054 決定3, ADR-0064 決定3, #1219, #1308:
// 経路B（values-local.yaml）の Stage 0 の記録の欄を、**配備で実際に読まれる env の形のまま**束縛して検査する。
//
// 見るのは 5 点である。
//   1. 欄は束縛でき、オーナー承認（2026-10-13・#1219）の値で埋まっている（int / bool の欄を "" にすると束縛で例外になる）。
//   2. 承認値の組の見積りはオーナーの承認額 1,700 円に（超過の上限込みで）収まり、書いた承認額は見積りと一致する。
//   3. 🔴 承認ゲートは「≥」ではなく一致で見る。1,700 をそのまま書けば NotApproved で LLM を呼ばない。
//   4. 🔴 経路B の構成のまま走らせると承認ゲートは通るが、as-of 入力の基底の供給（#1308）が無いあいだは記録 0 件・LLM 0 回で終わる。
//   5. 🔴 本番（values.yaml）の取引判断には記録の欄を足していない（本番の挙動を変えない）。
public class Stage0RecordingLocalProfileTests
{
    private const string LocalValues = "deploy/helm/ai-stock-trading/values-local.yaml";
    private const string ProductionValues = "deploy/helm/ai-stock-trading/values.yaml";

    // #1219: オーナー承認（2026-10-13）の承認額。見積りはこれ以下でなければならない。
    private const decimal OwnerApprovedBudgetJpy = 1700m;

    // 単価は values-local の 5.5 系の行と同じ値（円/1k）。
    private static LlmPriceTable Prices() =>
        LlmPriceTable.From([("claude-sonnet-5-5", "0.327", "1.637"), ("claude-haiku-5-5", "0.0164", "0.0819")]);

    private static readonly string[] ApprovedSymbols =
        ["TSLA", "META", "GOOGL", "AMZN", "AAPL", "SMCI", "PLTR", "NVDA", "COIN", "MSTR", "MARA", "MSFT"];

    // FR-15, ADR-0033 決定5, #1219: オーナー承認（2026-10-13）の値。
    [Fact]
    public void 経路Bの記録の欄は承認値で埋まっている()
    {
        var options = Bind(Env(LocalValues, "trade-decision"));

        options.Enabled.Should().BeTrue();
        options.ParsePeriod().Should().Be((new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 9)));
        options.ResolveSymbols().Should().Equal(ApprovedSymbols.Select(s => (s, Market.UnitedStates)));
        options.VoteCount.Should().Be(1);
        options.ApprovedVoteCount.Should().Be(1);
        options.ApprovedEstimateJpy.Should().Be(1680.09m);
        options.InputTokensPerDecision.Should().Be(3478);
        options.OutputTokensPerDecision.Should().Be(425);
        options.ScreeningInputTokensPerDecision.Should().Be(2269);
        options.ScreeningOutputTokensPerDecision.Should().Be(582);
        options.OutputPath.Should().Be("/tmp/stage0/records.json");
        // ADR-0064 決定1・決定3: 本判断の層は 5.5 系のピン。カットオフは 5.5 系の 2026-06-30。
        options.Model.Should().Be("claude-sonnet-5-5");
        options.ScreeningModel.Should().BeNull("一次は割当表のピンに委ねる（本番と同じ扱い）");
        options.ParseLlmTrainingCutoff().Should().Be(new DateOnly(2026, 6, 30));
        // 月報 §7 の分母は取引判断側と同値（ADR-0037 決定3）。
        Env(LocalValues, "report").Should().Contain("Stage0Recording__ApprovedEstimateJpy", "1680.09");
    }

    // ADR-0033 決定5, #1219: 12 銘柄 × 平日 73 日 × (一次 1 ＋ 多数決 1)。実績は見積りを最大 1 判断ぶん上回り得るため、
    // その上限を足してもオーナーの承認額に収まることを見る。書いた承認額は見積りを円未満 2 桁で写した値である。
    [Fact]
    public void 承認値の組の見積りはオーナーの承認額_1700_円に収まる()
    {
        var options = Bind(Env(LocalValues, "trade-decision"));
        var recorder = Recorder(new CountingLlm(), new CapturingSink());

        var estimate = recorder.Estimate(options);

        Stage0RecordingBudget.WeekdayCount(new DateOnly(2026, 7, 1), new DateOnly(2026, 10, 9)).Should().Be(73);
        estimate.CallCount.Should().Be(1752, "12 銘柄 × 平日 73 日 ×（一次 1 ＋ 多数決 1）");
        estimate.TotalJpy.Should().Be(1680.0877584m);
        var perDecisionJpy = estimate.TotalJpy / (12 * 73);
        (estimate.TotalJpy + perDecisionJpy).Should().BeLessThanOrEqualTo(OwnerApprovedBudgetJpy);
        decimal.Round(options.ApprovedEstimateJpy!.Value, 2).Should().Be(decimal.Round(estimate.TotalJpy, 2));
    }

    // 🔴 #1308: 経路B の構成のまま（承認値入り）走らせると、承認ゲートは通る（NotApproved ではない）のに、
    // 本番の組み立て（Program.cs）の最も内側が NoAsOfDecisionInputProvider であるため、全判断時点が飛ばされる。
    // #1308 が基底の供給を入れたら、この試験は記録が作られる側へ書き換える。
    [Fact]
    public async Task 経路Bの承認値のまま走らせると承認ゲートを通り_as_of_の基底が無ければ記録は_0_件()
    {
        var llm = new CountingLlm();
        var sink = new CapturingSink();
        var recorder = Recorder(llm, sink);

        var outcome = await recorder.RunAsync(Bind(Env(LocalValues, "trade-decision")), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed, "承認ゲートは通っている");
        outcome.RecordSet!.Records.Should().BeEmpty();
        outcome.ActualCostJpy.Should().Be(0m);
        llm.Calls.Should().Be(0);
        sink.Saved.Should().NotBeNull("0 件でも記録集合は書き出す");
    }

    // 🔴 否定形: 承認ゲートは「承認額 ≥ 見積り」ではなく一致で見る。オーナーの承認額 1,700 円をそのまま書くと
    // NotApproved で LLM を 1 回も呼ばない（values-local に 1680.09 を写した理由）。
    [Fact]
    public async Task 承認額に_1700_をそのまま入れると承認ゲートを通らない()
    {
        var env = Env(LocalValues, "trade-decision");
        env["Stage0Recording__ApprovedEstimateJpy"] = "1700";
        var llm = new CountingLlm();
        var sink = new CapturingSink();

        var outcome = await Recorder(llm, sink).RunAsync(Bind(env), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.NotApproved);
        llm.Calls.Should().Be(0);
        sink.Saved.Should().BeNull();
    }

    // 記録の対象銘柄と backtest の評価銘柄がずれると、再生側が RecordingMismatch で判定を組まない。
    [Fact]
    public void 経路Bの記録の対象銘柄と_backtest_の評価銘柄は同じ集合である()
    {
        static string[] Symbols(Dictionary<string, string> env, string prefix) =>
            [.. env.Where(kv => Regex.IsMatch(kv.Key, $@"^{prefix}__Symbols__\d+__(Symbol|Market)$"))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key[(prefix.Length + "__Symbols__".Length)..] + "=" + kv.Value)];

        var recording = Symbols(Env(LocalValues, "trade-decision"), "Stage0Recording");
        var evaluation = Symbols(Env(LocalValues, "backtest"), "Backtest__Stage0");

        recording.Should().NotBeEmpty();
        recording.Should().Equal(evaluation);
    }

    // backtest に足した 2 欄は、コードの既定（Stage0EvaluationOptions: LookbackDays 365・IntervalSeconds 86,400）と同値である
    // （＝PoC が値を変えるまで評価の挙動は変わらない）。
    [Fact]
    public void 経路Bの_backtest_の窓と巡回間隔はコードの既定と同値である()
    {
        var env = Env(LocalValues, "backtest");

        env.Should().Contain("Backtest__Stage0__LookbackDays", "365");
        env.Should().Contain("Backtest__Stage0__IntervalSeconds", "86400");
    }

    // 🔴 本番の取引判断には記録の欄を足していない（カットオフと承認額の 2 つだけ。いずれも従来から在る）。
    [Fact]
    public void 本番の取引判断の記録の欄は従来のままで_記録は無効である()
    {
        var env = Env(ProductionValues, "trade-decision");

        env.Keys.Where(k => k.StartsWith("Stage0Recording__", StringComparison.Ordinal))
            .Should().BeEquivalentTo(["Stage0Recording__LlmTrainingCutoff", "Stage0Recording__ApprovedEstimateJpy"]);
        Bind(env).Enabled.Should().BeFalse();
    }

    private static Stage0DecisionRecorder Recorder(CountingLlm llm, CapturingSink sink) =>
        new(
            llm,
            DecisionOrchestrationOptions.Default,
            // Program.cs の組み立ての最も内側と同じ（#1308 が入るまでの基底の供給）。
            new NoAsOfDecisionInputProvider(),
            sink,
            new Stage0RecordingUsageCollector(new NullReporter()),
            Prices(),
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<Stage0DecisionRecorder>.Instance);

    // env 名（`A__B`）を構成キー（`A:B`）へ写して束縛する（配備で ASP.NET Core が env を読むのと同じ写し方）。
    private static Stage0RecordingOptions Bind(Dictionary<string, string> env)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(env.Select(kv =>
                new KeyValuePair<string, string?>(kv.Key.Replace("__", ":", StringComparison.Ordinal), kv.Value)))
            .Build();
        var options = new Stage0RecordingOptions();
        configuration.GetSection(Stage0RecordingOptions.SectionName).Bind(options);
        return options;
    }

    // values の `services.<name>.extraEnv` のうち `- { name: X, value: "Y" }` の行（コメント行と secretKeyRef は除く）。
    private static Dictionary<string, string> Env(string path, string service)
    {
        var block = ServiceBlock(File.ReadAllText(Path.Combine([RepoRoot(), .. path.Split('/')])), service);
        block.Should().NotBeEmpty($"{path} に {service} がある");
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

    private sealed class CountingLlm : ILlmCompletionClient
    {
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult("""{"action":"Hold","rationale":"試験"}""");
        }
    }

    private sealed class CapturingSink : IStage0DecisionRecordSink
    {
        public Stage0DecisionRecordSet? Saved { get; private set; }

        public Task<bool> SaveAsync(Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default)
        {
            Saved = recordSet;
            return Task.FromResult(true);
        }
    }

    private sealed class NullReporter : ILlmUsageReporter
    {
        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
