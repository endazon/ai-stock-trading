using NotificationService.Features.Notifications;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-10, FR-14, #1561, IADR-0620: kill switch の版の前提条件まわりの共通の規則（REST と gRPC の両実装が使う。文言は 1 つ）。
internal static class KillSwitchVersions
{
    // 提供側（リスク管理）が拒否の本文の code（gRPC は末尾の metadata）で返す固定の語。
    internal const string OperatorHoldCode = "operator_hold";
    internal const string VersionConflictCode = "version_conflict";
    internal const string PreconditionRequiredCode = "precondition_required";

    internal const string VersionConflictMessage =
        "kill switch の状態は、確認を出した後に変わっています。解除していません。`/killswitch off` からやり直してください。";

    internal const string PreconditionRequiredMessage =
        "kill switch の版を確かめられなかったため、解除していません。`/killswitch off` からやり直してください。";

    /// <summary>版の形（10 進の数字・19 文字まで）なら前後の空白を落として返す。それ以外は null（版なしで扱う）。</summary>
    internal static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var trimmed = version.Trim();
        return trimmed.Length <= 19 && trimmed.All(char.IsAsciiDigit) ? trimmed : null;
    }

    /// <summary>拒否の code を利用者向けの結果にする。知らない code（と null）は null（呼び出し側の汎用の失敗へ）。</summary>
    internal static KillSwitchResult? RejectionOf(string? code) => code switch
    {
        // 保持の間は停止したまま（Engaged=true）。
        OperatorHoldCode => new KillSwitchResult(false, true, HttpKillSwitchController.OperatorHoldMessage),
        // 版の競合・版なしは何も変えていない（状態は不明なので Engaged は主張しない＝false。従来の失敗と同じ扱い）。
        VersionConflictCode => new KillSwitchResult(false, false, VersionConflictMessage),
        PreconditionRequiredCode => new KillSwitchResult(false, false, PreconditionRequiredMessage),
        _ => null,
    };
}
