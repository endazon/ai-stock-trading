---
title: 計画 ID レンジ宣言の遅れを週次監査で専用 issue へ起票する
type: spec
status: accepted
related_ids: [NFR, ADR-0029, IADR-0170, IADR-0320, IADR-0504]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0093_plan-id-ranges-derived-and-published.md (決定 3: 実装リポの突合は読み取り専用の HTTP・警告に限る・ビルドの前提にしない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0029_impl-docs-restructure.md (決定 2: planning 非依存)
---

# 仕様書: 計画 ID レンジ宣言の遅れを週次監査で専用 issue へ起票する（#1208）

## 起点となる計画書（トレーサビリティ）

- 起点: 無採番 NFR（メタ作業。トレーサビリティ検査の一次情報の鮮度）
- 関連 ADR: 計画 `MSP/ADR-0093` 決定 3（実装リポの突合は読み取り専用の HTTP・警告に限る）／計画 ADR-0029 決定 2（planning 非依存）
- 起票: #1208（#1199 受け入れ基準 3 の後続）。対の issue: MSP#1775
- 前例: #710（2026-09-09）／#483 §6（2026-10-05 の週次監査）／#1199（2026-10-07 の第 4 回全体監査 B-1）

## 目的・背景

計画 ADR のレンジ宣言（`.claude/rules/traceability.repo.md`）の遅れは 3 回目である。issue 本文は案 1 として
「週次の棚卸しで計画側と突き合わせ、ずれていれば issue を起票するか報告に載せる」を挙げる。

**着手前の実測で、案 1 の「報告に載せる」は既に動いていたことが分かった。**

- `scripts/check-planning-adr-range.js`（#717 / IADR-0320）が `backlog-audit.yml` の Claude ステップの前に走り、
  計画側の公開 `tools/doc-checks/kg-ranges.json` と FR / UC / SC / ADR の 4 種を突き合わせている。
- 2026-10-05 の週次監査（#483 §6）は `status: behind`・`scanned: 4`・「ADR: 宣言 0001..0051 が実物 0052 に 1 件遅れている」を
  正しく報告した。報告は「この遅れを追う open issue は見当たらない（起票候補）」で終わり、**誰も引き直さなかった。**
- 2 日後の全体監査（#1199）が同じ遅れ（その間に 0055 まで進んだ）を B-1 として再び指摘した。

**欠けていたのは検知ではなく、検知を作業（issue）へ変える段である。** 本作業はその段を足す。

## 対象範囲

- 対象:
  - `scripts/check-planning-adr-range.js`: `--upsert-issue`（ずれたら専用 issue を upsert）、宣言の不読を `error`・exit 1 へ、`--rules <path>`（試験用）、結果 JSON に `lagIssue`
  - `.github/workflows/backlog-audit.yml`: 前段ステップへ `--upsert-issue` と `GITHUB_TOKEN` / `GITHUB_REPOSITORY`、Claude ステップに `if: ${{ !cancelled() }}`、プロンプト項目 6 に専用 issue と `error` を追記
  - `scripts/scripts.repo.test.js`: 実バイナリの fail-loud と配線の回帰試験
  - `scripts/README.md`: 当該行
  - `.ai-context/adr/IADR-0504_*.md`（新設）・索引行、`IADR-0320` へ日付つき追記（決定 3 の「宣言不読も exit 0」だけを改める）
- 対象外:
  - 引き直し PR の自動作成（#1199 受け入れ基準 3 の例）。`contents: write` と PR を作れる token（GITHUB_TOKEN で作った PR は CI を起動しない）が要り、本 issue の受け入れ基準を超える
  - PR CI への突合の追加（`PLANNING_REPO_TOKEN` を PR 経路へ渡す是非は IADR-0320 §残るもの のまま）
  - 実行頻度の変更（週次のまま。遅れの窓は最大 7 日）
  - NFR の突合（本リポの宣言は「NFR はレンジを持たない」。対の MSP#1775 は NFR 採番を宣言しているので MSP 側だけで扱う）
  - 計画側の ADR 追加 PR のチェックリスト（案 2）は planning#742 が既に計画側へ提案している。本 PR からは環流しない

## 設計

### 選んだ形（案の比較は IADR-0504）

1. **専用 issue の upsert**（`--upsert-issue`）: `status` が `behind` / `ahead` のとき、ラベル `plan-range-lag`・
   マーカー `<!-- plan-range-lag -->` の open issue を探し、在れば本文を上書き、無ければ作る。本文は種別ごとの表・
   `reason`・引き直しの手順（転記元は計画リポの `gen-plan-ranges.js --check`・別紙への追記・`Closes #N`）。
   **自動でクローズしない**（IADR-0170 決定6）。`ok` / `unverified` / `error` では API を呼ばない。
2. **終了コード**: 計画側に届かない（`unverified`）は exit 0 のまま（計画 `MSP/ADR-0093` 決定 3）。
   宣言が読めない（`error`）は exit 1。ずれを検知したのに起票できない（token 不在・API 失敗）も exit 1。
3. **前段が exit 1 でも監査本体は走る**（Claude ステップに `!cancelled()`）。後段の検査は元から `always()`。

### 「黙って緑にならない」の担保

| 状況 | JSON の `status` | 前段の終了コード | 見える場所 |
| --- | --- | --- | --- |
| 一致 | `ok` | 0 | 監査報告 §6 |
| 遅れ / 先走り | `behind` / `ahead` | 0（起票に失敗したら 1） | warning・専用 issue・監査報告 §6 |
| 計画側に届かない | `unverified`（`scanned: 0`） | 0 | warning・監査報告 §6 の「未確認」 |
| 宣言が読めない | `error` | 1 | error アノテーション・ジョブ失敗（`ci-failure-issue.yml` は本ワークフローを監視していないため、ジョブの赤で見る） |

### 母集合（規則 9・10）

- 「宣言不読でも exit 0」を述べる箇所（`git grep -n -E "宣言不読|宣言が読めない、|常に exit 0"`、`.ai-context/specs/` を除く）:
  `scripts/check-planning-adr-range.js` の冒頭注記（直した）、`scripts/README.md` の当該行（直した）、
  `IADR-0320` 決定 3（凍結記録のため本文は残し、日付つき追記で IADR-0504 を指した）。
  `.claude/hooks/check-impl.js`・`scripts/README.md:165` の「常に exit 0」は別の検査器の話で対象外。
- 「自己試験 14 件」を述べる箇所: `scripts/README.md` の当該行だけ（25 件へ直した）。`IADR-0320` §結果 の 14 件は当時の記録で書き換えない。
- この変更で新たに誤りになる自分の記述: `backlog-audit.yml` の前段コメントの「secret が無い／API が失敗しても exit 0」は
  なお正しい（宣言不読を含めていない）。プロンプト項目 6 の status 一覧に `error` を足した。

## 受け入れ基準

1. 採った形を IADR に残す（同型事故 3 回の経緯を含める）→ IADR-0504
2. 宣言が遅れた状態を模した入力で検知が発火し、その出力が読める → 自己試験（種別ごとの陽性対照 4 件・本文の描画）と、
   宣言を `ADR-0001..0053` へ戻した写しを `--rules` で与えた実走（計画側は実ネットワーク）で `behind`・「ADR: 宣言 0001..0053 が実物 0055 に 2 件遅れている」
3. 計画リポジトリに到達できないときに黙って緑にならない → `unverified`・`scanned: 0`・warning・監査報告の「未確認」（明示の skip）
4. `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が全件 pass、変異試験で新しい試験が落ちることを確かめる

## テスト方針

- `check-planning-adr-range.js --self-test`（14 → 25 件）: 宣言不読 → `error`・exit 1、計画側が FR / UC / SC / ADR それぞれ 2 件先へ進んだ形で
  `behind` と種別名・件数が出る、issue 本文の描画、upsert の作成・上書き・何もしない・token 不在の 4 形（API は差し替え）。
- `scripts.repo.test.js`: 書式の崩れた宣言ファイルを与えた実バイナリが exit 1・`status: error`、`backlog-audit.yml` の配線。

## 計画書との差異

なし。計画 `MSP/ADR-0093` 決定 3 の 4 条件（対象は ID レンジの突合だけ・読み取り専用の HTTP・計画側の取得失敗で落とさない・
ビルドやテストの前提にしない）は維持する。exit 1 にするのは本リポジトリ内の欠陥（宣言不読）と、本リポジトリへの書き込み失敗だけである。

## 未決事項

- 週次の窓（最大 7 日）を縮めるか（日次の軽いワークフローへ分ける等）。今回の 3 件はいずれも窓の中で別の監査が拾っており、窓より「拾った後に誰も動かない」が主因だったため、頻度は据え置いた。
