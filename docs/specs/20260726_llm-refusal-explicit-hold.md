---
title: LLM 拒否（stopReason=refusal）を Hold 理由として明示し、拒否された断片を判断・成果物へ流さない
type: spec
status: review
related_ids: [FR-04, FR-06, FR-11, FR-16, UC-01, UC-02, ADR-0003, ADR-0011]
author: endazon (with Claude Code)
created: 2026-07-26
updated: 2026-07-26
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md
---

# 仕様書: LLM 拒否（stopReason=refusal）を Hold 理由として明示する（多層防御）

> Issue [#247](https://github.com/endazon/ai-stock-trading/issues/247)。取引フェーズ 2 前の安全課題。
> **判断の安全側縮退の精緻化**であって実弾化ではない。実弾 triple-latch（`Broker__Provider=paper` /
> `Broker:Moomoo:TrdEnv=simulate` / 起動時 real 拒否・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）
> には一切触れない。SIMULATE(paper) の挙動は拒否が起きない限り不変。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-04（AI 判断）、FR-06（報告書）、FR-11（監査ログ）、FR-16（報告書の数値定義＝数値はコード集計が権威）
- ユースケース（UC）: UC-01（定時サイクル）、UC-02（価格変動サイクル）
- ADR: [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)（不確実なら取引しない＝安全既定 Hold）、
  [ADR-0011](../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md)（LLM モデル固定・基盤既定層の追随）
- 関連 IADR: [IADR-0017](../adr/IADR-0017_trade-decision-structure.md)（判断コア・安全既定＝取引しない）、
  [IADR-0039](../adr/IADR-0039_decision-orchestration.md)（多数決・二段オーケストレーション・代表票の一体採用）、
  [IADR-0055](../adr/IADR-0055_llm-cost-metering-event.md)（LLM 費用計測は egress で行う）、
  [IADR-0061](../adr/IADR-0061_llm-production-wiring.md)（実 LLM 結線・プロンプト/生出力の全量ログ・既定オフ）、
  [IADR-0071](../adr/IADR-0071_report-service-remaining.md)（報告書散文の実 LLM 委譲・プレースホルダ縮退）、
  [IADR-0101](../adr/IADR-0101_opus-5-max-tokens.md)（`MaxTokens=4096`＝思考トークン込みの上限・劣化観測）。
  本作業で新規 [IADR-0104](../adr/IADR-0104_llm-refusal-explicit-hold.md)
- 対象 Issue: #247。上流（microservices-platform）側の前提: MSP #379 / PR #391（`stopReason` の契約追加・MSP IADR-0104）

## 目的・背景

上流の LLM ゲートウェイ（platform `LlmGateway`）は Anthropic の `stop_reason` を判別できず、安全性分類器による
**拒否（refusal）を「空応答」へ静かに縮退**させていた。MSP PR #391 でこれが修正され、`/complete` の応答に
`stopReason`（`end_turn` / `max_tokens` / `refusal` / `stop_sequence` / `tool_use`。未知値は透過）が載るようになった。

一方 AST 側の消費者は `stopReason` を**受けていない**。実装の現状は次のとおり（本作業の起点で実コード確認済み）。

| 箇所 | 応答 DTO | 判断続行の条件 |
| --- | --- | --- |
| `HttpLlmCompletionClient`（取引判断） | `CompletionResponse(Text, Sent, InputTokens, OutputTokens, Model)` | `dto is not null && dto.Sent && Text が非空` |
| `HttpReportNarrativeDrafter`（報告書散文） | `CompletionResponse(Text, Sent, Model)` | 同上 |

ここから 3 つの穴が生じている。

1. **拒否・空応答・上限到達・送信拒否が区別できない**。すべて同一の縮退（Hold もしくはプレースホルダ散文）へ落ち、
   同一の理由文字列（`"LLM ゲートウェイ送信不可のため見送り"`）になる。倒れる先は IADR-0017 の安全既定として正しいが、
   **監査（FR-11）に残る理由が誤っている**。
2. **本文の非空だけを根拠に判断へ進む構造**が残っている。現在は MSP PR #391 が拒否時の本文をゲートウェイ側で破棄する
   ため非ストリーミング `/complete` では断片が届かないが、これは**上流実装に全面依存**した安全性である。ストリーミング
   経路（MSP は `done` イベントの `stopReason` を見て破棄するのは呼び出し側責務と決定）や将来の実装変更では、拒否済みの
   断片が売買判断・報告書成果物へ流入し得る。AST 側に根拠を持った防御が無い＝多層防御の欠落。
3. **Hold の理由が監査ログへ届かない**（既存の欠落）。`TradeDecisionParser` は Hold の `rationale` を保持する
   （`LlmDecision.Hold with { Rationale = ... }`）が、`DecisionAggregator` は Hold 勝利時に投票を捨てて定数
   `LlmDecision.Hold`（`"解析不能または見送り"`）を返すため、**既定構成（`VoteCount=1`）でも LLM 由来の Hold 理由が
   失われる**。Hold は `TradeDecisionMade` を発行しないため、`TradeDecisionService.DecideAsync` の FR-11 ログ 1 行が
   Hold 時の唯一の記録であり、そこに理由が届かないと「拒否を理由として識別できる」要求を満たせない。

## 対象外（後続へ分離する）

- **ストリーミング経路の実装**。AST は非ストリーミング `/complete` のみを使う。本作業は「上流が断片を渡してきても
  AST が流さない」防御を入れるところまでで、ストリーミング購読自体は導入しない。
- **`ILlmCompletionClient` ポートの構造化返り値化**（`string` → 結果レコード）。拒否は Hold JSON の `rationale` と
  egress のログで表現でき、ポート変更は全アダプタ・テストへ波及するため本作業では採らない（IADR-0104 決定2 で棄却理由を記録）。
- **`max_tokens` の本文破棄**。IADR-0101 の劣化観測（本文が途中で切れることの検出）を壊すため行わない。区別可能な記録のみ。
- **拒否の通知・イベント化**（Discord 通知や新規イベント発行）。監査ログ・Hold 理由に残すところまで。

## 設計

### 1. 終了理由の語彙（共有・単一情報源）

`AiStockTrading.Shared.Contracts` に `Llm/LlmStopReasons` を新設し、上流語彙の写像を 1 箇所に置く。

```csharp
public static class LlmStopReasons
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string Refusal = "refusal";
    public const string StopSequence = "stop_sequence";
    public const string ToolUse = "tool_use";

    public static bool IsRefusal(string? stopReason);   // 大小無視
    public static bool IsMaxTokens(string? stopReason); // 大小無視
}
```

- 取引判断（`TradeDecisionService.Worker`）と報告書散文（`ReportService.Worker`）の 2 つの消費者が同じ判定を使うため、
  各アダプタに重複定義せず共有物へ置く（上流語彙の追随漏れが 2 箇所へ分散するのを防ぐ）。
- `enum` にしない: 未知の終了理由が既定値へ黙って落ちるのを避け、そのまま透過してログへ残す（上流 MSP IADR-0104 と同方針）。
- 置き場所は `Shared.Contracts.Llm`（`Events` 名前空間外）。イベント後方互換の契約テスト（IADR-0079・
  `EventTypeDiscovery` は `Events` 名前空間のみを母集合とする）の対象にならず、`event-schemas.baseline` は不変。

### 2. 取引判断 egress（`HttpLlmCompletionClient`）の評価順序

応答 DTO に `StopReason`（`string?`・欠落時 `null`）を追加し、**`Text` を読む前に**評価する。

```
非 2xx / 例外 / タイムアウト     → Hold（LLM ゲートウェイ送信不可のため見送り）    ※ 現行のまま
不正 JSON / JSON null            → Hold（LLM ゲートウェイ応答不正のため見送り）
!dto.Sent（機密区分の送信拒否）  → Hold（LLM ゲートウェイ送信不可のため見送り）    ※ 現行のまま
（ここで費用計測＝送信が成立した応答は本文の扱いによらず計上する。下記 3）
IsRefusal(dto.StopReason)        → 本文を破棄して Hold（LLM が要求を拒否したため見送り）
Text が空                        → Hold（上限到達なら「出力上限に到達し本文が無いため見送り」／それ以外は「応答が空のため見送り」）
IsMaxTokens(dto.StopReason)      → 本文は破棄せず継続（劣化として警告ログのみ・IADR-0101）
それ以外（end_turn / 未知値 / 欠落）→ 現行どおり本文を返す
```

不正 JSON は `ReadFromJsonAsync` が `JsonException` を投げるため、そのままでは外側の例外ハンドラ（＝伝送の失敗）に
落ちて「送信不可」と区別できない。読み取りを個別に握って「応答不正」へ分ける。

- Hold の理由文字列は 5 系統（送信不可 / 応答不正 / 拒否 / 空応答 / 上限到達で空）に分離し、`rationale` とログの
  双方で相互に区別できるようにする。倒れる先は全て Hold＝**安全側は一切変わらない**。
- 拒否時のログには本文の**長さ**を載せる（`textLength`）。全量ログ（`logPrompts`、既定オフ・IADR-0061 決定1）が
  無効でも「上流が非空の断片を渡してきた」事実を観測できる。本文そのものは従来どおり `logPrompts` 有効時のみ記録する。
- **`StopReason` 未設定（`null`）は現行挙動と完全に一致する**（未送信・未対応プロバイダ・上流未更新でも壊れない）。

### 3. 費用計測の位置（IADR-0055 との関係）

拒否は **`Sent=true` かつトークンを消費している**（上流はモデルへ実際に送信しており、「拒否でも `Sent=true` を保つ」
と決定している）。空応答・上限到達も同じで、とくに上限到達は**思考トークンを消費し切って本文が空になる形で課金される**
（IADR-0101）。そこで計測を本文の扱いから独立させ、`Sent` 判定の直後に一度だけ行う（計測点は egress 1 箇所のまま・
best-effort＝計測失敗は応答を壊さない）。

これは現行からの挙動変更である（従来は本文が非空の成功応答のみ計測し、拒否・空応答・上限到達は計上漏れしていた）。
`Sent=false`・非 2xx・例外は従来どおり計測しない。

### 4. 報告書散文 egress（`HttpReportNarrativeDrafter`）

同じ形の評価順序を入れる。拒否は本文が非空でも破棄して**プレースホルダ散文**へ倒す（成果物にしない）。`max_tokens` は
本文を残す（IADR-0071 と IADR-0101 の劣化観測を維持）が警告ログで区別する。数値には一切関与しない（FR-16 のまま）。

### 5. Hold 理由を監査ログへ届ける（`DecisionAggregator`）

Hold が勝利したとき、Buy/Sell と同じ「**実在する 1 票を代表として一体で採る**」規則を適用し、代表票の `Rationale` を
保つ（現行は定数 `LlmDecision.Hold` を返して投票を捨てている）。並べ替えキーは現行と同一（参照価格 → 損切り幅 →
根拠の序数比較）で、Hold 票は価格・損切り幅が 0 のため実質は根拠の序数順＝**決定的**。

- 首位タイ・空入力は現行どおり定数 `LlmDecision.Hold`（どの票も勝っていないため、実在票の根拠を騙らない）。
- `Action` は変わらない（Hold は Hold）ため**発注挙動は不変**。変わるのは FR-11 ログに残る理由文字列だけ。
- 二段オーケストレーション（`DecisionOrchestrator`）の**一次スクリーニングで打ち切る経路**も同様に、定数 Hold では
  なくスクリーニング判断そのものを返して根拠を保つ（`ScreenedOut=true` / `TotalVotes=0` は不変）。
- これにより、既定構成（`VoteCount=1`）で `stopReason=refusal` → `"LLM が要求を拒否したため見送り"` が
  `DecideAsync` の FR-11 ログ（`rationale=`）へ到達する。

## テスト（受け入れ基準の写像）

`HttpLlmCompletionClientTests`（`TradeDecisionService.Worker.Tests`）

- 拒否（`stopReason=refusal`）かつ**本文が非空**: Hold へ倒れ、返却 JSON に本文の断片が**含まれない**／`rationale` が拒否である
- 拒否は `Sent=false`・空応答・`max_tokens` と**相互に区別できる**理由文字列／ログである
- 拒否の大文字小文字（`REFUSAL`）を同一視する
- 拒否・空応答（上限到達）でも費用計測へトークンを渡す（`Sent=false`・非 2xx では渡さない＝現行維持）
- `max_tokens` かつ本文非空: 本文を返す（破棄しない）が上限到達を警告ログに残す
- `max_tokens` かつ本文が空: 空応答と区別できる理由で Hold
- `stopReason` 欠落・未知値（`end_turn` / `future_reason`）: 現行挙動どおり本文を返す（非破壊）

`HttpReportNarrativeDrafterTests`（`ReportService.Worker.Tests`）

- 拒否かつ本文非空: プレースホルダ散文へ倒れ、拒否された本文が成果物に**ならない**
- 拒否は送信拒否・空応答と区別できるログ／`max_tokens` の本文は破棄しない
- `stopReason` 欠落・未知値: 現行挙動どおり本文を返す

`DecisionAggregatorTests`（`TradeDecisionService.Domain.Tests`）

- Hold 勝利時に代表票の根拠を保つ（決定的選択）／首位タイ・空入力は定数 Hold のまま

`DecisionOrchestratorTests`（`TradeDecisionService.Application.Tests`）

- 一次スクリーニングで打ち切るときも見送りの根拠を保つ（`ScreenedOut` / 票数は不変）

`TradeDecisionServiceTests`（`TradeDecisionService.Application.Tests`）

- 拒否由来の Hold 応答で判断すると発注意図を作らず（`null`）、FR-11 ログに拒否の理由が残る（監査への到達を経路で固定）

`LlmStopReasonsTests`（`Shared.Contracts.Tests`）

- `null` / 空 / 未知値は拒否でも上限到達でもない（未知値の透過）

## 受け入れ基準

- [ ] `/complete` の `stopReason` を両アダプタの `CompletionResponse` で受け取り、`Text` を読む前に評価している
- [ ] `stopReason=refusal` のとき、本文が非空でも判断へ流さず Hold へ倒れ、拒否を理由として識別できるログ／`rationale` が残る
- [ ] 送信拒否（`Sent=false`）／応答不正／空応答／`max_tokens`／拒否がログ・`rationale` 上で相互に区別できる
- [ ] `report-narrative` 経路でも拒否された本文が成果物にならない
- [ ] `max_tokens` の本文は破棄されない（IADR-0101 の劣化観測を維持）
- [ ] `StopReason` 未設定時の挙動が現行と一致する（非破壊）
- [ ] 上記をテストへ写像し、`dotnet build` / `dotnet test` / `dotnet format` が通る
- [ ] 実装 ADR（[IADR-0104](../adr/IADR-0104_llm-refusal-explicit-hold.md)）に判断を記録した

## リスクと緩和

| リスク | 緩和 |
| --- | --- |
| 上流が `stopReason` を返さない構成（未対応プロバイダ・旧版）で挙動が変わる | `null` は現行の分岐へ素通り＝バイト等価。テストで固定する |
| 拒否の誤検知で正常な判断まで Hold になる | 判定は完全一致（大小無視）のみ。未知値は透過して現行挙動 |
| `DecisionAggregator` の変更が発注挙動へ波及する | 変えるのは Hold 勝利時の `Rationale` のみ。`Action`・代表票選択規則・票数は不変。既存テストで固定 |
| 拒否時の費用計測追加で二重計上 | 計測点は egress 1 箇所のまま（IADR-0055）。拒否は 1 応答 = 1 計測 |
