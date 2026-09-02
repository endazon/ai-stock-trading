---
title: IADR-0282 watchlist の初回シードは構成（Monitor:SeedSymbols）から供給し、全削除の意思は永続フラグで区別する
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-13, UC-06, SC-02, IADR-0088, IADR-0095, IADR-0114]
author: claude (Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs: []
---

# IADR-0282: watchlist の初回シードは構成（Monitor:SeedSymbols）から供給し、全削除の意思は永続フラグで区別する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: worker（Claude Code）／利用者裁定 2026-09-02

## 起点・関連

- 関連する計画書 ID: FR-02（取引サイクル・定時判断）, FR-13（監視銘柄の変更は利用者のみ・変更履歴を記録）,
  UC-06（監視設定の変更）, SC-02（監視銘柄変更画面）
- 関連する実装ADR: IADR-0088（監視銘柄 API の認可設計）, IADR-0095（HttpWatchlistProvider のフォールバック設計。
  「200＋空配列は正常応答」＝空の watchlist は利用者の正当な選択を尊重する）, IADR-0114（#279 決定5。
  本 issue（#286）の切り出し元）
- 関連する実装仕様書: `.ai-context/specs/20260902_286_watchlist-config-seed.md`
- Issue: [#286](https://github.com/endazon/ai-stock-trading/issues/286)

## コンテキストと課題

`TradeDecisionService` の `MarketMonitor__BaseUrl` を market-monitor へ結線すると、定時サイクルの監視銘柄
（watchlist）取得は権威源（market-monitor）の `GET /monitor/watchlist` へ切り替わる。ところが
`MarketMonitorService.Domain.MonitorDefaults.CreateSettings()` は監視銘柄を**空にシードする**ため、結線した
瞬間に watchlist が空になり、`HttpWatchlistProvider` のフォールバック（IADR-0095）は**非 2xx・timeout・
例外・不正応答（null）に限られ、200＋空配列は正常応答としてそのまま返す**設計のため、判断対象がゼロになり
取引サイクルが沈黙する（#279/IADR-0114 決定5 の調査で判明）。

フォールバック側を変える（空配列もフォールバック対象にする）と、IADR-0095 が守っている「利用者が SC-02 等で
明示的に全削除したら、それは正当な選択として尊重する」という設計思想を壊す。したがって**権威源（market-monitor）
に初期値を入れる**のが筋だが、単純に `MonitorDefaults.CreateSettings()` の既定値を非空にすると、今度は
「利用者が意図的に全削除した」状態と「まだ何も設定していない」状態を区別できず、削除しても次回読み出しで
既定値へ巻き戻ってしまう。

## 検討した選択肢

利用者裁定（2026-09-02）で検討した3案（#286 スコープに記載）:

1. **(a) デプロイスクリプトから `POST /monitor/watchlist` を叩く**: 投入は OwnerOnly のため owner トークン
   取得が要る（`ast-secrets` の owner クライアント資格情報・#226/IADR-0098 は既にある）。デプロイの度に
   「既に投入済みか」を判定する必要があり、冪等性の担保（重複追加は 400）をスクリプト側に持たせる複雑さが増す。
2. **(b) 構成からの初回シード**: `MonitorDefaults.CreateSettings()` を構成（`Monitor:SeedSymbols`）から
   供給し、単一行 JSON ストアに「未設定」と「利用者が明示的に全削除した」を区別するフラグを持たせる。
   アプリケーション自身が判定するため、デプロイスクリプトに認証ロジックを持ち込まない。
3. **(c) SC-02 からの手動投入を運用手順として文書化する**: コード変更が最小だが、再デプロイの度に人手の
   操作が要り、経路B（ローカル SIMULATE）の「臨時 overlay 無しに有効化する」という既定方針（IADR-0100）
   と整合しない。

## 決定

**(b) 構成からの初回シード**を採る。

1. `MarketMonitorService.Infrastructure.Persistence.MonitorSeedOptions`（構成節 `Monitor:SeedSymbols`。
   `TradeDecisionService.ConfigurationWatchlistProvider.WatchlistEntry`＝`TradeCycle:Watchlist` と対称の
   構成形式）を追加する。既定は空リストで、構成未投入の環境（本番 `values.yaml` 含む）は
   `MonitorDefaults.CreateSettings()` が従来どおり空でシードする（現行挙動のバイト等価）。
2. `MonitorSettingsRow`（単一行 JSON＋Version・IADR-0012 踏襲）へ **`SeededAt`（構成シードを最後に適用した
   時刻）と `ClearedByUserAt`（利用者が明示的に全削除した時刻）** を追加する。**ドメイン型
   `MarketMonitorSettings` には持たせない**——`GET /monitor/settings` の応答・全置換 PUT の入力型に混ぜると、
   API 契約で直接動かせる値に化ける（`CollectionIntervalNotConfigurableTests`／IADR-0164 決定1 と同型の理由）。
3. `EfMonitoredSymbolStore.GetSettings()`: 行が無ければ構成シードを適用して挿入する。行はあるが
   `MonitoredSymbols` が空 **かつ** `ClearedByUserAt is null` なら「未設定と同視」して構成シードを
   （再）適用する（本機能導入前に空で作られた既存行の後方互換を兼ねる）。構成シードが空ならホットパスで
   無意味な書き込みをしない。それ以外（非空、または `ClearedByUserAt` 設定済み）は触らずそのまま返す。
4. `EfMonitoredSymbolStore.Save()`（`MonitorWatchlistService.Add/Remove` と `MonitorSettingsService.
   UpdateMovementThreshold/UpdateCooldown/Replace` の共通下請け）: 保存前後で非空→空へ遷移したときだけ
   `ClearedByUserAt = now` を立てる。保存後に非空なら解除する（再追加で復活）。空のまま空を保存し直す
   部分更新ではタイムスタンプを上書きしない。**判定をドメイン層（`MonitorWatchlistService`）ではなく
   永続層（`EfMonitoredSymbolStore`）に置く**ことで、DELETE 経路だけでなく全置換 PUT で空にした場合も
   同じ規律で捕捉する（Save が単一のチョークポイント）。
5. `values-local.yaml`: `trade-decision.MarketMonitor__BaseUrl` を `http://market-monitor-service:8080` へ
   結線し、`market-monitor.Monitor__SeedSymbols__0__*` へ `trade-decision.TradeCycle__Watchlist__0__*`
   （AAPL/UnitedStates）と同じ銘柄を投入する。`TradeCycle__Watchlist__0__*` 自体は削除せず、照会不達時の
   フォールバックとして残す。本番 `values.yaml` は無変更（`MarketMonitor__BaseUrl` は引き続き空）。
6. `EfMonitoredSymbolStore.GetSettings()` の「空行の再シード」経路（決定3）も、`row is null` 分岐
   （真の未設定）と同じ規律で `DbUpdateConcurrencyException` を捕捉し読み直す（AI コードレビュー指摘・
   PR #639）。マイグレーション適用直後は既存の空行（`ClearedByUserAt=null`）に対して定時ポーリング・
   HTTP 照会が同時に再シードを試み得るため、非対称に無防備だと一過性の 500 として表面化しうる。
   `WatchlistConfigSeedTests` に「B の競合書き込みが A の追加（MSFT）を消してはならない」という
   否定形のテストを追加し、修正前は実際に未捕捉の例外で失敗することを確認したうえで固定した。

## 理由

- **フラグを永続層に閉じることで、API 契約・監査ログ（`MonitorSettingsChangeLog`）を汚さない。** 「構成
  シードを適用した／全削除の記録がある」は運用上の内部状態であり、SC-02 の画面や `GET /monitor/settings`
  の応答に現れる筋合いのものではない。
- **判定を `Save()` に集約することで、変更経路（DELETE watchlist／全置換 PUT／部分更新）を横断して一貫した
  挙動を保証する。** `MonitorWatchlistService.Remove` だけにフラグ操作を実装すると、全置換 PUT
  （`MonitorSettingsService.Replace`）で空にした場合に取りこぼす。
- **既存行の後方互換**: 本機能導入前に空で作られた行は列追加マイグレーションで `SeededAt`/`ClearedByUserAt`
  が両方 `NULL` になる。この状態を「未設定」と同一視して拾うことで、`Monitor:SeedSymbols` を設定した
  デプロイで実環境の既存行（空のまま放置されていたもの）も遡って救える。
- **IADR-0095 の設計思想（フォールバックさせず権威源側で解決する）を維持したまま**、#286 が指摘した
  「結線すると沈黙する」問題を解消する。フォールバック側（`HttpWatchlistProvider`／
  `ConfigurationWatchlistProvider`）は無改修。

## 結果

- 良い影響:
  - `MarketMonitor__BaseUrl` を結線しても判断対象が減らない（受け入れ基準①）。
  - SC-02 の追加／削除が定時サイクルへそのまま反映される（権威源の GET を無改修で使う。受け入れ基準②）。
  - 照会不達時のフォールバックは無改修のまま維持（受け入れ基準③）。
  - 利用者が明示的に全削除した状態は Pod 再作成・サービス再起動を跨いでも巻き戻らない（受け入れ基準④）。
  - 本番 `values.yaml` はバイト等価。構成未投入の環境（`Monitor:SeedSymbols` 無し）は現行挙動と完全に同じ。
- 悪い影響・トレードオフ:
  - `MonitorSettingsRow` に監視銘柄以外の 2 列が増え、EF マイグレーションが 1 本増える。
  - 「未設定」と「構成シードが空で全削除もされていない」状態が実質的に区別できない
    （どちらも `GetSettings()` の度に構成シード適用を試みる）。実害は無い
    （構成シードが空なら書き込みをしないため副作用ゼロ）が、将来 `SeedSymbols` を後から設定変更した場合、
    既に一度でも Add/Remove された行には効かない（＝「一度でも利用者が触った watchlist は、以後ずっと
    利用者管理」という設計）。
- フォローアップ:
  - 実環境での受け入れ基準 4 件の確認手順は作業仕様書
    `.ai-context/specs/20260902_286_watchlist-config-seed.md` §実環境での確認手順に記載。
    オーケストレータが再デプロイ後に実施する。

## 関連

- Supersedes: なし
- Superseded by: なし
