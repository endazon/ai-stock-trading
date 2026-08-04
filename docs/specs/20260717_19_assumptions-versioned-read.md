---
title: バージョン付き全体前提条件を各サービスが解決できるようにする（s2s 読み取り＋共有クライアント）— Issue #19 Slice B
type: spec
status: draft
related_ids:
  - FR-17
  - FR-13
  - UC-06
  - ADR-0007
  - IADR-0021
  - IADR-0051
  - IADR-0063
author: claude
created: 2026-07-17
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-17: 全体前提条件の一元管理)"
  - "../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md (全体前提条件)"
  - "../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06: 設定変更・取引の一時停止・緊急停止)"
related_specs:
  - "20260710_configuration-assumptions.md（#19 Slice A・設定サービス本体）"
  - "../adr/IADR-0021_trading-assumptions-configuration.md（設定サービスが前提条件を所有する決定）"
  - "../adr/IADR-0051_service-to-service-auth.md（s2s 認証・読み取りは OwnerOrService）"
  - "../adr/IADR-0063_assumptions-versioned-resolution.md（本スライスの決定）"
---

# 仕様書: バージョン付き全体前提条件の解決基盤（Issue #19 Slice B）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-17**（全体前提条件を設定として一元管理し、バージョン管理し、各報告書に適用バージョンを記録する）／FR-13（設定変更）
- ユースケース: **UC-06**（設定変更）／ADR-0007（変更は利用者のみ）
- Issue: [#19](https://github.com/endazon/ai-stock-trading/issues/19)（Slice B）。後続 [#139](https://github.com/endazon/ai-stock-trading/issues/139) の前提。

## 目的・背景

#19 Slice A（PR #66・IADR-0021）で `ConfigurationService` が全体前提条件を所有し、バージョン管理・変更履歴・
利用者のみ変更（OwnerOnly）・`AssumptionsChanged` 発行までを実装した。受け入れ基準
「前提条件の変更でバージョンが上がり、履歴と通知が残る。AI・自動処理からは変更できない」は充足済み。

しかし **前提条件を「共通参照」する仕組みが無い**。IADR-0021 が「損益集計（報告書）・AI 判断の採算評価（取引判断）・
費用込み上限判定（リスク管理）が共通参照する単一の真実源」と述べているにもかかわらず、`GET /assumptions` は
**すべて OwnerOnly** のため、サービストークン（`trading-service`・IADR-0051）で呼ぶと **403** になる。
結果として各消費側は前提条件を読めず、既定値のハードコードに留まっている:

- `CostControlService` の `DefaultCostLimitsProvider` → `TradingAssumptionsDefaults.Create().CostLimits` を返すのみ
  （**利用者が上限を変更しても費用統制に反映されない**。これが #139 の起点）。

本スライスは、この「**バージョン付き前提条件の取得・解決**」を基盤として実装する。#139（`DefaultCostLimitsProvider`
置換）は本基盤の上に載るだけで済む形にする。

## 対象範囲

**対象（本 PR）**

1. `ConfigurationService`: `GET /assumptions`（現在値＋Version）を **`OwnerOrService`** へ分離する（IADR-0051 の既存
   パターンと同形）。**`PUT /assumptions`（更新）・`GET /assumptions/history`（履歴）は `OwnerOnly` 据え置き**
   ＝サービスに変更権限・履歴閲覧権限を与えない（最小権限。ADR-0007「AI・自動処理は変更できない」を維持）。
2. 共有クライアント **`ConfigurationService.Client`**（新規プロジェクト）: どの消費側サービスからも
   `AddAiStockTradingAssumptions(configuration)` の 1 行で配線できる、バージョン付き前提条件の解決器。
   - `IAssumptionsProvider.GetCurrentAsync()` → `VersionedAssumptions`（前提条件＋Version）
   - HTTP 取得（s2s トークン付与）＋ キャッシュ ＋ `AssumptionsChanged` 購読による無効化 ＋ fail-safe
3. `VersionedAssumptions` を `ConfigurationService.Application.State` から `ConfigurationService.Domain` へ移す
   （消費側が Application 層に依存せず参照できるようにするため。既存の `CostControlService.Domain` →
   `ConfigurationService.Domain` 参照の前例に沿う）。

**対象外（後続）**

- **`DefaultCostLimitsProvider` の置換**（費用統制のしきい値判定へバージョン付き上限を適用する）＝ **#139**。
  本 PR は「読める・解決できる」までで、消費側の差し替えは行わない。
- FR-13 の監視銘柄・変動閾値・収集間隔（各サービス所管の設定・別 issue）。
- `CostCalculator` の 3 サービス（損益計算・AI 判断・リスク統制）からの実利用配線。
- 手数料・為替スプレッドの実額登録（口座開設後の運用）。
- 実 Keycloak 往復での s2s E2E（IADR-0051 決定 6 と同じく #82 の E2E に委ねる）。

## 設計

詳細は [IADR-0063](../adr/IADR-0063_assumptions-versioned-resolution.md)。要点:

- **認可**: 認可は親グループに付けず read/owner のサブグループで指定する（親に付けると合成され、サービストークンが
  403 になる。IADR-0051 の実装上の注意）。既存の `AssumptionsEndpoints` は**親に OwnerOnly が付いている**ため、
  これを外して owner サブグループへ移す。
- **キャッシュと無効化**: 取得成功値を保持し、`AssumptionsChanged`（Version つき）購読で無効化する。
  イベント取りこぼしに備え TTL（既定 5 分）でも失効させる。
- **fail-safe（優先順）**: ① 取得成功 → 最新値 ② 取得失敗だが過去に成功 → **last known good（陳腐化値）**
  ③ 一度も取得できていない → `TradingAssumptionsDefaults.Create()`（`Version = 0` ＝未解決の番兵）。
  ②が③に優先するのは、利用者が既定より**厳しい**上限へ変更していた場合に既定へ戻すと**緩む側**へ倒れるため。
- **BaseUrl 未設定なら HTTP を構築しない**（`DefaultAssumptionsProvider` ＝既定値のみ）。既定ビルド/CI は外部接続なしで緑。

## 受け入れ基準 → テスト写像

| 受け入れ基準 | テスト |
| --- | --- |
| サービストークンで現在値＋Version を読める | `AssumptionsEndpointsTests.サービストークンは現在値を読める` |
| サービストークンで更新・履歴は読めない（403） | `AssumptionsEndpointsTests.サービストークンは更新と履歴を拒否される` |
| 未認証は 401・無関係ロールは 403（現行維持） | `AssumptionsEndpointsTests.未認証は_401_ロール無しは_403` |
| 取得成功で前提条件と Version が解決される | `CachedAssumptionsProviderTests.初回は取得して返す` |
| 2 回目はキャッシュから返す（HTTP を叩かない） | `CachedAssumptionsProviderTests.二回目はキャッシュから返す` |
| 版が上がったら追随する（イベント無効化） | `CachedAssumptionsProviderTests.無効化後は再取得して新しい版を返す` / `AssumptionsChangedConsumerTests.前提条件の変更でキャッシュを無効化する` |
| 取得中に届いた変更を取りこぼさない | `CachedAssumptionsProviderTests.取得中に届いた変更は取りこぼさない` |
| TTL 経過で再取得する（イベント取りこぼし対策） | `CachedAssumptionsProviderTests.TTL経過で再取得する` |
| 取得不可かつ未取得なら既定へ倒れる | `CachedAssumptionsProviderTests.一度も取得できなければ既定へ倒れる` |
| 取得不可だが取得済みなら last known good | `CachedAssumptionsProviderTests.取得失敗時は既定ではなく最後の成功値を返す` |
| 障害復旧後は最新版へ戻る | `CachedAssumptionsProviderTests.障害復旧後は最新版へ戻る` |
| 非 2xx・不正応答・タイムアウトで例外を出さない | `HttpAssumptionsClientTests`（401/500/不正 JSON/タイムアウト） |
| BaseUrl 未設定なら HTTP を構築しない | `AssumptionsClientRegistrationTests.BaseUrl未設定なら既定プロバイダ` |

## リスク・留意

- `ICostLimitsProvider.GetLimits()` は**同期**のため、#139 は本 provider（async）に合わせて非同期化が必要になる
  （呼び出し元のエンドポイントは既に async）。本 PR では `ICostLimitsProvider` に触れない。
- 前提条件は機微情報ではない（税率・手数料体系・費用上限）ため、読み取りをサービスへ開放しても ADR-0007 の
  「変更は利用者のみ」に反しない。履歴（アクター・理由）は運用情報のため OwnerOnly を維持する。
