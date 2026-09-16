using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR-01, ADR-0006, IADR-0129（2026-09-17 追記）, #811: Program.cs の EF 移行を「ホストとして稼働するときだけ」に限る判定。
// ビルド段の `dotnet run -- codegen write` は DB 無しで通らなければならず、一方で `dotnet <dll>`（引数なし）・
// `run`・ASP.NET のフラグ（`--urls`）は従来どおり移行してから稼働する。
public class JasperFxCommandLineTests
{
    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("RUN")]
    [InlineData("--urls", "http://+:8080")]
    [InlineData("--environment", "Production")]
    public void ホスト稼働の引数では移行を走らせる(params string[] args)
    {
        JasperFxCommandLine.IsHostRun(args).Should().BeTrue();
    }

    [Theory]
    [InlineData("codegen", "write")]
    [InlineData("codegen", "preview")]
    [InlineData("describe")]
    [InlineData("check-env")]
    [InlineData("help")]
    public void JasperFx_のツールコマンドでは移行を走らせない(params string[] args)
    {
        JasperFxCommandLine.IsHostRun(args).Should().BeFalse();
    }

    [Fact]
    public void 引数が_null_なら例外で止める()
    {
        var act = () => JasperFxCommandLine.IsHostRun(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // 🔴 WebApplicationFactory は UseSetting（テストの HostSettings）を `--Key=Value` の引数としてエントリポイントへ渡す。
    // JasperFx は先頭が `-` なら `run` を前置し、知らないフラグを使い方の表示＋終了コード 1 で拒否するため、
    // ホストが一度も起動せず TestServer が "The server has not been started" で落ちる（実測 34 件）。
    // `--` で始まる引数は JasperFx へ渡さず、従来の RunAsync へ流す。
    [Theory]
    [InlineData("--Risk:SimulatorProfile:Enabled=false")]
    [InlineData("--urls", "http://+:8080")]
    [InlineData("--environment", "Testing")]
    public void ASP_NET_流の構成引数は_JasperFx_へ渡さない(params string[] args)
    {
        JasperFxCommandLine.UsesJasperFxCommands(args).Should().BeFalse();
    }

    [Theory]
    [InlineData]
    [InlineData("run")]
    [InlineData("run", "--check")]
    [InlineData("codegen", "write")]
    [InlineData("describe")]
    public void 引数なしと_JasperFx_の動詞は_JasperFx_へ渡す(params string[] args)
    {
        JasperFxCommandLine.UsesJasperFxCommands(args).Should().BeTrue();
    }
}
