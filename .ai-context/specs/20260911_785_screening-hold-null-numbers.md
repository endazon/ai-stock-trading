---
title: 一次スクリーニングの Hold 出力（数値項目 null）を解析不能にせず見送りとして読む
type: spec
status: done
related_ids: [FR-04, FR-11, IADR-0248, IADR-0039]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 解析器の数値項目を寛容に読む（#785）

## 起点

- 2026-09-11 13:43Z・米国開場中・日報確定済みの周回で、trade-decision は基盤 LlmGateway 経由の実 LLM（`claude-haiku-4-5`）に到達したが、
  一次スクリーニングの出力が `MalformedJson`（`Path: $.stopLossDistancePerShare`）で解析不能 → `screenedOut=True` → Hold。発注段へ進まない。
- `DecisionDto` は数値 3 項目を非 null の `decimal` で受けていた。スクリーニングのプロンプトは「関心の方向のみ」と言いながら同じ数値項目を
  要求し、モデルは Hold 候補で `null` を返す。`null → decimal` の変換例外は `action` を読む前に起き、**見送りが解析不能に化ける**。

## 設計

| 対象 | 変更 |
| --- | --- |
| `TradeDecisionParser` | 型付き Deserialize をやめ、`JsonDocument` で項目ごとに読む。数値は「数値・数値文字列なら値、それ以外は null（未供給）」。Hold は数値が無くても解析成功。Buy/Sell で数値が無ければ `InvalidValues`（従来どおり Hold に倒す） |
| `TradeDecisionPromptBuilder` | 本判断・スクリーニングの出力形式に「Hold のとき referencePrice / stopLossDistancePerShare は null でよい。Buy/Sell では必ず数値」を明記 |
| IADR-0248 | 日付つき追記（決定 1〜3 は不変）・索引の行に注記 |

## 走査した母集合（規則 2・9）

`DecisionDto|ParseDetailed|MalformedJson` で追跡下の全ファイルを走査: `TradeDecisionParser.cs`（変更）、`TradeDecisionParserTests.cs`（追加）、
`DecideTrade` の呼び出し側（据え置き。`ParsedTradeDecision` の契約は不変）、IADR-0248（追記）、docs（該当記述なし）。

## 受け入れ基準

- [x] Hold で数値項目が null / 欠損 / 非数値文字列 → 解析成功の見送り（Failure なし）
- [x] Buy / Sell で数値項目が無い → `InvalidValues`（陰性対照）
- [x] 数値文字列の Buy → 解析成功
- [x] 変異（修正を戻す）で新規 4 件が赤。TradeDecisionService.Tests 593 件緑・format 差分なし
- [ ] 稼働: イメージ再ビルド後の開場中の周回で `screeningUnparseable=False` になり、risk → order-execution へ進む（次のクラスタ構築時・AST#342）
