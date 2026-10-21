---
title: 連結ローカルの起動器は、機密クライアント 5 件の secret が開発用の値なら止まる（fail-closed・明示の opt-in でだけ許す。Refs GHSA-pcf9-8mh4-8wgh）
type: spec
status: done
related_ids: [NFR, IADR-0098, IADR-0109, IADR-0341, IADR-0051, IADR-0093, IADR-0323, IADR-0485]
author: claude (Claude Code)
created: 2026-10-22
updated: 2026-10-22
---

# 仕様書: 起動器の開発用の secret を fail-closed にする（Refs GHSA-pcf9-8mh4-8wgh）

> 本仕様書は実装着手前に作成した（着手 2026-10-22。AST `origin/develop` `40de5a61`）。

## 起点（トレーサビリティ）

- 起点: Refs GHSA-pcf9-8mh4-8wgh（オーナー裁定 2026-10-21。既定を fail-closed にする）。非機能＝ `NFR`。
- IADR: **IADR-0098 に日付つきの追記**（決定 3 の改め。凍結記録の規約に従い本文は書き換えない）。新規 IADR は作らない。
- 計画との矛盾: なし（計画は開発用の資格の扱いを定めていない）。

## 作業の範囲（AST 側）

1. `scripts/k8s-local-deploy.sh`: `[0/5]`（イメージを作る前）に `ast_dev_client_secret_guard` を足す。対象は 5 件
   （`service-auth-client-secret`・`discord-owner-auth-client-secret`・`kb-auth-client-secret`・`kb-reader-auth-client-secret`・`llm-auth-client-secret`）。
   開発用の値・確かめられない値があれば止める。opt-in は `--allow-dev-client-secrets` / `ALLOW_DEV_CLIENT_SECRETS=1`。
2. `scripts/k8s-local-deploy.test.sh`: T-pcf9-01〜09。
3. IADR-0098 の追記と索引の行、chart README・`scripts/README.md`・`infra/README.md`。

**範囲外（基盤側。オーナーへ報告する）**: 基盤の `scripts/lib/dev-client-secret-guard.sh` の `DEV_CLIENT_SECRET_GUARDED`・
`deploy/local/keycloak-setup/reconcile-realm.js` の `DEV_SECRET_GUARDED_CLIENTS`（`--check-dev-secrets` の対象）・
`deploy/local/vault/eso/bootstrap.sh` の AST の種（`ai-stock-trading/app-secrets`）の判定への 5 件の追加。
いずれも基盤のリポジトリにあり、AST からは変更できない。

## 判定の設計

| 経路 | 見る値 | Secret が無い |
| --- | --- | --- |
| ESO 所有（`AST_ESO_MODE=1`） | 稼働中の `ast-secrets` の値（base64 のまま）。env は見ない（ESO 所有では使われない） | 確かめられない → 止める |
| 従来経路 | `sync_ast_secrets` と同じ順: env の明示 → 既存の値 → 既定値 | 既定値で判断する（新規環境） |

- 開発用の値は宣言から導く: svc / owner は `AST_SECRET_KEYS` の既定値、kb-writer / kb-reader / llm-caller は
  client id の既定から基盤の realm の宣言と同じ規則で導く（`ast_dev_client_secret_value`）。値を新たに複写しない。
- 空は開発用の値とみなさない（その s2s を使わないだけで、公開の値で動くことにはならない）。ESO 所有で `ast-secrets` にキーが無い場合も空と同じ扱い
  （ESO は Vault に無いキーを作らず、起動器も書かない）。従来経路でキーが無い場合は、`sync_ast_secrets` が書く既定値で判断する。
- 稼働中の値の問い合わせは `{{with index .data "<key>"}}{{.}}{{end}}`（キーが無ければ空。`index` 単独は `<no value>` を出し、判定をすり抜ける。AI レビューの指摘）。
- 比較は base64 の表現どうし（`ast_b64` と Secret の `.data`）で、復号しない（IADR-0109 決定 3「平文を読み出さない」の延長）。表示はキー名と環境変数名だけ。
- opt-in の変数名は基盤の判定と同じ `ALLOW_DEV_CLIENT_SECRETS`（`1` だけ）。基盤の判定と違い、kube context は見ない（連結ローカルの PoC も k3d である）。

## 母集合（規則 9・10）

走査語: `adopt-existing-secrets`・`SERVICEAUTH_CLIENTSECRET`・`DISCORD_OWNERAUTH_CLIENTSECRET`・開発用の値の文字列（`.ai-context/specs/`・`CHANGELOG.md` を除く）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `scripts/k8s-local-deploy.sh` | 判定を足す |
| 2 | `scripts/k8s-local-deploy.test.sh` | 試験を足す |
| 3 | `deploy/helm/ai-stock-trading/README.md`（デプロイの節・鍵の表の owner の行） | 節を足し、表の行から開発用の値の文字列を外して「止まる」旨へ |
| 4 | `scripts/README.md` の起動器の行 | 判定を足す |
| 5 | `infra/README.md`（owner の dev secret の再インポート手順） | opt-in の注記を足す（単体起動のレルムの説明そのものは変えない） |
| 6 | `.ai-context/adr/IADR-0098`・索引 | 日付つきの追記 |
| 7 | `docker-compose.yml`・`.env.example`・`infra/keycloak/realm-export.json`・統合試験 | **対象外**（単体起動・単体 E2E の経路で、起動器を通らない） |
| 8 | `.ai-context/adr/IADR-0341` | 書き換えない（凍結記録） |

## 受け入れ基準

- [x] AC-1: 対象は 5 件ちょうどで、開発用の値は 5 件とも非空・互いに違う（T-pcf9-01）
- [x] AC-2: 従来経路の新規環境で env が無ければ止まり、空既定のキーは名指さない（T-pcf9-02）。開発用でない値なら進む（T-pcf9-03）
- [x] AC-3: env・既存の値のどちらから来た開発用の値でも止まる。Secret は在るがキーが無いときも既定値で判断する（T-pcf9-04・05・05b）
- [x] AC-4: ESO 所有で Secret が無ければ止まり、稼働中の値で判定し env は見ない（T-pcf9-06・07）
- [x] AC-5: 明示の opt-in（フラグ・`=1`）だけが通し、警告する。`=true` は opt-in ではない（T-pcf9-08）
- [x] AC-6: イメージを作る前に判定し、止まったら進まない（T-pcf9-09）
- [x] AC-7: 開発用の値・試験の値のどれも標準出力・標準エラーに出ない（各 T の「値を出さない」）
- [x] 変異: 比較を外す → 11 件赤／opt-in を常に真にする → 6 件赤

## 配備前の手順（稼働中の PoC。値は書かない）

1. 新しい値を作る（5 件それぞれ。32 文字以上の乱数）。
2. 認証基盤（Keycloak の MSP レルム）の 5 クライアント（`ai-stock-trading-svc`・`ai-stock-trading-owner`・`ai-stock-trading-kb-writer`・
   `ai-stock-trading-kb-reader`・`ai-stock-trading-llm-caller`）の client secret を新しい値にする。
3. 同じ値を Vault の `ai-stock-trading/app-secrets` の 5 プロパティ（`service-auth-client-secret`・`discord-owner-auth-client-secret`・
   `kb-auth-client-secret`・`kb-reader-auth-client-secret`・`llm-auth-client-secret`）へ入れ、`ast-secrets` を同期させる（基盤の画面か ESO の force-sync）。
   手順 2 と 3 の間は s2s の認証が失敗するため、**閉場後**に続けて行う。
4. 閉場後に `scripts/k8s-local-deploy.sh` を回す。`[0/5]` が通ることを確かめる。
   - 値を回さずに配備だけ通したいときは `--allow-dev-client-secrets` を付ける（警告が出る）。付けずに回すと `[0/5]` で止まり、
     **何も変えずに終わる**（稼働中の Pod には触れない）。

## 検証（実施記録）

- `bash scripts/k8s-local-deploy.test.sh`、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、helm.yml の起動器の検査、文書系検査
