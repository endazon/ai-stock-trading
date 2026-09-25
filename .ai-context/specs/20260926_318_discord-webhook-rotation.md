---
title: Discord Webhook 再発行と蓄積分の後始末の利用者手順（#318）
type: spec
status: accepted
related_ids: [NFR, FR-09, IADR-0109, IADR-0121, IADR-0333, IADR-0341]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs: []
---

# Discord Webhook 再発行と蓄積分の後始末の利用者手順

## 背景

#318 の 3 項目（Webhook の再発行・Loki / Tempo の蓄積分の扱い・再発行手順の文書化）のうち、AI が行えるのは 3 つ目と、
1・2 つ目のための読み取り専用の実測である。利用者の指示（2026-09-26・blocked:human の引き取り）による。

🔴 **ハードリミット**: 資格情報の作成・入力・ローテーション、Discord の画面操作、データの恒久削除、稼働クラスタへの書き込みは行わない。

## 実測（2026-09-26・読み取りのみ）

| 対象 | 方法 | 結果 |
| --- | --- | --- |
| URL の供給経路 | `values.yaml` / `values-local.yaml` の `secretKeyRef` | `Notifications__Discord__WebhookUrl` ← `ast-secrets` / `discord-webhook-url`（optional） |
| `ast-secrets` の所有 | `kubectl get externalsecret -n ai-stock-trading`・Secret の ownerReferences | **ExternalSecret 所有**（`vault-backend`・1h・SecretSynced）。キー名の一覧に `discord-webhook-url` あり（値は取得していない） |
| 反映 | notification-service の注釈 | `secret.reloader.stakater.com/reload: ast-secrets` |
| ログの秘匿 | notification-service のログ | 送信先は `https://discord.com/***`。平文 URL 形（`discord(app)?\.com/api/webhooks/[0-9]{15,}/[A-Za-z0-9_-]{20,}`）の一致 0 件 |
| collector | `platform-infra` の otel-collector ConfigMap とログ | エクスポータは traces / metrics / logs とも `debug` のみ。現・直前コンテナのログで一致 0 件 |
| Loki / Tempo | `kubectl get pods -A`・`kubectl get pv` | **Pod も PVC も無い。** namespace は 10 日前に作り直し、PV の回収方針は `Delete` |
| GitHub | `gh search issues`・`gh search code`、#289 と PR #311 の本文・コメント | 一致 0 件 |

**#318 の 2026-09-23 コメントとの差**: 同コメントの `kubectl create secret generic ast-secrets ... | kubectl apply` は、
ESO 所有の現構成では次の同期で Vault の値へ戻され、**新旧 URL が入れ替わって見える**。手順は Vault 側（画面、または `vault kv patch`）へ改めた。
`vault kv put` は同じパスの他のキーを消すため `patch` を明記した。

## 変更

- `docs/operations/discord-webhook-rotation-runbook.md` を新設（配線の実測・新規作成 → Vault → 旧削除の順・確認 5 点・蓄積先の棚卸しと既定の扱い・失敗時の分岐・限界）。
- `docs/operations/operations.md` 関連文書に行を追加。
- **監査の指摘で是正（2026-09-26）**: `ClusterSecretStore/vault-backend` の実測（`kubectl get clustersecretstore vault-backend -o jsonpath='{.spec.provider.vault}'`
  → `server: http://vault.platform-infra:8200`・`path: secret`・`version: v2`）に合わせ、フォールバックを
  `read -rs U; printf '%s' "$U" | vault kv patch -mount=secret ai-stock-trading/app-secrets discord-webhook-url=-` とした（末尾改行を入れない）。
  Vault への届き方（Pod 内 exec／port-forward）とトークンの出所（dev モードの `VAULT_DEV_ROOT_TOKEN_ID` ← Secret `platform-infra/vault-dev-token` の `token`。値は表示しない）を書いた。
  `=-` は Vault CLI の「値を標準入力から読む」であり、`@-` への置換提案は採らない。同じ誤り（`-mount` 欠落）を持つ
  `docs/operations/vault-secrets-runbook.md` 手順 1 の 3 行も同じ PR で直した（規則 9: `vault kv (put|patch) ai-stock-trading` で全文書を走査し、該当はこの 2 文書だけ）。`docs/security/security.md` のローテーション行に日付つき追記。

## 蓄積分の判断（利用者へ提示する既定）

旧 URL が Discord 側で削除されれば、残存する旧 URL は投稿の能力を失う。したがって後始末は衛生であり、既定は「消さずに流れるのを待つ」。
Loki / Tempo は現行クラスタに存在せず、基盤の切替計画でも可観測性データは破棄の裁定が出ている。利用者の端末の履歴・AI セッション記録・
名前なしボリュームは AI が中身を見ていないため、利用者の判断に残す。

## 受け入れ基準

- [x] 再発行の手順を `docs/operations/` に残した（#318 項目 3）
- [x] 手順が現行の配線（ESO 所有）と一致する
- [x] 秘密の値を取得・表示していない（キー名と一致件数のみ）
- [ ] 利用者が手順 1〜3 を実行し、旧 URL が 404 を返す（#318 項目 1）
- [ ] 利用者が手順 4 の扱いを決める（#318 項目 2。既定＝対応不要）
