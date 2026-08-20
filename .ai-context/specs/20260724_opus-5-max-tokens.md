---
title: 基盤既定モデルの Opus 5 化に備え LLM 呼び出しの MaxTokens を 1024 → 4096 へ引き上げる
type: spec
status: done
related_ids:
  - FR-04
  - FR-06
  - ADR-0011
  - IADR-0017
  - IADR-0061
  - IADR-0101
author: claude
created: 2026-07-24
updated: 2026-07-25
related_specs:
  - "../adr/IADR-0101_opus-5-max-tokens.md"
---

# 仕様書: LLM 呼び出しの MaxTokens 引き上げ（基盤 Opus 5 化への追従）

## 起点となる計画書（トレーサビリティ）

- 計画根拠: `MSP/ADR-0025`（LLM 利用モデルの改定 — グローバル既定を Claude Opus 5 へ更新・Accepted）。
  基盤 microservices-platform の LLM ゲートウェイのグローバル既定が `claude-opus-4-8` → `claude-opus-5`
  へ改定される（実装追従は `MSP/IADR-0101`、PR endazon/microservices-platform#376）。
- 制約: ADR-0011（計画リポ）
  （取引判断の LLM はモデルバージョンを固定し、基盤のモデル改定に自動追随しない・Accepted）。
  同 §決定は「**報告書生成の LLM は別扱い**。基盤の既定モデルを用いてよい」とも定める。
- 要求: FR-04（AI 判断のガードレール）、FR-06/16（報告書生成）。
- 本作業の実装判断は [IADR-0101](../adr/IADR-0101_opus-5-max-tokens.md)。

## 背景と問題

Opus 5 は Opus 4.8 と異なり、`thinking` パラメータを**省略すると adaptive thinking が有効**になる。
`max_tokens` は**思考トークンと本文の合算上限**であるため、1024 のままでは思考が上限を食い切り、
応答に `TextContent` が 1 つも含まれない（＝本文が空）状態が起こり得る。

本リポジトリには基盤ゲートウェイを叩く呼び出しが 2 箇所あり、いずれも `MaxTokens: 1024` を
ハードコードしている。かつ `purpose` が基盤の `PurposeModels` に未登録のため、ルーターの
優先順位（① 明示 Model → ② `PurposeModels[purpose]` → ③ `DefaultModel`）により
**`default`（＝Opus 5 化される層）へ着地する**。

| 呼び出し元 | `purpose` | 着地 | 影響 |
| --- | --- | --- | --- |
| `TradeDecisionService.Worker/Composable/Adapters/HttpLlmCompletionClient.cs` | `trade-decision` | `default` | 本文が空 → `string.IsNullOrWhiteSpace(dto.Text)` により `HoldFallback` へ縮退。**全判断が Hold に固定**され、例外もエラーも出ないまま取引機能が事実上停止する |
| `ReportService.Worker/Foundation/Adapters/HttpReportNarrativeDrafter.cs` | `report-narrative` | `default` | 途中で切れた文章がそのまま成果物になる（安全網なし） |

基盤側で `CompletionApiRequest.MaxTokens` の既定を 4096 へ引き上げても、**両者は `MaxTokens` を
明示指定しているため救済されない**。本リポジトリでの対応が必須である。

## ADR-0011 との関係

本作業は `max_tokens`（トークン予算）の変更であり、**モデル ID の選定・変更ではない**。
ADR-0011 が禁じる「基盤のモデル改定への自動追随」には当たらず、むしろ Opus 5 化が起きた際に
取引判断が静かに停止するのを防ぐ**防御的措置**である。

なお ADR-0011 のフォローアップ「基盤 LLM ゲートウェイの取引用途区分に固定モデル ID を設定する実装」
は未実施であり、`trade-decision` が `default` に追随する状態そのものは本作業では解消しない
（基盤側でのピン留めが必要。[IADR-0101](../adr/IADR-0101_opus-5-max-tokens.md) にフォローアップとして記録）。
`report-narrative` は ADR-0011 §決定により `default` 追随が仕様上正しいため、ピン留めの対象外である。

## 受け入れ基準

1. `HttpLlmCompletionClient` の `/complete` 要求の `MaxTokens` が 4096 である。
2. `HttpReportNarrativeDrafter` の `/complete` 要求の `MaxTokens` が 4096 である。
3. 変更理由（thinking 既定有効・合算上限）がコード内コメントに起点 ID 付きで残る。
4. 既存テストが通る（両クライアントのテストは要求本文の `prompt` / `model` / `confidentiality` /
   `purpose` を検証しており、`MaxTokens` の値には依存しない）。
5. `dotnet build` / `dotnet test` / `dotnet format` が通る。

## 対応方針（変更範囲）

- `backend/Services/TradeDecisionService/src/TradeDecisionService.Worker/Composable/Adapters/HttpLlmCompletionClient.cs`
  … `MaxTokens: 1024` → `4096`＋理由コメント
- `backend/Services/ReportService/src/ReportService.Worker/Foundation/Adapters/HttpReportNarrativeDrafter.cs`
  … 同上

## リスクと自己チェック

- **コスト**: 出力トークンの上限が 4 倍になる。ただし上限であって固定消費ではなく、増えるのは思考分の実消費のみ。
  月次 LLM 費用上限（15,000 円・`05_trading-assumptions` §6）の消費見積りは、上限超過時に定時サイクル間隔を
  延長する既存機構（非機能要件）で暴走を防止できる。IADR-0055 の使用量計測（egress 計測点）で実測する。
- **取引判断の挙動**: 本変更は `max_tokens` のみで、モデル・プロンプト・ガードレールは不変。
  `HoldFallback` への縮退条件（空応答）も不変であり、判断ロジックへの影響はない。
- **基盤が Opus 5 化される前に本変更が入っても無害**: Opus 4.8 でも `max_tokens` は上限であり、
  4096 に上げても短い応答の消費は変わらない。

## 非対象・除外

- 取引用途のモデルピン留め（ADR-0011 フォローアップ）。基盤側 `PurposeModels` の設定であり本リポジトリ外。
- `thinking` / `effort` パラメータの明示送信（基盤ゲートウェイの責務・本リポジトリからは送れない）。
- `MaxTokens` の設定可能化（過剰な抽象化を避ける。必要になった時点で別途）。

## 検証

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx`
- `dotnet format backend/backend.slnx --verify-no-changes`
- `grep -rn "MaxTokens: 1024" backend/` が 0 件
