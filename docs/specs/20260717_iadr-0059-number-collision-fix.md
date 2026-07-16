---
title: IADR-0059 の番号衝突解消（後着を IADR-0060 へ採番し直し・索引を補完）
type: spec
status: review
related_ids:
  - IADR-0059
  - IADR-0060
author: endazon (with Claude Code)
created: 2026-07-17
updated: 2026-07-17
plan_refs: []
---

# 仕様書: IADR-0059 の番号衝突解消

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（実装リポジトリ内の文書整合の是正。計画書起点を持たない housekeeping）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: [IADR-0059](../adr/IADR-0059_dedupe-retention-purge.md)（重複排除ストアのパージ・#137 / PR #144）、
  [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)（OpenD 本番化・#132 / PR #143。**本 PR で 0059 から採番し直し**）
- 計画書リンク: なし

## 目的・背景

並行ブランチの PR #144（重複排除ストアの保持期間パージ）と PR #143（OpenD 本番化）が、いずれも `develop` の
最新版号を確認せずに `IADR-0059` を採番した。両者が相次いで `develop` へマージされた結果、`docs/adr/` に
**同番号のファイルが 2 つ**存在する状態になった。

- `docs/adr/IADR-0059_dedupe-retention-purge.md`（PR #144・マージ時刻 2026-07-17 00:20:39）
- `docs/adr/IADR-0059_opend-production-cutover-gates.md`（PR #143・マージ時刻 2026-07-17 00:20:59）

さらに `docs/adr/README.md` の索引は `IADR-0058` で止まっており、上記 2 件はどちらも未登録である。
ADR README の運用ルール「連番はリポジトリ内で一意・昇順・欠番なし」に二重に違反している。

本作業はこの番号衝突と索引の欠落を解消する。**新規の意思決定は行わない**（＝新規 IADR を作らない）。
既存 2 件の決定内容は一切変更せず、後着の ID 表記のみを機械的に置換する。

## 対象範囲

- 対象:
  - 後着（PR #143・OpenD 本番化）の `IADR-0059` → `IADR-0060` への採番し直し。
    ファイル名・frontmatter `title`・見出し・本文中の ID 表記・作業仕様書・相互リンク・コード内コメント・
    Helm chart / CI ワークフロー内の参照を**すべて** 0060 へ統一する。
  - `docs/adr/README.md` の索引へ `IADR-0059`・`IADR-0060` の 2 行を ID 昇順で追記し、
    衝突の経緯と再発防止を注記する。
- 対象外:
  - 先着（PR #144・重複排除パージ）の `IADR-0059`。**番号・内容とも一切触れない**（先着尊重）。
  - 両 IADR の決定内容・状態（Accepted）・設計そのもの。
  - コードの挙動。本作業はコメント／文書の ID 表記のみを変更し、**実行時の挙動を変えない**。
  - 既存文書が参照する **microservices-platform 側の `IADR-0060`**（別リポジトリの採番空間。
    `docs/adr/IADR-0046` / `IADR-0048` / `docs/specs/20260712_107_runtime-scaffold.md` 等）。
    本リポジトリの `IADR-0060` とは別物であり、本 PR では触れない（後述「未決事項」）。

## 設計

**採番の決定規則**: プレイブックの「先着尊重」に従い、`git log` のマージ順で先にマージされた方が番号を保持する。

- 先着 = PR #144（`e7b99a8`・00:20:39）→ `IADR-0059` を**保持**
- 後着 = PR #143（`7a8c151`・00:20:59）→ `IADR-0060` へ**採番し直し**

`IADR-0060` は本リポジトリの `docs/adr/` において未使用であり（既存は 0000〜0059）、昇順・欠番なしを満たす。

**置換の安全性**: 単純な一括 `sed` は、両 IADR を参照する文書を破壊する。事前調査により、
`IADR-0059` を参照する 43 ファイルのうち**混在は `docs/operations/operations.md` の 1 件のみ**で、
残りは issue 番号（opend 側 = `#132` / パージ側 = `#137`）で機械的に判別できることを確認した。

- opend 側（`#132` を含む）15 ファイル: 一括置換
- パージ側（`#137` を含む）: 無変更
- `docs/operations/operations.md`: 行単位で判別して個別置換（opend 側 4 箇所のみ置換。
  frontmatter `related_ids` は両 IADR を参照するため `IADR-0060` を**追記**する）

## 受け入れ基準

- [x] `docs/adr/` に `IADR-0059` のファイルが 1 件だけ存在する（＝ `IADR-0059_dedupe-retention-purge.md`）
- [x] `docs/adr/IADR-0060_opend-production-cutover-gates.md` が存在し、frontmatter `title` と見出しが `IADR-0060` である
- [x] 旧ファイル名 `IADR-0059_opend-*` への**参照**がリポジトリ内に 1 件も残っていない
      （本仕様書「目的・背景」の**衝突前の状態を記録した記述**を除く。当該箇所はリンクではなく経緯の記録である）
- [x] opend 由来（`#132`）の `IADR-0059` 表記が 1 件も残っていない
- [x] パージ由来（`#137`）の `IADR-0059` 表記が 1 件も書き換わっていない（先着の番号は不変・61 箇所）
- [x] `docs/adr/README.md` の索引に `IADR-0059`・`IADR-0060` の両行が ID 昇順で並び、既存行が欠落していない
- [x] `node scripts/check-doc-links.js` がリンク切れ 0 で通る（165 件）
- [x] `node --test scripts/scripts.test.js` が通る（34 件）
- [x] `dotnet build` / `dotnet test` が通る（コメントのみの変更＝挙動不変の確認）
      ※ `dotnet test` は CI と同じ `--filter "Category!=Integration"` で全緑。フィルタ無しでは
      `AiStockTrading.IntegrationTests` が Docker 不在で落ちるが、本変更とは無関係（当該ファイルは未変更）。
- [x] CI（helm 描画ゲートを含む）が全緑である（14 チェック）

## テスト方針

本作業はコメント・文書の ID 表記の変更であり、新規のテストケースは追加しない（挙動を変えないため、
写像すべき受け入れ基準を持たない）。既存の検証で「壊していないこと」を示す。

- リンク整合: `scripts/check-doc-links.js`（相対リンク切れの検出。ADR ファイル名の変更が主リスク）
- スクリプト単体テスト: `scripts/scripts.test.js`
- ビルド／テスト: `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx`
- Helm: `.github/workflows/helm.yml` の描画ゲート（コメント変更のみだが、YAML の破損がないことを確認）
- 目視: 上記「受け入れ基準」の grep による確認（0059 / 0060 の残存・混在）

## 計画書との差異

- 差異: なし（計画書に対する変更を含まない。実装リポジトリ内の文書規約の是正に閉じる）

## 未決事項

- 本リポジトリの `IADR-0060` と、既存文書が参照する **microservices-platform 側の `IADR-0060`**
  （submodule ユニット運用・単一情報源継承）が、**プロセス上は別空間だが文面上は同じ表記**になる。
  参照箇所の多くは「microservices-platform IADR-0060」「platform IADR-0060」と修飾済みだが、
  `docs/adr/IADR-0046_unit-repo-layout.md` の 1 箇所は修飾がなく、文脈依存で読み分けている。
  本 PR の scope 外（既存の曖昧さであり、本変更が作り出したものではない）として触れないが、
  他リポジトリの IADR を参照する際は必ずリポジトリ名で修飾する運用が望ましい。要判断。
