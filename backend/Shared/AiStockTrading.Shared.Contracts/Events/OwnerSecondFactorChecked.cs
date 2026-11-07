namespace AiStockTrading.Shared.Contracts.Events;

// NFR-06, FR-14, FR-11, FR-09, IADR-0615, IADR-0626 決定 3・4: 危険な操作の第 2 要素（認証アプリの 6 桁）を確かめた結果。
// リスク管理（2 サービスで共有する状態の持ち主）が発行し、監査が記録し、通知がオーナーへ知らせる。
//
// 🔴 **コードそのものは載せない。** 載せるのは操作・対象・結果・失敗の数・停止の状態だけである。
// 発行するのは Verified・Invalid・Replayed・LockedOut の 4 つ（欠け〔Missing〕・未構成〔NotConfigured〕は状態を変えないので発行しない）。
public record OwnerSecondFactorChecked(
    // 操作の種類（SecondFactorOperation の名前。例 KillSwitchDisengage）。
    string Operation,
    // 操作の対象（例 kill-switch・stage:Stage2Small・<期間キー>@v<版>）。コードを確かめた結果はこの対象に結び付く。
    string Target,
    // 結果（SecondFactorOutcome の名前）。
    string Outcome,
    // 直近の停止の後の、連続した失敗の数（成功で 0 に戻る）。
    int ConsecutiveFailures,
    // 止めた回数（成功で 0 に戻る）。
    int LockoutCount,
    // 時間で解ける停止の終わり（止めていなければ null）。
    DateTimeOffset? LockedUntil,
    // 手動の解除（シークレットの作り直し）まで閉じているか。
    bool ManualUnlockRequired,
    // この確かめで止め始めたか（停止の通知の起点）。
    bool LockStarted,
    DateTimeOffset CheckedAt);
