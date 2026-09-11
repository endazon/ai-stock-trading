using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Architecture.Tests;

/// <summary>
/// NFR, #752, IADR-0335: <see cref="CSharpSource"/> の低レベルな潰し処理を直接固定する。
/// <para>
/// この前処理が壊れると <b>DI 登録の未結線検知が静かに誤る</b> ——「コメント内の言及を利用と数える」
/// （見落とし）か「実コードを文字列と誤読して潰す」（原因の分かりにくい赤）のどちらかになる。
/// <c>UnwiredDiRegistrationTests</c> の自己試験は判定の入口からしか通らないため、分岐ごとの固定はここに置く。
/// </para>
/// </summary>
public class CSharpSourceTests
{
    // 対（肯定形・先に置く）: 潰しは**長さを保つ**。
    // 長さが変わる実装にすると、あとから DI 登録の範囲を潰すときにオフセットがずれて無関係な場所を消す。
    [Theory]
    [InlineData("var a = 1; // Foo")]
    [InlineData("/* Foo */ var a = 1;")]
    [InlineData("var s = \"Foo\";")]
    [InlineData("var s = @\"Foo\"\"Bar\";")]
    [InlineData("var s = \"\"\"Foo\"\"\";")]
    [InlineData("var s = $\"{x.ToString(\"F2\")}\";")]
    [InlineData("var c = '\\'';")]
    public void 潰しても文字列長は変わらない(string source)
    {
        CSharpSource.BlankCommentsAndLiterals(source).Should().HaveLength(source.Length);
    }

    [Fact]
    public void 潰しても改行は残る()
    {
        CSharpSource.BlankCommentsAndLiterals("// Foo\n/* Bar\nBaz */\nvar a = 1;")
            .Split('\n').Should().HaveCount(4, "行番号の算出が狂わないよう改行だけは残す");
    }

    [Theory]
    // 行コメント・ブロックコメント。
    [InlineData("// Foo が呼ばれる\nvar a = 1;")]
    [InlineData("/* Foo\n   が呼ばれる */\nvar a = 1;")]
    [InlineData("/// <see cref=\"Foo\"/>\nvar a = 1;")]
    // 通常・逐語・生の文字列リテラル。
    [InlineData("var s = \"Foo\";")]
    [InlineData("var s = @\"C:\\Foo\\\";")]
    [InlineData("var s = \"\"\"Foo\"\"\";")]
    // 補間文字列の**リテラル部分**。
    [InlineData("var s = $\"Foo={x}\";")]
    // 文字リテラル（`'F'` 単独では拾えないので識別子として現れる形にする）。
    [InlineData("var s = \"Foo\" + 'x';")]
    public void コメントと文字列リテラルの中の識別子は消える(string source)
    {
        CSharpSource.Identifiers(CSharpSource.BlankCommentsAndLiterals(source))
            .Should().NotContain("Foo");
    }

    [Theory]
    [InlineData("var s = \"literal\"; var y = Foo.Bar();")]
    [InlineData("// comment\nvar y = Foo.Bar();")]
    [InlineData("/* comment */ var y = Foo.Bar();")]
    [InlineData("var s = @\"verbatim\"\"quoted\"; var y = Foo.Bar();")]
    [InlineData("var s = \"\"\"raw \" text\"\"\"; var y = Foo.Bar();")]
    [InlineData("var c = '\"'; var y = Foo.Bar();")]
    public void 潰したあとも後続のコードは残る(string source)
    {
        CSharpSource.Identifiers(CSharpSource.BlankCommentsAndLiterals(source))
            .Should().Contain("Foo", "リテラルの終端を読み違えると、後続の実コードまで潰れる");
    }

    /// <summary>
    /// 🔴 補間ホールの中はコードである。AI レビュー（#761）が本番コードで実在を示した形
    /// （<c>$"{x.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}"</c>。15 ファイル以上）を固定する。
    /// ホールを認識しないと、入れ子の <c>"yyyyMMdd"</c> の開きクォートが外側の終端と誤読され、
    /// <c>CultureInfo</c> がその後「文字列」として潰れる。
    /// </summary>
    [Fact]
    public void 補間ホールの中の識別子は残り入れ子のリテラルだけが消える()
    {
        var blanked = CSharpSource.BlankCommentsAndLiterals(
            "var url = $\"&d1={from.ToString(\"yyyyMMdd\", CultureInfo.InvariantCulture)}&i=d\"; var y = Foo.Bar();");
        var identifiers = CSharpSource.Identifiers(blanked);

        identifiers.Should().Contain("CultureInfo", "補間ホールの中はコードであり、利用として数える");
        identifiers.Should().Contain("InvariantCulture");
        identifiers.Should().Contain("Foo", "外側の終端を読み違えると後続の実コードまで潰れる");
        identifiers.Should().NotContain("yyyyMMdd", "ホールの中でも入れ子のリテラルは潰す");
        identifiers.Should().NotContain("d1", "ホールの外（リテラル部分）は潰す");
    }

    [Fact]
    public void 補間ホールの中の識別子は利用として数える()
    {
        CSharpSource.Identifiers(CSharpSource.BlankCommentsAndLiterals("var s = $\"{nameof(Foo)}\";"))
            .Should().Contain("Foo", "nameof は型の実利用である（コンパイラが解決する）");
    }

    [Fact]
    public void 波括弧のエスケープは補間ホールと取り違えない()
    {
        CSharpSource.Identifiers(CSharpSource.BlankCommentsAndLiterals("var s = $\"{{Foo}}\"; var y = Bar.Baz();"))
            .Should().NotContain("Foo").And.Contain("Bar");
    }

    [Fact]
    public void using行は潰されるが長さは変わらない()
    {
        const string source = "using Foo.Bar;\nvar y = Foo.Baz();";
        var blanked = CSharpSource.BlankUsingDirectives(source);

        blanked.Should().HaveLength(source.Length);
        blanked.Split('\n')[0].Trim().Should().BeEmpty("名前空間の並びは型の利用ではない");
        blanked.Split('\n')[1].Should().Contain("Foo", "using 以外の行は触らない");
    }

    [Fact]
    public void usingエイリアスは単純型名へ解決する()
    {
        var aliases = CSharpSource.UsingAliases("using AppSvc = Svc.Features.CostControlAppService;\n");

        aliases.Should().ContainKey("AppSvc").WhoseValue.Should().Be("CostControlAppService");
    }

    [Theory]
    [InlineData("global::Foo.Bar.Baz", "Baz")]
    [InlineData("IFoo<Bar>", "IFoo")]
    [InlineData("  Foo  ", "Foo")]
    [InlineData("@class", "class")]
    public void 型式から単純型名を取り出す(string expression, string expected)
    {
        CSharpSource.SimpleTypeName(expression).Should().Be(expected);
    }

    [Fact]
    public void 総称引数は最上位のカンマだけで割る()
    {
        CSharpSource.SplitTypeArguments("IFoo<A, B>, Foo")
            .Should().Equal("IFoo<A, B>", "Foo");
    }

    // 否定形: 妥当性判定が load-bearing であること。
    [Theory]
    [InlineData("Foo", true)]
    [InlineData("_foo1", true)]
    [InlineData("", false)]
    [InlineData("1Foo", false)]
    [InlineData("Foo[]", false)]
    [InlineData("(A, B)", false)]
    public void 単純識別子の判定は型式の残骸を弾く(string value, bool expected)
    {
        CSharpSource.IsSimpleIdentifier(value).Should().Be(expected);
    }
}
