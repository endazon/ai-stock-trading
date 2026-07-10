---
title: サービス間連携 第一歩（取引判断 IDailyPolicyProvider → 報告書 daily-policy 同期照会）
type: spec
status: review
related_ids: [FR-04, FR-07, ADR-0001, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# 仕様書: 取引判断の確定済み日報方針を報告書サービスから同期照会する

> Issue [#22](https://github.com/endazon/ai-stock-trading/issues/22)（サービス間連携）の第一歩。取引判断（#11）の
> `IDailyPolicyProvider`（現プレースホルダ＝方針なしで取引しない）を、報告書サービス（#14）の `GET /reports/daily-policy`
> を**同期 API で照会**する実装へ差し替える。パイプラインを実際に動かす最短路（確定済み日報方針が入れば取引判断が発注へ進める）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-04（AI 判断は確定済み日報方針の範囲内）、FR-07（確定前・未確定は取引しない）
- アーキ概要: `01_architecture-overview.md`「同期 API 依存（取引判断→検索/認可 等）は…契約（API）として管理する」・「確定済み日報だけを取引判断が参照する」
- ADR: ADR-0001（Database per Service）、ADR-0003（AI は確定済み方針の範囲内）
- 関連 IADR: 本作業で新規 [IADR-0028](../adr/IADR-0028_daily-policy-sync-api.md)（同期 API 方式の選定）
- 対象 Issue: #22（第一歩）

## 方式（IADR-0028）

- **同期 API 照会**を採用する（イベント read model の複製ではない）。ReportService が確定済み日報方針を所有し、取引判断は
  `GET /reports/daily-policy` で照会する。Database per Service を崩さず、可逆（アダプタ差し替えで将来イベント方式へ移行可能）。
- ReportService 不達・未確定（404）・エラー時は `null`（＝取引しない）に倒す（フェイルセーフ・FR-07 の安全既定と一致）。
- 既定では外部照会しない（`Reports:BaseUrl` 未設定なら従来のプレースホルダ＝null）。構成で有効化したときのみ実照会する（安全既定）。

## 対象範囲（取引判断サービス `TradeDecisionService`）

- `IDailyPolicyProvider` を**非同期化**（`Task<DailyPolicy?> GetCurrentAsync(CancellationToken)`）。同期 HTTP 呼び出しを sync-over-async に
  しないため。`TradeDecisionService.DecideAsync` は `await` で取得する。
- Worker: `HttpDailyPolicyProvider`（`HttpClient` で `GET {Reports:BaseUrl}/reports/daily-policy` → `ConfirmedDailyPolicy`(Date/Summary/
  AssumptionsVersion) を `DailyPolicy`(Date/Summary) に写像。404/非 2xx/例外/タイムアウト/不正応答は null＝取引しない・ログ）。
  `Program.cs` のインライン登録（`AddScoped<IDailyPolicyProvider>` ラムダ・専用クラスは設けない）で `Reports:BaseUrl` 未設定/不正 URI→
  プレースホルダ no-op、設定時→Http を**解決時に構成を読んで**選択する。`reports` HttpClient は短いタイムアウト（5s）を設定する。
- `PlaceholderDailyPolicyProvider` は非同期化して残す（安全既定）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋fake HttpMessageHandler＋MassTransit テストハーネス＋WebApplicationFactory）:
- [ ] `HttpDailyPolicyProvider`: 200 応答を `DailyPolicy`(Date/Summary) に写像する（fake handler・実ネットワーク不使用）。
- [ ] 404（未確定）・非 2xx・例外は `null`（取引しない）に倒す。
- [ ] `DailyPolicyProviderFactory`: `Reports:BaseUrl` 未設定は no-op（プレースホルダ）、設定時は Http。
- [ ] 取引判断は確定済み方針が得られたときのみ判断を進める（既存の FR-07 挙動を非同期化後も維持）。
- [ ] 既存テスト（取引判断コア・合流・価格変動結線）を緑に保つ。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] 実 ReportService への同期照会・service-to-service 認証（トークン）付き E2E。

## 対象外（後続）

- service-to-service 認証（`GET /reports/daily-policy` は現状 OwnerOnly。サービス用トークンの取得・付与は platform 統合の後続）。
- `ISizingContextProvider`・市場監視 `IPositionStore`・費用 poller の実データ化（#22 の他ステップ）。キャッシュ・リトライ方針。

## テスト方針

- `HttpDailyPolicyProvider` は fake `HttpMessageHandler`（200/404/500/例外）で写像とフェイルセーフを検証。
- `DailyPolicyProviderFactory` は構成による選択を検証。
- 取引判断の既存テストは `IDailyPolicyProvider` の非同期化に追随（`GetCurrentAsync`）。

## 関連仕様

- 連携先: [20260710_report-confirmation](20260710_report-confirmation.md)（`GET /reports/daily-policy`）
- 実装ADR: [IADR-0028](../adr/IADR-0028_daily-policy-sync-api.md)

## 未決事項

- service-to-service 認証・他プレースホルダの実データ化・キャッシュ/リトライは #22 の後続ステップで確定する。
