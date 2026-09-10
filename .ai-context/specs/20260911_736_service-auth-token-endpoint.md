---
title: s2s 発信者の ServiceAuth token エンドポイントを global.authAuthority から導出して注入する
type: spec
status: done
related_ids: [NFR-05, IADR-0324, IADR-0051]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 仕様書: s2s token エンドポイントの authAuthority 追随（#736）

## 起点

- issue #736。2026-09-10 の稼働クラスタ（MSP 連結・IADR-0324 適用後）で SIMULATE 周回を `run-once` で起動すると、
  trade-decision が report（daily-policy）と market-monitor（watchlist）から **401** を受け、fail-closed で「取引しない
  安全側」に倒れる。周回は完走するが発注へ到達しない。

## 実測（2026-09-10 15:05Z）

```
[15:05:31 WRN] 監視銘柄（watchlist）の照会に失敗（401）。既定 watchlist（構成）へフォールバックします。
[15:05:31 WRN] 確定済み日報方針の照会に失敗（401）。取引しない安全側に倒します。
[14:52:54 WRN] サービストークンの取得で例外（http://keycloak:8080/realms/ai-stock-trading/protocol/openid-connect/token・client_id=ai-stock-trading-svc）
```

| 稼働 Deployment | `Auth__Authority` | `ServiceAuth__ClientId` |
| --- | --- | --- |
| report / market-monitor / information-collection / cost-control | `realms/platform` | あり |
| **trade-decision** | **無し**（`auth: true` でない） | あり |

## 原因

`ServiceAuthExtensions.ReadOptions` は `ServiceAuth:TokenEndpoint` 未指定なら `Auth:Authority` から導出する（IADR-0051）。
chart は `auth: true` のサービスにだけ `Auth__Authority` を注入するため、**inbound 認証を掛けない s2s 発信者
（trade-decision）は導出元が無く、コード既定 `realms/ai-stock-trading` に倒れる**。受け手は `realms/platform` で検証
するので issuer 不一致 → 401。IADR-0324 決定 2 の「1 値で揃って移る」は、この経路では成立していなかった。

CronJob で起きた #456（token エンドポイントを `global.authAuthority` から導出）と**同型の 2 回目**なので、検査器
（helm.yml の描画検査）を足す。

## 設計

| 対象 | 変更 |
| --- | --- |
| `templates/deployment.yaml` | `extraEnv` に `ServiceAuth__ClientId` を持つサービスへ `ServiceAuth__TokenEndpoint` を `global.authAuthority` から導出して注入（cronjob.yaml #456・Notifications OwnerAuth #226 と同じ導出） |
| `.github/workflows/helm.yml` | 描画検査を追加: ServiceAuth を持つ Deployment 全部に TokenEndpoint が在り・持たないものには無い・trade-decision（values-local）が MSP レルム・`--set` に追随 |
| IADR-0324 | 決定 2 の前提の破れを日付つき追記 |

values にリテラルを置かない（`--set` で authAuthority を動かしたとき追随しないため）。

## 走査した母集合（規則 2・9）

`ServiceAuth__TokenEndpoint|ServiceAuth:TokenEndpoint|TokenEndpoint 未指定` で追跡下の全ファイル（`.claude/`
`.ai-context/specs/` `CHANGELOG` 除く）を走査: `ServiceAuthExtensions.cs`（導出の実装・据え置き）、
`ServiceTokenRegistrationTests.cs`（明示指定の試験・据え置き）、IADR-0051（導出の決定・据え置き）、IADR-0324
（決定 2 の記述 → 追記）、`values-local.yaml` 冒頭コメント（「ServiceAuth の token エンドポイント」を導出すると
書いており、本変更で正しくなる・据え置き）。

## 受け入れ基準

- [x] `helm template`（既定 / values-local）で ServiceAuth を持つ 5 Deployment に `ServiceAuth__TokenEndpoint` が在り、
      持たない Deployment には無い（陰性対照）
- [x] values-local の trade-decision が `realms/platform/protocol/openid-connect/token`
- [x] `--set global.authAuthority=…/realms/x/` で追随し末尾スラッシュが二重にならない
- [x] helm.yml の検査ステップは template 変更を外すと赤（変異検証: 既定描画で 5 Deployment を検出）
- [x] 稼働: `kubectl set env` で trade-decision に暫定注入し、周回再実行で 401 が消える（結果は issue #736 に記録）
