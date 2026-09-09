---
title: 仕様書: 計画 ID レンジの宣言を実物へ前進させ、公開 kg-ranges.json との突合へ切り替える
type: spec
status: draft
related_ids: [NFR, ADR-0029, MSP:ADR-0093, IADR-0200, IADR-0204, IADR-0206, IADR-0262]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0093_plan-id-ranges-derived-and-published.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0029_impl-docs-restructure.md
  - planning:draft/cross-project/20260909_ruflo-spec-kit-adoption-decision.md
  - planning:tools/doc-checks/kg-ranges.json
---

# 仕様書: 計画 ID レンジの宣言を実物へ前進させ、公開 kg-ranges.json との突合へ切り替える

> 対象 issue: planning#591（裁定依頼 Q2・案 A で確定）。`MSP/ADR-0093` のフォローアップ 1・2 のうち本リポジトリ分。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（メタ作業。NFR）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: `MSP/ADR-0093`（計画 ID レンジは実物から導出して公開し、実装リポジトリはそれへ追随する。Accepted 2026-09-09）／`ADR-0029` 決定 2（planning 依存の禁止。`MSP/ADR-0093` 決定 3 が範囲を 4 点に限って部分改定した）
- 関連 IADR: `IADR-0200`（クロスリポ参照表記）／`IADR-0206`（`check-cross-repo-refs.js` の置換点）／`IADR-0262`（`check-plan-id-qualification.js` の置換点）
- 計画書リンク: 隣接クローン `../project-planning` の読み取り、または GitHub URL（`ADR-0029` 決定 2 のとおり submodule は張らない）

## 目的・背景

`.claude/rules/traceability.repo.md` が宣言する計画 ID レンジが、計画側の実物とずれている。
**ずれている間、そのレンジ外の ID を引く PR は CI が落ちて通らない**（`check-trace-blocks.js` /
`check-commit-messages.js` がこの宣言を一次情報にしている）。

### 🔴 実測（2026-09-09。計画リポの `gen-plan-ranges.js --check` による）

| 種別 | 本リポの宣言 | 計画側の実物 |
| --- | --- | --- |
| FR | `FR-01..21` | [1, 21] ✅ |
| UC | `UC-01..07` | [1, 7] ✅ |
| SC | `SC-01..03` | [1, 3] ✅ |
| **ADR** | **`ADR-0001..0035`** | **[1, 37]**（37 件・欠番なし） |

**ずれているのは ADR だけで、2 件遅れている。** `#710` で `0032 → 0035` へ前進させた直後に再びずれた。

**ずれは正常な作業で広がる。** 計画 `MSP/ADR-0093` の実測 1 が示すとおり、裁定を 1 件片づけるたびに増える。
**計画 `MSP/ADR-0093` 自身のフォローアップは `..0037` と書いているが、その `MSP/ADR-0093` が加わって既に古く、
実物は `0093`（MSP）/ `0037`（AST）である。**——本仕様書は**実測値**を採り、ADR 本文の数値を転記しない。

### なぜ突合の出典を変えるか

現行の `scripts/check-planning-adr-range.js` は計画リポの `07_adr/` ディレクトリ一覧を
`gh api …/contents` で取り、**ADR の最大番号だけ**を突き合わせる。計画 `MSP/ADR-0093` 決定 1 で
**`tools/doc-checks/kg-ranges.json` が実物からの導出結果を公開する成果物になった**ため、
そちらを読めば **FR / UC / SC / ADR の 4 種すべて**を 1 回の取得で突き合わせられる。

## 対象範囲

- **対象**
  - `.claude/rules/traceability.repo.md` の計画 ADR レンジ宣言を `ADR-0001..0035` → `ADR-0001..0037` へ前進させる（節内の 2 箇所）
  - `scripts/check-planning-adr-range.js` の出典を公開 `kg-ranges.json` へ切り替え、FR/UC/SC/ADR の 4 種を突合する
  - **走査件数（`scanned`）を出力へ併記する**（`MSP/ADR-0093` 決定 3。「ずれが無い」と「検査が動いていない」を区別する）
  - `scripts/README.md` と `scripts/scripts.repo.test.js` の追随
- **対象外**
  - `NFR` のレンジ突合（`MSP/ADR-0093` 決定 2 で「レンジ表へ足さない。追加の可否は別途の裁定による」と明示されている）
  - 終了コードを 0 以外にすること（`MSP/ADR-0093` 決定 3「落とし方は警告に限り、ビルドやテストの前提にしない」）
  - `backlog-audit.yml` の呼び出し方（`--out` のまま。既存の JSON キーを壊さない）
  - microservices-platform への移植（同じ裁定の別リポ分。**別 PR で行う**）

## 設計

### 1. レンジ宣言の前進

`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節の 2 箇所を `0035` → `0037` にする。

- 9 行目: 宣言そのもの（`lib/plan-ranges.js` の `ADR_RANGE_RE` が**最初の一致**として拾う）
- 19 行目: 検査器の説明中の書式例（同じ値を書いているため、片方だけ直すと文書内で矛盾する）

あわせて**実測日と出典**を注記する。**転記元は計画 ADR の本文ではなく、`gen-plan-ranges.js --check` の実測である。**

### 2. 突合の出典を公開 `kg-ranges.json` へ切り替える

```
fetchPlanningRanges()   gh api repos/endazon/project-planning/contents/tools/doc-checks/kg-ranges.json
                        （Accept: application/vnd.github.raw。読み取り専用 HTTP。
                          token は env PLANNING_REPO_TOKEN → 子プロセスの GH_TOKEN）
                          ↓
                        { FR:[1,21], UC:[1,7], SC:[1,3], ADR:[1,37] }（ai-stock-trading キー）

readDeclaredRanges()    FR/UC/SC … check-test-traceability.js の readPlanIds()
                        ADR     … lib/plan-ranges.js の readPlanAdrRange()
                          ↓
compareRanges()         種別ごとに ok / behind / ahead を出し、scanned（比較できた種別数）を数える
```

- **全体の `status` は従来どおり 4 値**（`ok` / `behind` / `ahead` / `unverified`）。1 種でも `behind` があれば全体は `behind`（`ahead` より `behind` を優先して報告する。前進漏れのほうが実害＝CI 落ちを起こすため）。
- **既存の JSON キー（`status` / `declaredMax` / `planningMax` / `reason` / `checkedAt` / `source`）は残す。** `declaredMax` / `planningMax` は**従来どおり ADR の値**を入れる（`backlog-audit.yml` のプロンプトと `scripts.repo.test.js` が読んでいる契約）。
- **追加キー**: `scanned`（比較できた種別数。0 なら「検査が動いていない」）・`ranges`（種別ごとの `{kind, declared, planning, status}` の配列）。
- **fail-open は維持する。** 取得失敗・宣言不読はいずれも `status: "unverified"` ＋ 理由で **exit 0**。`MSP/ADR-0093` 決定 3 が「fail-open のままにしない」と述べた内容は、**同決定の本文どおり「走査件数の併記」であって終了コードの変更ではない**（本文: 「検出件数が 0 のとき『ずれが無い』のか『検査が動いていない』のかを区別できるよう、走査件数も併記させる」）。

### 3. 互換性の担保

`backlog-audit.yml` は `--out .backlog-audit/planning-adr-range.json` のまま変更しない。
`scripts.repo.test.js` の既存 3 件（自己試験・secret 不在の fail-open・ワークフロー文字列）は**そのまま通ること**を条件とする。

## 受け入れ基準

- [ ] `.claude/rules/traceability.repo.md` の計画 ADR レンジが `ADR-0001..0037` になっている（節内 2 箇所とも）
- [ ] `node scripts/check-planning-adr-range.js --self-test` が全件 pass する（新規ケースを含む）
- [ ] secret 不在で `--out` を実行すると **exit 0**・`status: "unverified"`・`scanned: 0`・理由つきの JSON が書かれる
- [ ] 公開 `kg-ranges.json` を模したスタブで実行すると `scanned: 4`・`status: "ok"` になる
- [ ] 宣言を 1 種だけずらしたスタブで `status: "behind"` になり、`ranges` にその種別だけが `behind` として出る（陽性対照）
- [ ] `node scripts/check-trace-blocks.js` / `check-commit-messages.js` が通る（レンジ前進による退行が無い）
- [ ] `node scripts/check-reading-budget.js` が warn を増やさない
- [ ] `node scripts/scripts.test.js` が通る（既存 3 件を含む）
- [ ] `scripts/README.md` の当該行が新しい出典・キー・自己試験件数を書いている

## テスト方針

- **自己試験（`--self-test`）に陽性対照を必ず置く。** 「一致なら ok」だけでは、比較が空振りしていても緑になる。**ずらしたら落ちること**と**`scanned` が期待どおりであること**を対で確かめる（計画 `MSP/ADR-0093` 実測 4 と同じ作法）。
- **本番の抽出関数そのものを呼ぶ。** 正規表現・パーサを試験側へ書き写さない（書き写すと本番だけ変えても試験が緑のままになる。ADR-0093 が PR planning#593 のレビュー指摘として記録している）。
- ネットワークは叩かない（`execFn` / `fetchFn` を差し替える既存の設計を踏襲）。

## 計画書との差異

- 差異: **あり（数値のみ）。** `MSP/ADR-0093` のフォローアップ 1 は「AST `..0035` → **`..0037`**」と書くが、同 ADR の本文表は起票時点の `0036` も併記しており、**実物は時間とともに動く**。本仕様書は**着手時点の実測値**（`gen-plan-ranges.js --check` の出力）を採る。**環流は不要**——ADR-0093 自身が「ずれは正常な作業で広がる」と明記しており、数値の陳腐化は既知である。

## 母集合（規則 1・2・9。誤りの側＝`ADR-0001..0035` の文字列で引いた）

```
rg -n 'ADR-0001\.\.0035' --hidden -g '!.git'
  → .claude/rules/traceability.repo.md:9, :19  （2 件）
```

### 除外したものと理由（規則 6）

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/` の既存記録 | **point-in-time の凍結記録**。当時の値を書いており、後から直すと当時の記述と食い違う |
| `.ai-context/adr/` の既存 IADR | 同上（凍結記録。本文プロズを後から書き換えない） |
| `CHANGELOG.md` | 生成物。コミット件名を書き換えず、必要なら `changelog-overrides.json` で是正する |

### 🔴 引き直しで捕まえた取りこぼし（規則 9）

**上の走査（`ADR-0001..0035` の文字列）では足りなかった。** レンジの値は**範囲表記以外の形**でも書かれている。
**誤りの側の「別の形」でも引き直したところ、1 件見つかった。**

```
rg -n 'ADR-0035|ADR-0036' scripts/
  → scripts/scripts.repo.test.js:1855  '#710: 計画 ADR レンジ宣言が ADR-0035 を実在として通し、ADR-0036 は依然として拒否する'
```

**この回帰テストは番号を直書きしており、レンジを前進させた時点で落ちる**（実際に落ちた）。
**値を直すのではなく、宣言から導出する形へ変える** —— `readPlanAdrRange().to` と `to + 1` で組み立てる。
**そうしないと、次に前進させる者が同じ修正を繰り返す。**

### 同型の穴の引き直し（規則 10）

**是正後に新たに誤りになる自分の記述を、是正後の語（`0037`）でも引き直す。**
`scripts/README.md` の `check-planning-adr-range.js` 行は出典を「`projects/ai-stock-trading/07_adr/` の実在最大番号」と書いており、**本作業で出典が `kg-ranges.json` へ変わるため誤りになる。**——同 PR で直す。

## 未決事項

- なし（`NFR` をレンジ表へ足すかは計画側の別裁定。ADR-0093 フォローアップ 3）
