---
title: IADR-0100 経路B（ローカル SIMULATE）の機能有効化を values-local の恒常設定へ落とし込む（本番はバイト等価）
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-08, FR-10, FR-16, UC-01, UC-02, ADR-0003, ADR-0004, ADR-0006]
author: endazon (with Claude Code)
created: 2026-07-25
updated: 2026-07-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# IADR-0100: 経路B の機能有効化を values-local の恒常設定へ落とし込む（本番はバイト等価）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-25
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-02（取引サイクル・現在値の判断供給）、FR-08（知識ベース保存/取得）、FR-10（リスク統制・時価評価）、
  FR-16（報告書の評価損益）、UC-01（定時サイクル）、UC-02（価格変動サイクル）、ADR-0004（情報源・現在値ソース＝finnhub）、
  ADR-0003（AI 判断ガードレール＝②実 LLM の判断範囲）、ADR-0006（ホスティング・インフラ＝デプロイ構成/GitOps の文脈）
- 対象 Issue: [#238](https://github.com/endazon/ai-stock-trading/issues/238)
- 関連する実装仕様書: [20260725_route-b-values-local-standing-config](../specs/20260725_route-b-values-local-standing-config.md)
- 関連 IADR: [IADR-0052](IADR-0052_k8s-helm-chart-shared-infra.md)（経路B の chart・ローカル k8s デプロイ）、
  [IADR-0058](IADR-0058_helm-chart-ci-gate.md)（Helm chart の CI ゲート＝派生描画で「有効化した瞬間に壊れる」を捕まえる）、
  [IADR-0068](IADR-0068_live-quote-feed-finnhub-extraction.md)（① 実市況＝Finnhub・共有 `IMarketDataSource`・既定 no-op）、
  [IADR-0061](IADR-0061_llm-production-wiring.md)（② 実 LLM＝platform LlmGateway `POST /complete`）、
  [IADR-0093](IADR-0093_kb-writer-cross-realm-s2s.md)（③ 実 KB の s2s は MSP レルム専用クライアント）、
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)（Discord Bot・多層認証・空既定＝全拒否/no-op）、
  [IADR-0099](IADR-0099_current-price-context-for-decision.md)（価格文脈＝`ICurrentPriceProvider`）、
  [IADR-0094](IADR-0094_local-infra-observability-gitops.md)（`ast-secrets` の ESO/Vault 同期・opt-in）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（実弾 triple-latch＝本決定は触れない）

## 背景・課題

経路B（ローカル k8s / SIMULATE）で取引サイクルを end-to-end 検証するため、①時価②実 LLM③実 KB＋Discord＋価格文脈を
有効化している。これらは **リポジトリ外の臨時 overlay**（`microservices-platform/overlay-cycle.yaml`）を毎回
`helm upgrade -f overlay-cycle.yaml` で手当てして有効化しており、標準手順 `scripts/k8s-local-deploy.sh`（`-f` 無し）
だけでは有効化されない。overlay には以下の弱点がある。

- Helm がリスト（`extraEnv`）を**置換**するため、各サービスの全要素の写しを人手で保守し続ける必要がある。
- AST リポジトリ外（MSP 側ホスト）にあるため、AST の CI（`helm.yml`）・レビュー・トレーサビリティの外にある。
- overlay の trade-decision には `MarketData` が無く、[#236](https://github.com/endazon/ai-stock-trading/issues/236) /
  IADR-0099 の**価格文脈が有効化されていない**（overlay が #236 より前に書かれたため）。

一方、**本番（ArgoCD）は `deploy/argocd/application.yaml` が `path: deploy/helm/ai-stock-trading` を `valueFiles` 無しで
同期する＝`values.yaml` のみ**で描画する。したがって本番へ実 LLM/実市況/Discord を漏らさない担保は「有効化を
`values.yaml` 以外のファイルに閉じ込め、本番描画がバイト等価であること」に帰着する。

## 決定

1. **local/SIMULATE プロファイルを chart 内の `values-local.yaml` として committed 化する。**
   `deploy/helm/ai-stock-trading/values-local.yaml` に6 サービス（risk-management / market-monitor / report /
   trade-decision / information-collection / notification）の `extraEnv` を**全要素**（Helm のリスト置換に合わせ、既定
   `values.yaml` の全要素＋有効化分）で置く。あわせて **trade-decision に価格文脈**
   （`MarketData:Provider=finnhub`＋鍵＋`MaxQuoteStalenessSeconds=300`）を追加する（overlay に無かった #236 分。#236 は
   別 enable フラグを持たず、`MarketData:Provider=finnhub`＋API キーで実結線され `ICurrentPriceProvider.IsEnabled` が真になる）。

2. **標準手順を values-local に結線する。** `scripts/k8s-local-deploy.sh` の `helm upgrade` に
   `-f deploy/helm/ai-stock-trading/values-local.yaml` を加え、標準手順だけで ①②③＋Discord＋価格文脈が有効化される。

3. **本番はバイト等価を厳守する。** `values.yaml`・全 templates・`Chart.yaml`・`deploy/argocd/*` を一切変更しない。
   本番描画（`helm template ast <chart>`＝valueFiles 無し）は develop とバイト等価。有効化は `-f values-local.yaml` を
   明示したとき（＝ローカル標準手順）に限る。

4. **secret は平文で埋め込まない。** API 鍵・トークン（finnhub / kb-writer / discord-*）は `secretKeyRef`
   （`ast-secrets`・`optional: true`）で参照し、実値は `k8s-local-deploy.sh` の `ast-secrets` 作成（env 上書き）／
   ESO(Vault) 同期（IADR-0094）に委ねる。LLM プロバイダ鍵は AST では扱わない（MSP LlmGateway 側が保持・ADR-0010）。

5. **Discord の環境固有値は空既定の設定点にする。** `GuildId` / `ChannelId` / `AllowedUserIds` / `UserMapping` は
   overlay の `<GUILD_ID>` 等プレースホルダを持ち込まず**空既定**。未設定時は IADR-0062 の安全既定（空 GuildId/ChannelId/
   AllowedUserIds は「全許可」ではなく**全拒否**）で no-op に倒れる。配線（`Provider=discord-webhook` /
   `Bot:Enabled=true`）は恒常化するが、`discord-webhook-url` / `discord-bot-token` secret が空なら実送信しない。

6. **実弾 OFF は不変。** order-execution を values-local に含めない＝chart template が `moomoo.enabled=false`（既定）から
   `Broker__Provider=paper` を決定＝paper 固定・triple-latch（IADR-0060）に触れない。G4 の確定日報はランタイム操作の
   ため設定化せず README に手順のみ記す。

7. **両検証を CI（`helm.yml`）に加える（単一情報源）。** (a) 本番（既定描画）の fail-safe 不変
   （`MarketData__EnableMarkToMarket=false`・`LlmGateway__BaseUrl` 空・`Notifications__Provider` 空・`Broker=paper`）と、
   (b) `-f values-local.yaml` 描画で ①②③＋Discord＋価格文脈が ON かつ **Broker=paper・opend/ExternalSecret 不在**で
   あることを描画で検査する（IADR-0058 の「有効化した瞬間に壊れる」を捕まえる方針の踏襲）。

## 却下した代替案

- **`values.yaml` の既定を有効化に反転**: 本番（ArgoCD の `values.yaml` のみ描画）が実 LLM/実市況/Discord を掴む。
  fail-safe 既定（実弾 OFF と同性質）の反転であり不可。
- **overlay をそのまま MSP 側に残す**: リポジトリ外・CI 外・#236 価格文脈が欠落。恒常設定の要件を満たさない。
- **local 値を `--set` 群で k8s-local-deploy.sh に列挙**: 数十キー＋リスト要素で保守困難、Helm のリスト置換で
  `extraEnv` を部分 `--set` すると要素欠落を起こしやすい。宣言的な values ファイルの方が安全でレビュー可能。

## 影響

- 追加: `deploy/helm/ai-stock-trading/values-local.yaml`。変更: `scripts/k8s-local-deploy.sh`・`.github/workflows/helm.yml`・
  chart `README.md`。不変: `values.yaml`・templates・`Chart.yaml`・C# コード・`deploy/argocd/*`（本番バイト等価）。
- MSP 側 `overlay-cycle.yaml` は不要になる（物理削除は別リポジトリ・任意）。
- 実弾解禁・Hetzner 実 k3s の GitOps 実同期（Tier 3）は本決定の対象外。
