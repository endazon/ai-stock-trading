---
title: IADR-0324 MSP 連結配備では AST サービスの認証レルムを MSP レルムへ統一する（`global.authAuthority` の 1 値で inbound 検証と s2s の発行元を揃えて移す）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-13, FR-17, FR-19, FR-20, UC-06, SC-01, SC-02, SC-03, IADR-0011, IADR-0050, IADR-0051, IADR-0093, IADR-0098, IADR-0176, IADR-0283]
author: endazon (with Claude Code)
created: 2026-09-10
updated: 2026-09-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
---

# IADR-0324: MSP 連結配備では AST サービスの認証レルムを MSP レルムへ統一する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- issue: [#727](https://github.com/endazon/ai-stock-trading/issues/727)（統合 SPA から AST 画面を開くと再読み込みの無限ループ）。
  MSP 側の分担は MSP#1372（realm への客体追加）。
- 画面 / 要求: SC-01 / SC-02 / SC-03（FR-17・FR-13 / FR-19 / FR-20・FR-10）。利用者（`trading-owner`）が画面から操作する経路。
- 関連 IADR: [IADR-0011](IADR-0011_foundation-min-port.md)（Keycloak 認証の最小移植。レルムの配置は定めていない）／
  [IADR-0050](IADR-0050_e2e-multiservice-and-auth.md)（単体 E2E。AST レルムの用途）／
  [IADR-0051](IADR-0051_service-to-service-auth.md)（`trading-service`・`ServiceAuth`）／
  [IADR-0093](IADR-0093_kb-writer-cross-realm-s2s.md)（KB 書き込みは MSP レルム専用クライアント）／
  [IADR-0098](IADR-0098_owner-realm-client.md)（Discord OwnerAuth。**選択肢 C の却下理由の前提を本 ADR が変える**）／
  [IADR-0176](IADR-0176_run-once-authorization-and-cronjob-token.md)（CronJob の token エンドポイントは `global.authAuthority` から導出）／
  [IADR-0283](IADR-0283_deploy-value-preservation-and-kb-realm-fix.md)（KB レルム名の是正。同型の先行事例）
- 仕様書: [`.ai-context/specs/20260910_727_msp-linked-realm-unification.md`](../specs/20260910_727_msp-linked-realm-unification.md)

## 背景・課題

統合 SPA（MSP の `frontend-service`）は BFF セッション方式（MSP/ADR-0032）で **MSP レルム（`platform`）**に対して身元を
成立させる。BFF は `SessionTokenPropagationMiddleware` がそのアクセストークンを `Authorization` へ昇格し、AST の BFF
モジュール（`AssumptionsBffEndpoints` ほか）が上流へ透過する。

一方、AST の各サービスは chart 既定 `global.authAuthority = http://keycloak:8080/realms/ai-stock-trading`（**AST レルム**）で
JWT を検証する。2026-09-10 の差分実験（同じ `GET /assumptions`）:

```
MSP レルムのトークン:  401  Bearer error="invalid_token", error_description="The issuer '…/realms/platform' is invalid"
AST レルムのトークン:  200
```

SPA の `apiFetch` は 401 を「セッション失効」と解釈してログインへ強制遷移し、SSO で即座に戻り、同じ 401 を受ける。
これが無限ループの機構である（BFF ログで 30 分に上流 401 が 120 回超）。

**この不整合は k8s に限る。** compose スタックは MSP#283 決定 2e で「AST サービスの `Auth__Authority` は MSP レルム共通、
`trading-owner` は MSP レルム側で定義」と決めて実装済みで、MSP レルムの `developer` は `trading-owner` を持つ。
k8s 側（本 chart の MSP 連結プロファイル）だけが chart 既定の AST レルムのまま取り残されていた。

## 決定

### 1. MSP 連結プロファイル（`values-local.yaml`）では `global.authAuthority` を MSP レルムへ向ける

```yaml
global:
  authAuthority: http://keycloak:8080/realms/platform
```

**認証レルムは配備単位で 1 つ**にする。統合 SPA の身元は MSP レルムでしか成立しないので、AST サービスもそこで検証する。

### 2. 1 値で inbound 検証と s2s の発行元を揃えて移す（設定点を増やさない）

chart は inbound の `Auth__Authority`・`ServiceAuth` の token エンドポイント（`Auth:Authority` から導出。IADR-0051）・
run-once CronJob の token エンドポイント（IADR-0176）・Discord OwnerAuth の `TokenEndpoint`（IADR-0098）を**すべて
`global.authAuthority` から導出する**。この設計のおかげで、レルムを移す変更は 1 値で済み、「inbound は MSP レルム・
s2s は AST レルム」という**片側だけ移って 401 になる**状態が構造的に作れない。

### 3. MSP レルムに要る客体は MSP 側が所有し、dev secret は AST レルムと同値にする

`trading-service` ロール・`ai-stock-trading-svc`（service-account に `trading-service`）・`ai-stock-trading-owner`
（service-account に `trading-owner`）を MSP レルムの宣言へ足す（MSP#1372。IADR-0093 の `ai-stock-trading-kb-writer` と同じ
「realm 定義は MSP 側 PR」の分担）。dev secret を AST レルム（`infra/keycloak/realm-export.json`）と同値にすることで、
稼働中の `ast-secrets`（`service-auth-*` / `discord-owner-auth-*`）を変えずに済む。本番 secret は従来どおり Vault 経由。

### 4. chart 既定と AST レルムは不変

`values.yaml`（本番描画）はバイト等価のまま。`realm-export.json` は単体 E2E（IADR-0050）の Keycloak へ import する
宣言として残す。MSP の起動器が AST レルムを同梱 import する動作も変えない（残っていても害は無い）。

### 5. IADR-0098 の選択肢 C の却下理由は前提が変わる（追記で残す）

IADR-0098 は「owner クライアントを MSP レルムに置く」案を「制御先 RiskManagement が AST レルムで検証するから」と却下した。
本 ADR で MSP 連結配備の検証レルムが MSP レルムになるため、**その前提は MSP 連結配備では成り立たない**。IADR-0098 の
決定 1〜3（専用 confidential client・TokenEndpoint を `global.authAuthority` から導出・inbound 認証を増やさない）は
生きるので、置換ではなく日付つき追記で残す。

## 検討した選択肢

- **A: 本 ADR（MSP 連結プロファイルで認証レルムを MSP レルムへ統一）** — 採用。
- **B: AST 各サービスで二重 issuer を受ける**（JwtBearer を 2 本立て `iss` で振り分け） — 却下。コード変更が要り、
  `developer` が両レルムに居る曖昧さ（どちらの `trading-owner` が本人か）を残す。同名ロールを 2 つのレルムで管理する
  運用も増える。
- **C: BFF でトークン交換（MSP レルム → AST レルム）** — 却下。過剰。
- **D: 統合 SPA の AST 画面を k8s では使わない** — 却下。SC-04（OpenD 認証操作）を含む画面群が PoC の入口。

## 影響・結果

- MSP 連結の k8s で統合 SPA から SC-01 / SC-02 / SC-03 が開ける（上流 401 が消える）。
- AST の s2s（`ServiceAuth`・CronJob・Discord OwnerAuth）も MSP レルムで発行・検証されるようになる。
  MSP レルム側の客体が無いうちに本 ADR だけ先に当てると s2s が 401 になるため、**反映順は MSP#1372（realm）→ 本 ADR
  （values）**である。
- AST レルムのトークンは MSP 連結配備の AST サービスで通らなくなる（陰性対照として実測する）。

## 残余リスク

- BFF が上流の 401 を透過する限り、上流の認証構成の不整合は**必ず**SPA のログインループになる。上流 401 を
  502（後段の構成不整合）へ写像する是正は別 issue で扱う。
- AST の計画書は認証レルムの配置を定めていない。「MSP 連結配備では利用者認証を MSP レルムで行う」は planning へ環流する。
