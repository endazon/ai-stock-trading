using FluentAssertions;
using Xunit;

namespace AiStockTrading.PlanConformance.Tests;

/// <summary>
/// FR-10, FR-19, FR-20, ADR-0008, ADR-0016, ADR-0018:
/// 計画確定値（<see cref="PlanRiskDefaults"/>）と実装（<see cref="ActualDefaults"/>）の適合検査。
/// 全面再実装（#344）の途上で受容する逸脱は <see cref="KnownPlanDeviations"/> に登録する（IADR-0127）。
/// </summary>
public class PlanConformanceTests
{
    private static readonly IReadOnlyDictionary<string, string> Actual = ActualDefaults.Snapshot();

    // 検査1: 計画からの「新規」逸脱を止める。登録簿に無いキーは計画どおりでなければならない。
    [Fact]
    public void 計画確定値と実装値が一致する_登録済み逸脱を除く()
    {
        var unexpected = PlanRiskDefaults.All
            .Where(plan => !KnownPlanDeviations.ByKey.ContainsKey(plan.Key))
            .Where(plan => Actual.TryGetValue(plan.Key, out var actual) && actual != plan.Value)
            .Select(plan => $"{plan.Key}: 計画「{plan.Value}」({plan.Citation}) / 実装「{Actual[plan.Key]}」")
            .ToArray();

        unexpected.Should().BeEmpty(
            "計画確定値からの逸脱は許されない。意図した変更なら計画側を改訂し、"
                + "再実装途上の一時的な逸脱なら担当 issue 付きで KnownPlanDeviations へ登録する。逸脱: {0}",
            string.Join(" / ", unexpected));
    }

    // 検査2: 抽出漏れ（キーの綴り誤り・実装側スナップショットからの削除）を止める。
    [Fact]
    public void 計画確定値の全キーが実装側スナップショットに存在する()
    {
        var missing = PlanRiskDefaults.All
            .Select(plan => plan.Key)
            .Where(key => !Actual.ContainsKey(key))
            .ToArray();

        missing.Should().BeEmpty(
            "計画側にあって実装側スナップショットに無いキーは、抽出漏れか綴り誤りである。"
                + "ActualDefaults.Snapshot() へ抽出を追加すること。欠落キー: {0}",
            string.Join(", ", missing));
    }

    // 検査3（本方式の要）: 登録簿の陳腐化を止める。
    // 担当 issue が値を直したのに登録簿を消し忘れると、ここで失敗する。
    [Fact]
    public void 登録済み逸脱は実際に逸脱している()
    {
        var stale = KnownPlanDeviations.All
            .Where(deviation => Actual.TryGetValue(deviation.Key, out var actual)
                && PlanRiskDefaults.ByKey.TryGetValue(deviation.Key, out var plan)
                && actual == plan.Value)
            .Select(deviation => $"{deviation.Key}（担当 #{deviation.OwningIssue}）")
            .ToArray();

        stale.Should().BeEmpty(
            "実装が計画に一致したにもかかわらず既知逸脱として登録されたままである。"
                + "KnownPlanDeviations から該当行を削除すること。解消済みの登録: {0}",
            string.Join(", ", stale));
    }

    // 検査4（検査3 の裏面）: 登録簿の現行値が実装の実際値と食い違っていないこと。
    // 担当 issue が値を「別の誤った値」へ変えた場合も検知する。
    [Fact]
    public void 登録済み逸脱の現行値は実装の実際値と一致する()
    {
        var drifted = KnownPlanDeviations.All
            .Where(deviation => Actual.TryGetValue(deviation.Key, out var actual) && actual != deviation.ActualValue)
            .Select(deviation =>
                $"{deviation.Key}（担当 #{deviation.OwningIssue}）: 登録「{deviation.ActualValue}」/ 実装「{Actual[deviation.Key]}」")
            .ToArray();

        drifted.Should().BeEmpty(
            "実装値が変わったが既知逸脱の登録が追随していない。計画どおりになったなら登録行を削除し、"
                + "まだ逸脱しているなら ActualValue を更新すること。差異: {0}",
            string.Join(" / ", drifted));
    }

    // 検査5: 「とりあえず登録して恒久化」を防ぐ。担当 issue と理由の記載を必須にする。
    [Fact]
    public void 登録済み逸脱はすべて担当issueと理由と計画上の裏付けを持つ()
    {
        KnownPlanDeviations.All.Should().OnlyContain(
            d => d.OwningIssue > 0, "逸脱には解消する担当 issue が必要である");
        KnownPlanDeviations.All.Should().OnlyContain(
            d => !string.IsNullOrWhiteSpace(d.Reason), "なぜ現時点で受容するのかを記録する");

        var unbacked = KnownPlanDeviations.All
            .Where(d => !PlanRiskDefaults.ByKey.ContainsKey(d.Key))
            .Select(d => d.Key)
            .ToArray();
        unbacked.Should().BeEmpty(
            "計画確定値に無いキーの逸脱は登録できない（キーの綴り誤りか、計画側の裏付けが無い）: {0}",
            string.Join(", ", unbacked));
    }

    // 検査6: 登録簿にキーの重複が無いこと（同一キーの二重登録は片方が死ぬ）。
    [Fact]
    public void 登録簿と計画表にキーの重複が無い()
    {
        KnownPlanDeviations.All.Select(d => d.Key).Should().OnlyHaveUniqueItems();
        PlanRiskDefaults.All.Select(d => d.Key).Should().OnlyHaveUniqueItems();
    }
}
