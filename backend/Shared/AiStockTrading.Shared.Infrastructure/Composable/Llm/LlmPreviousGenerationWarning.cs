using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Llm;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.Infrastructure.Composable.Llm;

// FR-04, ADR-0014, #1295, IADR-0524（移行期間のみ・#1296 で撤去）: 割当表が直前世代を受けたことを運用者へ知らせる。
//
// 割当表は移行期間に限り、各 5.5 系 ID の直前世代（claude-sonnet-5 / claude-haiku-4-5 / claude-opus-5）を同じ位置で受ける。
// 受けた評価には印（`LlmAssignmentEvaluation.PreviousGenerationAccepted`）が付くが、印を読む箇所が無ければ
// 「基盤がまだ切り替わっていない」ことは費用の計上ログからしか読めない。#1296 の外す条件（基盤の切り替えと PoC の確認）を
// ログで確かめられるよう、**用途とモデルの組ごとにプロセスあたり 1 回**だけ Warning を出す（毎回出すとサイクルごとに積み上がる）。
//
// 検索語（Loki）: `{namespace="ai-stock-trading"} |= "LLM 直前世代を移行期間の受け入れで採用"`。
public sealed class LlmPreviousGenerationWarning
{
    /// <summary>警告文の目印（運用者がログを引く語。作業仕様書 20261010_1295 の監査対応に検索語として書いてある）。</summary>
    public const string Marker = "LLM 直前世代を移行期間の受け入れで採用";

    /// <summary>プロセス全体で共有する既定の門（本番の呼び出し側はこれを使う）。</summary>
    public static LlmPreviousGenerationWarning Shared { get; } = new();

    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 評価が直前世代の受け入れなら、用途とモデルの組ごとに初回だけ Warning を出す。出したら true。
    /// 直前世代でない評価・2 回目以降は何もしない（false）。
    /// </summary>
    public bool WarnOnce(ILogger logger, string? purpose, LlmAssignmentEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!evaluation.PreviousGenerationAccepted)
            return false;

        if (!_warned.TryAdd($"{purpose}\u001f{evaluation.EffectiveModel}", 0))
            return false;

        logger.LogWarning(
            Marker + "（purpose={Purpose} effective={Effective} expected={Expected} outcome={Outcome}）。"
            + "基盤の LLM ゲートウェイがまだ直前世代を割り当てている。基盤の切り替えと PoC の確認が済んだら"
            + "移行段を外す（#1296）。この警告は用途とモデルの組ごとにプロセスあたり 1 回だけ出す。",
            purpose, evaluation.EffectiveModel, evaluation.ExpectedModel, evaluation.Outcome);
        return true;
    }
}
