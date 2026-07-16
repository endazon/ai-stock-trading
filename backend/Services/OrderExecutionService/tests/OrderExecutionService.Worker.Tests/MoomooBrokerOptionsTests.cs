using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// #132, FR-05, ADR-0002, IADR-0016, IADR-0059: OpenD 接続パラメータの外部化と、本番切替の安全ゲートを検証する。
// 本テストは実 OpenD を使わない（構成の解釈と preflight のみ）。実接続・実発注は #132 の実測フェーズ（実基盤）。
//
// ⚠️ ここでの「TrdEnv ゲート」は実弾を可能にするものではない。TrdHeader は TrdEnv_Simulate 固定のまま
//    （IADR-0016 / IADR-0056）で、本ゲートは「config で実弾を要求されたら黙って SIMULATE で流さず停止する」閂。
public class MoomooBrokerOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // ---- 既定値（現行挙動の固定）----

    [Fact]
    public void 構成が空なら常駐_OpenD_の既定_host_port_で解決する()
    {
        var options = MoomooBrokerOptions.FromConfiguration(Config());

        options.OpenDHost.Should().Be("opend");
        options.OpenDPort.Should().Be(11111);
        options.RsaPrivateKeyPath.Should().BeNull();
        // #132: 応答待ちは外部化したが、既定は現行のハードコード値（15 秒）と同一であること。
        options.ReplyTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void host_port_rsa_パスを構成から読む()
    {
        var options = MoomooBrokerOptions.FromConfiguration(Config(
            ("Broker:Moomoo:OpenD:Host", "opend.prod.svc"),
            ("Broker:Moomoo:OpenD:Port", "21111"),
            ("Broker:Moomoo:OpenD:RsaPrivateKeyPath", "/opt/opend/rsa/opend_rsa.pem")));

        options.OpenDHost.Should().Be("opend.prod.svc");
        options.OpenDPort.Should().Be(21111);
        options.RsaPrivateKeyPath.Should().Be("/opt/opend/rsa/opend_rsa.pem");
    }

    // ---- 応答タイムアウトの外部化（#132）----

    [Fact]
    public void 応答タイムアウトを構成から読む()
    {
        var options = MoomooBrokerOptions.FromConfiguration(Config(
            ("Broker:Moomoo:OpenD:ReplyTimeoutSeconds", "45")));

        options.ReplyTimeout.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("601")] // 上限（10 分）超過。無期限待ちに近い設定を誤って入れさせない。
    public void 応答タイムアウトが不正なら起動時に停止する(string configured)
    {
        var act = () => MoomooBrokerOptions.FromConfiguration(Config(
            ("Broker:Moomoo:OpenD:ReplyTimeoutSeconds", configured)));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ReplyTimeoutSeconds*");
    }

    // ---- 実弾（TrdEnv_Real）を拒否する閂（IADR-0059 決定 5）----

    [Fact]
    public void TrdEnv_未設定なら_SIMULATE_として扱う()
    {
        MoomooBrokerOptions.FromConfiguration(Config()).TrdEnv.Should().Be("simulate");
    }

    [Theory]
    [InlineData("simulate")]
    [InlineData("SIMULATE")]
    [InlineData(" Simulate ")]
    public void TrdEnv_に_simulate_を明示しても受理する(string configured)
    {
        MoomooBrokerOptions.FromConfiguration(Config(("Broker:Moomoo:TrdEnv", configured)))
            .TrdEnv.Should().Be("simulate");
    }

    [Theory]
    [InlineData("real")]
    [InlineData("REAL")]
    [InlineData("TrdEnv_Real")]
    [InlineData("live")]
    [InlineData("1")]
    public void TrdEnv_に実弾を要求されたら起動時に停止する(string configured)
    {
        // 黙って SIMULATE へ倒すと「実弾で動いている」と運用者が誤認する。明示的に落とす（IADR-0059 決定 5）。
        var act = () => MoomooBrokerOptions.FromConfiguration(Config(("Broker:Moomoo:TrdEnv", configured)));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("IADR-0056"); // 実弾解禁には別 IADR＋明示 config が要る旨を案内する。
    }

    // ---- preflight（本番切替でいちばん踏みやすい罠を起動時に落とす）----

    [Fact]
    public void RSA_鍵パスが構成済みでファイルが無ければ_preflight_で停止する()
    {
        // 現行は黙って非暗号化へフォールバックし、「接続はするが trade だけ失敗する」形で表面化していた。
        // cross-network（worker→opend）trade は RSA 必須（#13）。Secret のマウント漏れは起動時に落とす。
        var options = new MoomooBrokerOptions("opend", 11111, "/opt/opend/rsa/opend_rsa.pem");

        var act = () => MoomooPreflight.Validate(options, fileExists: _ => false);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("/opt/opend/rsa/opend_rsa.pem");
    }

    [Fact]
    public void RSA_鍵パスが構成済みでファイルが有れば_preflight_を通す()
    {
        var options = new MoomooBrokerOptions("opend", 11111, "/opt/opend/rsa/opend_rsa.pem");

        var act = () => MoomooPreflight.Validate(options, fileExists: _ => true);

        act.Should().NotThrow();
    }

    [Fact]
    public void RSA_鍵パス未設定は_preflight_を通す()
    {
        // 非暗号化は OpenD が loopback（同一 Pod）で listen する構成でのみ許される。構成の意図として尊重する。
        var options = new MoomooBrokerOptions("opend", 11111, RsaPrivateKeyPath: null);

        var act = () => MoomooPreflight.Validate(options, fileExists: _ => false);

        act.Should().NotThrow();
    }

    [Fact]
    public void OpenD_ホストが空なら_preflight_で停止する()
    {
        var options = new MoomooBrokerOptions("", 11111);

        var act = () => MoomooPreflight.Validate(options, fileExists: _ => true);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Broker:Moomoo:OpenD:Host");
    }

    [Fact]
    public void OpenD_ポートが_0_なら_preflight_で停止する()
    {
        var options = new MoomooBrokerOptions("opend", 0);

        var act = () => MoomooPreflight.Validate(options, fileExists: _ => true);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Broker:Moomoo:OpenD:Port");
    }
}
