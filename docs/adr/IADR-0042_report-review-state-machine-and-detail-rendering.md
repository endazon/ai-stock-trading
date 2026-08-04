---
title: IADR-0042 対話的確定は純関数の版番号付きレビュー状態機械で表し、取引履歴明細は純関数でテンプレート化する
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-07, FR-16, UC-03, UC-04, UC-05, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/07_discord-bot-design.md
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
---

# IADR-0042: 対話的確定は純関数の版番号付きレビュー状態機械で表し、取引履歴明細は純関数でテンプレート化する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-07（対話的確定）、FR-06（階層方針）、FR-16（テンプレート準拠・数値はコード集計）、ADR-0003（確定前は不適用・**方針の確定には利用者との対話を要し完全無人での方針変更は行わない**＝OwnerOnly の根拠）
  - ※ 既存 [IADR-0024](IADR-0024_report-confirmation-and-policy.md) は OwnerOnly の根拠を ADR-0007 と誤引用していたが、実際の ADR-0007 は「取引商品（現物/信用）とガード設定」の決定であり無関係。本 IADR では ADR-0003 に訂正する。既存の Slice A 実装ファイル・IADR-0024 側の同誤引用は #299（PR #376・本 PR）で訂正済み。
- 対象 Issue: [#14](https://github.com/endazon/ai-stock-trading/issues/14)（対話的確定ロジック・明細レンダリングのスライス）
- 関連する実装仕様書: [20260711_report-interactive-confirmation-and-detail](../specs/20260711_report-interactive-confirmation-and-detail.md)
- 関連 IADR: [IADR-0024](IADR-0024_report-confirmation-and-policy.md)（版番号付き冪等確定・踏襲）、[IADR-0032](IADR-0032_report-generation.md)（純関数テンプレート化・踏襲）

## コンテキストと課題

Slice A（IADR-0024）は `ReportState.Draft/Confirmed` の二状態と `ReportService.Confirm`（版番号付き冪等確定）を実装したが、FR-07 の
**「対話的確定」**（07_discord-bot-design のシーケンス: ドラフト提示 → 修正指示 → 改訂 v2 → 承認 → 確定）が持つ**提示・差し戻し・改訂・承認の
複数状態遷移**が未実装で、Discord/チャットUI から確定操作を駆動する土台が無い。また 04_report-templates 日報 §2 の**取引履歴（全明細）＋
取引詳細**のレンダリングは IADR-0032 で明示的に後続とされていた。

この2点を、実 LLM・#63 台帳・Discord/HTTP 結線が無くても CI で検証できる形で実装したい。

## 検討した選択肢

### 対話的確定の表現

1. **既存の二状態（Draft/Confirmed）に確定操作だけを足す** — 差し戻し・改訂・提示という中間状態が表せず、07_discord-bot-design の
   対話フロー（「損切り幅を広げたい」→改訂 v2→承認）を状態として追跡できない。二重確定防止の版検証も呼び出し側に散る。
2. **Application/Worker に手続き的に状態遷移を書く** — DB・HTTP・Discord に結合し、実データ無しでは検証しづらい。遷移規則が
   副作用と混ざりテストが重くなる。
3. **純関数のレビュー状態機械（採用）** — `ReviewState` と `Decide(現在, コマンド)→決定` を Domain の純関数で表す。副作用ゼロで
   全遷移・冪等・版排他・OwnerOnly・終端不変を決定的にテストできる。HTTP/Discord/永続は本状態機械を駆動する薄い層として後続で結線する。

### 取引履歴明細の表現

1. **ReportRenderer に明細セクションを直接足す** — 既存の日報本文（サマリ/散文/方針）構造を変え、回帰リスクが高い。明細は #63 台帳連携が
   前提のため、本文合流は連携スライスの方が安全。
2. **独立した純関数レンダラ（採用）** — `TradeHistoryRenderer` を IADR-0032 と同じ純関数方針で独立実装し、`TradeHistoryView`（fake 可能な
   入力 record）から §2 を決定的に生成する。`ReportRenderer` 本文への合流は #63 連携スライスで行う。

## 決定

**対話的確定＝選択肢3（純関数レビュー状態機械）**、**明細＝選択肢2（独立純関数レンダラ）** を採用する。

- **`ReportReviewStateMachine`（Domain・純関数）**: `ReviewState`（`Drafting`/`PendingApproval`/`ChangesRequested`/`Confirmed`）を、既存の
  永続 `ReportState.Draft/Confirmed` の Draft 局面を精緻化した対話ライフサイクルとして導入する。`Decide(ReportReview, ReviewCommand)` が
  `ReviewDecision(Review, Transitioned, Rejection?)` を返す。
  - **ガード**: 操作者必須（OwnerOnly・ADR-0003: 方針の確定には利用者との対話を要する）／版番号の楽観排他（二重実行防止・古い版は拒否）／
    冪等（確定済みへの同版 Approve・提示済みへの Present・差し戻し済みへの RequestChanges は `Transitioned=false`）／終端不変（Confirmed からの
    改訂系は拒否・ADR-0003）／不正遷移の拒否。版番号は**内容が変わる遷移（改訂 Revise・確定 Approve）でのみ +1** し、状態のみ変える提示/差し戻しは
    版を上げない（IADR-0024 の版番号付き冪等確定を状態機械へ一般化）。
  - **確定済み再確定の版検証（IADR-0024 の再検討事項への回答）**: [IADR-0024](IADR-0024_report-confirmation-and-policy.md) は「対話的確定追加時に、確定済み
    再確定の版不一致検知（現状は冪等 200）も再検討する」をフォローアップに挙げていた。本 IADR は**版非依存の冪等成功を意図的に継続**する。理由は
    07_discord-bot-design の二重確定シナリオでは、利用者が確定ボタンを押した時点で保持している版は確定前の版であり（確定で版が +1 されるため）、
    再送要求が古い版を伴っても冪等に成功させる必要があるため。版不一致を確定済みで拒否すると、正当な二重タップが誤って競合扱いになる。未確定局面の
    版排他（`VersionConflict`）は従来どおり維持する。
- **`TradeHistoryRenderer`（Domain・純関数）**: `TradeHistoryView`（`TradeHistoryLine`／`TradeDetailBlock`／`SkippedDecision`）から
  04_report-templates 日報 §2（全明細表＋取引詳細ブロック＋見送り判断）を決定的に Markdown 生成する。市場・売買は既存
  `AiStockTrading.Shared.Contracts.Trading`（`Market`/`TradeSide`）を再利用し `PeriodTradeFill` と整合させる。空データはプレースホルダで形式を保つ。
- **fake データはテスト内**に置き、本番コードに fake を持ち込まない（IADR-0032 の「ポート抽象＋テスト fake」と整合）。
- **範囲**: 本スライスは純関数ドメイン（状態機械＋明細レンダラ）に限定する。HTTP/Discord/チャットUI 結線・#63 台帳の実約定写像・
  実 LLM 取引詳細文・無応答既定・KB 保存・`ReportRenderer` 本文への明細合流は後続。#14 はクローズしない。

## 理由

- 対話的確定の遷移規則を純関数へ隔離することで、実 LLM・DB・Discord 無しでも承認/差し戻し/改訂/冪等/版排他/OwnerOnly を決定的に
  全面検証でき、後続の HTTP/Discord 結線は「状態機械を駆動する薄いフロント」に単純化できる（07_discord-bot-design の「Bot はステートレス・
  対話文脈は報告書サービスに一元保持」と整合）。
- 版番号付き冪等確定（IADR-0024）を状態機械へ一般化することで、二重確定防止のロジックの単一情報源を保てる。
- 明細を独立純関数レンダラにすることで、既存日報本文の回帰リスクを負わずに §2 テンプレート準拠を決定的にテストでき、#63 連携時に本文へ合流できる。

## 結果

- 良い影響: FR-07 の対話的確定（承認/差し戻し/改訂の状態遷移）と FR-16 の明細レンダリングが、実データ非依存で CI 検証可能になる。
- 悪い影響・トレードオフ: レビュー状態（`ReviewState`）は本スライスでは永続 `ReportState` と別レイヤの純関数モデルに留まる（統合は HTTP/永続結線
  スライスで判断）。明細は `ReportRenderer` 本文と未合流。実データ・実 LLM・Discord/HTTP・認可は後続。
- フォローアップ: 状態機械の HTTP/Discord 結線（未認証 401・ロール無し 403 の実認可）、#63 台帳→明細写像、実 LLM 取引詳細文、無応答既定動作、
  KB 保存（#18）、`ReportRenderer` 本文への明細合流と週報/月報の明細粒度。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0024](IADR-0024_report-confirmation-and-policy.md)（版番号付き冪等確定）、[IADR-0032](IADR-0032_report-generation.md)（純関数テンプレート化）
