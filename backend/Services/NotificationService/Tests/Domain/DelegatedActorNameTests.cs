using NotificationService.Domain;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Tests;

// FR-14, FR-20, FR-07, UC-06, #861, #868, IADR-0240 決定11, IADR-0383 決定4:
// **代理される利用者（onBehalfOf）の値域**と、多層認証の対応付けの値域検査（純関数）。
//
// 値域外の対応付けは「たまに失敗する」ではなく「その利用者は**恒常的に**報告書を確定できず段階も動かせない」を
// 意味する（Discord が唯一の窓口であるため）。#861 の監査が稼働環境で `'山田'` / `'dev owner'` の Rejected を実測した。
//
// テスト ID: T-139（`docs/tests/FR-20_staged-gates-tests.md`）。
public class DelegatedActorNameTests
{
    [Theory]
    [InlineData("a")]                                                                    // 境界: 1 文字
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 境界: 64 文字
    [InlineData("endazon")]
    [InlineData("first.last@example.com")]
    [InlineData("a_b-c+d.e@f")]
    public void 値域内の利用者名は通る(string value) => DelegatedActorName.IsInRange(value).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]                                                                      // 境界: 0 文字
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 境界: 65 文字
    [InlineData("山田")]                                                                  // #861 実測: 非 ASCII
    [InlineData("dev owner")]                                                             // #861 実測: 空白入り
    [InlineData("owner\n@everyone")]                                                      // 改行の注入
    [InlineData("owner\r")]                                                               // 復帰
    [InlineData("**owner**")]                                                             // マークダウン
    [InlineData("owner;drop")]
    public void 値域外の利用者名は通らない(string? value) =>
        DelegatedActorName.IsInRange(value).Should().BeFalse();

    [Fact]
    public void 末尾の改行は値域を通らない()
    {
        // 🔴 .NET の `$` は**末尾 LF の直前にもマッチする**（IADR-0240 決定6 の追記）。`\A…\z` で書いていないと
        // `owner\n` が値域を通り、通知本文・監査要約へ改行つきで運ばれる。
        DelegatedActorName.IsInRange("owner\n").Should().BeFalse();
    }

    [Fact]
    public void 値域外の対応付けだけを_Discord_ユーザーID_の昇順で返す()
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["300"] = "endazon",      // 値域内
            ["100"] = "山田",          // 値域外
            ["200"] = "dev owner",    // 値域外
        };

        var offenders = DelegatedActorName.OutOfRangeMappings(mapping);

        offenders.Select(p => p.Key).Should().Equal("100", "200");
        offenders.Select(p => p.Value).Should().Equal("山田", "dev owner");
    }

    [Fact]
    public void すべて値域内なら空を返す()
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal) { ["1"] = "endazon" };

        DelegatedActorName.OutOfRangeMappings(mapping).Should().BeEmpty();
    }

    [Fact]
    public void 対応付けが空でも例外にしない()
    {
        DelegatedActorName.OutOfRangeMappings(new Dictionary<string, string>()).Should().BeEmpty();
    }
}
