using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Notification.Application.Tests;

// FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定2/決定3, IADR-0182:
// GFV 違反による停止の解除コマンドの閂（多層認証 → コマンド解析 → 確認フレーズ → Risk 呼び出し）。
//
// ADR-0028 §結果 が「**解除操作そのものが新たな攻撃面・誤操作面になる**」と明記しており、
// **kill switch と同じ確認水準・同じ多層認証**に揃えることが issue の要求である。
//
// **拒否時に Risk を呼ばないこと**が本テスト群の主眼である——閂が「呼んだ後で無視する」形なら、
// 統制の解除がサーバ側で実際に起きてしまう。
public class GoodFaithViolationCommandHandlerTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";
    private const string Phrase = "解除します";
    private const string Reason = "決済済み資金の判定を修正した";

    private sealed class FakeController : IGoodFaithViolationController
    {
        public int Calls { get; private set; }

        public string? LastReason { get; private set; }

        public Task<GoodFaithViolationClearResult> ClearAsync(
            string reason, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastReason = reason;
            return Task.FromResult(new GoodFaithViolationClearResult(
                true, true, "GFV 違反による停止を解除しました（対象 2 件）。"));
        }
    }

    // 確認フレーズは kill switch と**同じ設定値**を用いる（ADR-0028 §結果 が求める「同じ確認水準」）。
    // 解除ごとに別のフレーズを設ける根拠が計画に無く、設定点を増やすと**設定漏れの面が増える**。
    private static DiscordBotOptions FullyConfigured(string? phrase = Phrase)
    {
        var options = new DiscordBotOptions
        {
            GuildId = Guild,
            ChannelId = Channel,
            KillSwitchConfirmationPhrase = phrase,
        };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static GoodFaithViolationCommandHandler Handler(
        IGoodFaithViolationController controller, DiscordBotOptions options) =>
        new(controller, options, NullLogger<GoodFaithViolationCommandHandler>.Instance);

    private static DiscordCommandContext Context(
        string raw = "/gfv clear", string? user = OwnerUser, bool isDm = false) =>
        new(Guild, Channel, user, isDm, raw);

    // 受け入れ基準1: 閂をすべて通れば解除が実行される。
    [Fact]
    public async Task 本人が確認フレーズを添えれば解除を実行する()
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), Phrase, Reason);

        result.WasExecuted.Should().BeTrue();
        result.Cleared.Should().BeTrue();
        controller.Calls.Should().Be(1);
        // ADR-0028 決定2: 理由に操作者が残る（監査ログの「誰が」の土台）。
        controller.LastReason.Should().Contain("endazon");
    }

    // 🔴 受け入れ基準4（**否定形**）: 確認フレーズが違えば **Risk を呼ばない**。
    [Theory]
    [InlineData("ちがうフレーズ")]
    [InlineData("")]
    [InlineData(null)]
    public async Task 確認フレーズが不一致_未入力なら_Risk_を呼ばない(string? phrase)
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0, "拒否は Risk を呼ぶ前に起きなければならない");
    }

    // 🔴 **否定形**: 確認フレーズが**未設定**なら拒否する（安全既定）。
    // 設定漏れを「フレーズ不要」と解釈すると、統制の解除が確認なしで通る。
    [Fact]
    public async Task 確認フレーズが未設定なら解除を拒否する()
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured(phrase: "")).HandleAsync(Context(), Phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    // 受け入れ基準3（**否定形**）: 多層認証を通らない要求は拒否する（DM・許可外の利用者・別チャンネル）。
    [Fact]
    public async Task DM_からの解除要求は拒否する()
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context(isDm: true), Phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    [Fact]
    public async Task 許可リストに無い利用者の解除要求は拒否する()
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured())
            .HandleAsync(Context(user: "discord-stranger"), Phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    // **否定形**: 空設定（多層認証が構成されていない）は全拒否＝安全既定。
    [Fact]
    public async Task 多層認証が未設定なら全拒否する()
    {
        var controller = new FakeController();

        var result = await Handler(controller, new DiscordBotOptions()).HandleAsync(Context(), Phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    // 🔴 **否定形**: GFV 解除以外のコマンドは本ハンドラで実行しない。
    // 種別を絞らないと、別種のコマンドが解除経路へ落ちる。
    [Theory]
    [InlineData("/killswitch")]
    [InlineData("/killswitch off")]
    [InlineData("/pause")]
    [InlineData("/stage promote 2")]
    [InlineData("/gfv")]        // 副コマンド欠落
    [InlineData("/gfv clea")]   // typo
    [InlineData("/gfv clear extra")]
    public async Task GFV解除以外のコマンドは実行しない(string raw)
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(raw), Phrase, Reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    // 🔴 **否定形**: 解除の理由が空なら **Risk を呼ばない**。
    //
    // ADR-0028 §理由 は「解除の記録が監査ログに残ることで、**後から「なぜ解除したか」を追える**」
    // ことを決定2 の根拠に挙げている。**決定3 により Discord が唯一の窓口である**ため、
    // ここを定型文で埋めると**全解除の理由が同一文字列になり「なぜ」が監査から復元できない**。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task 解除の理由が空なら_Risk_を呼ばない(string? reason)
    {
        var controller = new FakeController();

        var result = await Handler(controller, FullyConfigured()).HandleAsync(Context(), Phrase, reason);

        result.WasExecuted.Should().BeFalse();
        controller.Calls.Should().Be(0);
    }

    // 利用者が入力した理由が**そのまま** Risk へ渡る（監査に残るのは定型文ではなく「何を是正したか」）。
    [Fact]
    public async Task 利用者が入力した理由が監査へ渡る()
    {
        var controller = new FakeController();

        await Handler(controller, FullyConfigured()).HandleAsync(Context(), Phrase, Reason);

        controller.LastReason.Should().Contain(Reason, "定型文ではなく利用者の記述が残る");
        controller.LastReason.Should().Contain("endazon", "誰が解除したかも辿れる");
    }
}
