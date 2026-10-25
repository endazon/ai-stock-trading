---
title: 別紙 — 計画 ID レンジの引き直し履歴と実測の記録
type: annex
status: active
related_ids: [NFR, ADR-0029, IADR-0207, IADR-0320]
author: claude (Claude Code)
created: 2026-09-27
updated: 2026-10-17
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
- **2026-09-30 に 0048 へ**〔ADR-0048 定時の判断へ渡す出来高は日足 K 線の前日までの値とし既存源の内側として扱う。K 線を判断へ流すのは取得枠の確認と分割の確認が済んでから。planning#702。本リポの追随は #1117（確認の道具〔検証口〕と同じ PR）〕。
  **2026-09-30 実測**: 隣接クローンの `origin/main`（`2b0716c`。`git rev-parse --is-shallow-repository` → `true` のため `git archive origin/main` で展開した木で実行。
  ファイルの実在の検査であり履歴は使わない）で `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 48]・48 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-09-30 に 0050 へ**〔ADR-0048 定時の判断へ渡す出来高は moomoo の日足 K 線から得る前日までの値とし、K 線が判断へ通るまでは「未提供」と明示する。planning#702／ADR-0049 損切り幅は AI の提案に系が下限（1.0 × ATR(14)、得られないときは参照価格の 2%）を掛け、割った幅は下限まで広げる。planning#703／ADR-0050 保護された建玉への判断の手仕舞いは処理中の決済を差し引いた残りだけを送る（ADR-0040 決定 4 の部分改定）。planning#704。本リポの追随は #1120（ADR-0049 の実装と同じ PR。並行する #1119 が 0048 へ引き直しており、マージで 0050 に揃える）〕。
  **2026-09-30 実測**: 隣接クローンの `origin/main`（`4867280`。`git rev-parse --is-shallow-repository` → `true` のため `git archive origin/main` で展開した木で実行。
  ファイルの実在の検査であり履歴は使わない）で `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 50]・50 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-02 に 0051 へ**〔ADR-0051 日報 §7 の利確条件は書式の行で銘柄ごとに数値で書き、欠ければ確定の前に警告する（確定は止めない）。ADR-0048 決定 4 の補完。planning#709。実装は先行して #1129（IADR-0470）。本リポの追随は本 PR〕。
  **2026-10-02 実測**: 隣接クローンの `origin/main`（`b9d0d27`。`git rev-parse --is-shallow-repository` → `true` のため `git worktree add --detach origin/main` で展開した木で実行。
  ファイルの実在の検査であり履歴は使わない）で `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 51]・51 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-06 に 0052 へ**〔ADR-0052 入力が未供給のまま作られた報告書の下書きは、所有者の操作（`/report regenerate`）で作り直す。対象は未確定の下書きに限り、方針の節は保って事実と散文だけを作り直し、中核の入力がなお未供給なら断る。planning#711。本リポの追随は #1156（IADR-0491）〕。
  **2026-10-06 実測**: 計画リポの作業ツリー（`c3ad458` = `origin/main`。`git rev-parse --is-shallow-repository` → `true`。ファイルの実在の検査であり履歴は使わない）で
  `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 52]・52 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-06 に 0053 へ**〔ADR-0053 報告書の生成タイミング「閉場後」は市場ごとの閉場を指す。各報告書は前回の同種の報告書の生成の後から今回の生成までに閉場したセッションを集計し、集計したセッションの範囲を市場ごとに明記する。planning#724。本リポの追随は #1172（IADR-0492。範囲の明記〔決定 3〕の実装と同じ PR）〕。
  **2026-10-06 実測**: 計画リポの作業ツリー（`6e8022a` = `origin/main`。`git rev-parse --is-shallow-repository` → `true`。ファイルの実在の検査であり履歴は使わない）で
  `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 53]・53 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-07 に 0055 へ**〔ADR-0054 取引判断の割当モデルを層別とし（スクリーニングと本判断で別のモデル）、フォールバック禁止と Stage 0 の再検証は両層に及ぼす。planning#731（planning#734 で追加）／ADR-0055 取引判断のプロンプト（スクリーニング・本判断）の機密区分は一律 internal とする。planning#732（planning#735 で追加）。本リポの追随は #1199（第 4 回全体監査 B-1。宣言の遅れの指摘は 2026-09-09 横断監査 A-3〔#710〕・#483 §6 に続き 3 回目）〕。
  **2026-10-07 実測**: 計画リポの作業ツリー（`b5b584f` = `origin/main`。`git rev-parse --is-shallow-repository` → `true`。ファイルの実在の検査であり履歴は使わない）で
  `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 55]・55 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-09 に 0060 へ**〔ADR-0056 利確の書式の条件は、比べられる保有について到達・未到達を系が計算して示す（ADR-0051 決定 3 の部分改定）。planning#736／ADR-0057 報告書の「月・日」は集計したセッションの窓で数え、月次 LLM 費用上限は JST の暦月で数える（ADR-0027 決定 3・ADR-0053 決定 2 の補完）。planning#746／ADR-0058 損切りの価格を呼値へ丸めるときは保護を緩めない向き（早く発火する側）を優先し、損切り幅の下限は 1 刻み未満の誤差を許す（ADR-0049 決定 3 の部分改定）。planning#741／ADR-0059 日報 §6 の週次目標との照合は、前週の週報 §6 の書式どおりの数値目標の行を参照値とし、週報 §1 と同じ定義の週初来の実現損益と比べる（ADR-0030 決定 5 の補完）。planning#748／ADR-0060 PoC 項目 9 は百分率表記まで確定とし年率かを照会で確かめ、項目 8 は暦の期限をやめて現金口座の利用を決めた時点で確かめ、両項目の結果は ADR-0019 §結果 に記録する（ADR-0025 決定 1・ADR-0026 決定 3 の部分改定）。planning#740。いずれも Accepted。前回の実測（`b5b584f`）以降、既存の AST 計画 ADR の状態の変更は 0 件。本リポの追随は #1273〕。
  **2026-10-09 実測**: 計画リポの作業ツリー（`2b08fbe` = `origin/main`。`git rev-parse --is-shallow-repository` → `true`。ファイルの実在の検査であり履歴は使わない）で
  `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 60]・60 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-10 に 0064 へ**〔ADR-0061 週報 §1 の週次目標に対する達成は、週間実現損益が週次目標の下限以上なら達成・下限未満なら未達とし、照合できないときは「照合不能」と書き理由を添える（ADR-0059 フォローアップ 6 の補完）。planning#766（planning#777 で追加）／ADR-0062 日本株は「無効」とし発注・価格変動の監視・ウォッチリストの対象から外し、moomoo証券（日本法人）の OpenAPI が日本株に対応したことを確かめた時点で再裁定する（05_trading-assumptions §5 の部分改定）。planning#767（planning#776 で追加）／ADR-0063 高ボラティリティ銘柄の区分（ATR(14, 日足) ÷ 参照価格 ≥ 4% の自動判定と利用者の明示指定の併用）を設け、区分の 1 注文あたりの発注金額上限を equity の 5% とする（区分外の 25% は据え置く）。planning#782（planning#787 で追加）／ADR-0064 Claude の割当モデルを 5.5 系へ改め旧モデルは廃棄し、Stage 0 は 5.5 系の 2 層の組で再実施する（ADR-0014 決定 1・ADR-0015・ADR-0017 決定 1・ADR-0054 決定 1・3 の部分改定）。planning#783（planning#788 で追加）。いずれも Accepted。前回の実測（`2b08fbe`）以降、既存の AST 計画 ADR の状態の変更は 0 件（本文の部分改定の注記のみ）。本リポの追随は本 PR〕。
  **2026-10-10 実測**: 計画リポの作業ツリー（`1a05215` = `origin/main`。d854539 の後に planning#787・planning#788 がマージされた先端。`git rev-parse --is-shallow-repository` → `true`。ファイルの実在の検査であり履歴は使わない）で
  `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 64]・64 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-13 に 0066 へ**〔ADR-0065 承認待ちの報告書（ドラフト）は、索引対象外の知識ユニットの文書として保存し、基盤の文書詳細画面（MSP/SC-03）で閲覧する。planning#784（planning#791 で追加）／ADR-0066 高ボラティリティ銘柄の区分の上限の構成範囲の下端は、最小の名目額の比率の既定値（1%）に固定する。明示指定と区分の上限は SC-02 から表示・設定できるようにする（ADR-0063 決定 2 の部分改定・同 フォローアップ 1・5 と決定 6 の暫定手段の補完）。planning#793（planning#794 で追加）。いずれも Accepted。前回の実測（`1a05215`）以降、既存の AST 計画 ADR の状態の変更は 0 件（ADR-0063 は本文の例外 4 の記録と部分改定の注記のみ。状態は Accepted のまま）。本リポの追随は #1313（週次監査の自動起票。検知時点では 0065 まで）〕。
  **2026-10-13 実測**: 計画リポの作業ツリー（`a321adb` = `origin/main`。`git rev-parse --is-shallow-repository` → `true`。ファイルの実在の検査であり履歴は使わない）で
  `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 66]・66 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-16 に 0067 へ**〔ADR-0067 判断由来の決済の指値は、発注から N 分（既定 15 分）未約定なら系が取り消すだけにする（置き直し・訂正・成行への切り替えはしない）。既定は無効・1 建玉 1 日 1 回・有効時の N は相場操縦の検知の窓より長い。moomoo の失効は「失注」として拒否と区別する（ADR-0050 決定 1 の補完）。planning#797（planning#800 で追加）。Accepted。前回の実測（`a321adb`）以降、既存の AST 計画 ADR の状態の変更は 0 件（ADR-0050 は補完節と相互リンクの追記のみ）。本リポの追随は #1339（ADR-0067 の実装・IADR-0539 と同じ PR）〕。
  **2026-10-16 実測**: 計画リポの `origin/main`（`fc61ca3`。`git rev-parse --is-shallow-repository` → `true` のため `git archive origin/main` で展開した木で実行。
  ファイルの実在の検査であり履歴は使わない）で `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 67]・67 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-17 に 0068 へ**〔ADR-0068 効いている確定済み日報は、`/policy` で版を足して再確定できる。最新の確定版が効き、旧版は確定時刻つきで不変のまま残す（日報だけ）。最新の確定済み日報は「開始日が最大の期間キー → その中で確定時刻が最新の版（同時刻は版番号の大きい方）」の二段で選び、Stage 0 の判断時点の組み直しも判断時点までに確定した版だけで同じ二段で選ぶ。判断の記録に参照した方針の期間キーと版を残す。planning#803（planning#804 で追加）。Accepted。前回の実測（`fc61ca3`）以降、既存の AST 計画 ADR の状態の変更は 0 件（`07_adr/` の差分は ADR-0068 の追加と README のみ）。本リポの追随は #1373 段 A（IADR-0548 と同じ PR。段 B は別 PR）〕。
  **2026-10-17 実測**: 計画リポの `origin/main`（`55e464a`。`git rev-parse --is-shallow-repository` → `true` のため `git worktree add --detach origin/main` で展開した木で実行。
  ファイルの実在の検査であり履歴は使わない）で `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 68]・68 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-21 に 0071 へ**〔ADR-0069 Stage 0 の評価の窓を 365 暦日とし、DSR ≥ 0.95 を計画の基準として登録する。窓が 365 暦日に届くまで ADR-0033 の覆る条件を判定しない。planning#806（planning#812 で追加）／ADR-0070 閉場前の新規建ての締め切り（既定 60 分・市場ごと）と閉場前の手仕舞い（既定 15 分・既定は無効）を統制値として新設し、取引判断に市場の現地時刻と閉場までの残り時間を渡し、統制値は SC-02 からも設定できるようにする（配備後）。planning#808（planning#813 で追加）／ADR-0071 テーマごとの同時保有の上限を統制値として新設し（既定は暗号資産と AI・半導体の 2 テーマ・上限は各 1 銘柄・未約定の新規建ても数える）、取引判断に保有中の全銘柄を渡し、定義と上限は SC-02 からも設定できるようにする（IADR-0562 の「SC-02 は不採用」を改める）。planning#809（planning#813 で追加）。いずれも Accepted。前回の実測（`55e464a`）以降、既存の AST 計画 ADR の状態の変更は 0 件（`07_adr/` の差分は 3 件の追加のほか、ADR-0008・0033・0039・0045・0064 への日付つきの補完の注と README のみ。状態は Accepted のまま）。本リポの追随は本 PR〕。
  **2026-10-21 実測**: 計画リポの `origin/main`（`577d21f`。`git rev-parse --is-shallow-repository` → `true` のため `git archive origin/main` で展開した木で実行。
  ファイルの実在の検査であり履歴は使わない）で `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 71]・71 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
- **2026-10-24 に 0075 へ**〔ADR-0072 判断由来の決済の指値の未約定時の取消は、PoC の経路に限り AST#1214 の実弾確認の前に有効にする（ADR-0067 決定 6 の部分改定）。planning#821／ADR-0073 基準資金（equity）の「照会できない」は、直前の米国東部暦日に成功した観測が無いことを指し、そのときは新規建てを拒否して SC-03 と Discord へ知らせる（ADR-0041 決定 2・フォローアップ 3 の補完）。planning#829／ADR-0074 月次 LLM 費用上限の 100% 到達では定時・価格変動の両方の判断を LLM の前で見送る（NFR-13・§6.1 の補完）。planning#830／ADR-0075 日報の無応答時の設定「停止」は、期限を過ぎて日報が未確定の間、取引判断の全体を見送る（FR-07・UC-03・ADR-0003 フォローアップの補完）。planning#831。いずれも Accepted。前回の実測（`577d21f`）以降、既存の AST 計画 ADR の状態の変更は 0 件（`07_adr/` の差分は 4 件の追加のほか、ADR-0003・0041・0063・0066・0067・0070・0071 への補完・例外 4 の注記と README のみ。状態は Accepted のまま）。本リポの追随は #1423 M-3（IADR-0581 と同じ PR）〕。
  **2026-10-24 実測**: 計画リポの `origin/main`（`a47c050`。`git rev-parse --is-shallow-repository` → `true` のため `git worktree add --detach origin/main` で展開した木で実行。
  ファイルの実在の検査であり履歴は使わない）で `node tools/doc-checks/gen-plan-ranges.js --check` が exit 0、ai-stock-trading は ADR **[1, 75]・75 件・欠番なし**〔FR [1, 21] / UC [1, 7] / SC [1, 4] も宣言と一致〕。
