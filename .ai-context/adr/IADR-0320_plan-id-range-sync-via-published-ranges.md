---
title: IADR-0320 計画 ID レンジの突合は公開 kg-ranges.json を出典とし、4 種を突き合わせて走査件数を併記する
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0029, MSP:ADR-0093, IADR-0200, IADR-0204, IADR-0206, IADR-0262]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0093_plan-id-ranges-derived-and-published.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0029_impl-docs-restructure.md
  - planning:tools/doc-checks/kg-ranges.json
---

# IADR-0320: 計画 ID レンジの突合は公開 `kg-ranges.json` を出典とし、4 種を突き合わせて走査件数を併記する

> 実装ADR（IADR）は本リポジトリに閉じた実装判断の記録である。計画側の裁定は計画 ADR（`MSP/ADR-0093`）が持つ。

- 状態: Accepted
- 日付: 2026-09-09
- 決定者: Claude Code（計画 `MSP/ADR-0093` Accepted のフォローアップ 1・2 の本リポジトリ分。裁定は planning#591 で受領済み）

## 起点・関連

- 関連計画書 ID: `MSP/ADR-0093`（計画 ID レンジは実物から導出して公開し、実装リポジトリはそれへ追随する）／`ADR-0029` 決定 2（planning 依存の禁止。`MSP/ADR-0093` 決定 3 が範囲を 4 点に限って部分改定した）
- 対象 issue: planning#591（裁定依頼 Q2・案 A で確定）／#710（前回のレンジ遅れ）／#717（前段ステップの新設）
- 関連する実装仕様書: `20260909_591_plan-id-range-sync`
- 関連 IADR: `IADR-0204`（必読規約の予算）／`IADR-0206`（`check-cross-repo-refs.js` の置換点）／`IADR-0262`（`check-plan-id-qualification.js` の置換点）

## コンテキストと課題

本リポジトリが宣言する計画 ID レンジ（`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節）は、
`check-trace-blocks.js` と `check-commit-messages.js` の**一次情報**である。
**宣言が計画側の実物より遅れている間、そのレンジ外の ID を引く PR は CI が落ちて通らない。**

### 🔴 実測 1 —— 前回の是正の翌日に再びずれた

| 種別 | 是正前の宣言 | 実物（計画リポ `gen-plan-ranges.js --check`・2026-09-09） |
| --- | --- | --- |
| FR / UC / SC | `01..21` / `01..07` / `01..03` | 一致 |
| **ADR** | **`0001..0035`** | **`0001..0037`**（37 件・欠番なし） |

#710 で `0032 → 0035` へ前進させた直後に、環流の裁定 2 件（`ADR-0036` / `ADR-0037`）でずれた。
🔴 **異常な作業ではない。裁定を 1 件片づけるたびにずれる。**

### 🔴 実測 2 —— 転記元を計画 ADR の本文にすると、その時点で既に古い

計画 `MSP/ADR-0093` のフォローアップは「AST `..0035` → **`..0037`**」と書くが、
**MSP 側は同じ文で `..0092` と書いており、その `ADR-0093` 自身が加わって実物は `0093` である**。
**ADR 本文の数値は、その ADR が着地した時点で古くなる。**

### 実測 3 —— 従前の突合は ADR しか見ておらず、出典も別だった

`scripts/check-planning-adr-range.js`（#717 で新設）は計画リポの `07_adr/` ディレクトリ一覧を
`gh api …/contents` で取り、**ADR の最大番号だけ**を比較していた。FR / UC / SC のずれは検知できない。

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| 1 | **公開 `kg-ranges.json` を 1 回取得し、FR/UC/SC/ADR の 4 種を突き合わせる** | **採用**（決定 1・2）。計画側が `MSP/ADR-0093` 決定 1 で同ファイルを「実物からの導出結果を公開する成果物」へ格上げし、`gen-plan-ranges.js --check` を CI の必須チェックに置いた。**取得回数は 1 回のまま、見える範囲が 4 倍になる** |
| 2 | `07_adr/` 一覧の取得を続け、FR/UC/SC 用に追加の取得を足す | 採らない。**取得回数が増え、導出規則を本リポジトリ側にも持つことになる**。`MSP/ADR-0093` 決定 2 は「導出規則は種別ごとに違う」と実測しており、**同じ規則を 2 箇所に持てば片方が腐る** |
| 3 | ずれ検出で exit 1 にする | 採らない。`MSP/ADR-0093` 決定 3 が「**落とし方は警告に限り、ビルドやテストの前提にしない**」と定める。同決定が述べる「fail-open のままにしない」の内容は、**同決定の本文どおり走査件数の併記**である |

## 決定

### 決定 1: レンジ宣言を実物へ前進させる。**転記元は計画 ADR の本文ではなく実測である**

`ADR-0001..0035` → `ADR-0001..0037`（節内 2 箇所。宣言そのものと、検査器の説明中の書式例）。
🔴 **出典は計画リポの `node tools/doc-checks/gen-plan-ranges.js --check` の出力**であり、その旨を節に書いた（実測 2）。

### 決定 2: 突合の出典を公開 `kg-ranges.json` へ切り替え、FR/UC/SC/ADR の 4 種を見る

- 取得は `gh api repos/endazon/project-planning/contents/tools/doc-checks/kg-ranges.json`（`Accept: application/vnd.github.raw`）の 1 回。token は env `PLANNING_REPO_TOKEN` を子プロセスの `GH_TOKEN` へ写す（`GITHUB_TOKEN` では 404。#717 の実測）。
- 宣言側は**本番の抽出関数そのものを呼ぶ** —— FR/UC/SC は `check-test-traceability.js` の `readPlanIds()`、ADR は `lib/plan-ranges.js` の `readPlanAdrRange()`。**パーサを書き写さない**（書き写すと本番だけ変えても試験が緑のままになる）。
- 🔴 **`NFR` は突き合わせない。** `MSP/ADR-0093` 決定 2 が「`kg-ranges.json` へ足さない。追加の可否は別途の裁定による」と明示している。**検査の射程を黙って広げない。**
- **`behind` を `ahead` より優先して報告する。** 前進漏れのほうが実害（レンジ外 ID を引く PR の CI 落ち）を起こす。

### 決定 3: **走査件数（`scanned`）を必ず併記する。終了コードは変えない**

- `scanned` は実際に突き合わせられた種別の数である。🔴 **`scanned: 0` は「ずれが無い」ではなく「検査が動いていない」。**
- 種別ごとの内訳を `ranges` に出す。既存の JSON キー（`status` / `declaredMax` / `planningMax` / `reason` / `checkedAt` / `source`）は**維持する** —— `backlog-audit.yml` のプロンプトと `scripts.repo.test.js` が読んでいる契約である。`declaredMax` / `planningMax` は従来どおり ADR の値を入れる。
- **常に exit 0**（fail-open）。secret 不在・API 失敗・宣言不読はいずれも `status: "unverified"` ＋ 理由。

### 決定 4: 回帰テストから番号の直書きを外し、宣言から導出する

`scripts.repo.test.js` の #710 の回帰テストは `ADR-0035` を実在・`ADR-0036` を拒否と**直書き**しており、
**レンジを前進させた本作業で落ちた**（実測）。値を書き換えるのではなく、
`readPlanAdrRange().to` と `to + 1` から組み立てる形へ変えた。
🔴 **導出値は書き写さず計算し直す**（母集合の規則 10）。**そうしないと、次に前進させる者が同じ修正を繰り返す。**

## 結果

### 実測した変異（陽性対照）

`--self-test` に**ずらしたら落ちること**を対で入れた（14 件）。

| 変異 | 期待 | 実測 |
| --- | --- | --- |
| 宣言の ADR だけ `0035` へ戻す | `behind`・指摘は ADR の 1 種だけ | 一致 |
| 宣言の SC だけ `1..2` へ戻す | `behind`（**従前は ADR しか見ていなかったので検知できなかった形**） | 一致 |
| 計画側のレンジ表を空にする | `scanned: 0`・`unverified` | 一致 |
| secret を外して実バイナリを実行 | exit 0・`unverified`・`scanned: 0`・理由に `PLANNING_REPO_TOKEN` | 一致 |

`node scripts/scripts.test.js` は **338 件合格**（自己試験 14 件と既存 3 件の契約テストを含む）。

🔴 **陽性対照はフィクスチャで書き、「現在の宣言値」をリテラルで書かない**（MSP 側の AI レビュー指摘を受けて両リポで是正）。
実物の宣言を使う 2 件は**配線が通ること**（4 種そろってパースでき、比較器が処理できること）だけを固定する。
**現在値をリテラルと突き合わせると、次にレンジを前進させる PR で自己試験まで同時に直さないと `ahead` で落ちる**
—— **決定 4 で回帰テストから外したのと同じ「導出値の書き写し」である。** **実際のずれは CI の本走が公開ファイルと突き合わせる。**

### 残るもの

- 🔴 **突合が実際に走るのは週次の `backlog-audit.yml` だけである。** PR ごとには走らない。**ずれは最大 1 週間気付かれない。** PR CI へ入れるかは、`PLANNING_REPO_TOKEN` を PR 経路へ渡す是非を含めて別途判断する。
- 🔴 **本 IADR は本リポジトリ分だけである。** microservices-platform への移植（`MSP/ADR-0093` フォローアップ 2 の残り）は同リポジトリの別 PR で行う。
- **`NFR` はレンジ検査の対象外のままである**（計画側の別裁定待ち。`MSP/ADR-0093` フォローアップ 3）。
- **宣言する側が本リポジトリにある構造は変えていない。** 突合は警告であり、前進させるのは人（または後続 PR）である。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連 IADR: `IADR-0204`（必読規約の予算。本作業は必読側を +約 0.4KB だけ動かす）
