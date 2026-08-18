---
title: check-test-traceability.js へ readPlanIds() を実装し、コミット件名の FR/UC/SC 実在性検査を実効させる
type: spec
status: approved
related_ids: [NFR, IADR-0206]
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - ../../planning/tools/impl-handoff-kit/repo-template/scripts/check-commit-messages.js
related_specs:
  - 20260818_530_pin-179a69a-catchup.md
---

# 仕様書: `readPlanIds()` 拡張点の実装（#532）

## 起点

- 起点 issue: #532（#530 のフォローアップ）
- 起点 ID: **NFR**（無採番。検査器の整備＝工程のメタ作業。`.claude/rules/traceability.md`「無採番 NFR を許す 2 つの場合」の場合 2）
- 実測時点: `develop` = `2fe0575` ＋ PR #531 の 4 コミット / 計画 pin `d5fa84b`

## 課題

#530 でキットの実在性検査を `check-commit-messages.js` へ移植したが、キットが探す拡張点 `scripts/check-test-traceability.js` の `readPlanIds()` が本リポに無く、FR / UC / SC の実在性検査が **notice 付き skip** のままである。この間 `feat(SC-99)` のような実在しない起点 ID が exit 0 で受理され、スカッシュ後の恒久履歴へ載る（force push 禁止で事後修正できない）。

## 設計

### 方式の選択（planning 走査は採らない）

| 方式 | 判定 |
| --- | --- |
| planning submodule を走査（既存 `planIds(root)` を export） | **却下**。`ci.yml` の `commit-messages` ジョブは submodule を取得しない（実測: checkout に `submodules` 指定なし）。走査すると実在集合が空になり**全 ID が違反**になる。キット版 `loadExistingPlanIds()` は `new Set(readPlanIds())` を呼ぶため、`null` を返しても空 Set に潰れて同じ結果になる |
| **本リポの追跡ファイルに宣言したレンジを読む（MSP 同型）** | **採用**。`.claude/rules/traceability.repo.md` は追跡下で必ず読めるため、読めない／パースできないのは環境差ではなく**規約側の破壊**であり fail-loud にできる |

### レンジの実測（planning `d5fa84b`）

| 種別 | 走査元 | 実在番号 | 宣言 |
| --- | --- | --- | --- |
| FR | `02_requirements/` | 01〜21（連続・欠番なし） | `FR-01..21` |
| UC | `03_usecases/` | 01〜07（連続） | `UC-01..07` |
| SC | `05_screens/` | 01・02・03（＋ 13・16） | `SC-01..03` |

**SC-13 / SC-16 を含めない理由**: いずれも `05_screens/01_screens.md` の地の文で**基盤（microservices-platform）の画面を明示的に参照**している記述である（例: 「共通シェル上部右端のユーザーアイコンから**基盤の** SC-16（アカウント設定）へ遷移する」＋基盤 `05_screens` への相対リンク）。本リポの名前空間の画面ではないため、実在集合には入れない（`.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合の ID 修飾」の考え方に沿う）。

### 変更

| ファイル | 変更 |
| --- | --- |
| `.claude/rules/traceability.repo.md` | 「起点 ID の種別（固有）」節を新設し、レンジと走査基準 pin・SC の除外理由を宣言（**必読規約が増えるため予算を再測する**） |
| `scripts/check-test-traceability.js` | `RULES_FILE` / `PLAN_RANGE_HEADING` / `PLAN_KINDS` と `planRangeSection` / `parsePlanRanges` / `expandPlanIds` / `readPlanIds` を実装し export（MSP の同名実装と同型） |
| `scripts/scripts.repo.test.js` | 正例・負例・fail-loud（節の欠落／レンジ不正）・実バイナリでの検出力（`feat(SC-99)` が落ちる）を固定 |

**fail の向き**（キット docstring と同じ）: モジュールが無い → キット側が skip（本リポは実装するので該当しない）／**節をパースできない → 例外**（黙って 0 件検査へ落ちない）。

## 受け入れ基準

- [x] `node scripts/check-commit-messages.js` の「readPlanIds を持たない」notice が消える
- [x] `feat(SC-99)` / `feat(FR-99)` が違反として検出される（実バイナリで確認）
- [x] `feat(SC-03)` / `feat(FR-21)` / `feat(UC-07)` は合格する（偽陽性ゼロ）
- [x] 規約節を壊すと `readPlanIds` が例外を投げる
- [x] `node scripts/scripts.test.js` 全件 pass・`check-reading-budget.js` が予算内

## 検証（実測）

```text
node -e "readPlanIds()"        31 件（FR-01..21 / UC-01..07 / SC-01..03）
                               SC-04 / SC-13 / SC-16 / FR-22 / UC-08 は含まない（実測）

実バイナリでの検出力（node scripts/check-commit-messages.js --title "<件名>"）
  feat(SC-99): x    exit=1  「計画レンジに実在しない」1 件
  feat(FR-99): x    exit=1  同上
  feat(SC-03): x    exit=0  違反 0（偽陽性なし）
  feat(FR-21): x    exit=0  違反 0
  feat(UC-07): x    exit=0  違反 0
  chore(NFR): x     exit=0  違反 0（NFR はレンジ対象外）

notice の消失
  node scripts/check-commit-messages.js | grep -c "readPlanIds を持たない"   → 0

fail-loud（すべて例外）
  読めないパス / 節が無いファイル / 種別の欠落 / 範囲の不正   → 4 件とも throw

node scripts/scripts.test.js                              ✓ 336 tests passed（+4 件）
検査器 8 本（kit-sync / doc-links / cross-repo-refs / plan-id-qualification /
  reading-budget / ai-workflow-config / adr-index-sync / test-traceability）  全て exit=0
node scripts/check-reading-budget.js   Claude Code: 39,318B / 51,200B（76.8%）
  ……規約節の追加で 38,032B から +1,286B。予算内（余白 11,882B）
```

**キット側の入口も実測した**: `check-commit-messages.js` の `loadExistingPlanIds()` が拡張点を解決し 31 件の Set を返すこと（＝キットから見えていること）を回帰テストで固定した。純関数だけを試験すると「拡張点が見つからず skip のまま緑」を見逃す。

## 計画書との差異・未決事項

- **レンジ宣言は人手更新である**（走査基準 pin を節に明記）。計画側で FR/UC/SC が増えたとき、宣言を更新しないと**新 ID が「実在しない」と判定されて落ちる**。落ち方は fail であって黙る形ではないため、更新漏れは検知できる（`SC-04` を使う PR が出た時点で赤くなる）。
- MSP は同じ拡張点を `#579` で先行実装済み。本作業でキット同型が両実装リポに揃った（§11 パリティ）。
