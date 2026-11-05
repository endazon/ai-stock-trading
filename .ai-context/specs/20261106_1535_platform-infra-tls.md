---
title: 基盤（Postgres・RabbitMQ・LLM ゲートウェイ）への接続を、基盤の CA で検証する TLS へ切り替えられるようにする（段の切り替えは values・CA が無ければ描画で止める）
type: spec
status: accepted
related_ids: [NFR, NFR-18, ADR-0006, IADR-0615, IADR-0568, IADR-0129, IADR-0323, IADR-0332]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# 仕様書: 基盤への接続の TLS（#1535）

## 起点

- #1535。基盤（MSP）は段 1 で Postgres・RabbitMQ に TLS を足し、平文と併存させている（MSP の IADR-0591）。
  - 基盤のサービスは切り替え済みである。
  - AST の chart は、平文の `amqp://…:5672` と、`SSL Mode` の無い Postgres の接続文字列を描く。
  - そのため、基盤は平文の口を閉じられない（MSP の IADR-0591 の段 4b-2・4b-3）。
- 基盤の起動器は、CA を ConfigMap `platform-infra-ca`（キー `ca.crt`）として AST の名前空間へ写している（MSP の IADR-0591 決定 6）。
- LLM ゲートウェイは、クラウドの構成で https（アプリの TLS・同じ CA が発行）になる（MSP#2409）。経路 B では http のままである。

## 決定（IADR-0615）

1. **値で切り替える**（段を踏む）。既定（`values.yaml`）は従来どおり平文で、`values-local.yaml` が有効にする。
   - `global.db.tls.enabled`
   - `global.rabbitmq.tls.enabled` / `port`（5671）
   - `global.infraCa.configMap` / `caKey`
2. **形は基盤の chart と同じにする**:
   - DB: `SSL Mode=VerifyFull;Root Certificate=/etc/platform-infra-ca/ca.crt`
   - ブローカ: `amqps://<利用者>:$(RABBITMQ_PASSWORD)@rabbitmq:5671`
   - 信頼ストア: `SSL_CERT_DIR=/etc/ssl/certs:/etc/platform-infra-ca`（既定の `/etc/ssl/certs` を先に残すので、外部の公開 CA の検証は変わらない）
   - CA の ConfigMap は、`/etc/platform-infra-ca` へ読み取り専用・非 optional でマウントする。
3. **CA の設定が無いときは描画で止める**（TLS を有効にしたのに `infraCa` が空・ポートが空）。ConfigMap がクラスタに無いときは、Pod が起動しない（非 optional）。どちらも平文へは戻らない。
4. **ブローカの名前の照合**: `UseAiStockTradingRabbitMq` は amqps のとき、RabbitMQ.Client が既定で入れる「名前の不一致を受け入れる」を空に戻し、`ServerName` を URI の host にする（基盤の `UsePlatformRabbitMq` と同じ）。
5. **LLM ゲートウェイの HttpClient は専用の検証で基盤の CA を信頼する**（`PlatformInfraCaTrust`。取引判断の `llm`・報告書の `report-llm` の主ハンドラ）。
   - 判定は次のとおり。
     - 既定の検証で誤りが無ければ受ける（システムの信頼ストア。`SSL_CERT_DIR` の CA を含む）。
     - 誤りが連鎖だけのときは、`PlatformInfraCa:CertificatePath` の CA だけを根に連鎖を組み直し、通れば受ける。
     - 名前の不一致・証明書なしは常に拒む。
     - CA が無い・読めないときは拒む。
   - chart は CA をマウントしたサービスに `PlatformInfraCa__CertificatePath` を描く。
   - 🔴 平文や検証なしへは倒れない。https の URL で検証に失敗すれば、呼び出しは失敗として扱われる（取引判断は Hold・報告書はプレースホルダ。従来の失敗の経路）。
   - ほかの HttpClient（Finnhub・SEC・FRED・BOJ ほか）は既定のハンドラのまま（システムの信頼ストア）。
6. LLM ゲートウェイの URL を https にするのはクラウドの構成（values-cloud。#1525）の側で行う。本 PR は経路 B の URL（http）を変えない。

## 母集合（規則 9）

- **DB の接続文字列**: `deploy/helm/ai-stock-trading/templates/deployment.yaml` の `ConnectionStrings__DefaultConnection`（`$svc.db` を持つ 7 サービス）。
- **ブローカの接続**: 同ファイルの `RabbitMq__ConnectionString`（全 11 サービス）。コードは `UseAiStockTradingRabbitMq`（全サービスがこれを使う。`git grep -n "UseRabbitMq(" -- backend ':!*Tests*'` で 1 か所）。
- **LLM ゲートウェイの HttpClient**: `git grep -n "AddHttpClient(\"llm\"\|AddHttpClient(\"report-llm\"" -- backend` で取引判断・報告書の 2 か所。
- **除外**:
  - gRPC の経路（`LlmGateway:Grpc`。h2c）。クラウドでは基盤が受け口を外すので本 PR では変えない。
  - OpenD の Pod（基盤へ繋がない）。
  - 取引サイクルの CronJob（クラスタ内の情報収集を叩くだけ）。
  - 基盤の側の段 4b-3（平文を閉じる）は基盤の作業。

## 受け入れ基準

- [x] values-local の描画で、DB を持つ全サービスの接続文字列が `SSL Mode=VerifyFull;Root Certificate=/etc/platform-infra-ca/ca.crt` を持つ（helm.yml）。
- [x] 同じ描画で、全サービスのブローカの宛先が `amqps://…:5671` で、`SSL_CERT_DIR` が `/etc/ssl/certs` を先に持つ（helm.yml）。
- [x] TLS を有効にして CA の設定が無いと描画が止まる。ConfigMap は非 optional でマウントされる（helm.yml）。
- [x] 既定（`values.yaml`）の描画は平文のまま変わらない（helm.yml）。
- [x] ブローカの amqps は名前の不一致を受け入れない（xUnit）。
- [x] LLM ゲートウェイの https が、基盤の CA で検証を通る。名前の違う証明書・CA が無い・別の CA では失敗し、平文や検証なしへ倒れない（xUnit。ループバックの TLS の受け手）。
- [x] 外部 API の HttpClient はシステムの信頼ストアのまま（専用の検証を使わない。DI で確かめる）。既定の検証で誤りが無い証明書は専用の検証でも受ける（xUnit）。
- [x] 経路 B の切り替えの手順（順番と戻し方）を手順書に書く。
- [x] `/verify` が通る。
