---
title: 作業仕様書 — 取引ユニットの外への HTTPS の中継の受け口を、送り元（サービス）ごとに描く（基盤の中継の送り元ごとの受け口に合わせる）
type: spec
status: in-progress
related_ids: [NFR, IADR-0647, IADR-0644]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: [ADR-0006]
---

# 作業仕様書 — 中継の受け口を送り元ごとに（NFR）

> 着手 2026-11-08（develop `b78b0b196`。#1607 のマージの後）。判断の記録は IADR-0647（IADR-0644 は凍結のため新規。0645・0646 は並行の組が使うので 0647）。
> 基盤の側は MSP#2517（基盤の IADR-0709。本書の着手時点でマージ前）。

## 起点

- #1607（IADR-0644）は中継の口を 1 つ（`networkPolicy.egressProxy.url` / `port` の 3128）で描いた。
- 基盤の側（MSP#2517）は中継の受け口を送り元ごとに分け、受けた口で送り元を見分けて送り元ごとの許可表を使う設計になった（オーケストレーターの連絡 2026-11-08）。
  取引ユニットの各 Pod は 3128 ではなく自分の受け口を使う。割り当て:

| app | 受け口 |
| --- | --- |
| information-collection-service | 3141 |
| market-monitor-service | 3142 |
| notification-service | 3143 |
| report-service | 3144 |
| risk-management-service | 3145 |
| trade-decision-service | 3146 |
| opend | 3147（取引ユニットは使わない。OpenD は独自の手順で中継を通らない） |

- Discord の再接続先（`gateway-<地域>.discord.gg`）は、基盤の表が `*.discord.gg` で受ける（#1607 の残るリスクの 1 つが基盤の側で閉じる）。

## 方式

- 受け口は `networkPolicy.services.<名前>.egressProxyPort`（宣言の側）。`values-cloud.yaml` が上の割り当てを持つ（`values.yaml` は宣言が空のまま）。
- `networkPolicy.egressProxy` は `url` / `port` をやめて `host`（`egress-proxy.platform-infra.svc.cluster.local`）を持つ。
  - `HTTPS_PROXY` = `http://<host>:<そのサービスの受け口>`（`ast.egressProxy.url`）。
  - 出の方針の中継の規則は、そのサービスの受け口の TCP だけ（`ast.egressProxy.port`）。
- 描画で止める（`ast.egressProxy.port` / `ast.egressProxy.validate`）:
  - `Report`・`Enforce` で `externalHttps` のサービスに受け口が無い（null も無いと読む）。
  - 受け口が 1024〜65535 の整数でない（使うときに検める）。
  - 受け口が 2 つのサービスで重なる・`externalHttps` でないサービスに受け口がある（段に依らず。`networkPolicy.enabled=true` のとき）。
  - 旧の `networkPolicy.egressProxy.url` / `port` を書いた（黙って無視しない）。
- `Off`（既定）の描画は変わらない（既定・`values-local`・`values-cloud`＋fixture の描画が develop と 1 バイトも同じであることを確かめた）。

## 母集合（規則 9・10）

- `egressProxy.url` / `egressProxy.port` / `3128` を読む所: `grep -rn` → `templates/deployment.yaml`（HTTPS_PROXY）・`templates/networkpolicy-services.yaml`（出の口）・
  `values.yaml`・`scripts/helm-network-policy.test.js`（`PROXY_URL` と変異 1 件）・手順書（段の表・前提・Report の確かめ）・IADR-0644 と #1607 の作業仕様書（凍結。直さない）。
- 既存の試験の入力のうち、`externalHttps=false` にする変異 2 件は、新しい止め（externalHttps でないサービスの受け口）に当たるので受け口も外す形へ直した（判定は変えない）。

## 受け入れ基準

| # | 基準 | 試験（`scripts/helm-network-policy.test.js`） |
| --- | --- | --- |
| AC1 | Report・Enforce で、6 サービスの `HTTPS_PROXY` がそれぞれ自分の受け口で、出の方針の中継の口もその受け口の TCP だけ | 7. Report / 7. Enforce（`egressProxyViolations`） |
| AC2 | values-cloud の割り当てが基盤の割り当て（上の表）と一致する | 7. 割り当ての一致 |
| AC3 | Report・Enforce で受け口が無いと止まる。陰性対照: Off なら無くても通り、中継の口も `HTTPS_PROXY` も描かない | 7. 受け口の無い（2 件）・陰性対照 |
| AC4 | 重なり・範囲の外・externalHttps でないサービスの受け口・旧の url / port で止まる | 7.（4 件） |
| AC5 | 変異: 受け口の入れ替え・別の空きへの変更・宛先の名前の違いで赤 | 変異（3 件） |
| AC6 | 既定・values-local・values-cloud（Off）の描画が develop と同じ | 手元で diff（下） |

## 陰性対照（手元で実施）

- 出の方針の口を 3128 に固定 → 4 件赤。`HTTPS_PROXY` の口を 3128 に固定 → 4 件赤。受け口の欠けの止めを外す → 2 件赤。
  重なりの止めを外す → 1 件赤。externalHttps でない受け口の止めを外す → 1 件赤。旧キーの止めを外す → 1 件赤。範囲の止めを外す → 1 件赤。

## Enforce へ上げる手順

- 基盤の側（MSP#2517）のマージとクラスタへの適用が前提。手順は通信の制限の Runbook §外への HTTPS を基盤の中継へ寄せる（前提を送り元ごとの受け口に書き直した）。
