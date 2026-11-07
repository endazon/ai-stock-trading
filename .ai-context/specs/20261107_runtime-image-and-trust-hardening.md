---
title: 稼働の前に実行環境とイメージを硬くする（Worker の段 3・LLM 費用の停止を読めないときの段・基盤の CA を基盤の宛先だけで信頼・OpenD のイメージの固定と照合・稼働のイメージの curl と依存の監査）
type: spec
status: accepted
related_ids: [NFR, NFR-05, NFR-13, NFR-18, ADR-0006, ADR-0074, IADR-0639, IADR-0609, IADR-0621, IADR-0629, IADR-0582, IADR-0031, IADR-0049, IADR-0053]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-05・NFR-13・NFR-18)
  - planning:projects/ai-stock-trading/07_adr/ADR-0074_llm-cost-cap-halt-covers-price-movement-decisions.md (決定 1・2)
---

# 仕様書: 稼働の前に実行環境とイメージを硬くする（#1560 の稼働前の確認）

## 起点

- クラウドへの移行（#1560）の稼働前の確認の項目のうち、実行環境とイメージの 5 つ。所有者の裁定（2026-11-07）で、稼働の前にすべて直す。
- 決定は IADR-0639。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| A1 | values-cloud の Worker は段 3（runAsNonRoot・uid 1654）と readOnlyRootFilesystem・`/tmp` の emptyDir で描かれる。PSA の enforce は baseline | `scripts/helm-pod-security.test.js`「values-cloud: Worker は段 3・…」「宣言どおりの段の形」 |
| A2 | 段を上書きする描画（段 1〜3）は readOnlyRootFilesystem を明示し、プロファイルの宣言で段 1 の描画が止まらない | 同上（`stageArgs`） |
| B1 | 費用の停止の照会は読めた値と「読めない」を分けて返す（REST・gRPC） | 既存の `CostControlHaltReadContractTests`・`GrpcLlmCostHaltGateTests`（段 Off の読み方は不変） |
| B2 | 段 Off（既定）は従来の関所のまま。Report / Enforce は fail-closed の関所で包む | `LlmCostHaltUnavailableModeWiringTests` |
| B3 | Enforce: 一度も読めていなければ停止・猶予の内は最後の値・猶予を過ぎたら停止・読めれば戻る。Report は止めない | `FailClosedLlmCostHaltGateTests` |
| B4 | 段・猶予の読めない値、Enforce で宛先が無いときは起動で止める | `FailClosedLlmCostHaltGateTests`・`LlmCostHaltUnavailableModeWiringTests` |
| B5 | values-cloud の取引判断は Enforce。既定・values-local は描かない | `helm.yml`「Assert values-cloud LLM cost halt query is fail-closed」 |
| C1 | 基盤の CA の場所があるときだけ、JwtBearer の Backchannel・トークンの取得の HttpClient・amqps の検証に基盤の CA が付く。呼び出し先の HttpClient には付かない | `PlatformInfraCaDestinationWiringTests` |
| C2 | 既定の主ハンドラへ足すと基盤の CA の証明書を受け、足していない既定のハンドラは受けない（実際の TLS） | `PlatformInfraCaTrustTests` |
| C3 | chart は既定で `SSL_CERT_DIR` を描かない。`global.infraCa.addToSystemTrust=true` のときだけ従来の形で描く | `helm.yml`「Assert platform-infra TLS」「Assert values-cloud authorization server TLS」 |
| D1 | OpenD の基底は digest つき・Dependabot の対象。Dockerfile は展開の前に sha256 を照合し、無い・合わないと止まる。`opend-build.sh` は記録の値で照合して渡す | `helm-pod-security.test.js`「deploy/opend: …」・手元の偽の配布物でのビルド（合う・合わない・無い） |
| E1 | 稼働段に apt-get・curl・wget が無い。compose の healthcheck は bash の `/dev/tcp` | `helm-pod-security.test.js`「backend/Dockerfile: 稼働段に …」・手元の稼働の基底イメージでの実走（200 で 0・404 で 1・不達で 1） |
| E2 | NuGet Audit は all・NU1903 / NU1904 を復元の失敗にする（条件なしの PropertyGroup） | `helm-pod-security.test.js`「Directory.Build.props: …」・手元で既知の high の版を参照した復元が失敗し、変更前の設定では通る |

陰性対照: 各試験は、変更前の develop のファイル（または実装の該当行を外した形）で赤になることを手元で確かめた。

## 母集合（規則 9）

- **Worker が実行時に書く場所**: `git grep -nE 'File\.(Write|Append|Create|Open)|Directory\.CreateDirectory|new FileStream|AddDataProtection|PersistKeysToFileSystem|GetTempPath' -- backend ':!*Tests*'`
  - `FileStage0DecisionRecordSink`（Stage 0 の記録。PVC の下。クラウドは `stage0Recording.persistence.enabled: false`）。
  - `OpendAuthGateway`（OpenD の Pod の認証サイドカー。Worker ではない。クラウドは無効）。
  - 他に無し（DataProtection・Cookie・一時ファイルの直書きは無い）。.NET の一時ファイル・診断の IPC は `/tmp`（emptyDir）。
- **基盤の CA を要る宛先（クラウド）**: `values-cloud.yaml` の `https://` と `amqps`、および Authority から導く取得先。
  - 認可サーバー: `AddJwtBearer`（`AuthExtensions`）・`ServiceAuthExtensions`・`PlatformRealmAuthExtensions`（LLM ゲートウェイの Auth・KB）・`DiscordOwnerAuthExtensions`（2 か所）・`KnowledgeBaseAuthExtensions`。`git grep -n 'AddHttpClient(.*[Tt]oken' -- backend ':!*Tests*'` の 5 か所すべて。
  - ブローカ: `UseAiStockTradingRabbitMq`（1 か所）。
  - LLM ゲートウェイ: `llm`・`report-llm`（既存）。
  - DB: 接続文字列の Root Certificate（既存。`SSL_CERT_DIR` に依らない）。
  - 除外: OpenBao は ESO が読む（アプリは呼ばない）。OTLP はクラスタ内の http。KB はクラウドで未結線。
- **`SSL_CERT_DIR` の言及**: `git grep -n SSL_CERT_DIR` → chart のテンプレート・values.yaml・helm.yml・chart の README・`docs/security/security.md`・`docs/operations/cloud-keycloak-tls-runbook.md`。すべて直した。凍結の記録（`.ai-context/`）は直さない。
- **費用の停止の照会**: `git grep -n 'ILlmCostHaltGate' -- backend ':!*Tests*'` → `HttpLlmCostHaltGate`・`GrpcLlmCostHaltGate`・`NoOpLlmCostHaltGate`・`TradeDecisionAppService`・`Program.cs`。情報収集の `HttpCostControlGate`（収集の巡回の停止）は対象外（LLM を呼ばない）。報告書は月次の停止を読まない（ADR-0074 の対象外。残る危険）。
- **稼働のイメージの curl**: `git grep -nE '\bcurl\b' -- backend/Dockerfile docker-compose.yml deploy/helm` → Dockerfile の稼働段（外した）・compose の healthcheck（替えた）・chart の CronJob（別のイメージ `curlimages/curl`。対象外）。手順書の `curl` はポートフォワードの先を手元で叩くもので、イメージの中の道具を使わない。
- **OpenD のイメージ**: `deploy/opend/Dockerfile`・`scripts/opend-build.sh`・`deploy/opend/README.md`・手順書 §3・`values-cloud.yaml` の注記。

## 実装

- chart: `values-cloud.yaml`（段 3・readOnlyRootFilesystem・取引判断の `CostControl__HaltQuery__*`）、`values.yaml`（`global.infraCa.addToSystemTrust`）、`templates/deployment.yaml`（`SSL_CERT_DIR` は切り戻しの時だけ）。
- backend: `PlatformInfraCaTrust` を共通の配線へ移し、宛先ごとの付け方（`TrustPlatformInfraCa`・`ApplyTo`・`CreateValidationCallback`）を足した。JwtBearer・トークンの取得・amqps に付けた。取引判断の `FailClosedLlmCostHaltGate`・`LlmCostHaltQueryOptions`・`LlmCostHaltConfirmation`。
- イメージ: `backend/Dockerfile`（curl を外す）・`docker-compose.yml`（healthcheck）・`deploy/opend/Dockerfile`（digest・照合）・`deploy/opend/opend-tarball.sha256`・`scripts/opend-build.sh`・`.github/dependabot.yml`・`Directory.Build.props`（NuGet Audit）。
- CI: `helm.yml`（起動の paths に新しい入力を足し、検査を直す・足す）。

## 利用者の作業

- OpenD の配布物の sha256 の 1 行を `deploy/opend/opend-tarball.sha256` へ足す PR を出す（手順書 §3）。行が無い版はビルドできない。
- LLM の提供者のコンソールで月の上限を設定する（手順書 §8。リポジトリでは設定できない）。
