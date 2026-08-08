using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Notification.Application.Tests;

// FR-20, FR-11, SC-02, UC-06, #466, 06_daytrading-review §4.1 追補3（2026-08-07・質問票 第15回 Q13-a）,
// IADR-0180: **`/stage promote`（承認操作）に最小取引件数の引き下げ警告を出す。**
//
// 裁定が名指しで否定したのは「`/stage status`（現況照会）だけで済ませること」である——
// 「承認前に status を読む」は**人の運用に依存する前提**であり、読まなければ警告が届かない。
public class StagePromoteWarningTests
{
    private const string Guild = "guild-1";
    private const string Channel = "channel-1";
    private const string OwnerUser = "discord-owner-1";

    // 警告文言は Risk 応答の宣言（BelowStatisticalBasis）に基づきアダプタが整形する。
    // 本テストは**ハンドラが付加するか否か**だけを見るため、識別できる文字列を置く。
    private const string Warning = "⚠ Stage 1 の最小取引件数が 5 件に設定されています（既定 100 件）。統計的な根拠…";

    private sealed class FakeStageGateController(
        string? warning, bool accepted = true, bool statusSucceeds = true) : IStageGateController
    {
        public int TransitionCalls { get; private set; }

        public int StatusCalls { get; private set; }

        public Task<StageGateStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            return Task.FromResult(statusSucceeds
                ? new StageGateStatusResult(true, "現段階: Stage 1（SIMULATE）", warning)
                : new StageGateStatusResult(false, "段階ゲートの照会に失敗しました（HTTP 503）"));
        }

        public Task<StageTransitionCommandResult> RequestTransitionAsync(
            int targetStage, CancellationToken cancellationToken = default)
        {
            TransitionCalls++;
            return Task.FromResult(new StageTransitionCommandResult(
                Succeeded: true,
                Accepted: accepted,
                Message: accepted
                    ? $"段階を Stage {targetStage} へ遷移しました。"
                    : "段階遷移は受理されませんでした（未充足の基準: 取引件数が 100 件に届かない（Stage 1→2））。",
                Stage1Warning: warning));
        }

        public Task<StageGateStatusResult> EvaluateWithdrawalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StageGateStatusResult(true, "撤退評価: 撤退基準には抵触していません。"));
    }

    private static DiscordBotOptions FullyConfigured()
    {
        var options = new DiscordBotOptions { GuildId = Guild, ChannelId = Channel };
        options.AllowedUserIds.Add(OwnerUser);
        options.UserMapping[OwnerUser] = "endazon";
        return options;
    }

    private static StageGateCommandHandler Handler(IStageGateController controller) =>
        new(controller, FullyConfigured(), NullLogger<StageGateCommandHandler>.Instance);

    private static DiscordCommandContext Context(string raw) => new(Guild, Channel, OwnerUser, false, raw);

    // 受け入れ基準1: 引き下げ状態で `/stage promote` を実行すると、応答に警告が含まれる。
    [Fact]
    public async Task 最小取引件数が引き下げられていると昇格承認の応答に警告が含まれる()
    {
        var controller = new FakeStageGateController(Warning);

        var result = await Handler(controller).HandleAsync(Context("/stage promote 2"));

        result.WasExecuted.Should().BeTrue();
        result.Message.Should().Contain(Warning);
        // 遷移そのものは呼ばれ、受理されている（警告は経路を変えない）。
        controller.TransitionCalls.Should().Be(1);
    }

    // 受け入れ基準3（**否定形・最重要**）: 警告が出ていても昇格は拒否されない。
    // 裁定は「警告を伴う利用者の明示的な選択として認める」としている。
    [Fact]
    public async Task 警告が出ていても昇格そのものは拒否されない()
    {
        var controller = new FakeStageGateController(Warning);

        var result = await Handler(controller).HandleAsync(Context("/stage promote 2"));

        result.Accepted.Should().BeTrue("止めるのではなく、選んだ事実を残すのが本件の主旨である");
        result.Message.Should().Contain("遷移しました");
    }

    // 受け入れ基準2（**否定形**）: 既定値のままなら警告は出ない。
    // 常時警告は「読まれない警告」になり、警告そのものの意味が失われる。
    [Fact]
    public async Task 既定値のままなら昇格承認に警告は出ない()
    {
        var controller = new FakeStageGateController(warning: null);

        var result = await Handler(controller).HandleAsync(Context("/stage promote 2"));

        result.WasExecuted.Should().BeTrue();
        result.Message.Should().NotContain("⚠");
        result.Message.Should().NotContain("統計的な根拠");
    }

    // 決定2（**否定形**）: 差し戻し（`/stage demote`）には出さない。
    // 裁定が名指ししたのは**昇格承認**である。安全側の操作へ同じ警告を出すと「読まれない警告」化する。
    [Fact]
    public async Task 差し戻しには警告を出さない()
    {
        var controller = new FakeStageGateController(Warning);

        var result = await Handler(controller).HandleAsync(Context("/stage demote 1"));

        result.WasExecuted.Should().BeTrue();
        controller.TransitionCalls.Should().Be(1);
        result.Message.Should().NotContain(Warning);
    }

    // 決定1: **拒否された昇格にも警告を出す。** 承認操作は行われており、設定が下がっている事実は変わらない
    // （「拒否されたときだけ警告が消える」経路を作らない）。
    [Fact]
    public async Task 受理されなかった昇格でも警告は出る()
    {
        var controller = new FakeStageGateController(Warning, accepted: false);

        var result = await Handler(controller).HandleAsync(Context("/stage promote 2"));

        result.Accepted.Should().BeFalse();
        result.Message.Should().Contain("受理されませんでした");
        result.Message.Should().Contain(Warning);
    }

    // ---- IADR-0180 決定5: **確認を出す前**にも警告を届ける（§4.1 追補3 Q13-a の趣旨） ----
    //
    // ボタンを押した後にだけ警告が出る形は、裁定が名指しで否定した「読まなければ届かない」構図と
    // 実効的に同じである（押した時点で遷移は既に受理・記録されている）。

    [Fact]
    public async Task 確認を出す前に引き下げ警告を取得できる()
    {
        var controller = new FakeStageGateController(Warning);

        var result = await Handler(controller).GetPromotionWarningAsync(Context("/stage promote 2"));

        result.Should().Be(Warning);
        controller.StatusCalls.Should().Be(1);
        // **確認前の照会で遷移を起こさない**（読み取りのみ）。
        controller.TransitionCalls.Should().Be(0);
    }

    // **否定形**: 既定値のままなら確認前にも警告は出ない。
    [Fact]
    public async Task 既定値のままなら確認前の警告は_null()
    {
        var controller = new FakeStageGateController(warning: null);

        var result = await Handler(controller).GetPromotionWarningAsync(Context("/stage promote 2"));

        result.Should().BeNull();
    }

    // **否定形**: 照会に失敗しても確認そのものは止めない（警告は昇格を妨げない）。
    [Fact]
    public async Task 照会に失敗したら確認前の警告は_null_で確認を止めない()
    {
        var controller = new FakeStageGateController(Warning, statusSucceeds: false);

        var result = await Handler(controller).GetPromotionWarningAsync(Context("/stage promote 2"));

        result.Should().BeNull();
    }

    // **否定形**: 許可外の着信へ現況を漏らさない（多層認証は確認前の照会にも掛かる）。
    [Fact]
    public async Task 許可外の利用者には確認前の警告を返さず照会もしない()
    {
        var controller = new FakeStageGateController(Warning);
        var foreign = new DiscordCommandContext(Guild, Channel, "discord-stranger", false, "/stage promote 2");

        var result = await Handler(controller).GetPromotionWarningAsync(foreign);

        result.Should().BeNull();
        controller.StatusCalls.Should().Be(0);
    }

    // 旧版 Risk（合格条件を返さない）への耐性: 宣言が無ければ警告を出さない。
    // **警告の不在を「安全である」と読ませない**ための境界であり、判定を Discord 側で写経しない帰結でもある。
    [Fact]
    public async Task 合格条件を返さない旧版応答では警告を出さない()
    {
        var controller = new FakeStageGateController(warning: null);

        var result = await Handler(controller).HandleAsync(Context("/stage promote 2"));

        result.Message.Should().NotContain("⚠");
    }
}
