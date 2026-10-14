using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Xunit;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// FR-15, ADR-0033 決定5, #1342, IADR-0537 決定2: 印の鍵（実行の同一性）と台帳の鍵（承認の単位）。
public class Stage0RecordingIdentityTests
{
    private static Stage0RecordingOptions Options() => new()
    {
        Enabled = true,
        From = "2026-09-15",
        To = "2026-10-09",
        LlmTrainingCutoff = "2026-06-30",
        Symbols =
        [
            new Stage0RecordingOptions.SymbolEntry { Symbol = "TSLA", Market = Market.UnitedStates },
            new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates },
        ],
        VoteCount = 1,
        ApprovedVoteCount = 1,
        ApprovedEstimateJpy = 437.28m,
        InputTokensPerDecision = 3478,
        OutputTokensPerDecision = 425,
        ScreeningInputTokensPerDecision = 2269,
        ScreeningOutputTokensPerDecision = 582,
        OutputPath = "/var/lib/ai-stock-trading/stage0/records.json",
        StateDirectory = "/var/lib/ai-stock-trading/stage0/state",
        Model = "claude-sonnet-5-5",
    };

    // 🔴 置き場・有効化の欄・銘柄の並びでは鍵が変わらない（変わると同じ承認値の記録がもう一度走る）。
    [Theory]
    [InlineData("output")]
    [InlineData("state")]
    [InlineData("enabled")]
    [InlineData("order")]
    [InlineData("approved-jpy-trailing-zero")]
    public void 置き場や銘柄の並びを変えても鍵は変わらない(string change)
    {
        var changed = Options();
        switch (change)
        {
            case "output": changed.OutputPath = "/elsewhere/records.json"; break;
            case "state": changed.StateDirectory = "/elsewhere/state"; break;
            case "enabled": changed.Enabled = false; break;
            case "approved-jpy-trailing-zero": changed.ApprovedEstimateJpy = 437.280m; break;
            default:
                changed = new Stage0RecordingOptions
                {
                    Enabled = true,
                    From = "2026-09-15",
                    To = "2026-10-09",
                    LlmTrainingCutoff = "2026-06-30",
                    Symbols =
                    [
                        new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates },
                        new Stage0RecordingOptions.SymbolEntry { Symbol = "TSLA", Market = Market.UnitedStates },
                    ],
                    VoteCount = 1,
                    ApprovedVoteCount = 1,
                    ApprovedEstimateJpy = 437.28m,
                    InputTokensPerDecision = 3478,
                    OutputTokensPerDecision = 425,
                    ScreeningInputTokensPerDecision = 2269,
                    ScreeningOutputTokensPerDecision = 582,
                    OutputPath = "/var/lib/ai-stock-trading/stage0/records.json",
                    Model = "claude-sonnet-5-5",
                };
                break;
        }

        Stage0RecordingIdentity.RunKey(changed).Should().Be(Stage0RecordingIdentity.RunKey(Options()));
        Stage0RecordingIdentity.LedgerKey(changed).Should().Be(Stage0RecordingIdentity.LedgerKey(Options()));
    }

    // 承認の単位（承認額・承認回数・期間・銘柄）が変われば、印も台帳も別になる（通し試験の組と本番の組は互いを止めない）。
    [Theory]
    [InlineData("approved-jpy")]
    [InlineData("approved-votes")]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("symbols")]
    public void 承認の単位が変われば印も台帳も別になる(string change)
    {
        var changed = Options();
        switch (change)
        {
            case "approved-jpy": changed.ApprovedEstimateJpy = 7.67m; break;
            case "approved-votes": changed.ApprovedVoteCount = 3; break;
            case "from": changed.From = "2026-09-16"; break;
            case "to": changed.To = "2026-09-19"; break;
            default:
                changed = new Stage0RecordingOptions
                {
                    Enabled = true,
                    From = "2026-09-15",
                    To = "2026-10-09",
                    LlmTrainingCutoff = "2026-06-30",
                    Symbols = [new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates }],
                    VoteCount = 1,
                    ApprovedVoteCount = 1,
                    ApprovedEstimateJpy = 437.28m,
                    InputTokensPerDecision = 3478,
                    OutputTokensPerDecision = 425,
                    ScreeningInputTokensPerDecision = 2269,
                    ScreeningOutputTokensPerDecision = 582,
                    Model = "claude-sonnet-5-5",
                };
                break;
        }

        Stage0RecordingIdentity.RunKey(changed).Should().NotBe(Stage0RecordingIdentity.RunKey(Options()));
        Stage0RecordingIdentity.LedgerKey(changed).Should().NotBe(Stage0RecordingIdentity.LedgerKey(Options()));
    }

    // 記録の中身を決める欄（モデル・カットオフ・トークン量・多数決回数）が変われば別の実行になるが、
    // 承認の単位が同じなら**台帳は引き継ぐ**（同じ承認額の中で、欄を動かしてもう一度課金できない）。
    [Theory]
    [InlineData("model")]
    [InlineData("screening-model")]
    [InlineData("cutoff")]
    [InlineData("tokens")]
    [InlineData("votes")]
    public void 記録の中身の欄が変われば印は別で台帳は同じ(string change)
    {
        var changed = Options();
        switch (change)
        {
            case "model": changed.Model = "claude-opus-5-5"; break;
            case "screening-model": changed.ScreeningModel = "claude-haiku-5-5"; break;
            case "cutoff": changed.LlmTrainingCutoff = "2026-05-31"; break;
            case "tokens": changed.InputTokensPerDecision = 3479; break;
            default: changed.VoteCount = 3; break;
        }

        Stage0RecordingIdentity.RunKey(changed).Should().NotBe(Stage0RecordingIdentity.RunKey(Options()));
        Stage0RecordingIdentity.LedgerKey(changed).Should().Be(Stage0RecordingIdentity.LedgerKey(Options()));
    }

    [Fact]
    public void 印の鍵と台帳の鍵は別の値である()
    {
        Stage0RecordingIdentity.RunKey(Options()).Should().NotBe(Stage0RecordingIdentity.LedgerKey(Options()));
        Stage0RecordingIdentity.RunKey(Options()).Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
