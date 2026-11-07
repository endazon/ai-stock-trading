using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 NFR-13, ADR-0074 決定2, IADR-0639: 月次 LLM 費用の停止を読めないときの段（Off / Report / Enforce）と猶予。
// Enforce は猶予を過ぎても読めなければ停止として扱う（fail-closed）。猶予の内は最後に読めた値を使う。Report は判断を止めない。
public class FailClosedLlmCostHaltGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 14, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // 読めた値（true / false）か、読めない（null）を順に返す偽の照会。
    private sealed class ScriptedReader(params bool?[] answers) : ILlmCostHaltReader
    {
        private int _i;

        public Task<bool?> ReadHaltedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(answers[Math.Min(_i++, answers.Length - 1)]);
    }

    private static (FailClosedLlmCostHaltGate Gate, ManualClock Clock) Gate(LlmCostHaltUnavailableMode mode, params bool?[] answers)
    {
        var clock = new ManualClock(T0);
        var gate = new FailClosedLlmCostHaltGate(
            new ScriptedReader(answers),
            new LlmCostHaltConfirmation(),
            new LlmCostHaltQueryOptions(mode, TimeSpan.FromMinutes(15)),
            clock,
            NullLogger<FailClosedLlmCostHaltGate>.Instance);
        return (gate, clock);
    }

    private static Task<bool> Ask(FailClosedLlmCostHaltGate gate) => gate.IsHaltedAsync(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(LlmCostHaltUnavailableMode.Report)]
    [InlineData(LlmCostHaltUnavailableMode.Enforce)]
    public async Task 読めた値はそのまま返す(LlmCostHaltUnavailableMode mode)
    {
        var (gate, _) = Gate(mode, false, true);

        (await Ask(gate)).Should().BeFalse();
        (await Ask(gate)).Should().BeTrue();
    }

    [Fact]
    public async Task Enforce_一度も読めていなければ停止として扱う()
    {
        var (gate, _) = Gate(LlmCostHaltUnavailableMode.Enforce, (bool?)null);

        (await Ask(gate)).Should().BeTrue("起動から一度も読めていない＝猶予の外（fail-closed）");
    }

    [Fact]
    public async Task Enforce_猶予の内は最後に読めた値を使い_猶予を過ぎたら停止として扱う()
    {
        var (gate, clock) = Gate(LlmCostHaltUnavailableMode.Enforce, false, null);

        (await Ask(gate)).Should().BeFalse();
        clock.Now = T0.AddMinutes(15);
        (await Ask(gate)).Should().BeFalse("猶予（15 分）の内は最後に読めた値（停止していない）");
        clock.Now = T0.AddMinutes(15).AddSeconds(1);
        (await Ask(gate)).Should().BeTrue("猶予を過ぎても読めない＝停止として扱う");
    }

    [Fact]
    public async Task Enforce_最後に読めた値が停止なら猶予の内も停止のまま()
    {
        var (gate, clock) = Gate(LlmCostHaltUnavailableMode.Enforce, true, null);

        (await Ask(gate)).Should().BeTrue();
        clock.Now = T0.AddMinutes(1);
        (await Ask(gate)).Should().BeTrue();
    }

    [Fact]
    public async Task Enforce_読めるようになれば戻る()
    {
        var (gate, clock) = Gate(LlmCostHaltUnavailableMode.Enforce, null, false);

        (await Ask(gate)).Should().BeTrue();
        clock.Now = T0.AddMinutes(1);
        (await Ask(gate)).Should().BeFalse();
    }

    // 陰性対照: Report は判定だけで止めない（倒す向きは Off と同じ）。
    [Fact]
    public async Task Report_猶予を過ぎても読めなくても判断を止めない()
    {
        var (gate, clock) = Gate(LlmCostHaltUnavailableMode.Report, false, null);

        (await Ask(gate)).Should().BeFalse();
        clock.Now = T0.AddHours(1);
        (await Ask(gate)).Should().BeFalse();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Theory]
    [InlineData(null, LlmCostHaltUnavailableMode.Off)]
    [InlineData("", LlmCostHaltUnavailableMode.Off)]
    [InlineData("off", LlmCostHaltUnavailableMode.Off)]
    [InlineData("Report", LlmCostHaltUnavailableMode.Report)]
    [InlineData(" enforce ", LlmCostHaltUnavailableMode.Enforce)]
    public void 段は名前で読み既定はOffで猶予の既定は15分(string? raw, LlmCostHaltUnavailableMode expected)
    {
        var options = LlmCostHaltQueryOptions.FromConfiguration(Config((LlmCostHaltQueryOptions.ModeKey, raw)));

        options.Mode.Should().Be(expected);
        options.Grace.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("300", 300)]
    [InlineData("86400", 86400)]
    public void 猶予は0以上1日以下の秒(string raw, int seconds)
    {
        LlmCostHaltQueryOptions.FromConfiguration(Config((LlmCostHaltQueryOptions.GraceSecondsKey, raw)))
            .Grace.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData(LlmCostHaltQueryOptions.ModeKey, "2")]
    [InlineData(LlmCostHaltQueryOptions.ModeKey, "Block")]
    [InlineData(LlmCostHaltQueryOptions.GraceSecondsKey, "-1")]
    [InlineData(LlmCostHaltQueryOptions.GraceSecondsKey, "86401")]
    [InlineData(LlmCostHaltQueryOptions.GraceSecondsKey, "15m")]
    public void 読めない値は起動で止める(string key, string raw)
    {
        var act = () => LlmCostHaltQueryOptions.FromConfiguration(Config((key, raw)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }
}
