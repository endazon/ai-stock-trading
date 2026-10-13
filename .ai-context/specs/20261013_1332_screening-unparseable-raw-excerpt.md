---
title: 一次スクリーニングの NoJsonObject（2/682）——解析不能の応答の原本を限られた長さで保全し、形の指紋で原因の候補を切り分ける（#1332）
type: spec
status: done
related_ids: [FR-04, FR-11, ADR-0003, ADR-0014, IADR-0248, IADR-0061, IADR-0316, IADR-0525, IADR-0531]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 判断根拠の記録・FR-11 監査可能なログ・NFR-10 監査ログの保持)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (出力の構造化・安全側)
---

# 仕様書: 一次スクリーニングの NoJsonObject の原本の保全と原因の切り分け（#1332）

> 本仕様書は実装着手前に作成した（着手 2026-10-13）。基点は AST `origin/develop` `baafcfef`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-04**（判断根拠を必ず記録する）、FR-11（後から監査できるログ）。
- 計画 ADR: ADR-0003（構造化出力・不正は安全側）、ADR-0014（一次＝claude-haiku-5-5 の割当）。
- 起票: #1332（PoC 2026-10-12 米国セッション。一次スクリーニングの構造化出力が 682 回中 2 回 `NoJsonObject`。出力 1,012 / 1,203 トークン・MaxTokens 4,096・上限到達の WRN 0 件＝切り詰めではない）。
- 関連 IADR: IADR-0248（解析不能と見送りの区別。**変えない**）、IADR-0061 決定 1（`LogPrompts` の全量記録・既定オフ。**変えない**）、IADR-0316（ログ値の発生源での正規化）、IADR-0525（#1290 の根拠文の化け）。
- 新規 IADR: **IADR-0531**（本件の決定）。

## 問題

解析不能のとき、ログに残るのは `kind=NoJsonObject detail=JSON オブジェクトを抽出できない` だけで、**応答の本文が 1 文字も残らない**。
本文を残す既存の手段（`LlmGateway__LogPrompts=true`）はプロンプト全量（保有・資金残枠）も毎回書くため既定オフであり、
0.29% の事象のために常時有効にはできない。原因を確定できない。

## 解析の経路（コードで追った事実）

1. 基盤のゲートウェイ（MSP `ClaudeProvider.CompleteAsync`）は `msg.Content.OfType<TextContent>().FirstOrDefault()?.Text` を返す。
   thinking ブロックは `AnthropicContentBlockSanitizer` が SDK の解釈前に落とす。🔴 **text ブロックが 2 個以上あると、2 個目以降は捨てられる**（MSP develop で確認）。
2. AST `HttpLlmCompletionClient.CompleteAsync` は Sent=false・割当外・refusal・空本文を Hold の定数 JSON に置き換え、それ以外は `dto.Text` をそのまま返す（上限到達は WRN を出して本文を渡す）。
3. `DecisionOrchestrator.DecideAsync` が `TradeDecisionParser.ParseScreening` を呼ぶ。`ReadEnvelope` は 空 → `EmptyOutput`、`ExtractJsonObject` が null → **`NoJsonObject`**、`JsonDocument.Parse` 失敗 → `MalformedJson`、action 不明 → `UnknownAction`。
4. `ExtractJsonObject`: **最初の ASCII `{`** から、ASCII の `"`（`\` のエスケープを考慮）で文字列の内外を切り替えつつ `{` `}` の深さを数え、深さ 0 に戻った位置までを返す。コードフェンス・前後の散文・2 個目以降の JSON は無視される（最初の 1 個だけ）。
5. したがって **`NoJsonObject` ⇔ (a) ASCII `{` が 1 個も無い、または (b) 最初の `{` からの走査が末尾まで深さ 0 に戻らない**。(b) は「末尾で文字列の内側にいる（`"` の数が奇数）」か「`{` が `}` より多い」のどちらか。
6. 解析不能のときの扱い: 一次は打ち切り（Hold・`ScreenedOut=true`・`ScreeningUnparseable=true`）、WRN 1 行。**再試行はしない**。二次は Hold 票として多数決へ入れる。

## 原因の候補（原本なしでの絞り込み）

| 候補 | 結果の種別 | 本件の候補か |
| --- | --- | --- |
| 前置きの散文のあとに JSON | 成功 | **除外**（最初の `{` から取るので通る） |
| コードフェンスで囲んだ JSON | 成功 | **除外** |
| JSON の文字列の中の括弧 | 成功 | **除外** |
| JSON が 2 個 | 成功（最初の 1 個） | **除外** |
| 出力形式の雛形をそのまま書き写す（`参照価格` など） | MalformedJson | **除外**（種別が違う） |
| 思考だけで本文が空 | EmptyOutput | **除外**（種別が違う） |
| 切り詰め | — | **除外**（stop_reason が max_tokens でない） |
| **A. 散文だけ（拒否・説明・「以下が出力です：」で終わる）** | NoJsonObject（`{` なし） | 候補 |
| **B. JSON が 2 個目の text ブロックにある（ゲートウェイが 1 個目しか返さない）** | NoJsonObject（`{` なし。本文は A と同じに見える） | 候補（基盤側。A と区別するには本文の末尾を見る） |
| **C. 全角の括弧 `｛ ｝` で書いた JSON** | NoJsonObject（`{` なし） | 候補 |
| **D. 文字列が閉じない（エスケープされない `"` が奇数個。#1290 で一次モデル（当時 claude-haiku-4-5）に出た語の化けが `"` を含むトークンを出す、根拠文に `"` を素で書く）** | NoJsonObject（末尾で文字列の内側） | 候補（**JsonDocument なら MalformedJson になる形が、走査器では NoJsonObject になる**） |
| **E. JSON より前の散文に閉じない `{`、または JSON の閉じ `}` の欠落** | NoJsonObject（深さが残る） | 候補 |

原本の**形の指紋**（`{` `}` の数・全角括弧の数・`"` の数・フェンスの数・最初の `{` の位置・走査が文字列の内側で終わったか・残った深さ）を
残せば、A〜E は本文を読まなくても機械的に分かれる（A/B は本文の末尾で見分ける）。

## 対象範囲

- 対象:
  - `TradeDecisionParser` に**形の指紋**（`DescribeShape`）を足す。走査は `ExtractJsonObject` と同じ関数を使う（2 か所に書かない）。抽出の挙動は**変えない**。
  - `DecisionOrchestrator` で、解析不能（`EmptyOutput` を除く全種別）のとき、**原本の抜粋（先頭 1,000 字＋末尾 500 字）と形の指紋**を WRN 1 行で残す。一次・二次の両方（同じ関数）。
  - 試験: 候補 A〜E と除外した形の**種別の固定**（特性試験）、指紋の分類、抜粋の長さの上限・1 行化、オーケストレータが原本の行を出す／出さない。
  - IADR-0531（保全の方式・量の上限・保持期間・抽出を頑健にしない理由・再試行の費用・PoC の手順）。
- 対象外:
  - **抽出の頑健化**（2 個目以降の `{` からの再走査・全角括弧の読み替え・閉じない文字列の補修）。いずれも「誤って別の JSON を拾う」経路を開く（IADR-0531 決定 3）。原本で原因が確定してから決める。
  - **再試行**（費用の見積りは IADR-0531 決定 4。原本で原因が確定し、発生率が上がってから決める）。
  - 基盤のゲートウェイ（候補 B）の修正。原本で B と確定したら MSP へ起票する（基盤無改修）。
  - `TradeDecisionAppService`・`Program.cs`・`docs/tests/FR-10_risk-controls-tests.md`（並行作業 #1330 / #1331 / #1291 と同じファイルに触らない。FR-04 は網羅裁定 #211 の必須範囲外＝作業仕様書と xUnit を正の記録とする）。

## 母集合（規則 9・10。着手前に引いた）

- 解析不能を記録する箇所: `grep -rn "IsUnparseable\|ParseScreening(\|ParseDetailed(" backend/Services --include=*.cs`（テスト・obj を除く）→
  `DecisionOrchestrator`（一次・二次）と `Stage0DecisionRecorder`（オーケストレータを使う。同じ行が出る）。報告書サービスは別のパーサ（対象外）。
- 応答の本文を残す既存の手段: `LogPrompts`（`HttpLlmCompletionClient`。全量・既定オフ）だけ。監査台帳（FR-11）へ LLM の本文を入れる経路は無い。
- 新たに誤りになる自分の記述（規則 10）: `TradeDecisionParseFailureKind.NoJsonObject` の要約「散文のみ・括弧が閉じない」は**文字列が閉じない**を含んでいない → 要約を直す。

## 受け入れ基準

1. 解析不能（`NoJsonObject` / `MalformedJson` / `UnknownAction` / `InvalidValues`）のとき、WRN 1 行に原本の抜粋と形の指紋が出る。抜粋は 1,500 字＋省略の注記を超えず、改行を含まない。
2. 解析できた応答（見送りを含む）・空応答では原本の行を出さない（ログの肥大化を避ける）。
3. 形の指紋が候補 A〜E を分ける（試験で固定）。
4. 抽出・種別・挙動（Hold に倒す・打ち切り）は変わらない（既存試験が全緑）。
5. 候補・除外の種別を特性試験で固定する（抽出を将来変えたら赤くなる）。
