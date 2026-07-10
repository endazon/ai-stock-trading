---
title: IADR-0028 取引判断は確定済み日報方針を報告書サービスから同期 API で照会する
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-07, ADR-0001, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0028: 取引判断は確定済み日報方針を報告書サービスから同期 API で照会する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（確定済み方針の範囲内で判断）、FR-07（未確定は取引しない）、ADR-0001（Database per Service）、ADR-0003
- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)（サービス間連携・第一歩）
- 関連する実装仕様書: [20260710_daily-policy-wiring](../specs/20260710_daily-policy-wiring.md)
- 関連 IADR: [IADR-0024](IADR-0024_report-confirmation-and-policy.md)（報告書が方針を所有・`GET /reports/daily-policy`）、[IADR-0017](IADR-0017_trade-decision-structure.md)（`IDailyPolicyProvider`）

## コンテキストと課題

取引判断は「確定済み日報の方針」の範囲内でのみ判断する（ADR-0003・FR-04/07）。現状 `IDailyPolicyProvider` はプレースホルダで
常に null（取引しない）を返し、報告書サービス（#14・確定済み日報方針を所有）と結線されていない。取引判断がどの方式で確定済み
日報方針を得るか（サービス間連携の方式）を決める必要がある。Database per Service を崩さず、可逆な設計にしたい。

## 検討した選択肢

1. **同期 API 照会（採用）** — 取引判断が報告書サービスの `GET /reports/daily-policy` を実行時に同期照会する。報告書サービスが
   方針を所有し、取引判断は参照するだけ（Database per Service を保つ）。アーキ概要「同期 API 依存（取引判断→検索/認可 等）は…
   契約（API）として管理する」に一致。
2. **イベント駆動 read model** — 取引判断が `ReportConfirmed` を購読して確定済み方針の複製を自サービス DB に保持する。実行時に
   報告書サービスに依存しないが、方針の状態を 2 箇所に複製し（結果整合）、取引判断に永続層を足す必要がある。
3. **共有 DB/直接参照** — Database per Service に反する（不採用）。

## 決定

**選択肢 1（同期 API 照会）** を採用する。

- 取引判断の `IDailyPolicyProvider` を、報告書サービスの `GET /reports/daily-policy` を `HttpClient` で照会する実装
  （`HttpDailyPolicyProvider`）に差し替える。`ConfirmedDailyPolicy`(Date/Summary/AssumptionsVersion) を `DailyPolicy`(Date/Summary) に写像する。
- **ポートを非同期化**（`Task<DailyPolicy?> GetCurrentAsync`）。同期 HTTP を sync-over-async にしないため。
- **フェイルセーフ**: 報告書サービス不達・未確定（404）・非 2xx・例外は `null`（＝取引しない）に倒す。FR-07 の安全既定（未確定なら
  取引しない）と一致し、依存先障害時も安全側に倒れる。
- **安全既定でゲート**: `Reports:BaseUrl` 未設定なら従来のプレースホルダ（no-op・null）を用い、構成で有効化したときのみ実照会する。
- **可逆性**: 方式はアダプタ（`IDailyPolicyProvider` 実装）に閉じており、将来イベント read model へ移行する場合もポート実装の差し替えで済む。
- **service-to-service 認証**（`/reports/daily-policy` は現状 OwnerOnly）は platform 統合の後続で結線する（本スライスはアダプタと写像・
  フェイルセーフを実装し、fake HttpMessageHandler で検証する）。

## 理由

- アーキ概要が同期 API 依存を契約として管理する方針を明記しており、方針の単一所有（報告書サービス）と Database per Service を保てる。
- イベント read model は状態複製・永続層追加のコストがあり、方針という「単一の確定済み値」を照会する用途には過大。障害時フェイルセーフも
  同期照会の方が単純（未取得＝取引しない）。
- アダプタ実装に閉じているため可逆（イベント方式への移行余地を残す）。

## 結果

- 良い影響: 確定済み日報方針が取引判断に供給され、パイプラインが発注へ進める土台ができる。障害時は取引しない安全側。
- 悪い影響・トレードオフ: 実行時に報告書サービスへ同期依存する（可用性 NFR：不達時は取引停止＝安全側）。service-to-service 認証・
  キャッシュ/リトライは後続。頻繁な照会はキャッシュで最適化する余地（後続）。
- フォローアップ: service-to-service 認証（サービストークン）、キャッシュ/リトライ、`ISizingContextProvider`・`IPositionStore`・費用 poller の
  実データ化（#22 の他ステップ）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0024](IADR-0024_report-confirmation-and-policy.md)（方針の所有・照会 API）
