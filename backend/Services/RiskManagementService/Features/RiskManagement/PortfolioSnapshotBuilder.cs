using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, IADR-0005, IADR-0008, ADR-0009: 判定入力 PortfolioSnapshot（Domain・非永続の派生データ）を組み立てる。
// 生の運用状態（IPortfolioStateProvider）に kill switch 状態（IKillSwitchStore）と一時停止状態（IPauseStore）を合成する。
// InvestedCapital（取得額合計）・UnrealizedPnl（含み損益）はプロバイダが供給した実値をそのまま反映する。
//
// FR-19, #375, ADR-0021 決定3, IADR-0153: あわせてブローカーへ照会した口座種別の観測を合成する。
// **未供給・失効は null のまま渡す**（判定コアが新規建てを止める）。ここで既定値を補ってはならない。
//
// FR-19, FR-11, #425, ADR-0025 決定2, IADR-0165: GFV 発生回数（**自前計数**）も合成する。
// **ブローカー照会（BrokerAccountState）とは別経路である**——moomoo API に該当フィールドは存在せず、
// 自前で数えられるのは「自らのガードをすり抜けた買付」だけであり、両者が一致する保証はない。
//
// **台帳が結線されていなければ null（未供給）のまま渡す。** 判定コアは未供給を「止める」へ倒すため
// （ADR-0025 決定2 の fail-closed）、結線し忘れは**安全側**に落ちる。IADR-0148 決定2 が「必須引数にせよ」と
// したのは既定が fail-open（0 件＝合格）だったからであり、向きが逆の本件では省略可能でよい。
// FR-01, FR-02, FR-10, ADR-0020, #337, #564, IADR-0249, IADR-0267: 情報収集の縮退状態（新規建て停止）も合成する。
// **供給が「不明」（現況観測が未着・失効）のときも止める側の値が返る**——ポートが向きを持っているため、
// ここで補正しない。
// **必須依存である**（IADR-0163 決定2「不在が統制の無効を意味する依存は必須にする」——省略可能引数で
// 受けると、配線を削ってもコンパイルが通り、縮退中の新規建て停止だけが静かに効かなくなる）。
public sealed class PortfolioSnapshotBuilder(
    IPortfolioStateProvider stateProvider,
    IKillSwitchStore killSwitchStore,
    IPauseStore pauseStore,
    IBrokerAccountObservationStore accountObservations,
    IInformationDegradationStore informationDegradation,
    IGoodFaithViolationStore? goodFaithViolations = null)
{
    public PortfolioSnapshot Build()
    {
        var state = stateProvider.GetCurrent();
        var killSwitch = killSwitchStore.GetState();
        var pause = pauseStore.GetState();

        return new PortfolioSnapshot
        {
            Capital = state.Capital,
            OpenPositionCount = state.OpenPositionCount,
            InvestedCapital = state.InvestedCapital,
            DailyOrderedAmount = state.DailyOrderedAmount,
            DailyRealizedPnl = state.DailyRealizedPnl,
            UnrealizedPnl = state.UnrealizedPnl,
            DrawdownRatio = state.DrawdownRatio,
            ConsecutiveLosses = state.ConsecutiveLosses,
            SymbolsTradedToday = state.SymbolsTradedToday,
            Account = accountObservations.GetCurrent(),
            GoodFaithViolations = goodFaithViolations?.GetTally(),
            KillSwitchEngaged = killSwitch.Engaged,
            TradingPaused = pause.Paused,
            InformationDegradedBlocksNewEntries = informationDegradation.BlocksNewEntries,
        };
    }
}
