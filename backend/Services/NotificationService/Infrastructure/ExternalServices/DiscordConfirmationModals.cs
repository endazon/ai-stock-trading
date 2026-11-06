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

    // FR-10, FR-14, #1561, IADR-0620: 版つきの解除の確認の画面（"<prefix><version>"。版は 10 進の数字だけ）。版の無い ID（上）も従来どおり受ける。
    internal const string KillSwitchDisengageModalPrefix = "ast-ks-off-m-";
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
    // FR-10, #1561: 版があれば ID に載せる（送信の受け取りが解除の要求へ添える）。版の形でなければ版なしの ID。
    internal static Modal KillSwitchDisengage(string? version = null) =>
        WithSecondFactor(new ModalBuilder()
            .WithTitle("停止の解除")
            .WithCustomId(KillSwitchVersions.Normalize(version) is { } v ? KillSwitchDisengageModalPrefix + v : KillSwitchDisengageModalId)
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

    /// <summary>
    /// NFR-06, #1552, IADR-0097: 送信された確認の画面を、モーダルの ID（完全一致か接頭辞）から操作の種別に振り分け、ハンドラへ渡す
    /// コマンドの文字列・確認フレーズ・理由・確認コードを取り出す。ID が書式外（他機能・対象の欠けた ID）なら <c>null</c>＝何もしない。
    /// <para>
    /// Gateway は結果に従ってハンドラを呼ぶだけにする（振り分けと取り出しの規則はここにだけ置き、試験で固定する）。
    /// </para>
    /// </summary>
    /// <param name="modalId">送信されたモーダルの ID。</param>
    /// <param name="valueOf">入力欄の ID から送信された値を返す（無ければ <c>null</c>）。</param>
    internal static ConfirmationSubmission? Route(string modalId, Func<string, string?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(modalId);
        ArgumentNullException.ThrowIfNull(valueOf);

        var code = SecondFactorCodeOf(valueOf(SecondFactorInputId));

        if (modalId == KillSwitchEngageModalId)
            // 止める向き: 確認コードの欄は無い（送られても運ばない）。
            return new(ConfirmationKind.KillSwitchEngage, "/killswitch", valueOf(KillSwitchPhraseInputId), null, null);
        if (modalId == KillSwitchDisengageModalId)
            return new(ConfirmationKind.KillSwitchDisengage, "/killswitch off", valueOf(KillSwitchPhraseInputId), null, code);
        if (modalId.StartsWith(KillSwitchDisengageModalPrefix, StringComparison.Ordinal))
            // FR-10, #1561, IADR-0620: 版の形でなければ書式外（版を推測しない）。
            return KillSwitchVersions.Normalize(modalId[KillSwitchDisengageModalPrefix.Length..]) is { } version
                ? new(ConfirmationKind.KillSwitchDisengage, "/killswitch off", valueOf(KillSwitchPhraseInputId), null, code, version)
                : null;
        if (modalId == ResumeModalId)
            return new(ConfirmationKind.Resume, "/resume", null, null, code);
        if (modalId == GfvClearModalId)
            return new(ConfirmationKind.GfvClear, "/gfv clear", valueOf(GfvClearPhraseInputId), valueOf(GfvClearReasonInputId), code);

        if (modalId.StartsWith(StagePromoteModalPrefix, StringComparison.Ordinal))
        {
            // 遷移先の値域はハンドラの解析が改めて確かめる。空なら書式外。
            var target = modalId[StagePromoteModalPrefix.Length..];
            return target.Length == 0 ? null : new(ConfirmationKind.StagePromote, $"/stage promote {target}", null, null, code);
        }

        if (modalId.StartsWith(ReportApproveModalPrefix, StringComparison.Ordinal))
            return VersionedOf(modalId, ReportApproveModalPrefix) is { } report
                ? new(ConfirmationKind.ReportApprove, $"/report approve {report.Key} {report.Version}", null, null, code)
                : null;

        if (modalId.StartsWith(PolicyApproveModalPrefix, StringComparison.Ordinal))
            return VersionedOf(modalId, PolicyApproveModalPrefix) is { } policy
                ? new(ConfirmationKind.PolicyApprove, $"/policy approve {policy.Key} {policy.Version}", null, null, code)
                : null;

        if (modalId.StartsWith(DriftAdoptModalPrefix, StringComparison.Ordinal))
        {
            // "<market>-<symbol>"（市場の語はハイフンを含まないため最初のハイフンで分ける）。値域の検証はハンドラの解析が行う。
            var payload = modalId[DriftAdoptModalPrefix.Length..];
            var separator = payload.IndexOf('-', StringComparison.Ordinal);
            if (separator <= 0 || separator == payload.Length - 1)
                return null;
            return new(ConfirmationKind.DriftAdopt, $"/drift adopt {payload[(separator + 1)..]} {payload[..separator]}",
                valueOf(DriftAdoptPhraseInputId), valueOf(DriftAdoptReasonInputId), code);
        }

        return null;
    }

    /// <summary>
    /// 確定の確認の ID の "&lt;periodKey&gt;-&lt;version&gt;" を取り出す（書式外は <c>null</c>）。**版番号は最後のハイフン以降**
    /// （periodKey は英数字・ハイフンのみでパーサが値域を保証する＝periodKey 自体がハイフンを含んでも曖昧にならない）。
    /// </summary>
    internal static (string Key, string Version)? VersionedOf(string customId, string prefix)
    {
        ArgumentNullException.ThrowIfNull(customId);
        ArgumentNullException.ThrowIfNull(prefix);
        if (!customId.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var payload = customId[prefix.Length..];
        var separator = payload.LastIndexOf('-');
        return separator <= 0 || separator == payload.Length - 1 ? null : (payload[..separator], payload[(separator + 1)..]);
    }
}

/// <summary>NFR-06, #1552: 確認の画面の操作の種別（送信の受け取りが呼ぶハンドラを決める）。</summary>
internal enum ConfirmationKind
{
    KillSwitchEngage,
    KillSwitchDisengage,
    Resume,
    StagePromote,
    GfvClear,
    DriftAdopt,
    ReportApprove,
    PolicyApprove,
}

/// <summary>
/// NFR-06, #1552: 振り分けの結果。🔴 <see cref="ToString"/> は確認コード・確認フレーズ・理由を出さない（ログに紛れ込ませない）。
/// </summary>
internal sealed record ConfirmationSubmission(
    ConfirmationKind Kind, string RawCommand, string? Phrase, string? Reason, string? SecondFactorCode, string? ExpectedVersion = null)
{
    public override string ToString() => $"{Kind} {RawCommand}";
}
