using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Kernel.Tests.Trading;

// FR-17, IADR-0063 決定 5, IADR-0260, IADR-0264: 版付き全体前提条件の「解決済みか」の判定を固定する。
//
// 🔴 本テストは IADR-0260（共有カーネルの新設）で新設し、IADR-0264（`ConfigurationService.Client` の
// 廃止）で設定サービスの Domain から共有カーネルへ移した。廃止により消費側は共有クライアント経由では
// なく共有カーネルの型を直接見るため、VersionedAssumptions は「サービス境界をまたいで消費される型」に
// なった（IADR-0260 が除外した理由＝「認可された経路越しに使う」が成立しなくなった）。
// **中身は 1 行も変えていない**（namespace 宣言のみの移送。IADR-0260 が移送時に採ったのと同じ作法）。
public class VersionedAssumptionsTests
{
    private static VersionedAssumptions At(int version) =>
        new(TradingAssumptionsDefaults.Create(), version);

    // 番兵バージョン 0 は「設定サービスから一度も取得できず既定値へ倒れた」ことを表す（IADR-0063 決定 5）。
    [Fact]
    public void 未解決の番兵バージョンはゼロである()
    {
        VersionedAssumptions.UnresolvedVersion.Should().Be(0);
    }

    // 境界値: 実在する版は 1 から始まる（未設定時に既定をシードした時点で 1）。
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(7, true)]
    public void 解決済み判定は版が1以上のときだけ真になる(int version, bool expected)
    {
        At(version).IsResolved.Should().Be(expected);
    }

    // 否定形: 「既定値と同じ内容だから未解決」ではない。**判定の根拠は版だけである。**
    // 内容で判定すると、利用者が既定と同値を明示的に登録した版を「取得できていない」と誤って扱う。
    [Fact]
    public void 内容が既定値と同じでも版が立っていれば解決済みである()
    {
        At(1).Assumptions.Should().Be(TradingAssumptionsDefaults.Create());
        At(1).IsResolved.Should().BeTrue();
    }
}
