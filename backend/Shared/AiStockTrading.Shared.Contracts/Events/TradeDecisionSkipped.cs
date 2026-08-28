namespace AiStockTrading.Shared.Contracts.Events;

// FR-04, FR-09, FR-11, UC-01, ADR-0014 §決定3, ADR-0017 決定2, #335, IADR-0216:
// 割当モデルが利用できないため**取引判断を実行せず、発注も行わなかった**。
//
// 🔴 **これは障害ではなく、設計上の正常な結果である**（ADR-0017 決定2）。金融取引において
// 「判断できないので見送る」は正常な結果であり、「別のモデルで代替して判断する」より安全である。
// 計画が本文に明記したのは、**実装・運用の段階で「モデルが使えないのに発注が出ない＝バグ」と誤認され、
// 善意でフォールバックが追加される**ことを防ぐためである。本イベントを「エラー」として扱わないこと。
//
// ADR-0017 決定2 はあわせて次を求める:
//   - モデル利用不能により取引判断がスキップされた事実は**記録し、通知する**（沈黙のスキップにしない）
//   - **日報に当日のスキップ回数を記載する**（取引機会を逸した回数は、日報を方針書として読むうえで必要な情報）
//
// Reason は機械可読な事由（`TradeDecisionSkipReasons`）。EffectiveModel は基盤が名乗ったモデル（不明なら null）。
public record TradeDecisionSkipped(
    string Purpose,
    string Reason,
    string? ExpectedModel,
    string? EffectiveModel,
    DateTimeOffset OccurredAt);

// スキップ事由の語彙。enum ではなく文字列で持つのは、語彙が増えたときに未知の値が既定値へ黙って落ちるのを
// 避けるためである（Shared.Contracts.Llm.LlmStopReasons と同じ方針）。
public static class TradeDecisionSkipReasons
{
    /// <summary>上流が HTTP 400 系を返した＝モデルが利用できない（ADR-0017 決定3）。429 はここに含めない。</summary>
    public const string ModelUnavailable = "model-unavailable";

    /// <summary>
    /// 応答は返ったが、**ピン留めしたモデルではないモデル**が答えた（基盤の用途エントリ未登録・ZDR 除外・
    /// 提供終了で `DefaultModel` へ落ちた等）。別モデルの応答で発注することは ADR-0014 §決定3 の
    /// 「検証したモデルと本番モデルの一致」を空洞化させるため、鎖の有無にかかわらず見送る。
    /// </summary>
    public const string ModelMismatch = "model-mismatch";

    /// <summary>本システムで使用しないと決めたモデルが答えた（ADR-0015 / ADR-0017 決定1）。</summary>
    public const string ForbiddenModel = "forbidden-model";
}
