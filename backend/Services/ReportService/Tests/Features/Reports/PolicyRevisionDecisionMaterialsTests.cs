using AwesomeAssertions;
using ReportService.Domain;
using ReportService.Features.Reports;
using Xunit;

namespace ReportService.Tests;

// T-10-1841, FR-07, FR-04, ADR-0048 決定 4, 04_report-templates §日報の追加記載要件, #1118, IADR-0467 決定 7:
// 方針の改訂 LLM へ、取引判断へ渡る材料と「未提供」の材料を示す。渡らない材料を売買条件にしないよう求める。
// 出来高の行は判断サービスと同じ設定（DecisionVolume:Enabled。既定 false）で切り替わる。
public class PolicyRevisionDecisionMaterialsTests
{
    private static PolicyRevisionContext Context() =>
        new(ReportKind.Daily, "daily-2026-09-30", "現状維持", null, "モメンタムが確認できれば買い");

    [Fact]
    public void 既定では出来高を未提供と示し_渡らない材料を条件にしないよう求める()
    {
        var prompt = PolicyRevisionPromptBuilder.Build(Context());

        prompt.Should().Contain(PolicyRevisionPromptBuilder.DecisionMaterialsHeading);
        prompt.Should().Contain(PolicyRevisionPromptBuilder.VolumeNotProvidedMaterial);
        prompt.Should().NotContain(PolicyRevisionPromptBuilder.VolumeProvidedMaterial);
        prompt.Should().Contain(PolicyRevisionPromptBuilder.NotProvidedMaterialsRule);
        prompt.Should().Contain("前日比").And.Contain("当日始値比").And.Contain("日中の高値と安値");
    }

    [Fact]
    public void 判断の出来高を有効化した構成では前営業日の出来高と20日平均比を渡ると示す()
    {
        var prompt = PolicyRevisionPromptBuilder.Build(Context(), decisionVolumeProvided: true);

        prompt.Should().Contain(PolicyRevisionPromptBuilder.VolumeProvidedMaterial);
        prompt.Should().NotContain(PolicyRevisionPromptBuilder.VolumeNotProvidedMaterial);
        PolicyRevisionPromptBuilder.VolumeProvidedMaterial.Should().Contain("当日の出来高は渡らない").And.Contain("未提供");
    }

    // 材料の節はデータ（利用者の指示）より前に置く（指示で材料の一覧を偽装できない。ADR-0003 追補の構造分離）。
    [Fact]
    public void 材料の節はデータのフェンスより前にある()
    {
        var prompt = PolicyRevisionPromptBuilder.Build(Context());

        prompt.IndexOf(PolicyRevisionPromptBuilder.DecisionMaterialsHeading, StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("データ（各行は JSON 文字列", StringComparison.Ordinal));
    }
}
