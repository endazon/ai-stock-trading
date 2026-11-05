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
        ];
        foreach (var a in prefixes)
            prefixes.Where(b => b != a).Should().NotContain(b => b.StartsWith(a, StringComparison.Ordinal), $"{a} は他の ID の接頭辞にならない");
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
}
