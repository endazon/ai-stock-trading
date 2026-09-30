---
title: 別紙 — 計画 ID レンジの引き直し履歴と実測の記録
type: annex
status: active
related_ids: [NFR, ADR-0029, IADR-0207, IADR-0320]
author: claude (Claude Code)
created: 2026-09-27
updated: 2026-09-30
---

# 別紙: 計画 ID レンジの引き直し履歴と実測の記録（#1052）

> **参照時にだけ読む別紙である。毎セッション読む必要は無い。**
> 規範（現行のレンジ・欠番なし・転記元は計画リポの `gen-plan-ranges.js --check` の実測であること）は
> `.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節が持つ。検査器（`readPlanIds()` /
> `scripts/lib/plan-ranges.js`）が読むのもあちらであり、**本書は検査器に読まれない**。
>
> - **なぜ移したか**: 同節は毎セッション必読の規約に入り、引き直しのたびに履歴が伸びて読み込み予算
>   （`check-reading-budget.js`）の 90% を越えた（#1052 で Claude の集合が 46,072 → 46,661 バイト）。
> - **なぜ `docs/` ではなく `.ai-context/` か**: `docs/` は可視本文に計画 ID・IADR・修飾付き issue 参照を書けない
>   （trace ブロック規約〔ADR-0029 決定4〕。`check-trace-blocks.js` が検査する）。この履歴は
>   「どの計画 ADR が・どの planning issue で加わったか」が本体であり、`docs/` へ置くと原文のまま移せない。
>   `.ai-context/` は本文に ID をそのまま書いてよい。
> - **追記の作法**: **追記専用。過去の項は書き換えない。** レンジを引き直すたびに §2 の末尾へ
>   日付つきで 1 項足し、`updated:` を前進させる（書式は §1 の各項に倣う）。

## 1. 移設した原文（#1052 時点の同節の本文。バイト不変）

裸の ID は**本リポジトリ（ai-stock-trading）の計画書**を指す。レンジは
`FR-01..21` / `UC-01..07` / `SC-01..04`（#532。**2026-09-09 に SC を 03→04 へ更新**
〔SC-04 OpenD 認証操作画面。planning#594 の利用者裁定で新設〕）、計画 ADR は `ADR-0001..0045`（欠番なし。
project-planning `projects/ai-stock-trading/07_adr/` の実ファイルと一致。**2026-09-09 に 0032→0035→0037 へ更新**
〔0035 まで: ADR-0033 Stage 0 の評価対象／ADR-0034 空売り「含む」の判定／ADR-0035 費用率の分母。#688 / #710。
0037 まで: ADR-0036 Stage 0 入力の完全性／ADR-0037 sonnet-5 単価の是正。planning#591 Q2 裁定〕。
**2026-09-11 に 0037→0039 へ更新**〔ADR-0038 連結配備の認証レルムは基盤レルム。環流は planning#597 で本リポの追随は #776／
ADR-0039 探索なしでは PBO を評価できない〕。**2026-09-17 に 0040 へ**〔ADR-0040 SIMULATE の損切り機構・散文は数量を拘束しない。#822 / #819〕。
**2026-09-19 に 0041 へ**〔ADR-0041 システム外の売買は数量だけを取り込み、基準資金は口座照会へ寄せる。planning#642。本リポの追随は #869 / #870〕。
**2026-09-26 に 0042 へ**〔ADR-0042 利用者が確定した AI の監視銘柄の入れ替え案は Discord の確認ボタンで適用してよい（FR-14 の例外）・`/policy` に 1 日の回数上限。planning#663〕。
**2026-09-26 に 0043 へ**〔ADR-0043 Finnhub の日次上限を撤回し監視銘柄数を分次予算で統制。planning#667〕。
**2026-09-27 に 0045 へ**〔ADR-0044 監視銘柄の一覧は判断のプロンプトへ渡してよく、Stage 0 は当時の監視銘柄を再構成する。planning#673／ADR-0045 照合による未確定の予約の解放は取引環境ごとの実機の記録で基準を満たしてから開ける。planning#676。本リポの追随は #1052〕。
🔴 **転記元は計画 ADR の本文ではなく、計画リポの `node tools/doc-checks/gen-plan-ranges.js --check` の実測である**
（ADR 本文の数値は、その ADR 自身が加わった時点で古くなる。実測 2026-09-11: `git ls-tree origin/main
projects/ai-stock-trading/07_adr/` が 39 件・`ADR-0001`〜`ADR-0039` で欠番なし。2026-09-17: `--check` が ADR [1, 40]・40 件。**2026-09-19 実測**: 隣接クローン
`../project-planning` で `node tools/doc-checks/gen-plan-ranges.js --check` が ADR **[1, 41]・41 件・欠番なし**。**2026-09-26 実測**: 隣接クローンの作業ツリーが
`origin/main` より古いため 2026-09-11 と同じ手段で測った——`git ls-tree --name-only origin/main projects/ai-stock-trading/07_adr/`（`8b44bba`）が
`ADR-0001`〜`ADR-0042` の 42 件・欠番なし。0043 も同じ手段（`aeba6e6`）で 43 件・欠番なし。**2026-09-27 実測**: 隣接クローン（`2b25250` = `origin/main`）で
`node tools/doc-checks/gen-plan-ranges.js --check` が ADR **[1, 45]・45 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕）。
trace ブロック規約〔ADR-0029 決定4〕の値域検査が読む）。

## 2. 移設後の引き直し

- **2026-09-27 に 0046 へ**〔ADR-0046 当時の監視銘柄の再構成で「最初の変更の変更前」を使えるのは `SeededAt` 以降に限り、`SeededAt` が無い・矛盾する時点は再構成できないとする（ADR-0044 決定 3 の部分改定）。planning#685 / planning#686。本リポの追随は #1056〕。
  **2026-09-27 実測**: 隣接クローン（`36d1f10` = `origin/main`、`git rev-parse --is-shallow-repository` → `false`）で
  `node tools/doc-checks/gen-plan-ranges.js --check` が ADR **[1, 46]・46 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-09-27 に 0047 へ**〔ADR-0047 Discord ボットの所有者トークンはサービスの身元であり、ボットの呼び出しは east-west として gRPC へ移す。gRPC 面の所有者の門は呼び出し元のクライアントがボットであることを併せて求める。planning#690。本リポの追随は #1067（決定 3 の実装と同じ PR）〕。
  **2026-09-27 実測**: 隣接クローンの `origin/main`（`3c7949f`。`git rev-parse --is-shallow-repository` → `true` のため `git archive origin/main` で展開した木で実行。
  ファイルの実在の検査であり履歴は使わない）で `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 47]・47 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-09-30 に 0050 へ**〔ADR-0048 定時の判断へ渡す出来高は moomoo の日足 K 線から得る前日までの値とし、K 線が判断へ通るまでは「未提供」と明示する。planning#702／ADR-0049 損切り幅は AI の提案に系が下限（1.0 × ATR(14)、得られないときは参照価格の 2%）を掛け、割った幅は下限まで広げる。planning#703／ADR-0050 保護された建玉への判断の手仕舞いは処理中の決済を差し引いた残りだけを送る（ADR-0040 決定 4 の部分改定）。planning#704。本リポの追随は #1120（ADR-0049 の実装と同じ PR。並行する #1119 が 0048 へ引き直しており、マージで 0050 に揃える）〕。
  **2026-09-30 実測**: 隣接クローンの `origin/main`（`4867280`。`git rev-parse --is-shallow-repository` → `true` のため `git archive origin/main` で展開した木で実行。
  ファイルの実在の検査であり履歴は使わない）で `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 50]・50 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
