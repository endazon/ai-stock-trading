---
title: 環流記録の語彙と伝達検査を kit へ同期する（`resolved` の廃止・2 経路の検査・30 件の裁定段階の転記）
type: spec
status: approved
related_ids: [NFR, IADR-0047, IADR-0170, IADR-0188]
author: endazon (with Claude Code)
created: 2026-08-14
updated: 2026-08-14
---

# 仕様書: 環流記録の語彙と伝達検査の kit 同期

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#477](https://github.com/endazon/ai-stock-trading/issues/477)（kit 追随の棚卸し）のうち**環流語彙の項**
- 起点 ID: **NFR**（運用保守）
- 裁定: **2026-08-13〜08-14**（[project-planning#319](https://github.com/endazon/project-planning/issues/319)・
  [#320](https://github.com/endazon/project-planning/pull/320)／[#323](https://github.com/endazon/project-planning/issues/323)・
  [#325](https://github.com/endazon/project-planning/pull/325)）
- 実測の入力: [#483 のコメント](https://github.com/endazon/ai-stock-trading/issues/483#issuecomment-5294041520)（本作業の前段で実施した全数実測）

### 🔴 裁定は本リポジトリを名指しで指示している

planning#323 の裁定コメント（2026-08-14 01:36）は「**実装側で対応が要る事項**」を 3 件挙げ、うち 2 件が本リポジトリを名指しする。

> 2. **ai-stock-trading**: `resolved` 6 件を `accepted` へ移行する
> 3. **両リポジトリ**: キットの `check-feedback-dispatched.js` を同期する（`/pull/` 対応が未反映）

**[IADR-0047](../adr/IADR-0047_kit-template-sync-policy.md) 決定1 は「kit テンプレート更新への追随を原則とする」**と定めており、本作業はその適用である。

## 実装の現状（実測 2026-08-14・`develop` = `fed85a3`・計画 pin `cff0e7b`）

| 対象 | 現状 | kit（`repo-template/`） |
| --- | --- | --- |
| `feedback/README.md` | 25 行。**`status` の語彙節が無い** | 95 行。**4 値の語彙節・2 経路の証拠表・アンチパターン注記**を持つ |
| `feedback/TEMPLATE.md` | 48 行。**`dispatched:` / `planning_issue:` が無い** | 71 行。両鍵を持ち、語彙をコメントで説明する |
| 伝達検査 | `scripts/check-feedback-reflux.js`（203 行・本リポ固有・IADR-0170 / #439） | `scripts/check-feedback-dispatched.js`（646 行・**自己試験内蔵**） |
| 記録の `status` 分布 | **`open` 22 / `resolved` 8** | 語彙は `open` / `awaiting-decision` / `accepted` / `rejected` の 4 値。**`resolved` は語彙外** |

### 🔴 現行検査器は 2 経路の片方しか読まず、警告 9 件が全件偽陽性である

`feedback/README.md` 手順 3 は伝達を**両経路**で認めるが、`check-feedback-reflux.js` は
`project-planning#NNN` / `project-planning/issues/NNN` の 2 形しか証拠と認めない。

**実測（#483 コメントに全文）**: 警告 9 件は**全件が「記録ファイル経路だけで伝達した記録」**であり、
**9 件とも計画リポ `draft/feedback/` に実在し、9 件とも計画側で `status: accepted`** であった。
**記録に嘘は無く、検査器が経路を読めていない。**

これは planning#319 が姉妹検査器について指摘した defect と同型であり、**kit 側では planning#320 で解決済み**である。

### 🔴 `status` が 2 つの軸を 1 語に混ぜている

planning#323 の裁定は、**`status` は「計画側の裁定段階」だけを表し、「伝達したか」は
`dispatched:` / `planning_issue:` の別鍵が担う**と定めた。**1 つの語に 2 つの軸を持たせない。**

本リポの `resolved` は「解決した」としか読めず、**裁定段階なのか伝達済みなのかが読み分けられない**。

## 決定（[IADR-0188](../adr/IADR-0188_feedback-vocabulary-and-dispatch-kit-sync.md)）

### 決定1: kit の 3 ファイルを取り込み、本リポ固有の `check-feedback-reflux.js` を廃止する

**2 つの検査器を併存させない。** 同じ対象を別の規則で見る検査器が 2 本あると、
**どちらが正かを読む人が決めることになり、規則が 2 か所に分かれて必ず食い違う**（IADR-0186 決定1 と同じ規律）。

- 取り込む: `feedback/README.md`・`feedback/TEMPLATE.md`・`scripts/check-feedback-dispatched.js`
- 廃止する: `scripts/check-feedback-reflux.js`（CI 配線・回帰テストごと）

### 決定2: 30 件の `status` を**計画側の裁定段階の転記**として書き換える

語彙の定義どおり「**計画側の裁定を実装側が転記する**」。**実装側が独自に判断しない。**
転記元は次の優先順とする。

1. 計画リポ `draft/feedback/<同名>.md` の `status`（計画側のトリアージ出力そのもの）
2. 無ければ計画側 issue の state と裁定コメント

### 決定3: 伝達の事実は `dispatched:` / `planning_issue:` へ移す

`status` から伝達の軸を抜く。**両経路とも `dispatched: true`** で表し、
Issue 経路は `planning_issue:` に番号を残す。

### 決定4: **未伝達の 1 件は、警告を消すのではなく実際に伝達する**

`20260813_sc03-buy-in-count-period-undefined.md`（#470 / IADR-0186 決定1 の環流）は
**30 件中ただ 1 件の真に未伝達の記録**である。**記録に嘘を書いて警告を消さない**
（planning#319 が実装側 IADR-0184 決定2 として確立した規律）。**計画リポへ起票して `dispatched: true` にする。**

### 決定5: 振る舞いを持つコードは変更しない

本作業はドキュメント・検査器・CI 配線に閉じる。`backend/` は 1 行も触らない。

## やらないこと

- **記録の本文の書き換え**（frontmatter と、伝達の事実を述べる節だけを触る）。
  過去の経緯を述べた記述は書き換えない（`.claude/rules/traceability.md` の母集合の規則）。
- **kit 由来ファイルへの独自改変**（IADR-0047 決定1。改変が要るなら計画側へ環流する）。
- **`status` 値域の拡張**（kit の README が「値域を増やすときは本節を直してから使う」と定めている）。

## 受け入れ基準

- [x] `feedback/README.md` / `feedback/TEMPLATE.md` / `scripts/check-feedback-dispatched.js` が kit と**バイト一致**である
- [x] `scripts/check-feedback-reflux.js` と、その CI 配線・回帰テストが残っていない
- [x] 環流記録 31 件（本作業で 1 件追加）の `status` が 4 値のいずれかであり、**`resolved` が 0 件**である
- [x] `node scripts/check-feedback-dispatched.js` の警告が **0 件**である（**嘘で消していないこと**を記録ごとに確かめる）
- [x] `node scripts/check-feedback-dispatched.js --self-test` が通る
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る（**204 件**。companion の単体実行では何も走らない）
- [x] `node scripts/check-doc-links.js` が通る（456 件 OK）

## テスト方針

**伝達の判定そのものは二重に書かない。** kit の検査器は**自己試験を内蔵**しており（`--self-test`）、
本リポで同じ主張を書くと kit 同期のたびに二重の追随が要る（IADR-0047 決定1 の趣旨に反する）。

**本リポが独自に守るのは `status` の語彙と 2 鍵の形だけ**とし、**リポジトリ固有の回帰テスト**
（`scripts/scripts.repo.test.js`）へ置く。検査器を新設しないため kit 由来ファイルは無改変のまま保てる。

| 何を守るか | 何が守るか |
| --- | --- |
| 伝達の証拠の判定（2 経路・URL の宛先・鍵の誤記） | **kit 内蔵の自己試験**（CI の `scripts-tests` / `feedback-dispatched` から起動する） |
| **記録の `status` が語彙の 4 値内であること** | **本作業で足す回帰テスト**（`resolved` の再混入で赤くなる） |
| **`dispatched:` が `true` / `false` であること** | 同上（**YAML 1.1 の `no` / `off` も偽**であり、素の真偽値判定なら黙って通る） |
| **伝達済みなら `planning_issue:` を伴うこと** | 同上 |

> 🔴 **kit README は「この語彙を検査する機械は無い。値の誤りは沈黙する」と明記している。**
> **番人を置くのは同型 2 回目だからである**（planning#296「検査器・規約の追加は同型の事故が 2 回から」）
> —— 1 回目は本リポの `resolved` 8 件、2 回目は microservices-platform の `triaged` 7 件の誤用であり、
> **同じ kit を配った 2 リポジトリで語彙が割れていた**（planning#323 が実測）。

## 対照実験（実走した実測）

| 壊した箇所 | 赤くなったテスト | 予測 |
| --- | --- | --- |
| 記録 1 件の `status` を `resolved` へ戻す | **1 件**（`status が kit の語彙（4 値）の内にある`） | 一致 |
| 記録 1 件の `dispatched:` を消す | **1 件**（`dispatched: を true / false のいずれかで持つ`） | 一致 |
| 記録 1 件の `dispatched:` を **`no`** にする | **1 件**（同上） | 一致 |
| 伝達済み記録から `planning_issue:` を消す | **1 件**（`伝達済みの記録は planning_issue: を伴う`） | 一致 |

> 🔴 **1 回目の実験は無効であった（自戒として残す）。** `node scripts/scripts.repo.test.js` を直接叩き、
> **4 通りすべてで「赤 0 件・exit 0」**を得た —— **テストが発火していないのではなく、
> companion は関数を export するだけで単体実行では何も走らない**のが原因である。
> **これはまさに本リポが「緑だが検査されていない」と呼んできた形であり、対照実験の途中で自分が踏んだ。**
> 正しい起動経路（`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）で測り直して上表を得た。
> **CI は同経路であることを確認済み**（`ci.yml` の `scripts-tests` が `REQUIRE_REPO_TESTS: "1"` を設定している）。

## 計画への環流

- **計画側の記録 `draft/feedback/20260708_trading-defaults-derived-values.md` が `status: open` のまま**である
  （対応する planning#61 は CLOSED）。**計画側の記録が issue の決着に追随していない。** 環流する。
