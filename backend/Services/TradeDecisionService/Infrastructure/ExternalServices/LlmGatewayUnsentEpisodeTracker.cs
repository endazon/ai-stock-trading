using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-09, FR-11, #1267, IADR-0517: Sent=false の連続を「いつ通知すべきか」だけを決める純粋な判定器
// （FxSourceStatusTracker と同じく、発行の判断とメッセージバスを分ける）。
//
// 規則:
//   - Sent=false が**連続して** Threshold 回に達したら 1 回だけ `LlmGatewayUnsentDetected` を返す（1 回の連続＝1 件）。
//   - 通知済みの連続の後に Sent=true が来たら `LlmGatewayUnsentRecovered` を返し、状態を初期化する。
//   - 通知前に Sent=true が来たら黙って数え直す（一過性の不調を通知しない）。
//
// 🔴 **サイクルを数えない。** 判定器は呼び出しの順だけを見る。1 サイクルの全件（定時の保有 6 件の一次＝6 回）でも、
// 呼び出しの少ないサイクルの連続（急変の 1 銘柄＝1〜数回）でも、Sent=true が挟まらない限り同じ連続として数える。
// サイクルの境界を判定器へ渡すと、境界を運ぶ経路（定時・急変・再配信）ごとに配線が要り、片方だけ漏れる。
//
// 🔴 **用途（purpose）も分けずに 1 本で数え、内訳を運ぶ**（#1267 の AI レビュー 🟡・IADR-0517 決定4）。Sent=false は
// ゲートウェイの状態であって用途の性質ではない。一次（screening）と二次（trade-decision）は同じサイクルで交互に呼ばれ得るため、
// 用途ごとに数えると同じ障害の検知が遅れ、通知も 2 通に割れる。代わりに用途別の件数（UnsentByPurpose）を通知・台帳へ載せる。
public sealed class LlmGatewayUnsentEpisodeTracker
{
    /// <summary>既定のしきい値（連続回数）。定時の 1 サイクル（保有 6 件の一次）に収まる回数にする。</summary>
    public const int DefaultThreshold = 5;

    private readonly object _gate = new();
    private readonly int _threshold;

    private int _consecutive;
    private readonly SortedDictionary<string, int> _byPurpose = new(StringComparer.Ordinal);
    private DateTimeOffset? _firstUnsentAt;
    private bool _notified;

    public LlmGatewayUnsentEpisodeTracker(int threshold = DefaultThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        _threshold = threshold;
    }

    public int Threshold => _threshold;

    /// <summary>Sent=false を 1 件受け、通知すべきなら <see cref="LlmGatewayUnsentDetected"/> を返す（無ければ null）。</summary>
    public LlmGatewayUnsentDetected? OnUnsent(string purpose, LlmGatewayUnsentCause cause, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cause);

        lock (_gate)
        {
            _consecutive++;
            _firstUnsentAt ??= now;
            _byPurpose[purpose] = _byPurpose.GetValueOrDefault(purpose) + 1;

            if (_notified || _consecutive < _threshold)
                return null;

            _notified = true;
            return new LlmGatewayUnsentDetected(
                purpose, _consecutive, cause.Kind?.ToString(), cause.UpstreamStatusCode, cause.RoutingReason,
                cause.GatewayText, _firstUnsentAt.Value, now, Snapshot());
        }
    }

    /// <summary>Sent=true を 1 件受け、通知済みの連続が終わったなら <see cref="LlmGatewayUnsentRecovered"/> を返す。</summary>
    public LlmGatewayUnsentRecovered? OnSent(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_consecutive == 0)
                return null;

            LlmGatewayUnsentRecovered? recovered = _notified
                ? new LlmGatewayUnsentRecovered(_consecutive, _firstUnsentAt!.Value, now, Snapshot())
                : null;

            _consecutive = 0;
            _byPurpose.Clear();
            _firstUnsentAt = null;
            _notified = false;
            return recovered;
        }
    }

    /// <summary>
    /// 発行に失敗した分を戻す。🔴 戻さないと「通知済み」が残り、**送信不可が続いているのに二度と通知されない**
    /// （FxSourceStatusTracker.Rollback と同じ理由）。
    /// </summary>
    public void Rollback(object published)
    {
        lock (_gate)
        {
            switch (published)
            {
                case LlmGatewayUnsentDetected:
                    // 次の Sent=false で再び通知を試みる（連続の件数・始まりは保つ）。
                    _notified = false;
                    break;
                case LlmGatewayUnsentRecovered recovered when _consecutive == 0:
                    // 回復の通知を落とさない: 通知済みの連続へ戻し、次の Sent=true で再び回復を発行する。
                    _consecutive = recovered.UnsentCalls;
                    _firstUnsentAt = recovered.FirstUnsentAt;
                    _notified = true;
                    foreach (var (purpose, count) in recovered.UnsentByPurpose ?? new Dictionary<string, int>())
                        _byPurpose[purpose] = count;
                    break;
            }
        }
    }

    // 呼び出し時点の用途別の件数の写し（イベントは不変の値を持つ）。
    private Dictionary<string, int> Snapshot() => new(_byPurpose, StringComparer.Ordinal);
}
