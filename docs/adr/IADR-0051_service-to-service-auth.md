---
title: IADR-0051 サービス間同期照会の service-to-service 認証（client_credentials・呼び出し側トークン伝播・least-privilege サービスロール）
type: impl-adr
status: Accepted
related_ids:
  - IADR-0011 # Keycloak OIDC/JWT・OwnerOnly（Foundation 最小移植）
  - IADR-0028 # daily-policy 同期 API（呼び出し側 HttpDailyPolicyProvider）
  - IADR-0029 # sizing-context 同期 API（呼び出し側 HttpSizingContextProvider）
  - IADR-0030 # open-positions 同期 API（呼び出し側 HttpPositionStore・サイレント縮退の指摘元）
  - IADR-0050 # マルチサービス/認証つき統合 E2E（#82・本 IADR に依存）
author: claude
created: 2026-07-13
updated: 2026-07-13
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0007_owner-only-controls.md"
---

# IADR-0051: サービス間同期照会の service-to-service 認証

- 状態: Accepted
- 日付: 2026-07-13
- 決定者: endazon（起票: issue #76）・claude（実装詳細）

## 起点・関連

- 起点 issue: #76（`Refs #22`）。IADR-0028/0029/0030（同期 API 方式）の続き。
- 消費側: #82（IADR-0050）の「s2s トークン伝播つき同期照会 E2E」が本 IADR の成果を前提とする。
- 関連: [IADR-0011](IADR-0011_foundation-min-port.md)（Keycloak・OwnerOnly）・ADR-0007（利用者のみ操作）・platform ADR-0004（認証認可）。

## コンテキストと課題

#22 の同期照会（実データ化）で導入した 3 エンドポイントは、いずれも提供側で **OwnerOnly（Keycloak
`trading-owner`）** で保護されている一方、呼び出し側は**認証ヘッダなし**で呼んでいる。BaseUrl を設定して実運用を
有効化すると service-to-service 認証が無いため常に 401 になり、各サービスの安全既定へフォールバックする。

| 呼び出し側 | エンドポイント | 401 時の縮退 |
| --- | --- | --- |
| 取引判断 `HttpDailyPolicyProvider`（IADR-0028） | `GET /reports/daily-policy` | null＝取引しない（保守側） |
| 取引判断 `HttpSizingContextProvider`（IADR-0029） | `GET /risk-controls/sizing-context` | 残枠 0＝取引しない（保守側） |
| 市場監視 `HttpPositionStore`（IADR-0030） | `GET /risk-controls/open-positions` | 空列＝**損切り検知を抑止**（保護が働かない側・非対称） |

とくに `open-positions` は「接続されているのに損切り検知が働かない」**サイレント縮退**で、優先度が高い。

論点:
1. **どの認証方式か**（client_credentials か on-behalf-of / token-exchange か）。
2. **どのように呼び出し側へトークンを付与するか**（各 Http アダプタを個別改修 vs 横断ハンドラ）。
3. **サービスにどの権限を与えるか**（`trading-owner` をそのまま与えるか、最小権限の専用ロールか）。
4. **秘密未設定時・取得失敗時の挙動**（fail-safe をどう保つか）。

## 決定

### 決定 1: 方式は client_credentials（machine-to-machine）を採用する

- 呼び出し側（取引判断・市場監視）は **タイマ/イベント駆動の自律ワーカー**であり、契機となる**入力ユーザ
  トークンが存在しない**。よって on-behalf-of / token-exchange（ユーザトークンの伝播・交換）は
  アーキ上適用できない。サービス自身の機械 ID による **client_credentials** が唯一の適合方式。
- Keycloak dev realm の**機密（confidential）クライアント** `ai-stock-trading-svc`（`serviceAccountsEnabled`）を
  用い、`grant_type=client_credentials` でアクセストークンを取得する。platform ADR-0004（Keycloak OIDC）に準拠。

### 決定 2: 呼び出し側は横断の `ServiceTokenHandler`（DelegatingHandler）でトークンを付与する

- 各 Http アダプタ（`HttpDailyPolicyProvider` 等）は無改修とし、名前付き HttpClient（`"risk"` / `"reports"`）へ
  `DelegatingHandler` を差し込む形でトークン付与を横断化する。将来の同期照会追加にも自動適用できる。
- ハンドラは発信要求ごとに `IServiceAccessTokenProvider` からトークンを得て
  `Authorization: Bearer <token>` を付与する。トークンが得られない場合は**ヘッダを付けずに送信**し、
  提供側の 401 → 既存アダプタのフェイルセーフ（null/残枠0/空列）に倒す（決定 4）。
- `IServiceAccessTokenProvider` 実装は `ClientCredentialsTokenProvider`（token エンドポイントへ
  `client_credentials` を POST し、`expires_in` から安全マージンを引いた時刻までトークンをキャッシュ・
  スレッドセーフ・多重取得を単一化）のみ。**秘密未設定時はハンドラ自体を配線しない**（決定 4・provider も未登録）。

### 決定 3: 最小権限の専用ロール `trading-service` を導入し、読み取り系のみ許可する

- ADR-0007 は kill switch・リスク設定・段階昇格を「利用者のみ」に限定し、エンドポイントのコメントも
  「**生成AI・自動処理はこのロールを持たない**」と明記している。サービスアカウントへ `trading-owner` を
  与えると kill switch 操作・設定変更まで可能になり、この意図に反する（**過剰権限**）。
- そこで新ロール `trading-service` を追加し、**読み取り系の 3 エンドポイントのみ** `OwnerOrService`
  ポリシー（`trading-owner` **または** `trading-service`）へ分離する。書き込み系（kill switch・settings・
  report confirm 等）は厳格 `OwnerOnly` のまま。サービスアカウントには `trading-service` のみ付与する。
- 対象エンドポイントの再編:
  - RiskManagement: `/risk-controls/sizing-context`・`/risk-controls/open-positions` → `OwnerOrService`。
    その他（kill-switch・settings）は `OwnerOnly` 据え置き。
  - Reports: `/reports/daily-policy`（GET）→ `OwnerOrService`。その他（一覧・確定等）は `OwnerOnly` 据え置き。

### 決定 4: 秘密未設定・取得失敗時は fail-safe（現行挙動）を厳密に保つ

- `ServiceAuth:ClientId` と `ServiceAuth:ClientSecret` の**両方が設定され**、token エンドポイントが解決できる
  **時のみ** `ClientCredentialsTokenProvider` とハンドラを配線する。未設定なら**ハンドラを付けない**（no-op）。
  → トークン無し → 提供側 401 → 既存アダプタの安全既定。**既定ビルド/CI は秘密なしで従来通り緑**（外部接続なし）。
- トークン取得が失敗（Keycloak 不達・秘密誤り・非 2xx・タイムアウト）してもワーカーの巡回を止めない。
  ハンドラは**例外を送出せず**ヘッダ無しで送信し、フェイルセーフへ倒す。
- BaseUrl 未設定時は既存どおりプレースホルダ選択で HTTP 自体が発生しないため、二重にゲートされる。

### 決定 5: 取得失敗の可観測性を昇格する

- トークン取得失敗を**安定メッセージ＋構造化フィールド**（token エンドポイント・client_id・ステータス）で
  `LogWarning` する。既存の `AddAiStockTradingObservability`（Serilog + OTel）に載る。将来のメトリクス化
  （失敗カウンタ）フックのため単一箇所に集約する。受け入れ条件の「LogWarning の可観測性への昇格」に対応。

### 決定 6: 実 Keycloak 往復の E2E は #82 に委ねる

- 本 PR はユニット（fake token エンドポイントの `HttpMessageHandler`・DI 選択・ポリシー分離の
  エンドポイントテスト）で担保する。実 confidential クライアントの `client_credentials` 往復と
  認証済み同期照会の疎通は #82（IADR-0050 の残り・別 PR）で検証する（issue #76 受け入れ条件「E2E は別 issue」）。

## 影響

- PlatformShim（`AiStockTrading.TestSupport.PlatformShim`）に client 側 `AddAiStockTradingServiceToken` 拡張と
  `ServiceTokenHandler`・`IServiceAccessTokenProvider` を追加（server 側 `AddAiStockTradingAuth` の対）。
- RiskManagement / Reports のエンドポイントで読み取り系を `OwnerOrService` に分離（書き込みは不変）。
- `infra/keycloak/realm-export.json` に `trading-service` ロールと confidential クライアント
  `ai-stock-trading-svc`（dev 用固定秘密・sslRequired none の dev realm 内）を追加。**本番秘密は
  config（user-secrets/.env）から供給**し、コミットしない。
- 既定 CI は秘密なしで従来挙動（決定 4）。実疎通は #82 の integration ジョブ。

## 却下した代替案

- **サービスアカウントに `trading-owner` を付与**: 提供側改修が不要で最短だが、kill switch 操作・設定変更まで
  可能になり ADR-0007 の「自動処理はこのロールを持たない」に反する（過剰権限）。決定 3 の最小権限を採る。
- **on-behalf-of / token-exchange**: 自律ワーカーには伝播元のユーザトークンが無く適用不能（決定 1）。
- **各 Http アダプタを個別にトークン付与へ改修**: 重複が増え将来の同期照会追加ごとに漏れが出る。
  横断 DelegatingHandler（決定 2）の方が単純で一貫。
- **秘密未設定でもトークン取得を試みる**: 既定 CI/ビルドが Keycloak 接続に依存し脆くなる。設定ゲート（決定 4）で
  fail-safe を保つ。
