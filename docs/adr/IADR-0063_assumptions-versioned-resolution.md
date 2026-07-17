---
title: IADR-0063 バージョン付き全体前提条件は s2s 読み取り API ＋共有クライアント（キャッシュ・イベント無効化・last-known-good）で解決する
type: impl-adr
status: Accepted
related_ids:
  - FR-17
  - FR-13
  - UC-06
  - ADR-0007
  - IADR-0021 # 設定サービスが前提条件を所有・バージョン管理する
  - IADR-0051 # s2s 認証・読み取りは OwnerOrService・書き込みは OwnerOnly
  - IADR-0027 # 費用統制（#139 の消費側）
author: claude
created: 2026-07-17
plan_refs:
  - "../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md"
---

# IADR-0063: バージョン付き全体前提条件の取得・解決方式

- 状態: Accepted
- 日付: 2026-07-17
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 起点 issue: [#19](https://github.com/endazon/ai-stock-trading/issues/19)（Slice B・`Refs #19`）。Slice A は IADR-0021。
- 消費側: [#139](https://github.com/endazon/ai-stock-trading/issues/139)（`DefaultCostLimitsProvider` 置換）が本 IADR の成果を前提とする。
- 関連: [IADR-0021](IADR-0021_trading-assumptions-configuration.md)（所有・バージョン管理）／
  [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s 認証・最小権限）。

## コンテキストと課題

IADR-0021 は前提条件を「損益集計・AI 判断・費用統制が**共通参照**する単一の真実源」と定めたが、Slice A では
**参照する側の仕組みを実装しなかった**。現状 `GET /assumptions` は親グループごと `OwnerOnly` で保護されており、
サービストークン（`trading-service`）では 403 になる。そのため消費側は前提条件を読めず、
`DefaultCostLimitsProvider` のように**既定値のハードコード**で代替している＝**利用者の設定変更が反映されない**。

論点:

1. **消費側はどう前提条件を取得するか**（同期照会 / イベント購読でレプリカ保持 / 共有 DB）。
2. **サービスにどの権限を与えるか**（読み取りを開放すると ADR-0007「変更は利用者のみ」に反しないか）。
3. **キャッシュと版の追随**（毎回 HTTP か、キャッシュするなら無効化をどうするか）。
4. **取得不可時に何へ倒すか**（fail-safe の向き）。
5. **共有クライアントをどこに置くか**（各消費側が個別 Http アダプタを書くか、共有するか）。

## 決定

### 決定 1: 同期照会（`GET /assumptions`）を正とし、イベントはキャッシュ無効化の合図に用いる

- 前提条件は**低頻度変更・小サイズ・全消費側で同一**。イベント購読でレプリカを各サービスの DB に持つ方式は、
  取りこぼし・初期同期・整合の面倒を各サービスに複製する割に利点が無い。共有 DB は ADR-0001（Database per Service）違反。
- よって既存の同期照会パターン（IADR-0028/0029/0030/0031）に揃え、`GET /assumptions` を単一の取得口とする。
  `AssumptionsChanged`（Version つき・IADR-0021 で発行済み）は**キャッシュ無効化の合図**としてのみ使う
  （イベントの本文から値を復元しない＝版の逆転や取りこぼしで誤った値を保持しない）。

### 決定 2: 読み取りのみ `OwnerOrService` へ分離する（最小権限・IADR-0051 準拠）

- `GET /assumptions`（現在値＋Version）→ **`OwnerOrService`**。
- `PUT /assumptions`（更新）・`GET /assumptions/history`（履歴）→ **`OwnerOnly` 据え置き**。
- ADR-0007 が禁じるのは**変更**であり、参照ではない。前提条件（税率・手数料体系・費用上限）は機微情報ではなく、
  そもそも消費側が共通参照する前提で設計されている（IADR-0021）。一方、履歴は**誰がなぜ変えたか**という運用情報の
  ため、サービスへ開放する必要が無く OwnerOnly を維持する（最小権限）。
- **実装上の注意（IADR-0051 の既知の罠）**: 認可は**親グループに付けない**。既存 `AssumptionsEndpoints` は親
  `MapGroup("/assumptions")` に `RequireAuthorization(OwnerOnly)` を付けているため、これを外して owner サブグループへ
  移す。親に残したまま read サブグループへ `OwnerOrService` を足すと**ポリシーが合成され** OwnerOnly も要求され、
  サービストークンが 403 になる（`CostControlEndpoints`・`RiskControlEndpoints`・`ReportEndpoints` と同形にする）。

### 決定 3: 共有クライアント `ConfigurationService.Client` を置き、各消費側は 1 行で配線する

- 既存の同期照会は消費側ごとに Http アダプタを手書きしている（`HttpDailyPolicyProvider`・`HttpCostControlGate` 等）。
  前提条件は**3 サービス以上が同じ形で参照する**（IADR-0021）ため、同じキャッシュ・無効化・fail-safe を 3 回
  書き写すことになる。よって**共有クライアントを 1 つ置く**。
- 配置は新規プロジェクト `backend/Services/ConfigurationService/src/ConfigurationService.Client`。
  `ConfigurationService.Domain` を参照する（`CostControlService.Domain` → `ConfigurationService.Domain` という
  **既存の前例**に沿う）。`Shared.Infrastructure` へ置く案は、Shared が個別サービスの Domain に依存する
  **逆向きの依存**になるため採らない。
- 消費側は `services.AddAiStockTradingAssumptions(configuration)` の 1 行で配線し、`IAssumptionsProvider` を DI で得る。
  `AssumptionsChangedConsumer` は MassTransit の登録が消費側 Program にあるため、**型を公開して消費側が
  `x.AddConsumer<AssumptionsChangedConsumer>()` する**（購読はキャッシュ無効化のみで副作用なし）。
- `VersionedAssumptions` は `Application.State` から `Domain` へ移す（消費側が Application 層＝サーバ内部に依存しないため）。

### 決定 4: キャッシュはイベント無効化 ＋ TTL の二段で失効させる

- `AssumptionsChanged` 購読で即時無効化する（版の追随＝#139 の受け入れ基準）。
- ブローカ不達・購読取りこぼし・起動直後のレース に備え、**TTL（既定 5 分）でも失効**させる。前提条件は低頻度変更の
  ため 5 分の陳腐化は許容でき、逆にイベントのみに頼ると**恒久的に古い値**を掴む恐れがある。

### 決定 5: fail-safe は「last known good ＞ 既定値」の順に倒す

取得不可（不達・非 2xx・タイムアウト・不正応答）時の縮退順:

1. **過去に取得成功した値があれば、それ（陳腐化していても）を返す**。
2. 一度も取得できていなければ `TradingAssumptionsDefaults.Create()` を `Version = 0`（＝未解決の番兵）で返す。

- 「常に既定値へ倒す」を採らない理由: 利用者が既定より**厳しい**設定（例: 月次上限を 20,000 → 5,000 へ引き下げ）を
  していた場合、設定サービスの一時障害で既定へ戻すと**緩む側**（＝安全でない側）へ倒れる。last known good は
  利用者の意図に最も近く、かつ現行挙動（既定値）も未取得時には保たれる。
- 例外は送出しない（消費側の巡回・要求処理を止めない。IADR-0051 決定 4 と同じ姿勢）。`LogWarning` で可観測にする。

### 決定 6: `BaseUrl` 未設定なら HTTP を構築しない（既定 no-op）

- `Configuration:BaseUrl` 未設定/不正 URI なら `DefaultAssumptionsProvider`（既定値・`Version = 0`）を登録し、
  HTTP 自体を発生させない。s2s トークン（`ServiceAuth:ClientId/ClientSecret`）未設定なら既存どおりハンドラを
  付けない（＝401 → 決定 5 の縮退）。**既定ビルド/CI は外部接続なしで緑**。

### 決定 7: 実 Keycloak 往復の検証は #82 の E2E に委ねる

- 本 PR はユニット（fake `HttpMessageHandler`・DI 選択・ポリシー分離のエンドポイントテスト）で担保する。
  実 confidential クライアントでの `client_credentials` 往復と認証済み照会の疎通は #82（IADR-0050）に委ねる
  （IADR-0051 決定 6 と同じ切り分け）。

## 影響

- `AssumptionsEndpoints`: 親の `RequireAuthorization` を撤去し、read（`OwnerOrService`）/ owner（`OwnerOnly`）の
  サブグループへ再編。**外部から見た挙動の変化は「サービストークンで GET が 200 になる」だけ**（利用者・未認証は不変）。
- 新規 `ConfigurationService.Client`（＋テストプロジェクト）を `backend.slnx` へ追加。
- `VersionedAssumptions` の名前空間が `...Application.State` → `...Domain` へ変わる（参照は 5 ファイル・同一サービス内）。
- #139 は `ICostLimitsProvider` の実装を本 provider 経由にするだけでよい。ただし `GetLimits()` は**同期**のため、
  #139 側で非同期化（呼び出し元のエンドポイントは既に async）が必要になる。

## 結果

- 良い影響: 利用者の前提条件変更が消費側に届く経路が初めて通る。#139 は配線のみで済む。以降の消費側
  （損益計算・AI 判断・リスク統制の `CostCalculator` 実利用）も同じ 1 行で載る。
- 悪い影響・トレードオフ: キャッシュにより最大 TTL（5 分）＋イベント遅延ぶんの陳腐化が生じる。障害時は
  last known good を掴み続ける（可観測性は LogWarning のみ）。前提条件の読み取りがサービスへ開放される
  （変更・履歴は不可）。
- フォローアップ: #139（費用上限の実適用）、`CostCalculator` の 3 サービス実利用、#82 での実 Keycloak E2E。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0021](IADR-0021_trading-assumptions-configuration.md)・[IADR-0051](IADR-0051_service-to-service-auth.md)・
  [IADR-0027](IADR-0027_cost-control.md)
