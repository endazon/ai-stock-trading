---
title: テーマ（相関の高い銘柄の集まり）ごとの同時保有の上限を審査で強制し、判断のプロンプトへ保有中の全銘柄を渡し、利用者と方針の改訂の AI へ「この版から有効」と示す（#1397）
type: spec
status: done
related_ids: [FR-04, FR-10, FR-19, UC-01, UC-06, ADR-0003, ADR-0009, IADR-0163, IADR-0346, IADR-0351, IADR-0529, IADR-0561, IADR-0562]
author: claude (Claude Code)
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md（FR-10・FR-19・FR-04）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§5。本件の行は反映待ち）
  - planning#809（裁定 2026-10-20）
---

# 仕様書: テーマごとの同時保有の上限（#1397）

> 本仕様書は実装着手前に作成した（着手 2026-10-20）。#1396 のブランチの上で着手し（同じファイルを触るため直列）、#1399 のマージ後に AST `origin/develop` `4a8c3a65` の上へ載せ直した。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**、**FR-19**、FR-04
- 関連 ADR: ADR-0003、ADR-0009
- 裁定: planning#809（2026-10-20、利用者。原文を読んだ）。起票: #1397（`Closes #1397`）
- 新規 IADR: **IADR-0562**

## 裁定（planning#809 の 2026-10-20 のコメント）

1. テーマごとの同時保有の上限を新設する。集まり: 暗号資産＝COIN・MARA・MSTR・HOOD、AI・半導体＝NVDA・SMCI。上限は各 1。定義は設定値（審査で決定的に強制）。
   未約定の新規建てを数える。どの集まりにも属さない銘柄には上限を掛けない。
2. 補助: 取引判断のプロンプトに保有中の全銘柄（銘柄・方向・数量）を渡す。強制は 1 の審査で行う。
3. 利用者向けの明示: 運用仕様書の「発動しない機能」の一覧に載せる。方針の改訂の AI に「この制限は統制が入るまで実効が無い」と注記させる。
   統制の実装と同じ PR で入れるため、表示は「この版から有効」に更新する形にする（現状と食い違わない書き方）。

## 方針

IADR-0562 の決定 1〜4 のとおり。

［2026-10-20 追記 / PR #1400 のレビュー］市場の構成値は名前だけを受け付ける（数値の文字列を拒む）。同じ集まりの銘柄を既に上限を超えて持っているときは買い増しも止まる（安全側。そのままとする）。受け手の新しい読み取り（`GetHoldingsAsync`）には送り手の型の契約テストを足した（`RiskManagementReadContractTests`）。

## 母集合（規則 9・10）

走査語: `RejectionReason.`・`new OrderScreeningService(`・`PortfolioState {`/`PortfolioSnapshot`（射影の入力）・`IHeldSymbolsProvider` の実装・`ReserveChars`・「発動しない機能」。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | 拒否理由の列挙・分類と序数・分類の試験 | 末尾に追加・クラス A |
| 2 | `PortfolioProjection`・`PortfolioState`・`PortfolioSnapshot`・`PortfolioSnapshotBuilder` | 保有中 ∪ 未約定の新規建ての銘柄を運ぶ |
| 3 | `RiskEvaluator`・`OrderScreeningService`・`Program.cs` | 上限を足す・必須依存・ログ |
| 4 | `new OrderScreeningService(` の試験 17 か所（統合試験を含む） | 既定の定義を渡す。既存の試験で集まりの 2 銘柄を同時に建てるものは無かった（全試験が緑のまま） |
| 5 | `IHeldSymbolsProvider` の実装（Http・Grpc・NoOp・試験の偽物） | 既定の実装を持つメソッドを足し、Http・Grpc だけ実装する |
| 6 | プロンプトの組み立て（判断のサービス・Stage 0 の記録器） | 判断のサービスだけ渡す（Stage 0 は保有なしの記録で節を出さない＝従来のプロンプト） |
| 7 | 縮退の予算を式で書く試験 4 本 | 予約を同幅足す |
| 8 | `EntryStateBlockers`・gRPC の `EntryBlocker` | 足さない（IADR-0562 の検討表） |
| 9 | `PolicyRevisionPromptBuilder`（材料の行・日報の案内） | 保有中の全銘柄と注記を足す |
| 10 | `docs/blocked-tasks.md`（発動しない機能）・FR-10 の機能仕様書とテスト仕様書 | 行・節を足す |

## 受け入れ基準

- [x] AC-1: 同じ集まりの別の銘柄を保有中（未約定を含む）なら新規建てを `ThemeConcentrationExceeded` で拒否する（T-10-2937・T-10-2939）
- [x] AC-2: 同じ銘柄の買い増し・別の集まり・集まりに属さない銘柄・手仕舞いは止めない（T-10-2937）
- [x] AC-3: 未約定の新規建て・予約を数える（T-10-2938・T-10-2939）
- [x] AC-4: 定義は構成で置き換えられ、誤った定義は起動を止める。既定は裁定の 2 つ（T-10-2940・T-10-2941）
- [x] AC-5: プロンプト（本判断・一次）に保有中の全銘柄が載り、照会できなければ不明と書く（T-10-2942・T-10-2943）
- [x] AC-6: 運用仕様書の一覧と方針の改訂の AI の注記が「この版から有効」で現状と食い違わない（T-10-2944・`docs/blocked-tasks.md`）
- [x] AC-7: 統制を外す変異で赤になる

## 検証

- 変更したサービスの全テスト（--filter なし）、`dotnet format --verify-no-changes`、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、`check-adr-index-sync`
