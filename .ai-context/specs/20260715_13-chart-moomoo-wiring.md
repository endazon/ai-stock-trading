---
title: AST chart に moomoo（OpenD）発注を配線する — #13 in-cluster 有効化
type: spec
status: draft
related_ids:
  - FR-05
  - ADR-0002
  - IADR-0016
  - IADR-0052
  - IADR-0053
author: claude
created: 2026-07-15
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
related_specs:
  - "20260715_13_moomoo-broker-adapter.md（#13 アダプタ本体・別ブランチ feat/13）"
  - "20260714_124_opend-docker.md（OpenD 常駐＋RSA・別ブランチ feat/124）"
  - "20260713_122_k8s-helm-chart.md（chart 本体・本ブランチ feat/122）"
---

# 仕様書: AST chart に moomoo（OpenD）発注を配線する（#13）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-05**（発注執行）
- 関連 ADR: **ADR-0002**（moomoo OpenAPI）／**IADR-0016**（実弾防止・安全既定 paper）／
  **IADR-0052**（chart）／**IADR-0053**（OpenD 常駐＋RSA）

## 目的

in-cluster の `order-execution-service` が、常駐 OpenD（`opend:11111`・#124）へ **SIMULATE 限定**で発注できるよう、
chart に moomoo 配線を追加する。#13 のクライアント実装・#124 の OpenD 側 RSA 配備を chart から有効化する接続点。

## 対象範囲

**対象（本変更・feat/122）**
- `values.yaml`: gated `moomoo` セクション（既定 `enabled: false`＝fail-safe paper）。OpenD host/port・RSA Secret 名/キー/マウント先。
- `templates/deployment.yaml`: `order-execution` に、`moomoo.enabled` 時のみ
  `Broker__Provider=moomoo` ＋ `Broker__Moomoo__OpenD__{Host,Port,RsaPrivateKeyPath}` env と
  `moomoo-rsa` Secret の read-only マウントを注入。既定は `Broker__Provider=paper`。
- chart `README.md`: 有効化手順・前提（OpenD 常駐・Secret）を追記。

**対象外**
- moomoo アダプタ実装（#13・feat/13）・OpenD イメージ/RSA 配備（#124・feat/124）。
- 実弾（`TrdEnv_Real`）。SIMULATE 固定（IADR-0016）。

## 設計

- **fail-safe 既定**: `moomoo.enabled=false` のとき `order-execution` は `Broker__Provider=paper`（実発注しない）。
  chart の他の外部連携（LLM/Finnhub/Discord）と同じ opt-in ゲート方針。
- **有効化前提**: (1) OpenD 常駐（`deploy/opend/k8s/opend.yaml`・#124）、(2) Secret `moomoo-rsa`（RSA 秘密鍵・
  OpenD と同一鍵）。両者が無い状態で `enabled=true` にすると、クライアントは接続不可で `Rejected` に倒れる（fail-safe）。
- **暗号化**: cross-network（worker→opend）trade は RSA 必須（#13 で確定）。同一鍵を Secret でマウントし
  `Broker__Moomoo__OpenD__RsaPrivateKeyPath` で指す。
- **値の対応**: `Broker__Moomoo__OpenD__Host=opend`・`Port=11111`（既定）。RSA パスは `rsaMountPath/rsaSecretKey`。

## 受け入れ基準

- [ ] 既定（`moomoo.enabled=false`）で `order-execution` に `Broker__Provider=paper` のみが載る（RSA マウントなし）
- [ ] `moomoo.enabled=true` で `Broker__Provider=moomoo`＋OpenD env＋`moomoo-rsa` の read-only マウントが載る
- [ ] `helm template` が両値でエラーなくレンダリングされる（他サービスに影響しない）
- [ ] （可能なら）in-cluster で `moomoo.enabled=true` デプロイし、worker が OpenD 接続し SIMULATE 発注できる

## テスト方針

- `helm template`（`--set moomoo.enabled=true/false`）でレンダリング差分を検証（ユニット対象のコード無し・chart 値）。
- in-cluster live 検証は OpenD 常駐＋Secret 前提（手動）。

## 計画書との差異

- なし（ADR-0002 の in-cluster 有効化。実弾は IADR-0016 の後続で別途）。
