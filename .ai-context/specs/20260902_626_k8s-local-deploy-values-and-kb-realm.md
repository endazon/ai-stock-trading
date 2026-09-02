---
title: k8s-local-deploy.sh の前回値保持と values-local.yaml の KB レルム名是正
type: spec
status: draft
related_ids: [NFR, IADR-0109]
author: claude (Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs: []
---

# 仕様書: k8s-local-deploy.sh の前回値保持と values-local.yaml の KB レルム名是正

> 本仕様書は実装着手前に作成する。本作業は計画書由来の機能要求ではなく、ローカル k8s 配備手順
> （`scripts/k8s-local-deploy.sh` / `deploy/helm/ai-stock-trading/values-local.yaml`）の運用上の
> 不具合是正である（起点 [#626](https://github.com/endazon/ai-stock-trading/issues/626)）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（デプロイ手順・運用系の不具合。計画側の稼働要件表に対応する項目がないため
  `traceability.md` の無採番 `NFR` 許容 2（規約整備・検査器・メタ作業に準じる運用系是正）に該当）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（実装ADR IADR-0109 / IADR-0100 / IADR-0102 / IADR-0111 / IADR-0060 / IADR-0093 が
  関連する既存の実装判断。本書はそれらの延長）
- 計画書リンク: なし

## 目的・背景

2026-09-02 の実測で判明した 3 つの不具合を是正する（詳細は issue #626 に実測ログを記載）。

1. **前回リリースの値の消失**: `scripts/k8s-local-deploy.sh` の `helm upgrade --install` は
   `--reuse-values` を使わない。`BROKER_TIER` / `OPEND_ENABLED` の env passthrough 自体が無いため、
   一度 `--set broker.tier=moomoo-sim --set opend.enabled=true` で手動有効化した環境でも、本スクリプトを
   再実行すると両方とも既定（`paper` / `false`）へ黙って戻る。`opend.enabled=false` は OpenD の
   Deployment ごと削除する（35 日間の稼働実績が途切れた実測あり）。`DISCORD_BOT_*` は既に env
   passthrough があるが、これも同じ「export し忘れで無言に戻る」性質を共有している。
2. **KB レルム名の誤り**: `values-local.yaml` の `KnowledgeBase__Auth__Authority` が実在しない
   realm `microservices-platform` を指し、KB 保存が s2s トークン取得 404 で全件 fail-safe 縮退する。
3. **（追加実測）OpenD PVC の消失**: 上記1の事故で `opend.enabled` が `false` に戻ると、chart の
   PersistentVolumeClaim（`opend-persist`）も削除され、デバイス信頼状態が失われて次回有効化時に
   SMS/画像 CAPTCHA の再認証が要る。

## 対象範囲

- 対象:
  - `scripts/k8s-local-deploy.sh`（`OPEND_ENABLED` / `BROKER_TIER` の env passthrough・前回値保持）
  - `scripts/k8s-local-deploy.test.sh`（挙動の固定。helm スタブを追加）
  - `deploy/helm/ai-stock-trading/values-local.yaml`（KB レルム名の是正・114 行/149 行）
  - `deploy/helm/ai-stock-trading/templates/opend.yaml`（PVC に `helm.sh/resource-policy: keep`）
  - `deploy/opend/k8s/pvc.yaml`（dev 生 manifest 側にも同アノテーションを揃える）
  - `deploy/helm/ai-stock-trading/README.md` / `deploy/opend/README.md`（挙動の追随）
- 対象外:
  - moomoo-live（実弾）の解禁（既存の閂は変更しない）
  - `DISCORD_BOT_*` の再設計（既存の `--set-string` 空既定＝差し替えなしはそのまま。ただし本作業の
    「前回値保持」の要否は下記「検討した選択肢」で扱う）
  - Vault / External Secrets 経由の値供給（#24 の射程）

## 設計

### 1. 前回値の引き継ぎ方式

`helm upgrade --install` に `--reuse-values` を追加する案と、`broker.tier` / `opend.enabled` を
個別に前回値へ `--set` で補う案を比較した（下記「検討した選択肢」）。**個別引き継ぎ方式を採用する**。

- 新関数 `ast_prev_release_value <top> <nested>` が `helm get values ast -n ai-stock-trading -o yaml`
  の出力を awk で読み、`<top>:` ブロック内の `<nested>:` の値を返す（release 不在・キー不在は空文字）。
- 新関数 `resolve_ast_value_overrides` が `AST_VALUE_KEYS`（`broker.tier|BROKER_TIER` /
  `opend.enabled|OPEND_ENABLED`）を順に処理し、`AST_VALUE_OVERRIDES` 配列（`--set key=value` の列）を
  組み立てる:
  - env が明示設定（非空）→ その値を使う。
  - env が明示設定（空文字）＋前回値が非空 → `ast-secrets` の #263 と同じ「キー名を列挙して中断」
    （`--force-empty-values` で強制可）。
  - env が明示設定（空文字）＋前回値が空/不在 → 失うものが無いので空のまま `--set` する。
  - env 未設定＋前回値が非空 → 前回値を `--set` で補う（引き継ぎ）。
  - env 未設定＋前回値も空/不在 → 何もしない（chart 既定に委ねる）。
- `helm upgrade` コマンドへ `"${AST_VALUE_OVERRIDES[@]}"` を追加する。

### 2. values-local.yaml のレルム是正

`KnowledgeBase__Auth__Authority` を `http://keycloak:8080/realms/platform` へ是正する（114 行・149 行）。
根拠はコメントに残す（隣接クローン `../microservices-platform` の
`deploy/keycloak/microservices-platform-realm.json` 2 行目 `"realm": "platform"`、および MSP 自身の
Helm chart 既定値 `deploy/helm/microservices-platform/values.yaml` の `global.auth.authority` も同じ
`realms/platform` を指す）。

### 3. OpenD PVC の resource-policy

`templates/opend.yaml` の PersistentVolumeClaim に annotation
`helm.sh/resource-policy: keep` を付ける。これにより `opend.enabled` を `false` に戻す・
`helm uninstall` する操作のいずれでも PVC は削除されず、デバイス信頼状態が保たれる（明示的に消す場合は
`kubectl delete pvc opend-persist -n ai-stock-trading` を手動実行する）。`deploy/opend/k8s/pvc.yaml`
（dev 生 manifest）にも同じ annotation を揃える。

## 受け入れ基準

- [ ] `OPEND_ENABLED` / `BROKER_TIER` を export せずに `k8s-local-deploy.sh` を再実行しても、前回
      リリースの値が保たれる（helm upgrade に `--set broker.tier=<前回値>` 等が渡る）。
- [ ] 明示的な空指定（`OPEND_ENABLED=` 等）で非空の前回値を消す場合は中断し、
      `--force-empty-values` でのみ強制できる。
- [ ] 前回リリースが存在しない（新規環境）場合はエラーにならず、chart 既定のまま描画される。
- [ ] `values-local.yaml` の `KnowledgeBase__Auth__Authority` が `realms/platform` になる。
      `helm template` の描画差分が該当 2 行のみであることを確認する。
- [ ] OpenD の PVC（chart・dev 生 manifest 双方）に `helm.sh/resource-policy: keep` が付き、
      `opend.enabled` の有効/無効切り替えで PVC が消えないことを `helm template` の描画で確認する。
- [ ] `scripts/k8s-local-deploy.test.sh` が上記の不変条件を固定する。
- [ ] `.github/workflows/helm.yml` のゲート（lint・描画）がローカル実行で緑になる。

## テスト方針

- `scripts/k8s-local-deploy.test.sh` に `kubectl` スタブと同様の `helm` スタブ（`get values` のみ）を
  追加し、`resolve_ast_value_overrides` を直接呼んで「前回値保持 / 明示上書き / 明示空値での中断 /
  強制空値 / 新規環境」の 5 パターンを検証する（`AST_DEPLOY_LIB=1` で関数のみ読み込む既存方式を踏襲）。
- `helm lint --strict` と `helm template`（既定 / `values-local.yaml` / `opend.enabled=true` 各種）を
  ローカルで実行し、`.github/workflows/helm.yml` の各アサーションを手動再現する。

## 計画書との差異

- 差異: なし（本作業は計画書由来の機能要求ではなく、ローカル配備手順の不具合是正）

## 未決事項

- なし。`DISCORD_BOT_*` の前回値保持化（現状は空指定＝差し替えなしで前回値を壊さない設計に既になっている
  ため対象外とした）は、将来 `--force-empty-values` 相当の挙動が要求されたら別途検討する。
