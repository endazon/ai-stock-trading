---
title: 費用統制の月次上限をバージョン付き前提条件から取得する（DefaultCostLimitsProvider 置換）— Issue #139
type: spec
status: review
related_ids:
  - NFR
  - FR-17
  - UC-06
  - IADR-0027
  - IADR-0051
  - IADR-0063
  - IADR-0065
author: claude
created: 2026-07-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR: 費用統制 / FR-17: 全体前提条件の一元管理)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§6 月次費用上限 20,000円/月)
related_specs:
  - "20260717_19_assumptions-versioned-read.md（#19 Slice B・本 issue の前提基盤）"
  - "../adr/IADR-0065_versioned-cost-limits-resolution.md（本作業の決定）"
  - "../adr/IADR-0063_assumptions-versioned-resolution.md（前提条件の解決基盤・fail-safe の向き）"
  - "../adr/IADR-0027_cost-control.md（費用統制・上限供給は暫定と留保していた）"
---

# 仕様書: 費用統制の月次上限のバージョン付き取得（Issue #139）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR（費用統制）** — 月次費用上限に対する 80% 間隔延長 / 100% 停止
- 機能要求: **FR-17**（全体前提条件を設定として一元管理し、バージョン管理する）／UC-06（設定変更）
- Issue: [#139](https://github.com/endazon/ai-stock-trading/issues/139)（[#19](https://github.com/endazon/ai-stock-trading/issues/19) の明示的後続。[#79](https://github.com/endazon/ai-stock-trading/issues/79) から分離された残り 1 スコープ）

## 目的・背景

`CostControlService` の月次費用上限は IADR-0027 以来、暫定で既定値ハードコード（`DefaultCostLimitsProvider` →
`TradingAssumptionsDefaults.Create().CostLimits`）から供給しており、**利用者が設定サービスで上限を変更しても
費用統制のしきい値判定に反映されない**。

#19 Slice B（IADR-0063）が解決基盤を整備済みである。本 issue は**費用統制をその最初の消費者として配線する**。
新たな機構は作らない — 特に「版の追随」「last known good への fail-safe」は基盤側で既に解かれており、
消費側で解き直すとレースを再導入するため、**繋ぐことに徹する**（IADR-0065 決定 3/4）。

## 対象範囲

**対象（本 PR）**

1. `ICostLimitsProvider` の非同期化（`GetLimits()` → `GetLimitsAsync(CancellationToken)`）と、
   `CostControlService.Record`/`GetLlmState` への波及。
2. `AssumptionsCostLimitsProvider`（新規・`Worker/Composable/Adapters/`）: `IAssumptionsProvider` へ委譲する薄いアダプタ。
3. `CostControlService.Worker/Program.cs` の配線: `AddAiStockTradingAssumptions` ＋ `AddConsumer<AssumptionsChangedConsumer>`。
4. `DefaultCostLimitsProvider` の維持（非同期化のみ）＝ Application 層の外部依存なし既定アダプタ。
   `Configuration:BaseUrl` 未設定時に既定値へ倒す判断は共有クライアントへ一本化する（IADR-0065 決定 5）。
5. 上記の呼び出し元追随（`CostControlEndpoints`・`LlmCostIncurredConsumer`）。

**対象外（後続）**

- **実 ConfigurationService・実 Keycloak を跨いだ往復の検証** ＝ **#82 の E2E**（IADR-0063 決定 6 と同方針）。
  本 PR の単体テストは `IAssumptionsProvider` の偽物で閉じ、実基盤に依存しない。
- 前提条件の他の消費側（損益集計・AI 判断の採算評価・リスク統制の費用込み上限判定）の配線。
- 総額上限（`MonthlyCostLimits.Total`/`Infrastructure`/`Data`）による統制 — 現状 LLM のみが統制対象（IADR-0027 の範囲）。
- `ConfigurationService.Client` 自体の変更（基盤は #19 で完成済み・本 PR は消費するのみ）。

## 設計

詳細は [IADR-0065](../adr/IADR-0065_versioned-cost-limits-resolution.md)。要点:

- **非同期化**（決定 1）: `IAssumptionsProvider` が async のため、同期ポートのままだと `.Result` でブロックすることになる。
  費用計上は全 LLM 呼び出しの後段にあり流量がある経路のため、スレッドプール占有を避けて**ポート側を非同期へ寄せる**。
  呼び出し元（エンドポイント・Consumer）は既に async なので波及は表層で止まる。`Review` は上限を読まないため同期据え置き。
- **fail-safe の向き**（決定 3・IADR-0063 決定 5 の継承）: ① 最新値 ② last known good ③ 既定値。
  ②が③に優先するのは、利用者が上限を **15,000 → 5,000 に絞っていた**場合に既定へ戻すと**緩む＝浪費側**へ倒れるため。
- **版の追随**（決定 4）: `AssumptionsChanged` 購読 → `CachedAssumptionsProvider.Invalidate()` → 次回参照で再取得。
  **費用統制側にキャッシュを持たない**（二重キャッシュは無効化の届かない層を生み、版の取りこぼしが再発する）。
- **認可**: `GET /assumptions` は `OwnerOrService`（#19 で分離済み）。s2s トークンは `AddAiStockTradingServiceToken`
  が付与する（IADR-0051）。費用統制側の認可（`/costs/*` の Owner/Service サブグループ分離）には触れない。

## 受け入れ基準 → テスト写像

| Issue #139 の受け入れ基準 | テスト |
| --- | --- |
| 設定サービスで月次上限を変更すると、しきい値判定（80% 間隔延長）に反映される | `VersionedCostLimitsTests.設定サービスの上限がしきい値判定に反映される` |
| 同上（100% 停止の側） | `VersionedCostLimitsTests.設定サービスで上限を絞ると停止判定も追随する` |
| 上限変更はしきい値判定へ双方向に効く（純ドメイン側） | `CostControlServiceTests.上限が絞られるとしきい値判定に反映される` / `CostControlServiceTests.上限が緩められるとしきい値判定に反映される` |
| 取得不可・未設定時は既定値へ倒れる（現行挙動を壊さない） | `VersionedCostLimitsTests.一度も取得できなければ既定の上限へ倒れる` / `DefaultCostLimitsProviderTests.前提条件の既定の上限を返す` |
| 取得不可でも既定へ戻さず last known good を保つ（緩む側へ倒れない） | `VersionedCostLimitsTests.取得失敗時は既定へ戻さず最後に取得した上限を保つ` / `VersionedCostLimitsTests.障害時も絞られた上限で停止判定を保つ` |
| バージョン付き前提条件の版が上がったときに追随する（キャッシュ無効化を含む） | `VersionedCostLimitsTests.版が上がると新しい上限に追随する` |
| 計上のたびに設定サービスを叩かない（キャッシュが効く） | `VersionedCostLimitsTests.上限の取得はキャッシュされ計上のたびに照会しない` |
| 費用統制ホストが購読とプロバイダを配線している | `CostControlWiringTests.費用統制は前提条件の変更を購読する` / `CostControlWiringTests.月次上限はバージョン付き前提条件から供給される` / `CostControlWiringTests.BaseUrl未設定なら外部接続せず既定の上限へ倒れる` |

## リスク・留意

- `ICostLimitsProvider`/`CostControlService` の公開シグネチャ変更は本リポ内に閉じる（外部 API・イベント契約は不変）。
- 新イベントを追加しないため監査 Consumer の追加は不要（`AssumptionsChanged` は #17 が既に購読・記録済み）。
- DB マイグレーション不要（永続化スキーマに変更なし）。
- キャッシュミス時のみ費用計上経路に HTTP 往復（5 秒タイムアウト）が入る。TTL は既定 5 分。
