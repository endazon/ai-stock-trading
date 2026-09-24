using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-10, FR-11, UC-06, NFR, #947, IADR-0397:
// 権威源（リスク管理・監査台帳）の所在が構成されていないときの安全既定 3 つを、**本番の組み立てが結線する本物**で固定する。
//
// 組み立てガード（W3）の所見: 本番の組み立ては次の 3 ポートをこれらの Unsupplied 実装へ結線するが、既存の試験は
// 偽物（SuppliedSources / Stub / Throwing）だけを使い、本物を 1 度も通していなかった。
// 3 つとも「未供給は null（要確認）であり、空（該当なし・0 件）ではない」ことが存在理由である ——
// 本体を「空列を返す」へ変えても、既存の試験は全部緑のまま、報告書だけが「起きていない」と嘘を書く。
//   - 強制買戻し（推定）: ADR-0016 決定15 / IADR-0159 決定3（#419）。空列は「強制買戻しは起きていない」に見える。
//   - 為替の情報源の状態: IADR-0196 決定3（#381）。空は「劣化は無かった」という主張になる。
//   - 手動売買の取り込み: IADR-0360 決定2（#870）。空列は在庫の畳み込みから黙って落ち、実在しない建玉の評価損益を出す。
public class UnsuppliedLedgerSourcesTests(ReportWorkerWebApplicationFactory factory)
    : IClassFixture<ReportWorkerWebApplicationFactory>
{
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 30);

    [Fact]
    public async Task 強制買戻しの推定は未供給のとき空列でなくnullを返す()
    {
        var result = await new UnsuppliedBuyInInferenceRecordSource().GetInferencesAsync(From, To);

        result.Should().BeNull("空列は「推定 0 件＝強制買戻しは起きていない」と読まれる（ADR-0016 決定15）");
    }

    [Fact]
    public async Task 為替の情報源の状態は未供給のとき空でなくnullを返す()
    {
        var result = await new UnsuppliedFxSourceStatusSource().GetStatusAsync(From, To);

        result.Should().BeNull("空の状態は「期間内に劣化は無かった」という主張になる（IADR-0196 決定3）");
    }

    [Fact]
    public async Task 手動売買の取り込みは未供給のとき空列でなくnullを返す()
    {
        var result = await new UnsuppliedPeriodDriftAdoptionSource().GetDriftAdoptionsAsync(From, To);

        result.Should().BeNull("空列は取り込みが無かったと読まれ、在庫の畳み込みから黙って落ちる（IADR-0360 決定2）");
    }

    // 🔴 本番の組み立て（所在が未構成）が、上の 3 つの本物へ結線していること。
    [Fact]
    public void 所在が未構成の本番の組み立ては3つとも未供給の実装へ結線する()
    {
        factory.Services.GetRequiredService<IBuyInInferenceRecordSource>()
            .Should().BeOfType<UnsuppliedBuyInInferenceRecordSource>();
        factory.Services.GetRequiredService<IFxSourceStatusSource>()
            .Should().BeOfType<UnsuppliedFxSourceStatusSource>();
        factory.Services.GetRequiredService<IPeriodDriftAdoptionSource>()
            .Should().BeOfType<UnsuppliedPeriodDriftAdoptionSource>();
    }
}
