using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-05, FR-09, FR-10, #292, IADR-0118: 乖離を報告するかの判断（インメモリ・シングルトン）。
//
// 2 つの雑音を落とす。
//   1. **一過性の未反映**: 発注してから約定が台帳へ届くまでの間、台帳とブローカは正当にズレる。
//      同一内容が連続で観測されたときだけ報告することで、通常運行のたびに鳴るのを防ぐ。
//   2. **通知過多**: 解消されない乖離は毎巡回で観測され続ける。前回報告した内容と同一なら再報告しない。
//
// 状態はプロセス内のみ（乖離の権威は毎回の観測であって履歴ではない）。再起動後に 1 度だけ再報告され得るが、
// 監査は MessageId で重複を識別でき、通知も 1 通で済むため許容する。
public sealed class PositionDriftTracker(
    int requiredConsecutiveObservations = PositionDriftTracker.DefaultRequiredConsecutiveObservations)
{
    /// <summary>
    /// 報告に必要な連続観測回数。構成キーにはしない（運用で触る値ではなく、下げれば雑音・上げれば検知が遅れるだけ）。
    /// </summary>
    public const int DefaultRequiredConsecutiveObservations = 2;

    // 0・負値で「決して報告しない」状態を作らせない（検知器が黙る設定を許さない）。
    private readonly int _required = Math.Max(1, requiredConsecutiveObservations);
    private readonly object _gate = new();

    private string _observedSignature = string.Empty;
    private int _consecutive;
    private string _reportedSignature = string.Empty;

    /// <summary>今回観測した乖離を報告すべきなら true。乖離なし（空）は常に false。</summary>
    public bool ShouldReport(IReadOnlyList<PositionDriftItem> drifts)
    {
        ArgumentNullException.ThrowIfNull(drifts);

        var signature = Signature(drifts);

        lock (_gate)
        {
            if (signature == _observedSignature)
                _consecutive++;
            else
                (_observedSignature, _consecutive) = (signature, 1);

            if (drifts.Count == 0)
            {
                // 解消。報告済みを忘れることで、同じ乖離が再発したときに再び報告できる。
                _reportedSignature = string.Empty;
                return false;
            }

            if (_consecutive < _required || signature == _reportedSignature)
                return false;

            _reportedSignature = signature;
            return true;
        }
    }

    // 列挙順はブローカ応答に依存するため、順序に依らない正準形にする
    //（順序差を「内容が変わった」と誤認すると連続条件が永久に満たされない）。
    private static string Signature(IReadOnlyList<PositionDriftItem> drifts) =>
        string.Join(
            "|",
            drifts
                .Select(d => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{d.Symbol}:{(int)d.Market}:{d.LedgerQuantity}:{d.BrokerQuantity}:{(int)d.Kind}"))
                .OrderBy(s => s, StringComparer.Ordinal));
}
