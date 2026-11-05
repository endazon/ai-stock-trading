using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AwesomeAssertions;
using Xunit;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, FR-10, #1529, IADR-0617: 本番の Program.cs は UnanchoredEntryPriceOptions を構成から登録する（既定 false）。
// 登録を外すと未指定（null）の既定（false）になり振る舞いは同じだが、構成での opt-in が効かなくなる。登録の形を固定する。
public class UnanchoredEntryPriceWiringTests
{
    [Fact]
    public void Program_は構成の既定_false_で_UnanchoredEntryPriceOptions_を登録する()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeDecisionService.csproj")))
            dir = dir.Parent;
        dir.Should().NotBeNull();

        var program = File.ReadAllText(Path.Combine(dir!.FullName, "Program.cs"));

        program.Should().Contain("builder.Services.AddSingleton(new UnanchoredEntryPriceOptions(");
        program.Should().Contain("builder.Configuration.GetValue(UnanchoredEntryPriceOptions.ConfigKey, defaultValue: false)");
    }

    [Fact]
    public void 構成のキーは_TradeDecision_AllowUnanchoredEntryPrice() =>
        UnanchoredEntryPriceOptions.ConfigKey.Should().Be("TradeDecision:AllowUnanchoredEntryPrice");
}
