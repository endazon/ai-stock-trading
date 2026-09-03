using ReportService.Domain;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.KnowledgeBase;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ReportService.Tests;

// FR-08, IADR-0069/0071 決定3, #565, IADR-0274: 確定報告書→KB カタログ文書の写像を検証する。
// 機密区分 internal・タグ・属性に加え、本文（TradingReport.Body）の送信可否を固定する。
public class ReportKnowledgeMapperTests
{
    private static TradingReport Confirmed(string body = "") => new()
    {
        PeriodKey = "daily-2026-07-18",
        Kind = ReportKind.Daily,
        PeriodStart = new DateOnly(2026, 7, 18),
        State = ReportState.Confirmed,
        AssumptionsVersion = 3,
        PolicySummary = "翌営業日は継続",
        Body = body,
        ConfirmedAt = new DateTimeOffset(2026, 7, 18, 6, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void タイトルに種別と期間キーを含む()
    {
        ReportKnowledgeMapper.ToDocument(Confirmed()).Title.Should().Be("確定報告書 Daily daily-2026-07-18");
    }

    [Fact]
    public void 機密区分は_internal()
    {
        ReportKnowledgeMapper.ToDocument(Confirmed()).Confidentiality.Should().Be(KnowledgeConfidentiality.Internal);
    }

    [Fact]
    public void 本文が非空ならContentへ渡りContentTypeはtext_markdownになる()
    {
        // FR-08, #565, IADR-0274: RAG 検索が本文をヒットさせるには Content を送る必要がある。
        var report = Confirmed(body: "# 日次報告\n\n本日は継続方針。");

        var doc = ReportKnowledgeMapper.ToDocument(report);

        doc.Content.Should().Be("# 日次報告\n\n本日は継続方針。");
        doc.ContentType.Should().Be("text/markdown");
    }

    [Fact]
    public void 本文が空ならContentとContentTypeはnullで警告ログを残す_否定形()
    {
        // FR-08, #565, IADR-0274［2026-09-03 追記］: 手動確定などで本文が未供給（空文字列）のときは
        // 「本文あり（0 文字）」として送らず null に倒す。未供給に気づけるよう警告ログを残す。
        var logger = new CapturingLogger();

        var doc = ReportKnowledgeMapper.ToDocument(Confirmed(body: string.Empty), logger);

        doc.Content.Should().BeNull();
        doc.ContentType.Should().BeNull();
        logger.Warnings.Should().ContainSingle()
            .Which.Should().Contain("daily-2026-07-18");
    }

    [Fact]
    public void 本文が非空なら警告ログを残さない_対の肯定形()
    {
        var logger = new CapturingLogger();

        ReportKnowledgeMapper.ToDocument(Confirmed(body: "本文あり"), logger);

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void タグと属性に期間キー_種別_前提条件バージョン_確定日時を含む()
    {
        var doc = ReportKnowledgeMapper.ToDocument(Confirmed());

        doc.Tags.Should().Contain(["report", "daily"]);
        doc.Attributes!["periodKey"].Should().Be("daily-2026-07-18");
        doc.Attributes["kind"].Should().Be("Daily");
        doc.Attributes["assumptionsVersion"].Should().Be("3");
        doc.Attributes.Should().ContainKey("confirmedAt");
    }

    // 中央パッケージ管理にログ用のテストダブルが無いため最小の実装を置く
    // （AiStockTrading.Shared.Infrastructure.Tests.Fx.FxRateSourceFactoryTests.CapturingLogger と同型）。
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                _warnings.Add(formatter(state, exception));
        }
    }
}
