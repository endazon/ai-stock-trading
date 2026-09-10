---
title: MSP 連結 k8s では AST サービスの認証レルムを MSP レルムへ統一し、統合 SPA の再ログインループを止める
type: spec
status: done
related_ids: [FR-10, FR-13, FR-17, FR-19, FR-20, UC-06, SC-01, SC-02, SC-03, IADR-0324]
author: endazon (with Claude Code)
created: 2026-09-10
updated: 2026-09-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
---

# 仕様書: MSP 連結 k8s では認証レルムを MSP レルムへ統一する（#727）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面: **SC-01**（設定）・**SC-02**（リスク設定）・**SC-03**（承認・統制状態参照）。統合 SPA から開くと再読み込みの
  無限ループになる（2026-09-10 利用者報告）。SC-04（OpenD 認証操作）は上流が認証を持たないため影響しない。
- 機能要求: **FR-17**（全体前提条件の閲覧/変更）・**FR-13 / FR-19 / FR-20**（リスク上限の閲覧/変更）・**FR-10**（統制）。
  いずれも利用者（`trading-owner`）が画面から操作する経路である。
- ユースケース: **UC-06**（利用者による設定変更）。
- 実装 ADR: [IADR-0324](../adr/IADR-0324_msp-linked-deploy-single-auth-realm.md)（本件で新設）。
  前提を変える先: [IADR-0098](../adr/IADR-0098_owner-realm-client.md)（日付つき追記）。

## 症状と根本原因（実測。すべて 2026-09-10）

| 層 | 実測 |
| --- | --- |
| SPA | `apiFetch` は 401 を「セッション失効」と解釈し `/bff/auth/login?returnUrl=…` へ `location.assign` する（MSP `AuthProvider.tsx`）。SSO が生きているので即座に戻り、同じ 401 → 同じ遷移 |
| BFF | AST の BFF モジュール（`AssumptionsBffEndpoints` ほか）は上流の状態コードを**そのまま**返す。30 分で `risk-controls/status` 94 回・`assumptions` 29 回の上流 401 |
| 転送するトークン | MSP `SessionTokenPropagationMiddleware` がセッション（**MSP レルム**）のアクセストークンを `Authorization` へ昇格する |
| 上流（configuration-service） | `Auth__Authority=http://keycloak:8080/realms/ai-stock-trading`（chart 既定 `global.authAuthority`） |

差分実験（同じ `GET /assumptions` を port-forward 経由で直接呼ぶ）:

```
トークンなし:                                   401  WWW-Authenticate: Bearer
MSP レルム（abac-seeder / platform-admin）:      401  Bearer error="invalid_token",
                                                     error_description="The issuer 'https://keycloak.localhost/realms/platform' is invalid"
AST レルム（ai-stock-trading-svc / trading-service）: 200
```

**根本原因**: AST サービスは AST レルムで検証しているが、統合 SPA の身元は MSP レルムでしか成立しない。

compose スタックは MSP#283 決定 2e で「AST サービスの `Auth__Authority` は MSP レルム共通、`trading-owner` は MSP レルム側で
定義」と決めて実装済み（MSP `deploy/docker-compose.yml`。`developer` は MSP レルムで `trading-owner` を持つ）。
k8s 側（本 chart）はそれに追随しておらず、統合 SPA の AST 画面を k8s で通した実測が無かった。

## 設計（何をどう変えるか）

**MSP 連結プロファイル `values-local.yaml` で `global.authAuthority` を MSP レルムへ向ける。** 1 値である理由:
chart は inbound の `Auth__Authority`・`ServiceAuth` の token エンドポイント（`Auth:Authority` から導出）・run-once CronJob
（IADR-0176）・Discord OwnerAuth（IADR-0098）を**すべて `global.authAuthority` から導出する**設計になっており、
この 1 値で認証レルムが揃って移る。

| 対象 | 変更前 | 変更後 |
| --- | --- | --- |
| `values-local.yaml`（MSP 連結プロファイル） | `global.authAuthority` 未指定＝chart 既定（AST レルム） | `http://keycloak:8080/realms/platform` |
| `values.yaml`（chart 既定＝本番描画） | AST レルム | **不変**（バイト等価） |
| `infra/keycloak/realm-export.json`（AST レルム） | 単体 E2E（IADR-0050）用 | **不変** |
| MSP レルム（MSP 所有） | `trading-owner` のみ | ＋ `trading-service` ロール・`ai-stock-trading-svc`・`ai-stock-trading-owner`（MSP#1372） |
| `ast-secrets` | AST レルムの dev secret | **不変**（MSP レルム側の dev secret を同値にする） |

### 採らなかった案

- **AST 各サービスで二重 issuer を受ける**（JwtBearer 2 本＋`iss` で振り分け）: コード変更が要り、`developer` が両レルムに
  居る曖昧さ（どちらの `trading-owner` が本人か）を残す。認証レルムを 1 つにするほうが単純で安全。
- **BFF でトークン交換**: 過剰。
- **統合 SPA の AST 画面を k8s では使わない**: SC-04（OpenD 認証操作）を含む画面群が PoC の入口であり、選べない。

## 走査した母集合（規則 2・9）

「AST レルムで検証する」を前提にしている live な記述を、誤りの側の文字列（`realms/ai-stock-trading` / `authAuthority` /
`Auth__Authority` / `AST レルム`）で `.claude/` `node_modules` `.git` `bin` `obj` を除く全ファイルから引いた。

| 箇所 | 扱い |
| --- | --- |
| `deploy/helm/ai-stock-trading/values.yaml:18` | **不変**（chart 既定＝本番描画のバイト等価を守る） |
| `deploy/helm/ai-stock-trading/values-local.yaml` | **変更**（本件の実装点。ヘッダ注記も足す） |
| `scripts/k8s-local-deploy.sh:3-4`（前提の注記「realm `ai-stock-trading` を用意済み」） | **追記**（import は残るが利用者認証は MSP レルム） |
| `docs/security/security.md:100`（Authority の既定） | **追記**（既定は不変。MSP 連結プロファイルの値を 1 行足す。trace ブロックへ #727 / IADR-0324） |
| `.ai-context/adr/IADR-0098`（選択肢 C の却下理由） | **日付つき追記**（前提が変わる。決定 1〜3 は生きる） |
| `.ai-context/adr/IADR-0093` / `IADR-0061` / `IADR-0071` / `IADR-0323`（「AST レルムの ServiceAuth では issuer 不一致」） | **除外**: KB / LLM の s2s を MSP レルム専用クライアントで出す決定は本件後も正しい（MSP 連結では両者が同じレルムになるだけ）。凍結記録でもある |
| `.ai-context/adr/IADR-0176` | **除外**: 「`global.authAuthority` から導出する」設計そのものが本件を 1 値で成立させている |
| `backend/**/appsettings.Development.json`・`IntegrationTests`（Testcontainers の Keycloak） | **除外**: 単体起動・単体 E2E の値であり MSP 連結ではない |
| `docs/blocked-tasks.md:566` | **除外**: 過去の実測記録（AST レルムが 404 だった件） |
| `.ai-context/specs/`・`CHANGELOG.md` | **除外**: 凍結記録・生成物 |

## 受け入れ基準

- [x] `helm template ast deploy/helm/ai-stock-trading` の既定描画は変更前とバイト一致（`cmp` で確認）
- [x] `helm template … -f values-local.yaml` で `auth: true` の全サービスの `Auth__Authority`・CronJob の token エンドポイント・
      notification の `OwnerAuth__TokenEndpoint` が MSP レルムを指す
- [x] 稼働クラスタ: MSP レルムのトークンで `GET /assumptions` が 200、AST レルムのトークンは 401（陰性対照が反転した。下の実施記録）
- [ ] 稼働クラスタ: 統合 SPA から SC-01/02/03 を開いてもループしない（BFF ログに上流 401 が出ない）—— 利用者の操作待ち
- [x] OpenD の Deployment は本変更の描画差分を持たない（🔴 ただし適用作業の事故で一度消えた。下の実施記録）
- [x] `scripts/k8s-local-deploy.test.sh`（78 passed）・`helm lint -f values-local.yaml` が緑

## 計画書との差異・環流

- AST の計画書は認証レルムの配置を定めていない（IADR-0011 が「基盤 ADR-0004 の最小移植」として実装側で決めた）。
  **MSP 連結配備では利用者認証を MSP レルムで行う**という前提は計画に無いので、planning へ環流する（`feedback`）。

## 未決事項・残余リスク

- BFF が上流の 401 を透過すると、上流の認証構成の不整合が**必ず**SPA のログインループになる。上流 401 を
  502（後段の構成不整合）へ写像する是正は別 issue で扱う（本件の射程外）。

## 実施記録（2026-09-10・稼働クラスタ）

### 反映と差分実験

反映順は MSP#1372（realm。`reconcile-realm.sh` が drift=0 で収束。途中で MSP#1373 の不具合を直した）→ 本 values。
同じ `GET /assumptions`（port-forward 経由）:

```
platform レルム（ai-stock-trading-svc / trading-service）:        200 OK
ai-stock-trading レルム（同名クライアント）:                        401  error_description="The issuer '…/realms/ai-stock-trading' is invalid"
```

変更前は逆（platform 401 / ai-stock-trading 200）だった。**陰性対照が反転した。**

### 🔴 事故: 適用スクリプトの引き継ぎ漏れで OpenD の Deployment を消した

helm の適用に、`scripts/k8s-local-deploy.sh` を使わず**自作の手順**（前回リリースの `broker.tier` / `opend.enabled` /
`discord.bot.*` を YAML から自前で読む）を使った。その読み取りが空を返し、`[ -n "$v" ] || continue` が黙って飛ばしたため、
revision 18 は chart 既定（`opend.enabled=false`・`broker.tier=paper`・Discord 空）で描画され、**OpenD の Deployment が
削除された**（PVC `opend-persist` は `resource-policy: keep` で残った）。IADR-0283 / #673 が守っていた事故そのものである。

- 復旧: `helm get values --revision 17` から `broker` / `opend` / `discord` ブロックを抜き `-f` で重ねて revision 19 を当てた。
  値は全て戻り、OpenD は再起動して SMS を再要求した（利用者が受け取る検証コードは新しいものになる）。
- 副作用: revision 18 で AST の全 Pod（auth を持たないものを含む）が一度ロールアウトした。
- 判明したこと: SC-04 のサイドカー `opend-auth` は helm の値に無く（`opend.authGateway.enabled` 既定 false）、以前は
  helm 外で立っていた。次回のデプロイでも消える構造だったので、**本プロファイルへ `opend.authGateway.enabled: true` を置いた**。
- 教訓: **稼働リリースの値を触るときは、リポジトリの標準手順（`k8s-local-deploy.sh` ＋ env の明示）を使う。**
  引き継ぎの機構を自作しない。自作した時点で IADR-0283 の保護の外に出る。
