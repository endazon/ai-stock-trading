---
title: IADR-0021 全体前提条件は専用の設定サービスが所有し、バージョン管理・変更履歴・イベント発行で一元管理する
type: impl-adr
status: Accepted
related_ids: [FR-17, FR-13, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# IADR-0021: 全体前提条件は専用の設定サービスが所有し、バージョン管理・変更履歴・イベント発行で一元管理する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-17（全体前提条件の一元管理・バージョン管理）、FR-13（設定変更）、ADR-0001
- 対象 Issue: [#19](https://github.com/endazon/ai-stock-trading/issues/19)（Slice A）
- 関連する実装仕様書: [20260710_configuration-assumptions](../specs/20260710_configuration-assumptions.md)
- 関連 IADR: [IADR-0012](IADR-0012_risk-settings-persistence.md)（単一行 JSON＋Version 楽観排他・踏襲）、[IADR-0020](IADR-0020_notification-safe-outbound.md)（変更通知の購読先）

## コンテキストと課題

全体前提条件（税金・手数料・為替・計算方針・月次費用上限。05_trading-assumptions）は、損益集計（報告書）・AI 判断の採算
評価（取引判断）・費用込み上限判定（リスク管理）が**共通参照**する横断設定である（アーキ概要「全体前提条件の一元管理」）。
これをどこが所有し、どうバージョン管理・変更履歴・利用者変更・変更通知を実現するかを決める必要がある。既存の
`RiskManagementSettings`（ガード・上限・段階）はリスク管理固有の設定であり、前提条件とは別集約。

## 検討した選択肢

1. **既存のリスク管理設定ストアに相乗りさせる** — 前提条件を RiskManagement が持つと、報告書・取引判断が RiskManagement に
   依存してしまい、横断設定の一元管理（アーキ概要）に反する。集約境界も混ざる。
2. **各サービスが個別に前提条件を持つ** — 一元管理・単一の真実源にならず、バージョン不整合・重複のリスク。
3. **専用の設定サービス（ConfigurationService）が前提条件を所有し、専有 DB でバージョン管理・変更履歴を持ち、変更を
   イベント発行する（採用）** — 単一の真実源で一元管理でき、Database per Service に適合。共通参照は照会 API、変更通知は
   イベントで疎結合に実現できる。

## 決定

**選択肢 3** を採用する。

- **新規サービス `ConfigurationService`**（Domain + Application + Worker）が `TradingAssumptions`（前提条件集約）を所有する。
- **永続化・バージョニングは [IADR-0012] を踏襲**: 単一行 JSON＋`Version` 列の楽観的排他制御（EF 並行トークン）、未設定は
  既定シード（レース窓は再読で冪等化）、変更履歴は追記専用。専有 DB `configuration_svc`。
- **変更は利用者のみ**（FR-17）: `AssumptionsService.Update` はアクター・理由必須、`Version` 増分、前後値つきで履歴記録。
  ホストのエンドポイントは OwnerOnly（Keycloak `trading-owner`）。AI・自動処理はロールを持たず変更できない。
- **概算費用関数 `CostCalculator`**（05 §4）は Domain の純関数とする: 手数料（市場別 `CommissionSchedule`＝定率・最低額・上限クランプ）
  ＋為替スプレッド（非 JPY 市場に約定代金比で適用）。為替スプレッドは 05 が「円/USD」とするが、実 FX レート連携が未整備の
  現段階では**約定代金比の率（`FxSpreadRatio`）**で近似する（判断時の事前見積り用途。実レート連携は後続）。
- **手数料・為替スプレッドの既定は 0（未登録）**とする（05 §2/§3「数値は固定せず設定値として保持・口座開設後に登録」）。
  譲渡益税率 20.315%・月次費用上限（総額20,000/LLM15,000/インフラ5,000/データ0）・最小期待利益倍率 1.5 は確定値を既定にする。

> **【❌ 訂正 2026-08-07・#358 / [IADR-0173](./IADR-0173_minimum-expected-profit-tax-inclusive.md)】** 最小期待利益倍率 **1.5 は確定値ではなかった**。起案当時（2026-07-18）の計画は未確定の「&lt;1.5 倍&gt;」であり、**計画は 2026-07-23 の利用者決定で「往復費用＋税の 2 倍」へ確定した**。本 ADR の「確定値を既定にする」という記述は、**倍率については誤りである**。現行値は **2** であり、**基準も往復費用のみではなく「往復費用＋税」**である（税は譲渡益に掛かるため、しきい値は不動点として解く）。詳細は [IADR-0173](./IADR-0173_minimum-expected-profit-tax-inclusive.md) を正とする。
- **変更通知はイベント駆動**: 更新時に `AssumptionsChanged(Version,Actor,Reason,ChangedAt)` を発行し、通知サービスが購読して
  Discord 通知する（各サービスは Discord を直接呼ばない・IADR-0020）。

## 理由

- 横断設定を単一の真実源に集約でき、共通参照（API）と変更通知（イベント）を疎結合に実現できる。
- バージョニング・履歴・利用者変更は既存の実績パターン（IADR-0012・FR-19 のガード設定と同型）を踏襲でき、実装・レビューが容易。
- 費用関数を純関数の Domain に置くことで、将来 3 サービスが同一ロジックで採算評価・費用込み判定を行える基盤になる。

## 結果

- 良い影響: 前提条件のバージョン管理・履歴・利用者変更・変更通知が満たせる。報告書は将来 version を凍結参照できる（API 公開）。
- 悪い影響・トレードオフ: 手数料・為替スプレッドが未登録（0）の間は概算費用が過小になる（AI の採算ガードが甘くなる）。利用者の
  実額登録で解消する。為替スプレッドの率近似は実 FX レート連携までの暫定。損益/AI判断/リスク統制からの費用関数の実利用配線は後続。
- フォローアップ: 実額登録の運用、費用関数の 3 サービス実利用、報告書の `assumptions_version` 凍結（#14）、実レート連携・税精緻化（FR-18）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0012](IADR-0012_risk-settings-persistence.md)（踏襲）、[IADR-0020](IADR-0020_notification-safe-outbound.md)（変更通知の購読）
