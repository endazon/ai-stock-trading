---
title: テスト ID T-17-NN の 2 つの意味を解消し、テストコードが使うテスト ID の採番を検査する
type: spec
status: accepted
related_ids: [FR-17, FR-10, NFR, IADR-0376, IADR-0510, IADR-0177, IADR-0508]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs: []
---

# 仕様書: テスト ID T-17-NN の 2 つの意味を解消し、テストコードが使うテスト ID の採番を検査する（#1240）

## 起点となる計画書（トレーサビリティ）

- 起点: 無採番 NFR（メタ作業。テスト ID の採番の単一情報源の健全性）。対象の帯は FR-17（全体前提条件）
- 関連 IADR: IADR-0376（既存の重複は改番しない）・IADR-0177（#461）・IADR-0508（#1217）・**IADR-0510（本作業で新設）**
- 起票: #1240（AST#1239〔#1217〕の独立監査の 🟡。衝突自体は #1239 以前から）

## 目的・背景

`T-17-01`〜`T-17-04` が「既定値の試験」（`docs/tests/FR-10_risk-guard-core-tests.md`）と「採算の境界試験」
（`CostCalculatorTests`。#461）の 2 つを指していた。#1239 は採算側に `T-17-05`〜`T-17-13` を足したが、
`docs/tests/*.md` には載せなかった。検査器が出す採番の最大値は `T-17-4` のままだった。

やること:

1. 曖昧さを無くす（今後 1 つの番号が 1 つの試験だけを指す状態にする）
2. 採算側の番号を `docs/tests/*.md` に載せる
3. 同じ ID が 2 つの意味を持つ状態を `check-test-traceability` が捕まえられるようにする（#887 の T2 で足りない部分を足す）

## 母集合（規則 9: 誤りの側の文字列 `T-17-` で全文書を走査）

```
git grep -n "T-17-"   # origin/develop f0bbaa42
```

| 在り処 | ID | 指す試験 | 扱い | 理由 |
| --- | --- | --- | --- | --- |
| `docs/tests/FR-10_risk-guard-core-tests.md:107-110` | T-17-01〜04 | 既定値（`TradingDefaultsTests`） | **改番** → T-17-14〜17 | 外部参照 0 件。参照を持たない側（IADR-0510 決定 1） |
| `backend/Shared/AiStockTrading.Shared.Kernel.Tests/Trading/CostCalculatorTests.cs:121,161` | T-17-01/02/03・T-17-04 | 採算の境界 | 変えない | 正とする側。表へ載せた |
| `backend/Shared/AiStockTrading.Shared.Kernel.Tests/Trading/CostCalculatorTests.cs:249,268,279,297` | T-17-05〜08 | 事前見積りの諸費用 | 変えない | 表へ載せた |
| `backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/TradeDecisionServiceTests.cs:703` | T-17-09 | 採算見積りへ数量を渡す | 変えない | 表へ載せた |
| `backend/Services/TradeDecisionService/Tests/Infrastructure/ExternalServices/AssumptionsProfitabilityProviderTests.cs:62,80` | T-17-10・11 | 見積り不能・変わる判断 | 変えない | 表へ載せた |
| `backend/Services/TradeDecisionService/Tests/GrpcAssumptionsClientIntegrationTests.cs:120,140` | T-17-12・13 | 料率の受け渡し | 変えない | 表へ載せた |
| `.ai-context/specs/20260808_461_minimum-viable-profit-fail-closed.md:113-116` | T-17-01〜04 | 採算の境界 | 変えない（凍結） | 改番後は正しい意味だけを指す |
| `.ai-context/adr/IADR-0508_us-sell-fees-in-pre-trade-estimate.md:94,100` | T-17-11・T-17-05〜13 | 採算 | 変えない（凍結） | 同上 |
| `.ai-context/specs/20260923_887_test-id-duplicate-numbering.md:58` | `T-17-4`（最大値の転記） | — | 変えない（凍結） | point-in-time の出力 |
| `.ai-context/specs/20260919_885_grpc-deadline-test-flake.md:210` | 帯 `T-17` の言及 | — | 変えない（凍結） | 帯の列挙であり番号を指さない |
| `docs/tests/README.md:68`（コードブロック） | `T-17-4`（最大値の転記） | — | 変えない | 「書いた時点（2026-09-23）のもの・必ず自分で走らせること」と明記された例示。`T-10-633` も同様に古い |

既定値の試験（`TradingDefaultsTests.cs`）のコメントに `T-17` は無い（`git grep -n "T-17" -- '*TradingDefaultsTests.cs'` 0 件）。
**既定値の側の外部参照は 0 件**である。

### 改番を禁じる規則の確認

- `docs/tests/README.md`「再利用しない・改番しない・欠番は許す」と IADR-0376 決定 1 が改番を禁じている。
- IADR-0280 は IADR 番号の決定であり、テスト ID を拘束しない（README が「思想を揃える」として引くだけ）。
- IADR-0376 の理由（コード書き換え・指す側の不定・凍結記録の追随不能）はすべて外部参照に依る。参照 0 件の側の改番は、その前提の外にある。
  → 例外を **IADR-0510 決定 1** として明文化し、`docs/tests/README.md` に追記する。

## 選択肢

| # | 案 | 変更量 | 曖昧さ | 評価 |
| --- | --- | --- | --- | --- |
| 1 | 既定値の側（参照 0）を T-17-14〜17 へ改番し、採算側を FR-17 のテスト仕様書へ T-17-01〜13 で載せる | 表 4 セル＋新規 1 文書 | 無くなる | **採用** |
| 2 | 採算側を別の帯へ改番 | テストコード 4 ファイル 11 か所。凍結記録は直せない | 凍結記録の旧番号が既定値を指して残る | 不採用 |
| 3 | 改番せず重複 baseline へ（IADR-0376 の形） | baseline 4 件＋宣言 | 恒久化 | 不採用 |
| 4 | 既定値の側を `T-10-…` へ移す（issue 案 1） | 表 4 セル | 無くなる | 不採用。既定値は全体前提条件（FR-17）の値で、帯を変える理由が無い |

## 検査（T3 / T3b）の設計

- **#887（T2）がやること**: `docs/tests/*.md` の中で採番行の重複・参照行の宙吊りを赤にする。**テストコードは見ない**。
  T2 だけでは、FR-17 のテスト仕様書を起こさない限り #1240 は見えない。
- **T3**: テストの `.cs`（`testFiles()` と同じ母集合）に完全な形で書いた `T-<FR>-<N>` のうち、docs/tests が
  その帯を 1 件でも採番しているものは、表に採番されていなければ赤。帯を持たない `T-6` / `T-16` / `T-340` は対象外（件数だけ出す）。
- **意味の不一致（採番済み番号を別の意味で使う）は捕まえない。** 試作（表の行に書かれたメソッド名・クラス名がコードに在るかで照合）は
  1,007 種の ID を「不一致」と出した。表の行の多くがメソッド名を書いていないため、判定に使えない。IADR-0510 の残余に記録する。
- **既知の未採番**（導入時点 16 件・すべて `T-10-…`）は `scripts/test-id-unassigned-baseline.json` へ理由つきで固定する。
  - `T-10-483`: 表の行頭セルが `🔴 **T-10-483**`（T2 の解析は採番として数えない）
  - `T-10-917`〜`920`: 表の行頭セルが範囲表記 `**T-10-917〜920**`
  - `T-10-600`〜`607`・`609`: 予約の照合の試験。作業仕様書と IADR にだけ番号がある
  - `T-10-682`・`683`: 前営業日の資本基準の種。作業仕様書にだけ番号がある
  - いずれも本作業の範囲外（行の中身を確かめて足す作業になる）。
- **T3b**（baseline の増える側）: 範囲の解決と基準（マージベース）は T2b と同じ関数を使う。基準の取り方は #923 で
  規則 11 の 3 通りを実測済みであり（作業仕様書 `20260925_923_775_…`）、同じ形の baseline なので再測しない。
  宣言による例外は持たない（未採番は行を足せば常に解消できる）。基準の版に baseline が無ければ skip（本 PR が該当）。

### 実測（陽性対照: 表を直す前に T3 だけを入れた状態）

```
$ node scripts/check-test-traceability.js
[check-test-traceability] 違反 25 件を検出しました:
  - [T3] テスト ID T-10-483 をテストコードが使っていますが、docs/tests/ のどの表にも採番されていません …
  …（T-10 が 16 件）
  - [T3] テスト ID T-17-10 … / T-17-11 / T-17-12 / T-17-13 / T-17-5 / T-17-6 / T-17-7 / T-17-8 / T-17-9（9 件）
```

`T-17-05`〜`T-17-13` の 9 件を検出した。T-10 の 16 件は既存の未採番で、baseline へ記録した。

## 規則 10（この変更で新たに誤りになる自分の記述）

- `docs/tests/README.md` の例示出力の `T-17-4` → 日付つきの例示で「自分で走らせること」と書かれている。変えない。
- `scripts/test-id-duplicate-baseline.json` の `$comment`「既存 ID は改番しない（IADR-0376）」→ 重複 baseline の
  対象（両側に参照がある重複）には引き続き正しい。変えない。検査器の T2 のエラー文には IADR-0510 の例外を 1 句足した。
- `FR-10_risk-guard-core-tests.md` の T-17-17 の行は、テストメソッド名が実物（`運用段階の既定値はStage0で既定発注先は内蔵paperである`）と
  食い違っていたので、改番と同時に直した。

## 変更対象

| ファイル | 変更 |
| --- | --- |
| `docs/tests/FR-17_profitability-tests.md` | 新規。T-17-01〜13 |
| `docs/tests/FR-10_risk-guard-core-tests.md` | 既定値の 4 件を T-17-14〜17 へ。旧番号の注記。trace ブロック |
| `docs/tests/README.md` | 検査 5・改番の例外・コードで番号を使うときの規約・変更履歴・trace ブロック |
| `scripts/check-test-traceability.js` | 検査 5（T3）・T3b |
| `scripts/test-id-unassigned-baseline.json` | 新規。既知の未採番 16 件 |
| `scripts/scripts.repo.test.js` | T3 / T3b の試験 13 件（陽性対照・否定形を含む） |
| `scripts/README.md` | test-traceability 行に T3 / T3b |
| `.ai-context/adr/IADR-0510_…` ・ `.ai-context/adr/README.md` | 新設と索引行 |

テストコード（`.cs`）は変えない（`dotnet build` は不要）。

## 受け入れ基準

- [x] `T-17-01`〜`T-17-13` が `docs/tests/*.md` に採番され、それぞれ採算の試験だけを指す
- [x] 既定値の試験は `T-17-14`〜`T-17-17` で、旧番号を注記している
- [x] `node scripts/check-test-traceability.js` が緑で、採番の最大値に `T-17-17` が出る
- [x] T3 が表に無い番号のコード使用を赤にする（陽性対照: 修正前の実ツリーで T-17 の 9 件を検出）
- [x] T3 / T3b の試験が `node scripts/scripts.test.js` で緑
- [x] `check-trace-blocks` / `gen-knowledge-graph --check` / `check-reading-budget` が緑
