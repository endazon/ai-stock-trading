---
title: 報告書サービス 対話的確定ロジック・取引履歴明細レンダリング（fake データ）
type: spec
status: review
related_ids: [FR-06, FR-07, FR-16, UC-03, UC-04, UC-05, ADR-0003, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/06_technical/07_discord-bot-design.md
  - ../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
---

# 仕様書: 報告書サービス 対話的確定ロジック・取引履歴明細レンダリング

> Issue [#14](https://github.com/endazon/ai-stock-trading/issues/14)（FR-06/07/16/17・Must）の一部スライス。既存の Slice A（確定管理・[IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md)）と
> ドラフト生成（[IADR-0032](../adr/IADR-0032_report-generation.md)）に続けて、**(1) 対話的確定の状態遷移ロジック（承認/差し戻し/改訂）** と
> **(2) 取引履歴（全明細）＋取引詳細のレンダリング** を **fake データ＋テスト**で実装する。実データ連携（#63 台帳・実 LLM・
> Discord/チャットUI 結線）は後続として切り分け、本スライスでは純関数ドメインとして CI 緑にする。#14 はクローズしない（残スライスあり）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-07（対話的確定・確定前は不適用）、FR-06（階層方針）、FR-16（テンプレート準拠・数値はコード集計）
- ユースケース（UC）: UC-03〜05（報告書の確定）
- 技術検討:
  - `06_technical/07_discord-bot-design.md`（版番号付き冪等確定・二重実行防止・高リスク操作の確認ステップ・対話文脈は報告書サービスに一元保持・
    シーケンス「ドラフト提示→修正指示→改訂 v2→承認→確定」）
  - `06_technical/04_report-templates.md`（日報 §2 取引履歴（全明細）表・取引詳細ブロック・見送り判断）
  - `04_workflows/03_reporting-cycle.md`（確定で方針有効化）
- ADR: ADR-0003（確定前方針は不適用）、ADR-0007（確定は利用者のみ）
- 関連 IADR: 本作業で新規 [IADR-0037](../adr/IADR-0037_report-review-state-machine-and-detail-rendering.md)。踏襲 [IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md)（版番号付き冪等確定）・[IADR-0032](../adr/IADR-0032_report-generation.md)（純関数テンプレート化）
- 対象 Issue: #14（対話的確定ロジック・明細レンダリングのスライス）

## 目的・背景

Slice A は `TradingReport` の `Draft`/`Confirmed` 二状態と版番号付き冪等確定（`ReportService.Confirm`）までを実装した。しかし FR-07 の
**「対話的確定」** は、07_discord-bot-design のシーケンス（ドラフト提示 → 利用者の修正指示 → 改訂 → 承認 → 確定）にあるとおり、
提示・差し戻し・改訂・承認という**複数状態の遷移**を持つ。この遷移ロジックが未実装で、Discord/チャットUI から確定操作を駆動する
土台が無い。また 04_report-templates 日報 §2 の **取引履歴（全明細）＋取引詳細** のレンダリングは IADR-0032 で明示的に後続とされた。

本スライスはこの2点を、実データ非依存の**純関数ドメイン**として fake データ＋テストで実装する。Discord/チャットUI・HTTP 結線・
#63 台帳の実約定連携は後続に切り分ける。

## 対象範囲

### (1) 対話的確定の状態遷移ロジック（`ReportService.Domain`・純関数）

対話的確定の**レビュー状態**（既存の永続 `ReportState.Draft/Confirmed` を、Draft 局面のサブ状態へ精緻化した対話ライフサイクル）を導入する。

- `ReviewState`（enum）: `Drafting`（作成/改訂中）→ `PendingApproval`（提示済み・承認待ち）→ `Confirmed`（確定・終端）。
  差し戻し時は `ChangesRequested`（修正指示受領・改訂待ち）。`Confirmed` は `ReportState.Confirmed` に対応し、それ以外は `ReportState.Draft` に対応する。
- `ReportReview`（record）: `PeriodKey` / `State` / `Version`（版番号・楽観排他）の不変スナップショット。
- `ReviewAction`（enum）: `Present`（提示）/ `RequestChanges`（差し戻し）/ `Revise`（改訂＝新ドラフト）/ `Approve`（承認＝確定）。
- `ReviewCommand`（record）: `Action` / `Actor`（操作者・OwnerOnly）/ `ExpectedVersion`（楽観排他）。
- `ReportReviewStateMachine.Decide(ReportReview, ReviewCommand)` → `ReviewDecision(Review, Transitioned, Rejection?)`（純関数・決定的）。

**遷移表**（正常系）:

| 現在状態 | Present | RequestChanges | Revise | Approve |
| --- | --- | --- | --- | --- |
| Drafting | → PendingApproval | 不可 | → Drafting（版+1） | 不可 |
| PendingApproval | 冪等（変化なし） | → ChangesRequested | → Drafting（版+1） | → Confirmed（版+1） |
| ChangesRequested | → PendingApproval | 冪等（変化なし） | → Drafting（版+1） | 不可 |
| Confirmed | 不可 | 不可 | 不可 | 冪等（変化なし・再確定） |

**ガード（不変条件・すべて決定的）**:

- **操作者必須**（OwnerOnly・ADR-0007）: `Actor` が空なら `ActorRequired` で拒否。実際の認証/認可（未認証 401・ロール無し 403）は Worker/HTTP 層の後続結線で担う。
- **版番号の楽観排他**（07_discord-bot-design 二重実行防止）: 状態を変える操作は `ExpectedVersion == 現在の Version` を要求。古い版は `VersionConflict` で拒否（「最新ドラフトを確認してください」）。
- **冪等**: `Approve` 済み（Confirmed）への同版 `Approve` は冪等（`Transitioned=false`・拒否ではない）。同様に提示済みへの `Present`、差し戻し済みへの `RequestChanges` も冪等。
- **終端不変**: `Confirmed` からの `Present`/`RequestChanges`/`Revise` は `AlreadyConfirmed` で拒否（確定済みは不変・ADR-0003）。
- **不正遷移**: 上表「不可」は `InvalidTransition` で拒否。

`ReviewRejectionReason`（enum）: `ActorRequired` / `VersionConflict` / `InvalidTransition` / `AlreadyConfirmed`。拒否時は状態不変。

### (2) 取引履歴明細レンダリング（`ReportService.Domain`・純関数）

04_report-templates 日報 §2「取引履歴（全明細）」表・「取引詳細（選定・売買の判断理由）」ブロック・「見送り判断」を決定的に Markdown 生成する。

- 明細行 `TradeHistoryLine`: `#` / 時刻 / 市場 / 銘柄（コード＋名称）/ 売買 / 数量 / 約定単価 / 手数料・費用 / 税 / 実現損益 / トリガー / 判断根拠（要約）。
- 取引詳細 `TradeDetailBlock`: `#### #n HH:MM 銘柄 買/売` ＋ 銘柄選定の理由 / 売買判断の理由 / 参照した情報 / 想定シナリオ / 結果と評価。
- 見送り判断 `SkippedDecision`: 時刻 / 銘柄 / 理由。
- `TradeTrigger`（enum）: `Scheduled`（定時）/ `PriceMovement`（変動）/ `StopLoss`（損切り）。
- `TradeHistoryView`（record）: `Lines` / `Details` / `Skipped`。
- `TradeHistoryRenderer.RenderMarkdown(TradeHistoryView)` → `## 2. 取引履歴（全明細）` セクション（表＋取引詳細＋見送り判断）を純関数生成。
- 約定・見送りが無い日は決定的なプレースホルダ（「（当日の約定なし）」「（見送りなし）」）で形式を保つ。
- 市場（`Market`）・売買（`TradeSide`）は既存の `AiStockTrading.Shared.Contracts.Trading` を再利用（`PeriodTradeFill` と整合）。

### fake データ

実データ源（#63 台帳の実約定・実 LLM の取引詳細文）は本スライス対象外。fake データは**テスト内**（`ReportService.Domain.Tests`）に置き、
本番コードに fake を持ち込まない（IADR-0032 の「ポート抽象＋テスト fake」方針と整合）。#63 台帳→明細への写像・実 LLM 取引詳細・
Discord/チャットUI からの状態遷移駆動は後続。

## 受け入れ基準

CI で緑にする範囲（ユニット・純関数）:
- [ ] 状態遷移: `Drafting→Present→PendingApproval→Approve→Confirmed` の正常系で版番号が上がり `Transitioned=true`。
- [ ] 差し戻し・改訂: `PendingApproval→RequestChanges→ChangesRequested→Revise→Drafting`（改訂で版+1）が成立し、再 `Present`→`Approve` で確定できる。
- [ ] 冪等: 確定済みへの同版 `Approve` は `Transitioned=false`（副作用なし・拒否ではない）。
- [ ] 版排他: 古い `ExpectedVersion` の状態変更は `VersionConflict` で拒否し状態不変。
- [ ] OwnerOnly: `Actor` 空の操作は `ActorRequired` で拒否。
- [ ] 終端不変: 確定済みからの `Present/RequestChanges/Revise` は `AlreadyConfirmed` で拒否。
- [ ] 不正遷移: `Drafting` からの `Approve` 等は `InvalidTransition` で拒否。
- [ ] 明細レンダリング: 04_report-templates 日報 §2 の表ヘッダ・各行・取引詳細ブロック・見送り判断が定義どおり生成される。
- [ ] 空データ: 約定/見送りが無い日はプレースホルダで形式が保たれる。
- [ ] 既存テスト（確定管理・ドラフト生成・レンダリング）を緑に保つ。

## 対象外（後続）

- #63 取引台帳の実約定 → `TradeHistoryView` への写像、実 LLM による取引詳細文生成。
- 対話的確定の HTTP/Discord/チャットUI 結線（`ReportService.Worker` エンドポイント・通知サービス Bot）。未認証 401・ロール無し 403 の実認可。
- 無応答時の既定動作（翌営業日まで直近確定日報方針を継続）・KB 保存（FR-08・#18）・階層参照の強制。
- ポジション一覧・リスク統制・市況セクション（#12/#63 連携）と、明細セクションの `ReportRenderer` 本文への合流。

## テスト方針

- `ReportReviewStateMachine` は fake の `ReportReview` を出発点に、各遷移・冪等・版排他・OwnerOnly・終端不変・不正遷移を `[Fact]`/`[Theory]` で網羅。
- `TradeHistoryRenderer` は fake の `TradeHistoryView`（複数明細・取引詳細・見送り・空データ）でテンプレート準拠を検証。
- いずれも純関数のため実行時基盤・DB・ネットワーク非依存（PlatformShim は test-only 配置を維持）。

## 関連仕様

- 前提: [20260710_report-confirmation](20260710_report-confirmation.md)（Slice A・確定管理）、[20260711_report-generation](20260711_report-generation.md)（ドラフト生成・テンプレート化）
- 実装ADR: 新規 [IADR-0037](../adr/IADR-0037_report-review-state-machine-and-detail-rendering.md)、踏襲 [IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md) / [IADR-0032](../adr/IADR-0032_report-generation.md)

## 未決事項

- レビュー状態（`ReviewState`）を永続 `ReportState` に統合するか別テーブルで持つかは HTTP/永続結線スライスで確定する（本スライスは純関数ドメインに限定）。
- 取引詳細文の LLM 生成（数値は明細のコード値、文章のみ LLM）のポート形は実 LLM 結線スライスで確定する。
