---
title: IADR-0104 LLM の拒否（stopReason=refusal）を本文より先に評価し、Hold 理由として明示する（多層防御）
type: impl-adr
status: Accepted
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

# IADR-0104: LLM の拒否（stopReason=refusal）を本文より先に評価し、Hold 理由として明示する（多層防御）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-26
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（AI 判断）、FR-06（報告書）、FR-11（監査ログ）、FR-16（数値はコード集計が権威）、
  UC-01（定時サイクル）、UC-02（価格変動サイクル）、
  [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)（不確実なら取引しない）、
  [ADR-0011](../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md)（LLM モデル固定・基盤既定層の追随）
- 対象 Issue: [#247](https://github.com/endazon/ai-stock-trading/issues/247)
- 上流（microservices-platform）: #379 / PR #391（`/complete` の応答へ `stopReason` を追加。MSP IADR-0104・MSP ADR-0025）
- 関連する実装仕様書: [20260726_llm-refusal-explicit-hold](../specs/20260726_llm-refusal-explicit-hold.md)
- 関連 IADR: [IADR-0017](IADR-0017_trade-decision-structure.md)（判断コア・安全既定＝取引しない）、
  [IADR-0039](IADR-0039_decision-orchestration.md)（多数決・代表票の一体採用）、
  [IADR-0055](IADR-0055_llm-cost-metering-event.md)（LLM 費用計測は egress で行う）、
  [IADR-0061](IADR-0061_llm-production-wiring.md)（実 LLM 結線・全量ログ・既定オフ）、
  [IADR-0071](IADR-0071_report-service-remaining.md)（報告書散文の実 LLM 委譲・プレースホルダ縮退）、
  [IADR-0079](IADR-0079_event-backward-compat-contract-test.md)（イベント契約テストの母集合＝`Events` 名前空間）、
  [IADR-0101](IADR-0101_opus-5-max-tokens.md)（`MaxTokens=4096`・劣化観測）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（実弾 triple-latch＝本決定は触れない）

## 背景・課題

上流の LLM ゲートウェイは Anthropic の `stop_reason` を判別できず、安全性分類器による**拒否を「空応答」へ静かに縮退**
させていた（MSP #379）。PR #391 でこれが修正され、`/complete` の応答に `stopReason` が載る。既定モデル層は
MSP ADR-0025 で `claude-opus-5` であり、**拒否は HTTP 200・例外なしで実際に起き得る経路**である。

AST 側の 2 つの消費者（取引判断の `HttpLlmCompletionClient`・報告書散文の `HttpReportNarrativeDrafter`）は
`stopReason` を受けておらず、判断続行を `Sent` と**本文が非空か**だけで決めている。ここから 3 つの穴が生じる。

1. 拒否・空応答・上限到達・送信拒否が同一の縮退・同一の理由文字列へ潰れ、**監査（FR-11）に残る理由が誤る**。
2. **本文の非空を根拠に判断へ進む構造**が残る。現在拒否の断片が届かないのは MSP が本文を破棄しているからであり、
   **上流実装に全面依存**した安全性である（ストリーミング経路では上流の破棄は効かず、破棄は呼び出し側責務と決定されている）。
3. `DecisionAggregator` が Hold 勝利時に投票を捨てて定数 `LlmDecision.Hold` を返すため、**Hold の理由が監査ログへ
   届かない**（既定 `VoteCount=1` でも失われる既存の欠落）。Hold は `TradeDecisionMade` を発行しないため、
   `DecideAsync` の FR-11 ログ 1 行が唯一の記録である。

## 決定

### 決定1: 終了理由の語彙を `Shared.Contracts.Llm.LlmStopReasons` に single source として置く

上流語彙（`end_turn` / `max_tokens` / `refusal` / `stop_sequence` / `tool_use`）の写像と判定（`IsRefusal` /
`IsMaxTokens`・大小無視）を共有物へ置き、取引判断と報告書散文の 2 消費者が同じ判定を使う。各アダプタに重複定義すると
上流語彙の追随漏れが 2 箇所へ分散する。

`enum` にしない: 未知の終了理由が既定値へ黙って落ちるのを防ぎ、そのまま透過してログへ残す（上流 MSP IADR-0104 と同方針）。
置き場所は `Events` 名前空間**外**のため、イベント後方互換の契約テスト（IADR-0079。母集合は `EventTypeDiscovery` ＝
`Events` 名前空間の record 型）の対象にならず、`event-schemas.baseline` は不変である。

### 決定2: 拒否は「本文を読む前に」評価し、本文が非空でも破棄して Hold へ倒す

両アダプタの応答 DTO へ `StopReason`（`string?`）を追加し、評価順序を次のとおり固定する。

```
非 2xx / 例外 / タイムアウト → Hold（送信不可）                      ※ 現行のまま
dto is null（空・不正 JSON） → Hold（応答不正）
!dto.Sent                    → Hold（送信不可）                      ※ 現行のまま
IsRefusal(StopReason)        → 本文を破棄して Hold（拒否）           ← 追加（本文を読む前）
Text が空                    → Hold（上限到達で空 / 応答が空）
IsMaxTokens(StopReason)      → 本文は破棄せず継続（警告ログのみ）
それ以外（end_turn/未知値/欠落）→ 現行どおり本文を返す
```

上流が拒否時に本文を破棄していても、AST 側で**独立に**遮断する（多層防御）。倒れる先は全て Hold であり、
IADR-0017 の安全既定は変わらない。`StopReason` が `null`（上流未更新・未対応プロバイダ）のときは現行と完全に一致する
（非破壊）。

**棄却案**: `ILlmCompletionClient` の返り値を `string` から結果レコードへ構造化する案。拒否を型で表現できて明快だが、
ポート・全アダプタ・スタブ・既存テストへ波及する一方、得られる安全性は本決定（egress で遮断＋理由付き Hold JSON）と
同じである。安全課題の修正としては波及の小さい方を採る。

### 決定3: Hold の理由を 5 系統に分離する（監査で相互に区別できるようにする）

`rationale` とログの双方を「送信不可 / 応答不正 / 拒否 / 空応答 / 上限到達で空」に分離する。Hold へ倒れること自体は
どれも同じでも、**なぜ倒れたかが切り分けられなければ運用（日報・監査）で原因を追えない**というのが #247 の主題である。

拒否のログには本文の**長さ**（`textLength`）を載せる。全量ログ（`logPrompts`・既定オフ・IADR-0061 決定1）が無効でも
「上流が非空の断片を渡してきた」事実＝多層防御が実際に効いた事実を観測できる。本文そのものは従来どおり `logPrompts`
有効時のみ記録する（プロンプト・生出力の機微を既定でログ基盤へ流さない最小権限を崩さない）。

### 決定4: 拒否時もトークンを費用計測へ渡す

拒否は `Sent=true` かつモデルへ実送信済み＝**課金が発生している**。したがって拒否時も `ILlmUsageReporter` へ渡す
（best-effort＝計測失敗は応答を壊さない・現行と同じ）。`Sent=false`（越境させておらず費用が発生していない）で計測
しない現行の扱いとは別事象である。計上漏れは NFR の費用統制（月次上限・#23）を過少評価させるため、拒否は計測へ含める。

### 決定5: `max_tokens` の本文は破棄しない

上限到達は「拒否」ではなく劣化であり、本文を破棄すると IADR-0101 が入れた劣化観測（本文が途中で切れることの検出）を
壊す。本文は返しつつ警告ログで区別する。本文が**空**の上限到達だけは Hold へ倒れる（従来と同じ安全側）が、理由文字列を
通常の空応答と分ける。

### 決定6: `DecisionAggregator` は Hold 勝利時も代表票の根拠を保つ

Buy/Sell と同じ「**実在する 1 票を代表として一体で採る**」規則（IADR-0039）を Hold にも適用する。並べ替えキーは現行と
同一（参照価格 → 損切り幅 → 根拠の序数比較）で、Hold 票は価格・損切り幅が 0 のため実質は根拠の序数順＝決定的である。

- 首位タイ・空入力は現行どおり定数 `LlmDecision.Hold`。どの票も勝っていないため、実在票の根拠を騙らない。
- `Action` は不変（Hold は Hold）＝**発注挙動は変わらない**。変わるのは FR-11 ログに残る理由文字列だけ。

この変更なしでは、決定3 で分離した理由が集約で捨てられ「拒否を理由として識別できる」が既定構成でも成立しない。
すなわち本決定は #247 の受け入れ基準に対して**任意の改善ではなく必要条件**である。

## 影響

- 取引判断: 拒否時は本文の内容によらず Hold（発注抑止）。拒否が起きない限り現行とバイト等価。
- 報告書: 拒否時はプレースホルダ散文（数値には一切関与しない・FR-16 のまま）。
- 監査（FR-11）: Hold の理由が egress ログと `DecideAsync` のログ双方で原因別に残る。
- 費用（NFR・#23）: 拒否分のトークンが計上される（従来は計上漏れ）。
- 実弾（IADR-0060 の triple-latch）・SIMULATE の設定・イベント契約・DB スキーマはいずれも不変。

## 代替案

- **AST 側では何もせず上流の破棄に委ねる**: 非ストリーミング `/complete` では現に安全だが、単一実装への全面依存で
  あり、ストリーミング経路・将来の実装変更で断片が判断へ流入する。#247 が求めるのは多層防御であり採らない。
- **拒否を例外・エラーとして扱う**: 拒否は正常応答（HTTP 200）であり、例外化するとリトライ・アラートの意味が壊れる。
  倒れる先は安全既定の Hold が正しい。
- **拒否を新規イベント／通知にする**: 監査ログで足りる段階であり、イベント契約の追加は過剰。必要になれば後続で足す。
