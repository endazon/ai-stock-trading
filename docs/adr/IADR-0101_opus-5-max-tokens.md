---
title: IADR-0101 基盤既定モデルの Opus 5 化に備え LLM 呼び出しの MaxTokens を 1024 → 4096 へ引き上げる
type: impl-adr
status: Accepted
related_ids:
  - FR-04
  - FR-06
  - ADR-0011
  - IADR-0017
  - IADR-0055
  - IADR-0061
author: claude
created: 2026-07-24
updated: 2026-07-25
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md (取引判断の LLM モデル固定・Accepted)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (基盤のグローバル既定を Opus 5 へ改定・Accepted)"
---

# IADR-0101: LLM 呼び出しの MaxTokens 引き上げ（基盤 Opus 5 化への追従）

- 状態: Accepted
- 日付: 2026-07-24
- 決定者: claude（実装）

## 起点・関連

- 基盤側 `MSP/ADR-0025` によりゲートウェイのグローバル既定が `claude-opus-4-8` → `claude-opus-5` へ改定される
  （実装追従は `MSP/IADR-0101`）。本 IADR はその改定に対する本リポジトリ側の防御的追従である。
- 仕様書: `docs/specs/20260724_opus-5-max-tokens.md`。

## コンテキストと課題

Opus 5 は Opus 4.8 と異なり `thinking` 省略時に adaptive thinking が有効になり、`max_tokens` が
**思考トークンと本文の合算上限**になる。本リポジトリの 2 つの呼び出しは `MaxTokens: 1024` を
ハードコードし、かつ `purpose` が基盤の `PurposeModels` に未登録のため `default`（Opus 5 化される層）
へ着地する。

基盤側は `CompletionApiRequest.MaxTokens` の既定を 4096 へ引き上げたが、**明示指定している呼び出しは
既定値の影響を受けない**ため、本リポジトリでの対応が必須である。

想定される障害の性質が悪い点を強調する。思考が上限を食い切ると応答に `TextContent` が含まれず
本文が空になるが、**HTTP は 200 で返り例外も発生しない**。

- 取引判断: 空応答 → `HoldFallback` へ縮退（IADR-0017 の安全既定どおり）。安全側ではあるが
  **全判断が Hold に固定され、取引機能が事実上停止**する。ログは「送信不可/空応答」としか言わないため
  原因追跡が難しい。
- 報告書生成: 安全網が無く、途中で切れた文章がそのまま成果物になる。

## 検討した選択肢

1. **両呼び出しの `MaxTokens` を 4096 へ引き上げる（採用）** — 基盤の既定値と揃う。変更は 2 行。
2. `MaxTokens` を設定可能にする（`LlmGateway:MaxTokens` 等） — 運用で調整できるが、現時点で
   値を変える要件が無く、CLAUDE.md の「過剰な抽象化を行わない」に反する。必要になった時点で行う。
3. 対応せず基盤側のピン留め（ADR-0011 フォローアップ）だけで凌ぐ — **不十分**。ADR-0011 §決定は
   「報告書生成の LLM は別扱い。基盤の既定モデルを用いてよい」と定めており、`report-narrative` は
   仕様上 `default` に追随するのが正しい。取引用途をピン留めしても報告書生成の切断は残る。

## 決定

`HttpLlmCompletionClient`（`trade-decision`）と `HttpReportNarrativeDrafter`（`report-narrative`）の
`/complete` 要求の `MaxTokens` を **1024 → 4096** に引き上げる。基盤の
`CompletionApiRequest` 既定値（`MSP/IADR-0101`）と同値に揃える。

`thinking` / `effort` の明示送信は行わない（ゲートウェイの責務であり、本リポジトリからは送れない）。

## 理由

- 4096 は「本文想定長（〜1024）＋ adaptive thinking の作業領域（〜3000）」の見積りで、基盤側と同一の根拠に立つ。
  基盤とクライアントで値が食い違うと、どちらが効いているか追跡しにくくなる。
- `max_tokens` は**上限であって固定消費ではない**。短い応答のコストは変わらず、増えるのは思考分の実消費のみ。
  使用量は IADR-0055 の計測点（egress）で実測でき、月次上限超過時にサイクル間隔を延長する既存機構で暴走を防げる。
- 基盤が Opus 5 化される前に本変更が入っても無害（Opus 4.8 でも上限は上限）。マージ順序に依存しない。

## 結果

- 良い影響: Opus 5 化後も取引判断が静かに Hold 固定にならず、報告書の文章が途中で切れない。
- 悪い影響 / トレードオフ: 1 応答あたりの最大出力トークンが 4 倍になり、異常系での最大コストが増える。
  実消費の増分は思考分に限られる見込みだが、実測で確認する。
- フォローアップ:
  1. Opus 5 化後の出力トークン実測と 4096 の再調整（基盤 `MSP/IADR-0101` のフォローアップ 1 と対で行う）。
  2. **`ADR-0011` の未実施フォローアップ**（基盤 `PurposeModels` の取引用途に固定モデル ID を設定する）。
     本 IADR は `max_tokens` のみを扱い、モデルの自動追随そのものは解消しない。基盤側での対応が必要。
  3. 月次 LLM 費用上限（15,000 円・`05_trading-assumptions` §6）の消費見積り再評価。

## 関連

- Supersedes: なし
- Superseded by: なし
- 参照の是正（2026-07-25）: 基盤側の実装ADR は当初 `MSP/IADR-0100` として起票されたが、基盤 develop に
  別の `IADR-0100`（経路B ノードの inotify 上限を sysctl DaemonSet で引き上げる決定・
  endazon/microservices-platform#375）が先にマージされたため **`MSP/IADR-0101`** へ採番し直された
  （endazon/microservices-platform#376）。本 IADR 内の参照 3 箇所を是正した。決定内容の変更はない。
  なお本リポジトリの `IADR-0100`（経路B の values-local 恒常設定）とは無関係であり、基盤側を指す参照には
  `MSP/` 接頭辞を付けて区別する。
- 関連要求 / UC: FR-04（AI 判断のガードレール）、FR-06/16（報告書生成）、
  [ADR-0011](../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md)
