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
// 見るのは 6 点である。
//   1. 欄は束縛でき、オーナー承認（2026-10-13・#1219）の値で埋まっている（int / bool の欄を "" にすると束縛で例外になる）。
//   2. 承認値の組の見積りはオーナーの承認額 1,700 円に（超過の上限込みで）収まり、書いた承認額は見積りと一致する。
//   3. 🔴 承認値の組のまま、as-of 入力の基底の供給（#1308。外部の口は試験用のスタブ）の下で走らせると承認ゲートを通る。
//   4. 🔴 承認ゲートは「≥」ではなく一致で見る。1,700 をそのまま書けば NotApproved で LLM を呼ばない。
//   5. 🔴 ［#1308］経路B の配線の欄で as-of 入力の基底の供給が選ばれ、欄を埋めて承認すれば記録が作られる。
//   6. 🔴 本番（values.yaml）の取引判断には記録の欄を足していない（本番の挙動を変えない）。
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
        // 窓はオーナーの裁定（2026-10-13）で 2026-07-01 起点から 2026-09-15 起点へ縮めた（〜09-14 は as-of 入力を再構成できない）。
        options.ParsePeriod().Should().Be((new DateOnly(2026, 9, 15), new DateOnly(2026, 10, 9)));
        options.ResolveSymbols().Should().Equal(ApprovedSymbols.Select(s => (s, Market.UnitedStates)));
        options.VoteCount.Should().Be(1);
        options.ApprovedVoteCount.Should().Be(1);
        options.ApprovedEstimateJpy.Should().Be(437.28m);
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
        Env(LocalValues, "report").Should().Contain("Stage0Recording__ApprovedEstimateJpy", "437.28");
    }

    // ADR-0033 決定5, #1219: 12 銘柄 × 平日 19 日 × (一次 1 ＋ 多数決 1)。実績は見積りを最大 1 判断ぶん上回り得るため、
    // その上限を足してもオーナーの承認額に収まることを見る。書いた承認額は見積りを円未満 2 桁で写した値である。
    [Fact]
    public void 承認値の組の見積りはオーナーの承認額_1700_円に収まる()
    {
        var options = Bind(Env(LocalValues, "trade-decision"));
        var recorder = Recorder(new CountingLlm(), new CapturingSink());

        var estimate = recorder.Estimate(options);

        Stage0RecordingBudget.WeekdayCount(new DateOnly(2026, 9, 15), new DateOnly(2026, 10, 9)).Should().Be(19);
        estimate.CallCount.Should().Be(456, "12 銘柄 × 平日 19 日 ×（一次 1 ＋ 多数決 1）");
        estimate.TotalJpy.Should().Be(437.2831152m);
        var perDecisionJpy = estimate.TotalJpy / (12 * 19);
        (estimate.TotalJpy + perDecisionJpy).Should().BeLessThanOrEqualTo(OwnerApprovedBudgetJpy);
        decimal.Round(options.ApprovedEstimateJpy!.Value, 2).Should().Be(decimal.Round(estimate.TotalJpy, 2));
    }

    // 🔴 #1219, #1308: 経路B の承認値の組のまま（銘柄・期間・トークン量・承認値を書き換えずに）走らせると、承認ゲートを通る。
    // 基底の供給（#1308）の外部の口（日足・確定日報の方針・参考情報の源）は試験用のスタブである。見るのはゲートを通ることと、
    // 見積りが書いた承認額 437.28 円と一致することだけ（記録の中身は #1308 の試験が見る）。
    [Fact]
    public async Task 経路Bの承認値の組のまま走らせると承認ゲートを通る()
    {
        var options = Bind(Env(LocalValues, "trade-decision"));
        var llm = new CountingLlm();
        var sink = new CapturingSink();
        var recorder = Recorder(llm, sink, StubBaseInputs());

        recorder.Estimate(options).TotalJpy.Should().Be(437.2831152m);
        decimal.Round(recorder.Estimate(options).TotalJpy, 2).Should().Be(options.ApprovedEstimateJpy);

        var outcome = await recorder.RunAsync(options, TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed, "承認値は見積りと一致し、ゲートを通る");
    }

    // 🔴 否定形: 承認ゲートは「承認額 ≥ 見積り」ではなく一致で見る。オーナーの承認額 1,700 円をそのまま書くと
    // NotApproved で LLM を 1 回も呼ばない（values-local に見積り 437.28 を写した理由）。
    [Fact]
    public async Task 承認額に_1700_をそのまま入れると承認ゲートを通らない()
    {
        var env = Env(LocalValues, "trade-decision");
        env["Stage0Recording__ApprovedEstimateJpy"] = "1700";
        var llm = new CountingLlm();
        var sink = new CapturingSink();

        var outcome = await Recorder(llm, sink, StubBaseInputs()).RunAsync(Bind(env), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.NotApproved);
        llm.Calls.Should().Be(0);
        sink.Saved.Should().BeNull();
    }

    // 基底の供給（#1308）。外部の口は試験用のスタブ（BaseAsOfDecisionInputProviderTests の部品）。
    private static BaseAsOfDecisionInputProvider StubBaseInputs() =>
        new(
            BaseAsOfDecisionInputProviderTests.FakePrices.FollowingAsOf(close: 110m, previousClose: 100m),
            BaseAsOfDecisionInputProviderTests.FakePolicies.PreviousDay("前日に確定した方針"),
            new AsOfReferenceAssembler(BaseAsOfDecisionInputProviderTests.AllFeeds(), topK: 5, TimeSpan.FromHours(168)),
            NullLogger<BaseAsOfDecisionInputProvider>.Instance);

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

    // #1219, #1308 / IADR-0528 決定9: backtest の評価の窓 [EvaluationEnd − LookbackDays, EvaluationEnd] は記録の窓と一致する
    // （記録の From ≤ 窓の起点・記録の To ≥ 窓の終端。ずれると RecordingMismatch）。巡回間隔はコードの既定のまま。
    [Fact]
    public void 経路Bの_backtest_の評価の窓は記録の窓と一致する()
    {
        var backtest = Env(LocalValues, "backtest");
        var recording = Bind(Env(LocalValues, "trade-decision")).ParsePeriod()!.Value;

        var end = DateOnly.ParseExact(backtest["Backtest__Stage0__EvaluationEnd"], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var from = end.AddDays(-int.Parse(backtest["Backtest__Stage0__LookbackDays"], System.Globalization.CultureInfo.InvariantCulture));

        (from, end).Should().Be(recording);
        backtest.Should().Contain("Backtest__Stage0__IntervalSeconds", "86400");
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
