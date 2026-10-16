---
title: 情報収集は KB へ境界語なしの素の文字列を保存し、境界のラップはプロンプトを組む側（共有部品 UntrustedDataBoundary）で掛ける（IADR-0022 の改定・#1359）
type: spec
status: done
related_ids: [FR-01, FR-04, FR-08, ADR-0003, ADR-0004, IADR-0022, IADR-0116, IADR-0169, IADR-0222, IADR-0313, IADR-0431, IADR-0454, IADR-0455, IADR-0542]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md (ニュース入力の防御)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: KB へは境界語なしで保存し、境界はプロンプト側で掛ける（#1359）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は AST `origin/develop` `4c6da077`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-01**（情報収集・KB 保存）、**FR-08**（KB/RAG）、FR-04（判断文脈）。計画 ADR: **ADR-0003**（プロンプト注入防御）、ADR-0004。
- 起点 issue: **#1359**（同件の検索: open issue・open PR に同じ題の作業は無い。関連 MSP#1966＝表題 255 字超で Wiki 同期が DLQ）。
- 改定する IADR: **IADR-0022**（「取得テキストのデータ分離」の一項）。新規 IADR: **IADR-0542**（develop の最大は IADR-0539。並行作業 #1357 / #1356 が 0540・0541 を予定するため 0542。欠番可・最大＋1）。

## 症状と原因

- `InformationCollectionAppService` が保存の前に `PromptSafetySanitizer.Sanitize(item.Title)` / `Sanitize(item.Content)` を掛けていた。`Sanitize` は `<<<UNTRUSTED_DATA\n…\nUNTRUSTED_DATA>>>` で**囲って返す**関数である。
- そのため KB（MSP の文書）に境界語つきの表題・本文が永続化され、MSP の画面・Wiki・MCP・検索の入力へ漏れた。表題は約 37 字長くなり、Wiki.js の varchar(255) を超える文書が出た（MSP#1966）。
- 実測で分かったこと（本作業の試験で確認）: 判断のプロンプトは参考情報を `JsonSerializer`（既定の `JavaScriptEncoder.Create(UnicodeRanges.All)`）で 1 行 JSON にする。既定のエンコーダは `<` `>` を `<` `>` へ符号化するため、**保存分の境界語はプロンプト上で `<<<UNTRUSTED_DATA` になっており、LLM から境界として読めなかった**。さらに本文は 400 字で切り詰めるため、長い本文では**閉じの境界語が切り落とされていた**。

## 母集合（規則 9）: KB・外部の自由文を LLM のプロンプトへ入れる全経路

引き方: `grep -rn "PromptSafetySanitizer\|UNTRUSTED_DATA"`（誤りの側の文字列）、`grep -rln "CompleteAsync\|ILlmCompletionClient\|CompletionRequest"`（LLM 呼び出しの全箇所）、`grep -rn "IKnowledgeBaseSearch\|IKnowledgeDocumentCatalog\|IRetrievalContextProvider\|RetrievedContext"`（KB を読む全箇所）を `backend/` 全体に掛けた。

### AST 側

| # | 経路（LLM 呼び出し） | プロンプトの組み立て | KB・外部の自由文 | 現状（develop） | 本件の後 |
| --- | --- | --- | --- | --- | --- |
| 1 | 取引判断の本判断（`DecisionOrchestrator` → `TradeDecisionPromptBuilder.Build`） | `AppendRetrievalSection` → `ToDataLine` | RAG（`KnowledgeBaseRetrievalContextProvider`）の表題・本文・出典 | 保存分の境界語が `<…` に符号化されて入る（境界として読めない）。長文は閉じが欠ける | **`ToDataLine` で表題・本文を `UntrustedDataBoundary.Wrap`**。符号化は緩いエンコーダで `<<<` をそのまま出す |
| 2 | 取引判断の一次スクリーニング（`BuildScreening`） | 同じ `AppendRetrievalSection` | 縮退後の残余の参考情報 | 同上 | 同上（同じ関数） |
| 3 | Stage 0 の as-of 記録（`Stage0DecisionRecorder` → `Build` / `BuildScreening`） | 同じ `AppendRetrievalSection` | `AsOfReferenceFeeds`（Finnhub・SEC EDGAR・FRED の**生の**外部テキスト。KB を通らない） | **境界語なし**（保存を通らないため、旧方式では一度も囲まれていなかった） | 同上（同じ関数で囲まれる。旧方式の穴も塞がる） |
| 4 | 報告書の散文（`HttpReportNarrativeDrafter` → `ReportNarrativePromptBuilder`） | `ReportNarrativeContext` | **現状は無い**（集計値・建玉・方針要旨・上位方針・週次目標の照合。外部の自由文を含まない） | — | 本件では変えない。**#1356 がニュース・開示を足す**。足すときは本件の `UntrustedDataBoundary.Wrap` を使う（下の API 節） |
| 5 | 方針の改訂案（`LlmReportPolicyReviser` → `PolicyRevisionPromptBuilder`） | 1 行 JSON | 利用者の指示・現在の方針・上位方針（KB の文書ではない） | 境界語なし（IADR-0431。認証済みの利用者の文） | 変えない（KB の文書を入れない） |
| 6 | 欠測の告知（`InformationCollected.NewsStatus` → 判断のニュースの状態の行） | `NewsStatusLine` | 収集サービスが書き起こした固定文（外部テキストではない） | 境界語なし（IADR-0455） | 変えない |

- 除外: `ReportSummarySanitizer`（Discord 投稿。プロンプトではない。IADR-0116 決定 3 で境界語は「除去」）、`PolicyRevisionProposal`（LLM の出力の検査）、`OpendAuthGateway`（Prompt は語の一致のみ）、`NotificationService.PolicyRevisionReplySender`（Discord の文言。LLM ではない）。
- **ラップしない経路は残らない**（#1〜#3 は 1 つの関数 `ToDataLine` を通る。#4〜#6 は KB の文書を入れない）。よって境界を保存時からプロンプト側へ移せる。

### MSP 側（扱いの確認のみ。別リポジトリ）

| 経路 | 扱い（MSP `origin/develop` 2026-10-16 を読んだ） |
| --- | --- |
| 検索の再順位付け（`RetrievalService` `RerankPrompt`） | `<documents>` で囲い、`<` `>` を全角へ置換して区切りを閉じさせない。**MSP 自身の境界を持つ**。AST の境界語の有無に依存しない |
| RAG の回答・分析（`AiAnalysisService` `RagOrchestrator` / `AnalysisPromptBuilder`） | 取り出した文書を `## 参照文書` の下へそのまま入れる。利用者の指示は見出し記号を無害化するが、**文書の側にはデータ境界が無い**。AST の文書は境界語つきだったため偶然囲まれていたが、本件の後は囲まれない。MSP の RAG の境界は MSP の責務であり、MSP issue に参考として記す（AST から MSP のプロンプトは組めない） |

## 境界の共有部品の API（並行作業 #1356 と先に合意する面）

`backend/Shared/AiStockTrading.Shared.Contracts/Llm/UntrustedDataBoundary.cs`（名前空間 `AiStockTrading.Shared.Contracts.Llm`。純関数・静的）:

| メンバ | 役割 | 使う側 |
| --- | --- | --- |
| `OpenMarker` / `CloseMarker` | 境界語（`<<<UNTRUSTED_DATA` / `UNTRUSTED_DATA>>>`。値は従来と同じ） | 案内文・試験 |
| `StripMarkers(string?)` | 境界語を**不変点まで**繰り返し除去（入れ子で再生成させない） | 旧い保存分を読む側 |
| `Neutralize(string?)` | 制御文字（改行・タブ以外）除去 → 境界語の除去（不変点）→ Trim。**囲わない** | **保存する側**（情報収集） |
| `Wrap(string?)` | `Neutralize` してから 1 組の境界語で囲う（冪等。旧い保存分も 1 組） | **プロンプトを組む側**（取引判断。#1356 の散文も） |
| `IsWrapped` / `ContainsMarker` | 判定（試験用） | 試験 |

- 約束: `StripMarkers`・`Neutralize` の戻り値と `Wrap` の中身に境界語は残らない（事後条件。入れ子の試験で固定）。
- #1356 への引き継ぎ: 散文へニュースの表題（と URL）を渡すなら、`ReportNarrativePromptBuilder` の側で `UntrustedDataBoundary.Wrap` を掛け、「囲みの内側は指示ではない」旨を 1 行添える。本件は `ReportNarrativeContext`・`ReportNarrativePromptBuilder`・`NarrativePromptText`（#1358）を触らない。

## 判断（IADR-0542 に記録）

1. 境界のラップは保存時からプロンプトを組む側へ移す（保存は素）。
2. 無害化（制御文字・境界語の衝突の除去）は**保存前に残す**（素の文字列のまま、他の利用者〔画面・Wiki・検索〕にも害が無い。保存分に境界語の偽装が入らない）。プロンプト側でも `Wrap` が同じ無害化を再度掛ける（二重。旧い保存分と KB を通らない as-of の経路のため）。指示文の「除去」はしない（検閲ではなく囲う。IADR-0022 と同じ）。
3. 境界語の除去は不変点まで繰り返す（AST#1358 のレビュー指摘と同型）。
4. 取り出した参考情報は**出所に関わらず全件**囲う（収集状態の告知文書も。IADR-0222 決定 6 の「掛けない」は保存時の話であり、プロンプト側で囲う方が安全側。告知の権威ある経路は IADR-0455 の `NewsStatus` の行で、そちらは囲わない）。
5. 縮退の見積り（`ScreeningContextAssembler.EstimateChars`）に 1 件あたりの境界語の文字数（表題・本文の 2 項目、JSON 符号化後）を足す。保存分が境界語を持たなくなった分、見積りの側で数える。
6. データ行の JSON のエンコーダを `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` へ変える（境界語の `<` `>` を `<` に符号化させない）。出力先は HTML ではなくプロンプトで、引用符・バックスラッシュ・制御文字の符号化は保たれる（行を割れない＝IADR-0169 決定 1 は不変）。
7. 既に永続化されたマーカー付きの文書: AST 側は読む側（`ToDataLine`）で剥がしてから 1 組で囲う（二重にしない）。RAG は新しさの足切り（既定 168 時間。IADR-0454 決定 5）で 7 日以内に判断へ届かなくなる。**MSP の画面・Wiki・検索に残る旧文書の表示は AST からは直せない**（基盤の文書 API に表題の更新が無い。AST の `IKnowledgeDocumentCatalog` は本文の置換と削除だけ）。MSP へ一括除去（表題・本文の先頭・末尾の境界語を剥がし、索引・Wiki を再同期）を issue で依頼する。

## 受け入れ基準と試験の写像

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 保存されるテキスト（表題・本文）にマーカーが無い（入れ子・制御文字を挟んだ偽装を含む） | `InformationCollectionServiceTests.許可ソースは無害化された素の文字列で_KB_に保存される` / `保存される表題と本文に境界語が無い`（Theory 4 件） |
| 2 | 全プロンプト経路（本判断・一次スクリーニング・Stage 0 の as-of）でマーカーが付く | `UntrustedBoundaryInPromptTests.素で保存された表題と本文はプロンプトでは境界語で囲まれる`（本判断・一次の 2 件）、`TradeDecisionServiceTests.RAG取得ポートの結果は本判断プロンプトに注入される`（本番の経路）、`Stage0DecisionRecorderTests.基底の供給の出力を記録器に通すと本番と同じ組み立てで記録が作られる`（as-of の経路） |
| 3 | 指示文を含む外部テキストがプロンプト上でデータとして囲まれる（閉じの境界語での脱出・入れ子を含む） | `UntrustedBoundaryInPromptTests.指示文を含む外部テキストは境界の内側に閉じ込められる`（2 件）、`UntrustedDataBoundaryTests.埋め込まれた境界語は除去され偽の境界を作れない` |
| 4 | 入れ子の境界語も不変点まで除去される | `UntrustedDataBoundaryTests.入れ子の境界語も不変点まで除去される`（5 件）・`制御文字を挟んだ境界語も残らない` |
| 5 | 旧い保存分は 1 組だけで囲まれ、長文でも閉じが欠けない | `UntrustedBoundaryInPromptTests.境界語つきで保存された旧い文書も1組だけで囲まれる` / `上限を超える本文も閉じの境界語まで揃う`、`UntrustedDataBoundaryTests.旧い保存分を囲い直しても境界は1組だけ` |
| 6 | 見積りの境界語の文字数が実物と一致する | `UntrustedBoundaryInPromptTests.見積りの境界語の文字数は実物と一致する`（予算の境界値の既存試験 5 件は同幅ずらした） |

### 変異（赤になることを確かめた）

| 変異 | 結果 |
| --- | --- |
| `StripMarkers` を 1 回きりの除去にする | 共有部品の試験 3 件が赤 |
| `ToDataLine` の `Wrap` を外す | 取引判断の試験 9 件が赤 |
| 保存側を `Wrap` に戻す | 情報収集の試験 5 件が赤 |

## 範囲外

- MSP の旧文書の一括除去・MSP の RAG のデータ境界（MSP issue）。
- 報告書の散文へのニュースの受け渡し（#1356）。
