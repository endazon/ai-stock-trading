using AwesomeAssertions;
using Discord;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;

namespace NotificationService.Tests;

// NFR-06, FR-14, UC-06, ADR-0009, IADR-0097, #1552, IADR-0615: 確認の画面（モーダル）に、確認コードの欄が
// **取引を再開・緩める 7 操作にだけ**あり、止める向きの画面には無いことを固定する。欄は任意・6 桁ちょうど。
public class DiscordConfirmationModalsTests
{
    private static IEnumerable<TextInputComponent> TextInputsOf(Modal modal) => Walk(modal.Component.Components);

    private static IEnumerable<TextInputComponent> Walk(IEnumerable<IMessageComponent> components)
    {
        foreach (var component in components)
        {
            switch (component)
            {
                case TextInputComponent input:
                    yield return input;
                    break;
                case ActionRowComponent row:
                    foreach (var inner in Walk(row.Components))
                        yield return inner;
                    break;
                case LabelComponent label:
                    foreach (var inner in Walk([label.Component]))
                        yield return inner;
                    break;
            }
        }
    }

    public static TheoryData<string, Modal> LooseningModals() => new()
    {
        { "kill switch の解除", DiscordConfirmationModals.KillSwitchDisengage() },
        { "kill switch の解除（版つき）", DiscordConfirmationModals.KillSwitchDisengage("42") },
        { "取引の再開", DiscordConfirmationModals.Resume() },
        { "段階の昇格", DiscordConfirmationModals.StagePromote("2") },
        { "GFV 停止の解除", DiscordConfirmationModals.GfvClear() },
        { "乖離の取り込み", DiscordConfirmationModals.DriftAdopt("UnitedStates-AAPL") },
        { "報告書の確定", DiscordConfirmationModals.ReportApprove("daily-2026-09-01-3") },
        { "方針の確定", DiscordConfirmationModals.PolicyApprove("daily-2026-09-01-4") },
    };

    [Theory]
    [MemberData(nameof(LooseningModals))]
    public void 再開と緩める操作の画面には任意の_6_桁の欄がある(string operation, Modal modal)
    {
        var inputs = TextInputsOf(modal).Where(i => i.CustomId == DiscordConfirmationModals.SecondFactorInputId).ToList();

        inputs.Should().ContainSingle($"{operation} の画面に確認コードの欄が 1 つある");
        var input = inputs[0];
        input.Required.Should().BeFalse("段 1 の間は空でも従来どおり操作できる");
        input.MinLength.Should().Be(6);
        input.MaxLength.Should().Be(6);
        input.Style.Should().Be(TextInputStyle.Short);
    }

    [Fact]
    public void 止める向きの画面には確認コードの欄が無い()
    {
        TextInputsOf(DiscordConfirmationModals.KillSwitchEngage())
            .Should().NotContain(i => i.CustomId == DiscordConfirmationModals.SecondFactorInputId, "起動は止める向き（ADR-0009 の非対称）");
    }

    // 確認フレーズ・理由の欄は従来どおり（欄を足しても既存の閂を外さない）。
    [Fact]
    public void 既存の確認フレーズと理由の欄は従来どおり必須()
    {
        TextInputsOf(DiscordConfirmationModals.KillSwitchDisengage())
            .Should().Contain(i => i.CustomId == DiscordConfirmationModals.KillSwitchPhraseInputId && i.Required == true);
        TextInputsOf(DiscordConfirmationModals.GfvClear()).Where(i => i.Required == true).Select(i => i.CustomId)
            .Should().BeEquivalentTo([DiscordConfirmationModals.GfvClearReasonInputId, DiscordConfirmationModals.GfvClearPhraseInputId]);
        TextInputsOf(DiscordConfirmationModals.DriftAdopt("UnitedStates-AAPL")).Where(i => i.Required == true).Select(i => i.CustomId)
            .Should().BeEquivalentTo([DiscordConfirmationModals.DriftAdoptReasonInputId, DiscordConfirmationModals.DriftAdoptPhraseInputId]);
    }

    // 対象はボタンの CustomId からそのまま写る（送信の受け取りが接頭辞の後ろを復元する）。
    [Fact]
    public void モーダルの_ID_は対象を載せ_操作ごとに接頭辞が分かれる()
    {
        DiscordConfirmationModals.StagePromote("2").CustomId.Should().Be(DiscordConfirmationModals.StagePromoteModalPrefix + "2");
        DiscordConfirmationModals.ReportApprove("daily-2026-09-01-3").CustomId
            .Should().Be(DiscordConfirmationModals.ReportApproveModalPrefix + "daily-2026-09-01-3");
        DiscordConfirmationModals.PolicyApprove("daily-2026-09-01-4").CustomId
            .Should().Be(DiscordConfirmationModals.PolicyApproveModalPrefix + "daily-2026-09-01-4");

        string[] prefixes =
        [
            DiscordConfirmationModals.ResumeModalId, DiscordConfirmationModals.StagePromoteModalPrefix,
            DiscordConfirmationModals.ReportApproveModalPrefix, DiscordConfirmationModals.PolicyApproveModalPrefix,
            DiscordConfirmationModals.GfvClearModalId, DiscordConfirmationModals.DriftAdoptModalPrefix,
            DiscordConfirmationModals.KillSwitchEngageModalId, DiscordConfirmationModals.KillSwitchDisengageModalId,
            DiscordConfirmationModals.KillSwitchDisengageModalPrefix,
        ];
        foreach (var a in prefixes)
            prefixes.Where(b => b != a).Should().NotContain(b => b.StartsWith(a, StringComparison.Ordinal), $"{a} は他の ID の接頭辞にならない");
    }

    // FR-10, FR-14, #1561, IADR-0620: 解除の確認の画面は、照会した版を ID に載せ、送信の受け取りが版を解除の要求へ添える。
    // 版の形でない値（照会の失敗・壊れた ID）は版なしの画面＝従来の ID（段 1 では従来どおり通る）。
    [Theory]
    [InlineData("42", "ast-ks-off-m-42")]
    [InlineData(" 7 ", "ast-ks-off-m-7")]
    [InlineData(null, "ast-killswitch-disengage-modal")]
    [InlineData("", "ast-killswitch-disengage-modal")]
    [InlineData("abc", "ast-killswitch-disengage-modal")]
    [InlineData("12345678901234567890", "ast-killswitch-disengage-modal")]
    public void 解除の画面の_ID_は版の形のときだけ版を載せる(string? version, string expected)
    {
        DiscordConfirmationModals.KillSwitchDisengage(version).CustomId.Should().Be(expected);
    }

    [Fact]
    public void 版つきの解除の画面の送信だけが版を運ぶ()
    {
        DiscordConfirmationModals.Route(DiscordConfirmationModals.KillSwitchDisengageModalPrefix + "42", Inputs())!.ExpectedVersion.Should().Be("42");
        DiscordConfirmationModals.Route(DiscordConfirmationModals.KillSwitchDisengageModalId, Inputs())!.ExpectedVersion
            .Should().BeNull("版なしの画面（照会の失敗・旧版のボタン）は前提条件なし");
        DiscordConfirmationModals.Route(DiscordConfirmationModals.KillSwitchEngageModalId, Inputs())!.ExpectedVersion
            .Should().BeNull("起動（止める向き）は版を運ばない");
    }

    // 版（19 桁まで）を載せたボタン・画面の ID は Discord の上限 100 文字に収まる。モーダルの接頭辞はボタンの接頭辞より長くない。
    [Fact]
    public void 版つきの解除の_ID_は上限に収まる()
    {
        DiscordConfirmationModals.KillSwitchDisengageModalPrefix.Length
            .Should().BeLessThanOrEqualTo(DiscordNetBotGateway.KillSwitchDisengageButtonPrefix.Length);
        (DiscordNetBotGateway.KillSwitchDisengageButtonPrefix.Length + 19).Should().BeLessThanOrEqualTo(100);
    }

    // ボタンの CustomId（100 文字まで）に収まった値は、モーダルの ID にも必ず収まる。
    [Fact]
    public void モーダルの接頭辞はボタンの接頭辞より長くない()
    {
        DiscordConfirmationModals.ReportApproveModalPrefix.Length.Should().BeLessThanOrEqualTo(DiscordNetBotGateway.ReportApproveButtonPrefix.Length);
        DiscordConfirmationModals.PolicyApproveModalPrefix.Length.Should().BeLessThanOrEqualTo(DiscordNetBotGateway.PolicyApproveButtonPrefix.Length);
        DiscordConfirmationModals.StagePromoteModalPrefix.Length.Should().BeLessThanOrEqualTo("ast-stage-promote-".Length);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("123456", "123456")]
    [InlineData(" 123456 ", "123456")]
    public void 送信された値は空なら_null_それ以外は前後の空白だけ落とす(string? submitted, string? expected)
    {
        DiscordConfirmationModals.SecondFactorCodeOf(submitted).Should().Be(expected);
    }

    // ---- 送信の振り分け（Route） ----

    private const string Code = "864213";

    // 送信された入力欄（ID → 値）。確認コードの欄は全モーダルで共通。
    private static Func<string, string?> Inputs(string? code = Code) => id => id switch
    {
        DiscordConfirmationModals.SecondFactorInputId => code,
        DiscordConfirmationModals.KillSwitchPhraseInputId => "ks-phrase",
        DiscordConfirmationModals.GfvClearPhraseInputId => "gfv-phrase",
        DiscordConfirmationModals.GfvClearReasonInputId => "gfv-reason",
        DiscordConfirmationModals.DriftAdoptPhraseInputId => "drift-phrase",
        DiscordConfirmationModals.DriftAdoptReasonInputId => "drift-reason",
        _ => null,
    };

    public static TheoryData<string, string, string, string?, string?, string?> Routes() => new()
    {
        { DiscordConfirmationModals.KillSwitchEngageModalId, nameof(ConfirmationKind.KillSwitchEngage), "/killswitch", "ks-phrase", null, null },
        { DiscordConfirmationModals.KillSwitchDisengageModalId, nameof(ConfirmationKind.KillSwitchDisengage), "/killswitch off", "ks-phrase", null, Code },
        { DiscordConfirmationModals.ResumeModalId, nameof(ConfirmationKind.Resume), "/resume", null, null, Code },
        { DiscordConfirmationModals.StagePromoteModalPrefix + "2", nameof(ConfirmationKind.StagePromote), "/stage promote 2", null, null, Code },
        { DiscordConfirmationModals.GfvClearModalId, nameof(ConfirmationKind.GfvClear), "/gfv clear", "gfv-phrase", "gfv-reason", Code },
        { DiscordConfirmationModals.DriftAdoptModalPrefix + "us-AAPL", nameof(ConfirmationKind.DriftAdopt), "/drift adopt AAPL us", "drift-phrase", "drift-reason", Code },
        { DiscordConfirmationModals.ReportApproveModalPrefix + "daily-2026-09-01-3", nameof(ConfirmationKind.ReportApprove), "/report approve daily-2026-09-01 3", null, null, Code },
        { DiscordConfirmationModals.PolicyApproveModalPrefix + "weekly-2026-W36-4", nameof(ConfirmationKind.PolicyApprove), "/policy approve weekly-2026-W36 4", null, null, Code },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void 送信は_ID_から種別を決め_コマンドと入力を取り出す(
        string modalId, string kind, string raw, string? phrase, string? reason, string? code)
    {
        var routed = DiscordConfirmationModals.Route(modalId, Inputs());

        routed.Should().NotBeNull();
        routed!.Kind.ToString().Should().Be(kind);
        routed.RawCommand.Should().Be(raw);
        routed.Phrase.Should().Be(phrase);
        routed.Reason.Should().Be(reason);
        routed.SecondFactorCode.Should().Be(code, "止める向き（起動）は欄が無いので運ばない");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ast-unknown-modal")]
    [InlineData("ast-stage-prm-m-")]
    [InlineData("ast-report-apv-m-daily")]
    [InlineData("ast-report-apv-m-daily-")]
    [InlineData("ast-report-apv-m--3")]
    [InlineData("ast-policy-apv-m-")]
    [InlineData("ast-drift-adopt-modal-us")]
    [InlineData("ast-drift-adopt-modal--AAPL")]
    [InlineData("ast-drift-adopt-modal-us-")]
    [InlineData("ast-ks-off-m-")]
    [InlineData("ast-ks-off-m-abc")]
    [InlineData("ast-ks-off-m--1")]
    [InlineData("ast-ks-off-m-12345678901234567890")]
    public void 書式外の_ID_は振り分けない(string modalId) =>
        DiscordConfirmationModals.Route(modalId, Inputs()).Should().BeNull();

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" 864213 ", "864213")]
    [InlineData("12345", "12345")]
    public void 確認コードは空白を落とし空なら_null_桁数は確かめない(string? submitted, string? expected)
    {
        // 桁数・数字かは提供側の門が確かめる（ボットで弾くと届かなかった失敗が記録に残らない）。画面は 6 桁ちょうどしか送らない。
        DiscordConfirmationModals.Route(DiscordConfirmationModals.ResumeModalId, Inputs(submitted))!.SecondFactorCode.Should().Be(expected);
    }

    [Fact]
    public void 振り分けの結果の文字列化は確認コードとフレーズと理由を出さない()
    {
        var routed = DiscordConfirmationModals.Route(DiscordConfirmationModals.GfvClearModalId, Inputs())!;

        routed.ToString().Should().NotContain(Code).And.NotContain("gfv-phrase").And.NotContain("gfv-reason");
    }

    // ---- 差し替えに失敗しても結果を届ける ----

    public static TheoryData<string> DeliveryFailures() => ["http", "timeout", "canceled-not-requested"];

    private static Exception FailureOf(string kind) => kind switch
    {
        "http" => new Discord.Net.HttpException(System.Net.HttpStatusCode.BadRequest, null!),
        "timeout" => new TimeoutException(),
        _ => new TaskCanceledException(),
    };

    [Theory]
    [MemberData(nameof(DeliveryFailures))]
    public async Task 差し替えが失敗したら結果を追送する(string kind)
    {
        var followups = 0;

        await DiscordInteractionResponses.ReplaceOriginalOrFollowupAsync(
            () => Task.FromException(FailureOf(kind)),
            () => { followups++; return Task.CompletedTask; },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        followups.Should().Be(1);
    }

    [Fact]
    public async Task 差し替えが通れば追送しない_要求されたキャンセルと他の例外は上げる()
    {
        var followups = 0;
        Task Followup() { followups++; return Task.CompletedTask; }
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        await DiscordInteractionResponses.ReplaceOriginalOrFollowupAsync(() => Task.CompletedTask, Followup, logger);
        followups.Should().Be(0);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var requested = () => DiscordInteractionResponses.ReplaceOriginalOrFollowupAsync(
            () => Task.FromException(new TaskCanceledException("要求された", null, cts.Token)), Followup, logger);
        await requested.Should().ThrowAsync<TaskCanceledException>();

        var other = () => DiscordInteractionResponses.ReplaceOriginalOrFollowupAsync(
            () => Task.FromException(new InvalidOperationException()), Followup, logger);
        await other.Should().ThrowAsync<InvalidOperationException>();
        followups.Should().Be(0);
    }
}
