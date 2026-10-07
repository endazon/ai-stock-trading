using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-10, FR-20, ADR-0040 決定1, #1048（利用者裁定 2026-10-02・Q1）, IADR-0481 決定4: 設定上の発注先が実際の発注先と揃っていることを
// 確かめる手段（口座照会の観測のたびの判定と警告）。設定値は書き換えない（揃えるのは利用者の操作）。
// 作業仕様書 20261002_1048_same-symbol-method-coexistence-and-fill-tracking §受け入れ基準 12〜14。
public class BrokerProviderAlignmentTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 2, 13, 30, 0, TimeSpan.Zero);

    private sealed class RecordingLogger : ILogger<BrokerProviderAlignmentHandler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ThrowingSettings : IRiskSettingsStore
    {
        public RiskManagementSettings GetCurrent() => throw new InvalidOperationException("DB 障害（テスト）");

        public void Save(RiskManagementSettings settings) => throw new InvalidOperationException();

        public long GetProductTypesRevision() => throw new InvalidOperationException();
    }

    private static BrokerAccountObserved Observation(BrokerProvider actual) =>
        new(actual, new BrokerAccountState(AccountType.Margin, 1_000m), Observed);

    // T-10-2116: 受け入れ基準 12。純関数。同じなら null、違えば食い違いを返し、実際が実弾のときだけ重い側を立てる（3 × 3 の全組み合わせ）。
    [Theory]
    [InlineData(BrokerProvider.InternalPaper, BrokerProvider.InternalPaper, false, false)]
    [InlineData(BrokerProvider.MoomooSimulate, BrokerProvider.MoomooSimulate, false, false)]
    [InlineData(BrokerProvider.MoomooReal, BrokerProvider.MoomooReal, false, false)]
    [InlineData(BrokerProvider.InternalPaper, BrokerProvider.MoomooSimulate, true, false)]
    [InlineData(BrokerProvider.MoomooSimulate, BrokerProvider.InternalPaper, true, false)]
    [InlineData(BrokerProvider.MoomooReal, BrokerProvider.MoomooSimulate, true, false)]
    [InlineData(BrokerProvider.InternalPaper, BrokerProvider.MoomooReal, true, true)]
    [InlineData(BrokerProvider.MoomooSimulate, BrokerProvider.MoomooReal, true, true)]
    [InlineData(BrokerProvider.MoomooReal, BrokerProvider.InternalPaper, true, false)]
    public void T_10_2116_設定上の発注先と実際の発注先の食い違いの判定(
        BrokerProvider configured, BrokerProvider actual, bool misaligned, bool actualIsLive)
    {
        var result = BrokerProviderAlignment.Check(configured, actual);

        if (!misaligned)
        {
            result.Should().BeNull();
            return;
        }

        result.Should().Be(new BrokerProviderMisalignment(configured, actual, actualIsLive));
    }

    // T-10-2117: 受け入れ基準 13。観測のたびに確かめ、揃っていなければ（以前に観測された 内蔵 paper ／ moomoo SIMULATE の食い違い）
    // 揃える操作を名指しして知らせる。実際が実弾なら Error。揃っていれば 1 件も出さない。**設定値は書き換えない**。
    [Theory]
    [InlineData(BrokerProvider.InternalPaper, BrokerProvider.MoomooSimulate, LogLevel.Warning)]
    [InlineData(BrokerProvider.MoomooSimulate, BrokerProvider.MoomooReal, LogLevel.Error)]
    [InlineData(BrokerProvider.MoomooSimulate, BrokerProvider.MoomooSimulate, LogLevel.None)]
    public void T_10_2117_観測のたびに食い違いを知らせ設定は書き換えない(
        BrokerProvider configured, BrokerProvider actual, LogLevel expected)
    {
        var settings = new InMemoryRiskSettingsStore(TradingDefaults.CreateSettings() with { BrokerProvider = configured });
        var logger = new RecordingLogger();
        var handler = new BrokerProviderAlignmentHandler(settings, logger);

        handler.Handle(Observation(actual));

        if (expected == LogLevel.None)
        {
            logger.Entries.Should().BeEmpty("正常な見え方では 1 件も出さない");
        }
        else
        {
            var entry = logger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(expected);
            entry.Message.Should().Contain("PUT /risk-controls/settings/broker-provider")
                .And.Contain(configured.ToString()).And.Contain(actual.ToString());
        }

        settings.GetCurrent().BrokerProvider.Should().Be(configured, "観測から設定を黙って書き換えない（実弾への確認操作を迂回しない）");
    }

    // T-10-2118: 受け入れ基準 14。設定を読めなくても例外を投げない（同じ観測を扱う口座種別・基準資金の記録を巻き込まない）。
    [Fact]
    public void T_10_2118_設定を読めなくても例外を投げない()
    {
        var logger = new RecordingLogger();
        var handler = new BrokerProviderAlignmentHandler(new ThrowingSettings(), logger);

        var act = () => handler.Handle(Observation(BrokerProvider.MoomooSimulate));

        act.Should().NotThrow();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }
}
