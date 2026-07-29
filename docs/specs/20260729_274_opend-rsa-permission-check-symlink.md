---
title: OpenD entrypoint の RSA 鍵パーミッション検査を symlink 追跡（stat -Lc）へ是正し、誤警告を止める
type: spec
status: review
related_ids: [FR-05, ADR-0002, IADR-0053, IADR-0060, IADR-0109]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: RSA 鍵パーミッション検査の symlink 追跡（#274）

> Issue [#274](https://github.com/endazon/ai-stock-trading/issues/274)（fix・重大度 低）。
> 2026-07-28 の経路B moomoo SIMULATE 有効化時に実測された運用上の落とし穴。
>
> **実弾は撃たない。** 本作業は `deploy/opend/entrypoint.sh`（OpenD コンテナの起動スクリプト）と
> その自動テスト・CI ジョブに閉じ、アプリケーションコード（`backend/`）・chart の描画・
> 実弾の閂（[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) 決定5 ほか）には一切触れない。
> 稼働中の live 環境にも触れない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: **FR-05**（発注執行）。RSA 暗号化は cross-network（別 Pod 間）の trade 接続の前提
- 計画書: [03_moomoo-integration.md](../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md)、
  [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)（moomoo OpenAPI）
- 関連 IADR: [IADR-0053](../adr/IADR-0053_moomoo-opend-dockerization.md)（OpenD の Docker 化・entrypoint 導入）、
  [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) **決定3**（パーミッション制御は挙動中立なので既定で有効化。
  本検査はその一部）、[IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md)（Bash スクリプトを
  `AST_DEPLOY_LIB=1` で source して関数だけを読み込み、`kubectl` スタブでテストする既存 idiom）
- 対象 Issue: [#274](https://github.com/endazon/ai-stock-trading/issues/274)（起票の起点は #132 / IADR-0060 決定3）

## 目的・背景

`deploy/opend/entrypoint.sh` は、RSA 秘密鍵が所有者以外に読める状態（Secret の `defaultMode` 誤設定）を
起動時に検知して警告する（IADR-0060 決定3）。この検査が **k8s Secret ボリュームでは必ず誤警告になる**。

k8s の Secret ボリュームは実体を `..data/` 配下に置き、可視パスを **symlink** にする。
symlink 自身のパーミッションは常に `777` であり、`stat -c '%a'` は symlink を辿らない（lstat）ため、
実体が正しく `0400` でも常に `777` を読む。

```
$ ls -l /opt/opend/rsa/
lrwxrwxrwx 1 root root 20 ... opend_rsa.pem -> ..data/opend_rsa.pem
$ stat -c  '%a' /opt/opend/rsa/opend_rsa.pem   # lstat＝symlink 自身
777
$ stat -Lc '%a' /opt/opend/rsa/opend_rsa.pem   # 実体
400
```

機能影響は無い（OpenD は鍵を正常に読める。実際に Deployment の `defaultMode` は `256`＝`0400` で描画されている）。
問題は**毎起動で警告が出るため、本物の誤設定を検知できない**こと（狼少年化）であり、
IADR-0060 決定3 のハードニングが「効いていないように見える」ことである。

## 範囲

### 対象（In scope）

| # | 対象 | 内容 |
| --- | --- | --- |
| 1 | `deploy/opend/entrypoint.sh` | パーミッション検査を `stat -Lc '%a'`（symlink 追跡）へ是正。併せて RSA 鍵の検査（存在＋パーミッション）を関数へ切り出し、`AST_OPEND_LIB=1` で source したときは起動手順を実行しない（テスト容易性・既存 idiom） |
| 2 | `deploy/opend/entrypoint.test.sh` | 上記挙動の自動テスト（symlink 越し・実体直・誤設定・不在）。実コンテナ・実 OpenD・追加パッケージ不要 |
| 3 | `.github/workflows/ci.yml` | 既存 `shell-scripts` ジョブへ上記テストの実行ステップを追加 |

### 対象外（Out of scope）

- **アプリケーションコード（`backend/`）の変更**。`MoomooPreflight` は鍵の**存在のみ**を見ておりパーミッションは
  検査していないため、本件の影響範囲外（Issue #274 記載のとおり）。
- **chart の描画変更**。`opend.rsaSecretDefaultMode` は `0400` のまま。本番描画はバイト等価。
- **`entrypoint.sh` のそれ以外の挙動変更**（OpenD.xml 生成・`umask 077` / `chmod 600`・鍵不在時の起動停止）。
- **実弾解禁に関わる一切**（`Broker__Provider=paper` / `TrdEnv=simulate` / 起動時 real 拒否は不変）。
- **非 root 実行への切替**（IADR-0060 決定2 のまま。検査は `440` も許容し続ける）。

## 方式

### 1. `stat -Lc` で実体を検査する

`stat -c '%a'` → `stat -Lc '%a'`。`-L` は symlink を辿る（`stat(2)` 相当）。

- 直前の存在検査 `[ ! -f "$key_file" ]` は `test -f` ＝ symlink を辿るため、`-L` を付けても
  「壊れた symlink で `stat` が失敗する」経路は生じない（存在検査を通過した時点で実体がある）。
  それでも `|| mode="unknown"` を明示し、**失敗時の扱いを呼び出し文脈へ依存させない**。
  `set -e` は `f || exit 1` の文脈では関数内でも抑止される（＝空のまま警告へ倒れる）が、
  素の呼び出しでは代入の失敗で即座に落ちる——この差を読み手に推論させないための 1 語である。
- ベースイメージは `mcr.microsoft.com/dotnet/runtime-deps:8.0-jammy`（Ubuntu 22.04・GNU coreutils）であり
  `stat -L` を持つ。BusyBox の `stat` も `-L` を持つため、将来イメージを差し替えても壊れない。
- 実体が `400` / `440` でない場合だけ警告する点（推奨値・是正先の案内）は従来どおり。

`entrypoint.sh` 内の `stat` 使用箇所は本 1 箇所のみであり、他に同種の lstat 誤検知は無い
（`grep -rn 'stat -c\|stat --format' --include='*.sh'` で確認）。

### 2. 検査を関数へ切り出してテスト可能にする（既存 idiom の踏襲）

現行の検査は `entrypoint.sh` のトップレベルに直書きされており、スクリプト末尾で `exec ./OpenD` するため
**そのままでは自動テストできない**（`cd /opt/opend` と実バイナリを要求する）。

[IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md) が `scripts/k8s-local-deploy.sh` で確立した idiom
（`AST_DEPLOY_LIB=1` で source すると関数定義だけを読み込み、手順は実行しない）に揃え、
`require_rsa_key_file` を関数化して `AST_OPEND_LIB=1` の入口を設ける。

**新規 IADR は作成しない**（判断の記録は本仕様書に置く）。理由:

- 本体の修正は IADR-0060 決定3 が**意図していた挙動を回復する**バグ修正であり、新たな方式決定を含まない。
- 関数化とテスト入口は IADR-0109 で既に採用済みの方式の適用であって、新規の選択ではない。

### 3. 挙動の同一性

| 観点 | 変更前 | 変更後 |
| --- | --- | --- |
| 鍵未指定（`OPEND_RSA_KEY_FILE` 空） | 検査しない | 同左 |
| 鍵指定・ファイル不在 | `ERROR` 2 行 ＋ `exit 1` | 同左（メッセージ・終了コードとも同一） |
| 鍵指定・実体 `400`/`440` | symlink 経由なら誤って `WARN` | **`WARN` を出さない** |
| 鍵指定・実体がそれ以外 | （symlink 経由では実体を見ていない） | 実体のモードを添えて `WARN`（終了しない） |
| `OpenD.xml` の生成・`chmod 600`・`exec ./OpenD` | — | 不変 |

## 受け入れ基準（Issue #274 の基準に対応）

- [x] `deploy/opend/entrypoint.sh` の RSA パーミッション検査が `stat -Lc`（symlink 追跡）になっている
- [x] k8s Secret ボリュームと同じ構成（`..data/` 実体 ＋ symlink・実体 `0400`）で **警告が出ない**（T-274-01/02）
- [x] 実体のパーミッションが誤っている（`0400`/`0440` 以外）場合は**従来どおり警告が出る**（T-274-03/05）
- [x] 鍵ファイル不在時の起動停止（`exit 1`）は不変（T-274-06）
- [x] `entrypoint.sh` に他の lstat 誤検知が無いことを確認済み（リポジトリ全体で `stat` の使用は本 1 箇所）
- [x] CI（ci / security(gitleaks) / helm / doc-links / pr-title）が緑（PR [#275](https://github.com/endazon/ai-stock-trading/pull/275)）
- [x] `dotnet build` / `dotnet test` は**変更なしで緑**（`backend/` に一切触れない＝差分ゼロ）
- [x] 実弾 OFF・SIMULATE 固定・chart の本番描画バイト等価が不変（chart・`backend/` とも差分ゼロ）

検証記録（PR #275・`ubuntu-latest`）: `deploy/opend/entrypoint.test.sh` は **17 passed, 0 failed, 0 skipped**。
`stat -Lc` を修正前の `stat -c` へ戻す変異版では **13 passed, 4 failed**（rc=1）となり、テストが回帰を捕まえる
ことを実測した。

## テスト方針

`deploy/opend/entrypoint.test.sh`（Bash のみ・外部依存なし）で `require_rsa_key_file` の挙動を固定する。
`shellcheck` は CI に未導入（`scripts/k8s-local-deploy.test.sh` と同様に `# shellcheck source=` 注釈のみ置き、
ローカルで任意実行できる形にする）。

| ID | ケース | 期待 |
| --- | --- | --- |
| T-274-01 | k8s 相当の symlink 越し・実体 `0400` | 警告を出さない・終了コード 0（**本 Issue の回帰テスト**） |
| T-274-02 | k8s 相当の symlink 越し・実体 `0440`（非 root 時） | 警告を出さない・終了コード 0 |
| T-274-03 | k8s 相当の symlink 越し・実体 `0644`（誤設定） | 警告を出す・実体のモード `644` を表示・終了コード 0 |
| T-274-04 | symlink を介さない実ファイル `0400` | 警告を出さない（既存挙動の据置） |
| T-274-05 | symlink を介さない実ファイル `0644` | 警告を出す（既存挙動の据置） |
| T-274-06 | 鍵ファイル不在 | 終了コード 1・`ERROR` に Secret 名の案内 |
| T-274-07 | 警告文に是正先（`opend.rsaSecretDefaultMode`）が含まれる | 含まれる |

CI は `ubuntu-latest`（Bash ＋ symlink ＋ `stat -L`）で走るため追加のインストールは不要。
Windows（Git Bash）等 symlink を作れない環境では該当ケースを **skip** し、理由を表示して緑にする
（実行環境依存で偽陰性・偽陽性を出さない）。

## リスク・留意点

- 関数化により `entrypoint.sh` の構造が変わるが、実行経路（呼び出し順・メッセージ・終了コード）は同一。
  差分は「検査ブロックの関数化」「`stat -c` → `stat -Lc`」「lib 入口の追加」の 3 点に限られる。
  唯一の可視差は `WARN`（標準エラー）が `==> RSA encryption enabled`（標準出力）の**前**に出る点で、
  文言・終了コードは不変（別ストリームであり元々順序は保証されない）。
- `AST_OPEND_LIB` は**テスト専用の入口**であり、コンテナ実行時には設定されない（Dockerfile / chart のどこにも
  現れない）。値が `1` のときだけ早期 return するため、誤設定で起動が止まる経路は無い。
- `stat -L` は symlink の**先**を見るため、鍵の実体が別 Secret へ差し替わった場合もその実体を評価する
  （k8s の `..data` 切替は atomic な symlink 差し替えで行われるため、これが正しい評価対象である）。
