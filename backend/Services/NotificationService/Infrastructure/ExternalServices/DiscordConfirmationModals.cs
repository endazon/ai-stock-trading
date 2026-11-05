using Discord;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-14, UC-06, ADR-0009, IADR-0097, NFR-06, #1552, IADR-0615: 確認の画面（モーダル）の組み立て。
//
// Gateway（DiscordNetBotGateway）から切り出したのは、**どの操作の画面に確認コードの欄があるか**を試験で固定するためである
// （Socket* の相互作用は試験で作れないが、Modal は作れる）。送信の受け取り・ハンドラの呼び出しは Gateway が持つ。
//
// 🔴 **確認コードの欄は、取引を再開・緩める 7 操作の画面にだけ置く**（kill switch の解除・再開・段階の昇格・GFV 停止の解除・
// 乖離の取り込み・報告書の確定・方針の確定）。止める向き（kill switch の起動・一時停止・差し戻し）には置かない
// （ADR-0009 の非対称。提供側も止める向きでは確かめない）。
//
// 欄は**任意**（段 1＝記録だけの間は、鍵を登録していない利用者も従来どおり操作できる）。長さは 6 桁ちょうどに絞り、
// 途中まで入れた値を送らない（Discord の画面が送信前に止める）。数字かどうかは確かめない——確かめるのは提供側の門であり、
// ボットで弾くと「提供側に届かなかった失敗」が記録に残らない。
internal static class DiscordConfirmationModals
{
    // NFR-06, #1552: 確認コードの欄の識別子（全モーダルで共通。送信の受け取りはこの 1 つを読む）。
    internal const string SecondFactorInputId = "ast-second-factor-code";

    internal const string SecondFactorLabel = "認証アプリの 6 桁（登録していなければ空のまま）";

    internal const int SecondFactorLength = 6;

    // 確認フレーズ（kill switch・GFV・乖離の取り込み）。
    internal const string KillSwitchPhraseInputId = "ast-killswitch-phrase";
    internal const string GfvClearPhraseInputId = "ast-gfv-clear-phrase";
    internal const string GfvClearReasonInputId = "ast-gfv-clear-reason";
    internal const string DriftAdoptPhraseInputId = "ast-drift-adopt-phrase";
    internal const string DriftAdoptReasonInputId = "ast-drift-adopt-reason";

    // モーダルの識別子。IADR-0097: 操作ごとに分け、送信の受け取りが ID（接頭辞）の一致だけで種別を確定できるようにする。
    internal const string KillSwitchEngageModalId = "ast-killswitch-modal";
    internal const string KillSwitchDisengageModalId = "ast-killswitch-disengage-modal";
    internal const string GfvClearModalId = "ast-gfv-clear-modal";
    internal const string DriftAdoptModalPrefix = "ast-drift-adopt-modal-";

    // NFR-06, #1552: ボタンだけで実行していた 4 操作の確認の画面。対象（遷移先・期間キーと版番号）はボタンの CustomId から
    // そのまま写す。🔴 **接頭辞はボタンの接頭辞より短くする**（CustomId は 100 文字まで。ボタンに収まった値は必ずモーダルにも収まる）。
    internal const string ResumeModalId = "ast-resume-modal";
    internal const string StagePromoteModalPrefix = "ast-stage-prm-m-";
    internal const string ReportApproveModalPrefix = "ast-report-apv-m-";
    internal const string PolicyApproveModalPrefix = "ast-policy-apv-m-";

    // 止める向き: 確認フレーズだけ（確認コードの欄は置かない）。
    internal static Modal KillSwitchEngage() =>
        new ModalBuilder()
            .WithTitle("全取引の停止")
            .WithCustomId(KillSwitchEngageModalId)
            .AddTextInput("確認フレーズを入力してください", KillSwitchPhraseInputId, required: true)
            .Build();

    // IADR-0097: 解除も起動と同じくフレーズを求める（確認ボタンのみでは解除しない）。
    internal static Modal KillSwitchDisengage() =>
        WithSecondFactor(new ModalBuilder()
            .WithTitle("停止の解除")
            .WithCustomId(KillSwitchDisengageModalId)
            .AddTextInput("確認フレーズを入力してください", KillSwitchPhraseInputId, required: true))
            .Build();

    // #464, ADR-0028 決定2: 何を是正したかを書かせ、監査へそのまま残す（確認ボタンのみでは統制を解かない）。
    internal static Modal GfvClear() =>
        WithSecondFactor(new ModalBuilder()
            .WithTitle("GFV による停止の解除")
            .WithCustomId(GfvClearModalId)
            .AddTextInput("何を是正したか（監査に残ります）", GfvClearReasonInputId, TextInputStyle.Paragraph, required: true)
            .AddTextInput("確認フレーズを入力してください", GfvClearPhraseInputId, required: true))
            .Build();

    // FR-10, #871, IADR-0423: 対象（"<market>-<symbol>"）はボタンの CustomId から写す。
    internal static Modal DriftAdopt(string target) =>
        WithSecondFactor(new ModalBuilder()
            .WithTitle("乖離の取り込み")
            .WithCustomId(DriftAdoptModalPrefix + target)
            .AddTextInput("なぜ台帳を合わせるのか（監査に残ります）", DriftAdoptReasonInputId, TextInputStyle.Paragraph, required: true)
            .AddTextInput("確認フレーズを入力してください", DriftAdoptPhraseInputId, required: true))
            .Build();

    // NFR-06, #1552: 再開（確認フレーズは求めない＝従来どおり。確認コードの欄だけ）。
    internal static Modal Resume() =>
        WithSecondFactor(new ModalBuilder().WithTitle("一時停止の解除").WithCustomId(ResumeModalId)).Build();

    internal static Modal StagePromote(string target) =>
        WithSecondFactor(new ModalBuilder().WithTitle($"Stage {target} への昇格").WithCustomId(StagePromoteModalPrefix + target)).Build();

    // payload は "<periodKey>-<version>"（ボタンの CustomId の接頭辞の後ろ）。
    internal static Modal ReportApprove(string payload) =>
        WithSecondFactor(new ModalBuilder().WithTitle("報告書の確定").WithCustomId(ReportApproveModalPrefix + payload)).Build();

    internal static Modal PolicyApprove(string payload) =>
        WithSecondFactor(new ModalBuilder().WithTitle("方針の確定").WithCustomId(PolicyApproveModalPrefix + payload)).Build();

    private static ModalBuilder WithSecondFactor(ModalBuilder builder) =>
        builder.AddTextInput(
            SecondFactorLabel,
            SecondFactorInputId,
            TextInputStyle.Short,
            minLength: SecondFactorLength,
            maxLength: SecondFactorLength,
            required: false);

    /// <summary>
    /// 送信された確認コードの欄の値を、提供側へ運ぶ形にする（前後の空白を落とし、空なら <c>null</c>＝欄ごと送らない）。
    /// 🔴 値はここでも呼び出し側でもログに出さない。
    /// </summary>
    internal static string? SecondFactorCodeOf(string? submitted) =>
        string.IsNullOrWhiteSpace(submitted) ? null : submitted.Trim();
}
