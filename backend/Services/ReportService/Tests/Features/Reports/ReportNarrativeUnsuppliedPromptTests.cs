using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-16, UC-03〜05, #1156, IADR-0480 決定 1・2: 散文（LLM）のプロンプトへ**未供給と 0 を区別して**渡す。
//
// 実測（稼働 PoC 2026-10-02）: 日報の散文が「評価損益についても参照すべき建玉がなく」と書いた（実際は 3 銘柄を保有）。
// 週報の散文が費用 0.00 USD を「費用面での負担は発生しておらず」と書いた（経費明細は未取り込み）。
// プロンプトは建玉も未供給の一覧も受け取らず、評価損益・費用合計を確定値として渡していた。
public class ReportNarrativeUnsuppliedPromptTests
{
    private static ReportNarrativeContext Context(ReportKind kind = ReportKind.Daily) => new(
        Kind: kind,
        PeriodKey: "daily-2026-10-02",
        PeriodLabel: "2026-10-02",
        Markets: ["US"],
        Pnl: new PnlSummary(
            RealizedPnlGross: 0m, TotalCost: 0m, TaxWithheld: 0m, RealizedPnlNet: 0m,
            UnrealizedPnl: 0m, TradeCount: 0, RealizingTradeCount: 0, WinningTradeCount: 0),
        PolicySummary: "翌営業日は保有銘柄の損切りを維持");

    private static ReportPosition Position(string symbol, int quantity) => new(
        Market.UnitedStates, symbol, TradeSide.Buy, quantity, AverageEntryPrice: 100m, StopLossPrice: 90m,
        CurrentPrice: null, UnrealizedPnl: null, BorrowFeeTotal: null, HoldingDays: null);

    // ---- 未供給の一覧 ------------------------------------------------------------------------------

    // T-10-2160
    [Fact]
    public void 未供給の入力の一覧を載せ_無いや0と言い切らない指示を入れる()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context() with
        {
            UnsuppliedInputs = [ReportInput.FxSourceStatus, ReportInput.LlmUsage, ReportInput.TradeRationales],
            Positions = [Position("NVDA", 1049)],
        });

        prompt.Should().Contain("取得できなかった入力（未供給）: 為替の情報源の状態、LLM 利用実績、判断根拠");
        prompt.Should().Contain(ReportNarrativePromptBuilder.UnsuppliedRule);
        // #1156（独立監査）: 規則の要点は**文字列リテラルで**固定する（定数との比較だけでは、定数の文言を
        // 削っても緑のまま）。「無い」「0」と言い切らず「未供給（取得できなかった）」と書く、の 3 点。
        prompt.Should().Contain("「無い」「0」「発生しなかった」のではなく、値が分かりません。");
        prompt.Should().Contain("「未供給（取得できなかった）」と書き、「無い」「0」「なかった」と言い切らないでください。");
        prompt.Should().Contain("未供給（取得できなかった）");
    }

    // T-10-2161
    [Fact]
    public void 未供給が無ければ一覧の節を出さない()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context() with { Positions = [Position("NVDA", 1049)] });

        // 🔴 逆向きの禁止（05_screens）: 供給されている入力を「取得できなかった」と言わせない。
        prompt.Should().NotContain("取得できなかった入力");
        prompt.Should().NotContain(ReportNarrativePromptBuilder.UnsuppliedRule);
        prompt.Should().NotContain(ReportNarrativePromptBuilder.UnsuppliedValue);
    }

    // ---- 建玉（日報） ------------------------------------------------------------------------------

    // T-10-2162
    [Fact]
    public void 日報で建玉が未供給なら未供給と渡し_建玉なしと書かせない()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context() with
        {
            UnsuppliedInputs = [ReportInput.OpenPositions],
            Positions = null,
        });

        prompt.Should().Contain("建玉（現在の台帳）: 未供給（取得できなかった）");
        prompt.Should().Contain("「建玉なし」「ポジションを保有していない」とも書かないでください");
        // 🔴 否定形: 0 件（建玉なし）として渡していない。
        prompt.Should().NotContain("0 件（建玉なし）");
        prompt.Should().Contain("取得できなかった入力（未供給）: 建玉");
    }

    // T-10-2163
    [Fact]
    public void 日報で建玉が供給されていれば件数と銘柄を渡す()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context() with
        {
            Positions = [Position("NVDA", 1049), Position("MSFT", 468), Position("AMZN", 970)],
        });

        prompt.Should().Contain("建玉（現在の台帳）: 3 件（銘柄: AMZN, MSFT, NVDA）");
        prompt.Should().NotContain("建玉（現在の台帳）: 未供給");
    }

    // T-10-2163
    [Fact]
    public void 日報で建玉が空なら0件と渡し_未供給とは言わない()
    {
        // 🔴 逆向きの禁止: 供給された 0 は 0 である（未供給へ倒さない）。
        var prompt = ReportNarrativePromptBuilder.Build(Context() with { Positions = [] });

        prompt.Should().Contain("建玉（現在の台帳）: 0 件（建玉なし）");
        prompt.Should().NotContain(ReportNarrativePromptBuilder.UnsuppliedValue);
    }

    // T-10-2162
    [Theory]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 週報と月報は建玉を入力に持たないので_建玉の有無に触れさせない(ReportKind kind)
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context(kind));

        prompt.Should().Contain("建玉: 本報告書の入力に含まれていません。建玉の有無・保有状況には散文で言及しないでください。");
        // 🔴 否定形: 種別が使わない入力を「取得できなかった」とは言わない。
        prompt.Should().NotContain("建玉（現在の台帳）");
    }

    // T-10-2164
    [Fact]
    public void 評価損益は当期間の約定から畳んだ値であり_建玉の有無を推測させない()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context() with { Positions = [Position("NVDA", 1049)] });

        prompt.Should().Contain(ReportNarrativePromptBuilder.UnrealizedScopeNote);
    }

    // ---- 費用（経費明細が未取り込み） ---------------------------------------------------------------

    // T-10-2165
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 費用合計は概算として渡し_0でも負担なしと書かせない(ReportKind kind)
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context(kind));

        prompt.Should().Contain("- 費用合計: 0（概算）");
        // 計画 ADR-0035 決定 1・3, #1201: §1 の費用合計（借株料・為替スプレッドを含む）と同じ語で別の値を渡さない。
        prompt.Should().Contain("- 費用合計: 0（概算）（売買手数料・取引諸費用のみ。借株料・為替スプレッドは含まない）");
        prompt.Should().Contain(ReportNarrativePromptBuilder.CostEstimateNote);
        prompt.Should().Contain("「費用負担は無かった」");
    }

    // ---- 約定・手動売買の取り込みの未供給 ------------------------------------------------------------

    // T-10-2166
    [Fact]
    public void 約定が未供給なら損益と件数と費用の値を渡さない()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context() with
        {
            UnsuppliedInputs = [ReportInput.Fills],
            Positions = [Position("NVDA", 1049)],
        });

        prompt.Should().Contain("- 約定代金差額(費用・税の控除前): 未供給（取得できなかった）");
        prompt.Should().Contain("- 費用合計: 未供給（取得できなかった）（概算）");
        prompt.Should().Contain("- 評価損益(参考): 未供給（取得できなかった）");
        prompt.Should().Contain("- 約定件数: 未供給（取得できなかった） / 決済件数: 未供給（取得できなかった）");
        prompt.Should().Contain("「取引は無かった」「損益は 0 だった」等とも書かないでください");
        // 🔴 否定形: 0 件・損益 0 を確定値として渡していない。
        prompt.Should().NotContain("- 約定件数: 0");
        prompt.Should().NotContain("- 約定代金差額(費用・税の控除前): 0");
    }

    // T-10-2166
    [Fact]
    public void 手動売買の取り込みが未供給なら評価損益の値だけを渡さない()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context() with
        {
            UnsuppliedInputs = [ReportInput.DriftAdoptions],
            Positions = [Position("NVDA", 1049)],
        });

        prompt.Should().Contain("- 評価損益(参考): 未供給（取得できなかった）");
        // 約定は供給されているので、約定から数える値は渡す。
        prompt.Should().Contain("- 約定件数: 0 / 決済件数: 0 / 勝ち決済: 0");
        prompt.Should().Contain("- 約定代金差額(費用・税の控除前): 0");
    }

    // T-10-2177, IADR-0071 決定1: 未供給の一覧と建玉を足しても、プロンプトは決定的である。
    [Fact]
    public void 決定的_同一入力で同一出力()
    {
        var context = Context() with
        {
            UnsuppliedInputs = [ReportInput.LlmUsage, ReportInput.FxSourceStatus],
            Positions = [Position("NVDA", 1049), Position("AMZN", 970)],
        };

        ReportNarrativePromptBuilder.Build(context).Should().Be(ReportNarrativePromptBuilder.Build(context));
    }
}
