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
// 見るのは 4 点である。
//   1. 欄は束縛できる（int / bool の欄を "" にすると束縛で例外になり、常駐が落ちる。それを作らない）。
//   2. 🔴 否定形: 書いたままの構成では記録しない（Enabled=false・未承認。LLM を 1 回も呼ばない）。
//   3. 🔴 ［2026-10-13 改 / #1308］経路B の配線の欄で as-of 入力の基底の供給が選ばれ、欄を埋めて承認すれば記録が作られる
//      （#1308 より前は「基底が無いので記録 0 件・LLM 0 回」を固定していた。基底が入ったので記録が作られる側へ書き換えた）。
//   4. 🔴 本番（values.yaml）の取引判断には記録の欄を足していない（本番の挙動を変えない）。
public class Stage0RecordingLocalProfileTests
{
    private const string LocalValues = "deploy/helm/ai-stock-trading/values-local.yaml";
    private const string ProductionValues = "deploy/helm/ai-stock-trading/values.yaml";

    // 単価は values-local の 5.5 系の行と同じ値（円/1k）。
    private static LlmPriceTable Prices() =>
        LlmPriceTable.From([("claude-sonnet-5-5", "0.327", "1.637"), ("claude-haiku-5-5", "0.0164", "0.0819")]);

    [Fact]
    public void 経路Bの記録の欄は束縛でき_書いたままでは記録しない構成である()
    {
        var options = Bind(Env(LocalValues, "trade-decision"));

        options.Enabled.Should().BeFalse("既定は記録しない（PoC が承認後に true にする）");
        options.ApprovedVoteCount.Should().BeNull("承認回数は空＝未承認");
        options.ApprovedEstimateJpy.Should().BeNull("承認額は空＝未承認");
        options.ParsePeriod().Should().BeNull("期間は PoC が埋める");
        options.OutputPath.Should().BeNullOrEmpty("書き出し先は PoC が埋める");
        options.InputTokensPerDecision.Should().Be(0);
        options.OutputTokensPerDecision.Should().Be(0);
        options.ScreeningInputTokensPerDecision.Should().Be(0);
        options.ScreeningOutputTokensPerDecision.Should().Be(0);
        options.VoteCount.Should().Be(1);
        // ADR-0064 決定1・決定3: 本判断の層は 5.5 系のピン。カットオフは 5.5 系の 2026-06-30。
        options.Model.Should().Be("claude-sonnet-5-5");
        options.ScreeningModel.Should().BeNull("一次は割当表のピンに委ねる（本番と同じ扱い）");
        options.ParseLlmTrainingCutoff().Should().Be(new DateOnly(2026, 6, 30));
        options.ResolveSymbols().Should().Equal([("AAPL", Market.UnitedStates)]);
    }

    [Fact]
    public async Task 経路Bの構成のまま走らせても_LLM_を_1_回も呼ばない()
    {
        var llm = new CountingLlm();
        var sink = new CapturingSink();
        var recorder = Recorder(llm, sink);

        var outcome = await recorder.RunAsync(Bind(Env(LocalValues, "trade-decision")), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Disabled);
        outcome.CalledLlm.Should().BeFalse();
        llm.Calls.Should().Be(0);
        sink.Saved.Should().BeNull("記録しないなら書き出しもしない");
    }

    // 🔴 #1308, IADR-0528 決定7: 経路B の trade-decision の配線の欄（Reports__BaseUrl・OrderExecution__BaseUrl）で基底の供給が選ばれる。
    // 欄を埋めて承認すれば、基底の供給が組んだ入力で記録が作られ、LLM が呼ばれる（#1308 より前は 0 件・0 回だった）。
    // 🔴 配備の注意: Stage0Recording__Enabled=true・承認済みの values のまま #1308 の入った版を配備すると、起動した時点で記録が LLM を呼ぶ。
    [Fact]
    public async Task 経路Bの配線の欄で基底の供給が選ばれ_欄を埋めて承認すれば記録が作られる()
    {
        var env = Env(LocalValues, "trade-decision");
        TradeDecisionService.Infrastructure.ExternalServices.Stage0AsOfComposition
            .HasBaseEndpoints(Configuration(env)).Should().BeTrue("経路B は方針と日足の接続先を両方持つ");

        env["Stage0Recording__Enabled"] = "true";
        env["Stage0Recording__From"] = "2026-09-15";
        env["Stage0Recording__To"] = "2026-09-16";
        env["Stage0Recording__OutputPath"] = "/tmp/stage0/records.json";
        env["Stage0Recording__InputTokensPerDecision"] = "6000";
        env["Stage0Recording__OutputTokensPerDecision"] = "800";
        env["Stage0Recording__ScreeningInputTokensPerDecision"] = "4000";
        env["Stage0Recording__ScreeningOutputTokensPerDecision"] = "300";
        env["Stage0Recording__ApprovedVoteCount"] = "1";
        foreach (var key in env.Keys.Where(k => k.StartsWith("Stage0Recording__Symbols__", StringComparison.Ordinal)).ToList())
            env.Remove(key);
        env["Stage0Recording__Symbols__0__Symbol"] = "AAPL";
        env["Stage0Recording__Symbols__0__Market"] = "UnitedStates";
        var llm = new CountingLlm();
        var sink = new CapturingSink();
        var recorder = Recorder(llm, sink, new BaseAsOfDecisionInputProvider(
            BaseAsOfDecisionInputProviderTests.FakePrices.FollowingAsOf(close: 110m, previousClose: 100m),
            BaseAsOfDecisionInputProviderTests.FakePolicies.PreviousDay("前日に確定した方針"),
            new AsOfReferenceAssembler(BaseAsOfDecisionInputProviderTests.AllFeeds(), topK: 5, TimeSpan.FromHours(168)),
            NullLogger<BaseAsOfDecisionInputProvider>.Instance));
        var estimate = recorder.Estimate(Bind(env));
        env["Stage0Recording__ApprovedEstimateJpy"] = estimate.TotalJpy.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var outcome = await recorder.RunAsync(Bind(env), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        outcome.RecordSet!.Records.Should().HaveCount(2, "AAPL × 2026-09-15・09-16");
        llm.Calls.Should().Be(2, "一次が Hold なので本判断は呼ばない（判断時点ごとに 1 回）");
    }

    private static IConfiguration Configuration(Dictionary<string, string> env) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(env.Select(kv =>
                new KeyValuePair<string, string?>(kv.Key.Replace("__", ":", StringComparison.Ordinal), kv.Value)))
            .Build();

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

    private static Stage0DecisionRecorder Recorder(CountingLlm llm, CapturingSink sink, IAsOfDecisionInputProvider? inputs = null) =>
        new(
            llm,
            DecisionOrchestrationOptions.Default,
            // 既定は「入力なし」（基底の供給の口がそろわない構成と同じ）。#1308 の試験は基底の供給を渡す。
            inputs ?? new NoAsOfDecisionInputProvider(),
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
