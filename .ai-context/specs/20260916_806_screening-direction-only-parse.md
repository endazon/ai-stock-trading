---
title: 一次スクリーニングは方向（関心の有無）だけを読み、Buy/Sell の数値欠損で見送りにしない
type: spec
status: done
related_ids: [FR-04, FR-11, IADR-0248, IADR-0039, IADR-0212]
author: endazon (with Claude Code)
created: 2026-09-16
updated: 2026-09-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 一次スクリーニングの解釈を「方向のみ」に分離する（#806）

## 起点

- 2026-09-16 13:57:52Z・米国開場中・AST#342 の PoC 周回で、一次スクリーニング（`claude-haiku-4-5`・purpose=trade-decision-screening）が
  Buy 候補を返したが `stopLossDistancePerShare` が null だった。`DecisionOrchestrator` は一次出力も `TradeDecisionParser.ParseDetailed`
  で読むため、本判断用の不変量（Buy/Sell は価格・損切り幅が正）に掛かって `InvalidValues` → `screenedOut=True` / `screeningUnparseable=True`。
  関心ありの銘柄が本判断に届かなかった。

  ```
  [13:57:52 WRN] 一次スクリーニングの構造化出力が解析不能（見送りとは区別して記録・#290）: kind=InvalidValues detail=価格・損切り幅が不正: referencePrice=332.55 stopLossDistance=
  ```

- 同日 13:47 の周回では数値つきの Buy が返り二次へ進んだ（LLM 出力の揺れ）。#785 は Hold の null を見送りとして読むよう改めたが、
  **Buy/Sell の数値欠損は依然 InvalidValues＝解析不能扱い**であった。

## 原因（コード位置）

- `DecisionOrchestrator.cs` L39: 一次スクリーニングの出力を `ParseDetailed` で読む。
- `TradeDecisionParser.cs` L62-68: Buy/Sell で `referencePrice`・`stopLossDistancePerShare` が正でない／損切り幅が価格以上なら
  `InvalidValues` を返す。これは**二次本判断（サイジングへ渡す）にだけ必要な不変量**であり、一次で意味を持つのは方向（関心の有無）だけ
  （IADR-0039。価格・損切り幅は二次本判断が改めて出す）。

## 設計

| 対象 | 変更 |
| --- | --- |
| `TradeDecisionParser` | `ParseScreening(string?)` を追加。共通の前段（空出力／JSON 抽出／JSON 構文／action）だけを読み、**action が Buy/Sell なら数値の有無・不変量に関わらず「関心あり」**。Hold（数値の有無を問わず）は見送り。解析不能（Failure）は `EmptyOutput` / `NoJsonObject` / `MalformedJson` / `UnknownAction` のみ（`InvalidValues` は一次では出ない）。戻り値は `ParsedScreening(Action, Rationale, Failure)`（`IsUnparseable` / `IsInterested` / `AsHold`）。`ParseDetailed` の挙動は不変 |
| `DecisionOrchestrator` | 一次スクリーニング分岐だけ `ParseScreening` を使う。打ち切り条件は「関心なし」（Hold または解析不能）。ログ 2 行（解析不能 Warning／見送り Information）と `ScreeningUnparseable` の意味（真の解析不能のみ true）は不変。二次本判断の経路は不変 |
| `TradeDecisionPromptBuilder.BuildScreening` | 出力形式の末尾行を「Hold のときは null にしてよい。**Buy/Sell でも referencePrice / stopLossDistancePerShare は null でよい（価格・損切り幅は本判断で決める）**」に改める。本判断側（`Build`）は不変 |
| IADR-0248 | `［2026-09-16 追記 / #806］` を追加し `updated:` を進める。索引行（`.ai-context/adr/README.md`）に注記 |
| テスト | 下記「受け入れ基準」に写像（Parser / Orchestrator / PromptBuilder） |

変えないもの: `ParseDetailed` / `Parse`（二次本判断・Stage 0 記録の解釈）、サイジング、`OrchestratedDecision` の形、#290 の区別（解析不能と見送り）。

## 走査した母集合（規則 2・6・9）

走査語: `ParseDetailed|ParseScreening|IsUnparseable|InvalidValues|ScreeningUnparseable|screeningUnparseable` および
`必ず数値を入れる|null にしてよい|Buy/Sell では必ず|Buy/Sell で数値|Buy / Sell で数値`（拡張子で絞らず、`.git` / `bin` / `obj` のみ除外）。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/TradeDecisionService/Domain/TradeDecisionParser.cs` | 変更（`ParseScreening` 追加・#785 コメントの前提「Buy/Sell では必須」を一次に限らない形へ） |
| `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/DecisionOrchestrator.cs` | 変更（一次分岐のみ） |
| `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/TradeDecisionPromptBuilder.cs` | 変更（`BuildScreening` L161 の 1 行のみ。`Build` L112 は本判断用のため据え置き） |
| `backend/Services/TradeDecisionService/Features/TradeDecision/RecordStage0Decisions/Stage0DecisionRecorder.cs` | 据え置き（Stage 0 記録は本判断と同じ用途・同じ解釈。一次ではない） |
| `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/TradeDecisionAppService.cs` | 据え置き（`ScreeningUnparseable` を FR-11 ログへ出すだけ。意味は不変） |
| `backend/Services/TradeDecisionService/Tests/Domain/TradeDecisionParserTests.cs` | 追加（`ParseScreening` の陽性・陰性）。既存の #785 陰性対照（`ParseDetailed` の Buy/Sell 数値欠損 → InvalidValues）は**二次の契約として据え置き** |
| `backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/DecisionOrchestratorTests.cs` | 追加（一次 Buy＋null → 二次へ進む 等） |
| `backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/TradeDecisionPromptBuilderTests.cs` | 追加（一次の出力形式に「Buy/Sell でも null でよい」が入り、本判断側には入らない） |
| `.ai-context/adr/IADR-0248_parse-failure-vs-hold-distinction.md` / `.ai-context/adr/README.md` | 追記 |
| `.ai-context/specs/20260828_337_trading-cycle-and-screening.md` / `20260911_785_screening-hold-null-numbers.md` | 除外（確定済み作業仕様書は point-in-time の記録。書き換えない） |
| `docs/` | 該当記述なし（走査で 0 件） |

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 一次で Buy＋`stopLossDistancePerShare` null → 二次へ進む（`ScreenedOut=false`・`ScreeningUnparseable=false`・二次が呼ばれる） | `DecisionOrchestratorTests.一次のBuySellは数値が無くても不変量違反でも二次へ進む`（Theory の 1 行目） |
| 2 | 一次で Buy＋損切り幅 ≥ 価格（本判断なら InvalidValues）→ 二次へ進む | 同上（Theory の 2 行目）／`TradeDecisionParserTests.一次スクリーニングはBuySellの数値が無くても不変量違反でも関心ありとして読む` |
| 3 | 一次で Hold＋null → 見送り（`ScreenedOut=true`・`ScreeningUnparseable=false`・根拠を保つ） | `DecisionOrchestratorTests.一次のHoldは数値がnullでも見送りとして打ち切り解析不能にしない` ＋ 既存 `一次スクリーニングの見送りは解析不能として記録しない_対の肯定形` ＋ `TradeDecisionParserTests.一次スクリーニングのHoldは数値の有無を問わず見送り` |
| 4 | 一次で壊れた JSON → 見送り＋`ScreeningUnparseable=true` | `DecisionOrchestratorTests.一次の壊れたJSONや不明なactionは解析不能として打ち切る`（Theory）＋ 既存 `一次スクリーニングの解析不能は見送りと区別して打ち切る` |
| 5 | 一次で action 不明 → 解析不能（`UnknownAction`） | `TradeDecisionParserTests.一次スクリーニングの解析不能は失敗種別つき`（Theory） |
| 6 | 一次では `InvalidValues` を出さない（陰性対照） | 同上の Buy＋不変量違反ケースで `Failure` が null |
| 7 | `BuildScreening` の出力形式に「Buy/Sell でも null でよい」が入り、`Build`（本判断）には入らない | `TradeDecisionPromptBuilderTests.スクリーニングプロンプトはBuySellでも数値をnullでよいと述べる` ＋ `本判断プロンプトはBuySellに数値を必須とする_対の否定形` |
| 8 | 二次本判断の解釈は不変（既存 `ParseDetailed` テストが全件緑） | 既存テスト |
| 9 | `dotnet build` / `dotnet test`（TradeDecisionService.Tests）緑・`dotnet format --verify-no-changes` 差分なし | 実測: `dotnet build backend/backend.slnx` 成功・TradeDecisionService.Tests **616 件緑**（新規 23 件）・format 差分なし。変異（`ParseScreening` に損切り幅の不変量を戻す）で新規 5 件が赤 |

- [ ] 稼働: イメージ再ビルド後の開場中の周回で、Buy＋数値欠損の一次出力が二次へ進む（次のクラスタ構築時・AST#342。本 PR では検証しない）
