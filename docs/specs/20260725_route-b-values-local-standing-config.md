---
title: 経路B（ローカル SIMULATE）の ①時価②実LLM③実KB＋Discord＋価格文脈を values-local の恒常設定へ落とし込む
type: spec
status: review
related_ids: [FR-02, FR-08, FR-10, FR-16, UC-01, UC-02, ADR-0003, ADR-0004, ADR-0006]
author: endazon (with Claude Code)
created: 2026-07-25
updated: 2026-07-25
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# 仕様書: 経路B の機能有効化を values-local の恒常設定へ落とし込む

> Issue [#238](https://github.com/endazon/ai-stock-trading/issues/238)。**デプロイ構成（values プロファイル）の恒常化**であって
> 機能追加・実弾化ではない。実弾 triple-latch（`Broker__Provider=paper` / `Broker:Moomoo:TrdEnv=simulate` /
> 起動時 real 拒否・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）には一切触れない。
> 目的は **経路B（ローカル k8s / SIMULATE）で ①②③＋Discord＋価格文脈を、臨時 overlay 無しに標準手順
> `scripts/k8s-local-deploy.sh` だけで有効化される「恒常設定」にすること**。本番 values はバイト等価を厳守する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-02（取引サイクル・現在値の判断供給）、FR-08（知識ベース保存/取得）、FR-10（リスク統制・時価評価）、FR-16（報告書の数値定義・評価損益）
- ユースケース（UC）: UC-01（定時サイクル）、UC-02（価格変動サイクル）
- ADR: ADR-0004（情報源・現在値ソース＝finnhub＝①/価格文脈）、ADR-0003（AI 判断ガードレール＝②実 LLM の判断範囲）、
  ADR-0006（ホスティング・インフラ＝デプロイ構成/GitOps の文脈）
- 関連 IADR:
  - [IADR-0068](../adr/IADR-0068_live-quote-feed-finnhub-extraction.md)（実市況＝Finnhub・共有 `IMarketDataSource`・既定 no-op）
  - [IADR-0061](../adr/IADR-0061_llm-production-wiring.md)（実 LLM は platform LlmGateway `POST /complete`）
  - [IADR-0093](../adr/IADR-0093_kb-writer-cross-realm-s2s.md)（KB 書き込み/検索の s2s は MSP レルム専用クライアント）
  - [IADR-0062](../adr/IADR-0062_discord-bot-gateway-and-authorization.md)（双方向 Discord Bot・多層認証・空既定＝全拒否/no-op）
  - [IADR-0099](../adr/IADR-0099_current-price-context-for-decision.md)（取引判断への現在値供給・価格文脈）
  - [IADR-0058](../adr/IADR-0058_helm-chart-ci-gate.md)（Helm chart の CI ゲート＝派生描画の検査）
  - [IADR-0052](../adr/IADR-0052_k8s-helm-chart-shared-infra.md)（経路B のローカル k8s デプロイ）
  - 本作業で新規 [IADR-0100](../adr/IADR-0100_route-b-values-local-standing-config.md)
- 対象 Issue: [#238](https://github.com/endazon/ai-stock-trading/issues/238)

## 目的・背景

経路B（ローカル k8s / SIMULATE）で取引サイクルを end-to-end 検証するため、以下を有効化している。

- ① 時価評価（mark-to-market）: risk-management `MarketData:EnableMarkToMarket=true` / `Provider=finnhub`、
  market-monitor・report の `MarketData:Provider=finnhub`。
- ② 実 LLM: trade-decision・report の `LlmGateway:BaseUrl`（MSP LlmGateway）。
- ③ 実 KB: information-collection・report の `KnowledgeBase:Documents:BaseUrl`（MSP DocumentService）＋
  `KnowledgeBase:Auth`（MSP レルム・[IADR-0093](../adr/IADR-0093_kb-writer-cross-realm-s2s.md)）。
- Discord 通知: notification の `Notifications:Provider=discord-webhook` / `Bot:Enabled=true`。
- サイクル配線: information-collection の収集 provider（finnhub＋AAPL）、trade-decision の `Reports:BaseUrl` /
  `RiskManagement:BaseUrl` / `TradeCycle:Watchlist`。
- 価格文脈（[#236](https://github.com/endazon/ai-stock-trading/issues/236) / IADR-0099）: trade-decision の
  `ICurrentPriceProvider`。**これは `MarketData:Provider=finnhub`＋API キーで実結線され `IsEnabled` が真になり**、
  鮮度 `MarketData:MaxQuoteStalenessSeconds`（既定 300s）で古い現在値を no-op へ倒す。

**問題**: これらは臨時 overlay（`microservices-platform/overlay-cycle.yaml`）を毎回 `helm upgrade -f overlay-cycle.yaml`
で当てて有効化しており、標準手順 `scripts/k8s-local-deploy.sh`（`-f` 無し）だけでは有効化されない。overlay は
**Helm がリストを置換する性質**上、各サービスの `extraEnv` 全要素の写しを保守し続ける必要があり、リポジトリ外
（MSP 側ホスト）にあるため AST 側の CI/レビューの外にある。さらに **overlay の trade-decision には `MarketData` が無く、
#236 の価格文脈が有効化されていない**（overlay が #236 より前に書かれたため）。

## 方針

### 落とし込み先（local 限定）

1. **新規 `deploy/helm/ai-stock-trading/values-local.yaml`** を作成し、6 サービス（risk-management / market-monitor /
   report / trade-decision / information-collection / notification）の `extraEnv` を**全要素**committed 化する
   （Helm のリスト置換に合わせ、既定 `values.yaml` の全要素＋有効化分を写す）。あわせて **trade-decision に価格文脈
   （`MarketData:Provider=finnhub`＋鍵＋`MaxQuoteStalenessSeconds=300`）を追加**する（overlay に無かった #236 分）。
2. **`scripts/k8s-local-deploy.sh`** の `helm upgrade` に `-f deploy/helm/ai-stock-trading/values-local.yaml` を追加し、
   標準手順だけで有効化されるようにする。
3. 既存 `overlay-cycle.yaml` は不要になる旨を chart README / 本仕様に記す（MSP 側ファイルの削除は本 PR の対象外）。

### secret の扱い

- API 鍵・トークン類（finnhub / kb-writer / discord-*）は**平文で埋め込まない**。すべて `secretKeyRef`
  （`ast-secrets`・`optional: true`）で参照し、実値は `scripts/k8s-local-deploy.sh` の `ast-secrets` 作成
  （env 上書き）／ ESO(Vault) 同期（[IADR-0094](../adr/IADR-0094_local-infra-observability-gitops.md)）に委ねる。
- Discord の**環境固有値**（`GuildId` / `ChannelId` / `AllowedUserIds` / `UserMapping`）は**空既定**とし、ユーザーが
  値を与える設定点として置く。未設定時は [IADR-0062](../adr/IADR-0062_discord-bot-gateway-and-authorization.md) の安全既定
  （空 GuildId/ChannelId/AllowedUserIds は「全許可」ではなく**全拒否**に倒れ、Bot は接続しても操作を受け付けない）で
  no-op になる。overlay の `<GUILD_ID>` 等プレースホルダは持ち込まない（誤って未置換のまま適用される事故を避ける）。
- `Notifications:Provider=discord-webhook` / `Bot:Enabled=true` は配線の恒常化として local プロファイルに置くが、
  実送信は `discord-webhook-url` / `discord-bot-token` secret が空なら送らない（安全側）。

### 実弾不変

- ブローカーは**触らない**。chart template が `moomoo.enabled=false`（既定）から `Broker__Provider=paper` を決定するため、
  order-execution を values-local に含めない＝paper 固定・実弾 OFF 不変。
- **G4 の確定日報はランタイム操作**（Discord `/report confirm` 等）のため設定化しない。README に手順のみ記す。

## 受け入れ基準（テストへ写像）

`docs/tests/` の独立テスト仕様は本 FR 群の必須範囲外（[#211](https://github.com/endazon/ai-stock-trading/issues/211) の網羅裁定）。
検証は `helm.yml` の描画回帰（CI・単一情報源）で担保する。

1. **本番バイト等価**: `values.yaml`・templates・`Chart.yaml` を変更しない。`helm template ast <chart>`（既定描画＝
   ArgoCD prod 描画・valueFiles 無し）が develop と**バイト等価**であること。CI で既定描画の fail-safe 不変
   （`MarketData__EnableMarkToMarket` が `false`・`LlmGateway__BaseUrl` が空・`Notifications__Provider` が空・
   `Broker__Provider=paper`）を検査する。
2. **local 有効化**: `helm template ast <chart> -f values-local.yaml` で
   - ① `MarketData__EnableMarkToMarket` が `true`（risk-management）、`MarketData__Provider` が `finnhub`
   - ② `LlmGateway__BaseUrl` が MSP LlmGateway（trade-decision / report）
   - ③ `KnowledgeBase__Documents__BaseUrl` が MSP DocumentService（information-collection / report）
   - Discord `Notifications__Provider=discord-webhook` / `Bot__Enabled=true`（notification）
   - 価格文脈 `MarketData__Provider=finnhub`（trade-decision）
   が描画されること。
3. **local でも実弾/危険既定 OFF**: local 描画でも `Broker__Provider=paper` であり、`kind: ExternalSecret` /
   `name: opend` が現れない（overlay 相当の有効化で実弾経路・秘匿ストア誤有効化が起きない）こと。
4. **secret 非平文**: values-local に平文の API 鍵・トークンが無い（`secretKeyRef` のみ）こと（gitleaks green・目視全文確認）。

## 影響範囲

- 追加: `deploy/helm/ai-stock-trading/values-local.yaml`（新規）、`docs/adr/IADR-0100_*.md`、本仕様。
- 変更: `scripts/k8s-local-deploy.sh`（`-f values-local.yaml` 追加）、`.github/workflows/helm.yml`（local 描画回帰の追加）、
  `deploy/helm/ai-stock-trading/README.md`（values-local の説明・overlay 廃止・G4 手順）。
- 不変: `values.yaml`・全 templates・`Chart.yaml`・C# コード・`deploy/argocd/*`。

## 未対応（スコープ外）

- MSP 側 `overlay-cycle.yaml` の物理削除（別リポジトリ・任意）。
- 実弾解禁（triple-latch は不変）。
- Hetzner 実 k3s の GitOps 実同期（Tier 3・[IADR-0094](../adr/IADR-0094_local-infra-observability-gitops.md)）。
