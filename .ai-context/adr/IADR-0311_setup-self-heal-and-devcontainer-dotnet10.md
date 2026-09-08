---
title: IADR-0311 setup.sh の SDK 自己修復と devcontainer の .NET 10 化・Node 版の統一
type: impl-adr
status: Accepted
related_ids: [NFR]
author: claude (Claude Code)
created: 2026-09-09
updated: 2026-09-09
plan_refs: []
related_specs:
  - ../specs/20260909_709_712_ops-hygiene-bundle.md
---

# IADR-0311: setup.sh の SDK 自己修復と devcontainer の .NET 10 化・Node 版の統一

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

## 起点・関連

- 関連する計画書 ID: なし（開発環境セットアップというメタ作業。`NFR` 無採番＝工程のメタ作業。
  `.claude/rules/traceability.md` の無採番許容 2 に該当）
- 起点 issue: [#709](https://github.com/endazon/ai-stock-trading/issues/709)
- 関連する実装仕様書: [20260909_709_712_ops-hygiene-bundle](../specs/20260909_709_712_ops-hygiene-bundle.md)

## コンテキストと課題

`.devcontainer/devcontainer.json` は `mcr.microsoft.com/devcontainers/dotnet:8.0` を使っていたが、
本リポジトリのターゲットは `net10.0`（ルート `Directory.Build.props` / `global.json` の
`sdk.version: 10.0.100`）であり、コンテナのベースイメージだけが 2 世代遅れていた。

一方 `scripts/setup.sh`（SessionStart hook / devcontainer `postCreate` から実行）は
`command -v dotnet` が偽なら**何もせず**次の分岐へ進んでいた。プロキシ環境のセッションでは
`dotnet` が PATH に無いにもかかわらず `$HOME/.dotnet/dotnet` に SDK 本体が実在する構成が
実際に観測され（本 IADR 着手時点で `$HOME/.dotnet/sdk/10.0.400` が実在）、この場合
`dotnet restore` が丸ごとスキップされていることに気づきにくかった（ログの「.sln/.slnx が無いため
スキップ」は「dotnet が無いからスキップ」と読み違えられる）。

加えて Node のバージョンが `ci-latency-watch.yml` だけ `22` で、他の全ワークフロー・
devcontainer（`20`）と揃っていなかった。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | devcontainer のベースイメージを `dotnet:10.0` へ上げるだけ | △ devcontainer を使わない実行環境（本セッションのようなプロキシ環境）の症状は直らない |
| B | `setup.sh` に自己修復ロジックを足すだけ | △ devcontainer が 8.0 のままでは新規に devcontainer を立てた人が毎回この自己修復に頼ることになり、事前に直せる不整合を放置する |
| **A + B（採用）** | 両方を実施する | ✅ devcontainer は正規の手段（イメージから正しいバージョンが入る）として直し、`setup.sh` の自己修復は devcontainer に依らない実行環境（プロキシ環境のセッション等）への保険として持つ。互いに排他的ではない |

## 決定

### 決定 1: `.devcontainer/devcontainer.json` のベースイメージを `mcr.microsoft.com/devcontainers/dotnet:10.0` へ上げる

`mcr.microsoft.com/v2/devcontainers/dotnet/tags/list`（Docker Registry HTTP API v2）を実際に
取得し、プレビューではない GA タグ `10.0` / `10.0-noble` が存在することを確認した上で採用した
（実測 2026-09-09。取得した完全なタグ一覧は本 IADR には転記しない——タグ一覧そのものは
移り変わるため、この決定が正しかったかの再確認は都度 API を叩く）。旧イメージの表記
（バージョン番号のみ・ディストリビューション接尾辞なし）と同じ書式に揃え、`8.0` → `10.0` の
1 行差分に留めた。Node feature（`ghcr.io/devcontainers/features/node:1`）は既に `20` であり、
CI と揃っていたため変更していない。

### 決定 2: `setup.sh` に .NET SDK の自己修復を足す（devcontainer に依らない保険）

`command -v dotnet` が偽のとき、2 段で対処する。

1. **`$HOME/.dotnet/dotnet` が実在すればこのプロセスの PATH へ追加する**
   （`export PATH="$HOME/.dotnet:$PATH"`）。本スクリプトは別プロセスとして実行されるため、
   ここでの `export` は**呼び出し元シェルには伝播しない**——同一セッションで対話的に使うには
   利用者・エージェントが同じ `export` を実行する必要があり、その旨をログで案内する
   （PATH を汚染して呼び出し元へ勝手に反映させる手段は無い。`.bashrc` 等の恒久変更もしない
   ——本スクリプトは非対話のセットアップフックであり、シェル起動設定を書き換える責務を持たない）。
2. **無ければ `https://dot.net/v1/dotnet-install.sh` で `$HOME/.dotnet` へ導入を試みる**
   （**fail-open**。ネットワーク不可な環境を想定し、失敗しても `exit 0` で継続する）。
   channel は `global.json` の `sdk.version`（例 `10.0.100` → `10.0`）を優先し、無ければ
   `Directory.Build.props` の `<TargetFramework>netX.Y</TargetFramework>` から導く
   （jq 等の追加依存を持たず grep/sed のみで抜き出す。本スクリプトの既存方針＝依存ゼロに揃える）。

**実測（本 IADR 着手時点・2026-09-09、プロキシ環境のセッション）**:

- **経路 1（PATH 追加）を実走で確認した。** `dotnet` を PATH から外した状態で `setup.sh` を
  実行すると、`$HOME/.dotnet/dotnet`（SDK `10.0.400`）が検出されて PATH へ追加され、後続の
  `dotnet restore backend/backend.slnx` が全 40 プロジェクトぶん成功した。
- **経路 2（新規導入）も実走で確認できた。** 当初は「ネットワーク不可を想定」としていたが、
  空の `$HOME` で実際に `dotnet-install.sh` を取得・実行したところ **成功し、SDK `10.0.401` が
  導入され、その場で `dotnet restore` まで通った**。🔴 **`dot.net` への到達性は変化していた**
  ——`curl -sI https://dot.net/v1/dotnet-install.sh` は `HTTP/2 301` で
  `https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh` へリダイレクトし、
  `curl -fsSL`（既定でリダイレクトを追う）はそれを辿って `200` を得る。本セッションのプロキシは
  この経路を通す。**したがって「ネットワーク不可を前提に fail-open にする」という設計判断自体は
  変えないが**（プロキシの許可先は環境ごとに違い得り、恒久的な到達性を仮定できないため）、
  少なくとも本セッションでは経路 2 も実働することが分かった。
- **`--self-test` 相当の合成テストは `DOTNET_INSTALL_DRY_RUN=1` で用意した。** 実ネットワークを
  叩かずに channel 導出（`global.json` 優先・`Directory.Build.props` フォールバック・既定
  `10.0`）だけを固定できる。`bash -n scripts/setup.sh` の構文検査と合わせ、
  `scripts/scripts.repo.test.js` に回帰テストとして追加した。

### 決定 3: `npm ci` は `setup.sh` の必須範囲に含めない

`scripts/setup.sh` には Node 依存導入の例（コメントアウト済み）が既にあるが、有効化しなかった。
本リポジトリの `frontend/` は独立した Node パッケージであり、Node 依存の導入は
`npm --prefix frontend ci` のように**個別に呼ぶ運用**が CI（`ci.yml` 各所の `Bash(npm --prefix:*)`
許可）・`.claude/settings.json` の許可リストと既に一致している。`setup.sh` は「スタックに合わせて
必要なセットアップを追記する」設計（ファイル冒頭のコメント）であり、**バックエンド（.NET）の
自動 restore と同列でフロントエンドの `npm ci` を常時実行するようにすると**、`frontend/` を
変更しない大多数のセッションでも毎回 Node 依存解決が走り、SessionStart のたびに不要な待ち時間が
生じる。現状の「個別に呼ぶ」運用を変える理由が無いため、本 IADR では着手しない。

### 決定 4: Node のバージョンを `20` へ統一し `.nvmrc` を新設する

`ci-latency-watch.yml` だけが `node-version: '22'` で、他の全ワークフロー
（`ci.yml` / `backlog-audit.yml` / `changelog.yml` / `claude-code-review.yml` / `openapi.yml` /
`pr-title.yml`）と devcontainer の Node feature（いずれも `20`）から浮いていた。
`ci-latency-watch.yml` を `20` へ揃え、`.nvmrc`（`20`）を新設した。
`scripts/check-action-versions.js` 等の既存検査器は `node-version` の値そのものは検査していない
（grep で確認済み。アクションのメジャーバージョンのみを見る）ため、本件の再発を機械で
止める仕組みは今回追加していない——残余リスクとして記録する。

## 影響

- devcontainer で新規にコンテナを立てると `net10.0` に合った SDK が入る。
- devcontainer を使わない実行環境（本セッションのようなプロキシ下のクラウドセッション）でも、
  `setup.sh` が PATH 追加または新規導入で `dotnet` を使えるようにする。
- Node のバージョンが全ワークフロー・devcontainer で `20` に揃う。

## 残余リスク

- **`dot.net` への到達性は環境依存であり、恒久的な保証ではない。** 別のプロキシ設定・別の
  ネットワークポリシーでは再び到達不能になり得る。その場合は決定 2 の fail-open により
  `setup.sh` 自体は失敗しないが、`dotnet` が使えないまま以降の restore がスキップされる
  （ログで判別可能）。
- **Node バージョンの単一化を機械で強制していない。** `check-action-versions.js` はメジャー
  バージョン退行のみを見ており、`node-version` の値の乖離（本件のような `22` の混入）は
  検出しない。再発したら検査器の追加を検討する（CLAUDE.md の「同型事故 2 回から」に照らし、
  本件はまだ 1 回目）。
- **`.nvmrc` を読むツール（`nvm use` 等）は CI では使っていない。** CI の各 `setup-node@v7` は
  引き続き `node-version: "20"` を明示する（`.nvmrc` を読ませる `node-version-file:` へは
  移行していない）——両方を同時に変えると差分の切り分けが難しくなるため、本 IADR では
  `.nvmrc` の新設のみに留めた。
