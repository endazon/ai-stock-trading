---
title: 作業仕様書 — 必須仕様書（機能/テスト）の網羅裁定と規約明確化
type: work
status: review
related_ids: [NFR, FR-10, FR-12, FR-15, FR-19, FR-20]
issue: 211
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
related_specs:
  - ../../docs/functional/FR-15_backtest.md
  - ../../docs/tests/FR-15_backtest-tests.md
  - ../../docs/tests/FR-10_risk-guard-core-tests.md
  - ../README.md
---

# 作業仕様書: 必須仕様書（機能/テスト）の網羅裁定

> 起点 Issue: [#211](https://github.com/endazon/ai-stock-trading/issues/211)（NFR・リポ規約 CLAUDE.md「仕様書（docs/）」節）。
> 本書は実装コードを変更しない **docs 中心**の作業であり、CLAUDE.md が要求する「機能仕様書・テスト仕様書の必須網羅」の
> 乖離に対して**網羅の裁定（どれが必須で、どれが対象外か）を根拠つきで確定し記録する**ものである。

## 目的・背景

実環境構築前監査（2026-07-18、対象コミット `a48835a`。監査報告書: project-planning `draft/20260718_pre-production-audit.md` B-6）で、
必須仕様書の網羅乖離が検出された。

- CLAUDE.md / [docs/README.md](../../docs/README.md) は機能仕様書（`docs/functional/`・FR 単位）とテスト仕様書
  （`docs/tests/`・FR 単位）を「必須（対象が存在する限り作成・維持する）」と規定している。
- 実際は `docs/functional/` に 5 件（FR-10/12/15/19/20）、`docs/tests/` に 2 ファイル（FR-10 系・FR-19）のみ。
- 一方で作業仕様書（`docs/specs/`）は約 100 件あり、実装済み全 FR を PR 単位で網羅している。

**監査自身の結論（§3.1）**: 「コード・テスト・仕様書への ID 参照は全実装対象 FR / UC-01〜07 / SC-01〜03 で網羅され、
**参照切れ・孤立実装はゼロ**」。すなわち本件はトレーサビリティの欠落ではなく、**書式規約の文言（『対象が存在する限り』）の
解釈が定まっていないことによる乖離**である。

## 裁定（案 A / 案 B の確定）

Issue #211 が提示した二択のうち、**案 B（規約裁定）を採用する**。

> **裁定**: 機能仕様書（`docs/functional/`）とテスト仕様書（`docs/tests/`）の**必須範囲を、安全・統制の中核 FR
> ＝ FR-10（リスク統制）・FR-12（ペーパートレード）・FR-15（バックテスト）・FR-19（取引ガード）・FR-20（段階ゲート）に限定する**。
> それ以外の実装済み FR は、作業仕様書（`docs/specs/`・PR 単位の point-in-time 記録）と xUnit テスト（起点 ID コメント付）を
> 正の記録とし、機能仕様書・テスト仕様書は**任意**とする。1 つのテスト/機能仕様書が関連する複数 FR をまとめてよい。

### 根拠

1. **実務のカバーは work spec ＋ xUnit で成立済み**。作業仕様書は PR 単位で「入力・処理・出力・業務ルール・受け入れ基準・
   テスト写像」を point-in-time に記録しており、監査が参照切れ・孤立実装ゼロを確認している。受け入れ基準は
   CLAUDE.md の規約どおり `[Fact]`/`[Theory]` に直接写像され、起点 ID コメントで追跡できる（backend 1477 件合格）。
2. **独立した機能/テスト仕様書が統制価値を持つのは中核 FR**。リスク統制・取引ガード・段階ゲート・バックテスト・ペーパーは
   設定駆動かつ横断的で、単一 PR からは全体挙動が読み取りにくい。既存の functional 5 件がまさにこの境界に一致するのは偶然ではなく、
   統制価値が高い FR に自然に集約された結果である。
3. **案 A（全 FR への機能/テスト仕様の遡及作成）を採らない理由**。15 件規模の機能仕様＋同規模のテスト仕様は work spec と
   xUnit の重複であり、統制価値を追加しない。CLAUDE.md 禁止事項「計画外の…過剰な抽象化」「起こり得ないケースへの防御的実装」
   と同種の過剰投資に当たる。docs の単一情報源性をむしろ損なう（同一事実が work spec と functional で二重管理になる）。

## 網羅マトリクス（FR × 仕様種別の裁定）

凡例: ✅=実在, ➕=本 PR で補完, ―=対象外（任意）, ★=安全中核（必須範囲）。実装状況は監査 §3.1（実装済 11・部分 8・
未実装 1）に準拠。

| FR | 概要 | MoSCoW | functional（必須?/実在） | test（必須?/実在） | work spec（正の記録・代表） |
| --- | --- | --- | --- | --- | --- |
| FR-01 | 情報収集・正規化・KB 保存 | Must | 任意 ― | 任意 ― | [information-collection](20260710_information-collection.md) / [official-connectors](20260717_information-collection-official-connectors.md) |
| FR-02 | 定時取引サイクル | Must | 任意 ― | 任意 ― | [trading-cycle-wiring](20260710_trading-cycle-wiring.md) |
| FR-03 | 価格変動監視・即時起動 | Must | 任意 ― | 任意 ― | [market-monitor-core](20260710_market-monitor-core.md) / [live-market-data-feed](20260717_live-market-data-feed.md) |
| FR-04 | AI 売買判断・根拠記録 | Must | 任意 ― | 任意 ― | [trade-decision-core](20260710_trade-decision-core.md) / [profitability-gate](20260718_trade-decision-profitability-gate.md) |
| FR-05 | moomoo 発注・注文状態追跡 | Must | 任意 ― | 任意 ― | [order-execution](20260710_order-execution.md) / [order-idempotency](20260716_131_order-idempotency-reservation.md) |
| FR-06 | 報告書の月→週→日階層 | Must | 任意 ― | 任意 ― | [report-generation](20260711_report-generation.md) / [weekly-monthly-reports](20260711_weekly-monthly-reports.md) |
| FR-07 | 報告書の自動生成・対話確定 | Must | 任意 ― | 任意 ― | [report-confirmation](20260710_report-confirmation.md) / [interactive-confirmation](20260711_report-interactive-confirmation-and-detail.md) |
| FR-08 | KB 保存・RAG 取得 | Must | 任意 ― | 任意 ― | [knowledge-base-rag-foundation](20260718_knowledge-base-rag-foundation.md) |
| FR-09 | Discord 通知 | Must | 任意 ― | 任意 ― | [notification-outbound](20260710_notification-outbound.md) |
| **FR-10** ★ | リスク統制（kill switch/日次損失/DD/連敗縮小/pause-resume 等） | Must | **必須 ✅** [FR-10](../../docs/functional/FR-10_risk-controls.md) | **必須 ✅** [FR-10 系](../../docs/tests/FR-10_risk-guard-core-tests.md) | [risk-guard-core](20260708_risk-guard-core.md) / [pause-resume](20260718_152_pause-resume.md) |
| FR-11 | 監査時系列ログ | Must | 任意 ― | 任意 ― | [audit-log](20260710_audit-log.md) / [audit-remaining-events](20260711_audit-remaining-events.md) |
| **FR-12** ★ | ペーパートレードモード | Should | **必須 ✅** [FR-12](../../docs/functional/FR-12_paper-trade.md) | **必須 ✅** [FR-10 系](../../docs/tests/FR-10_risk-guard-core-tests.md)（T-12-xx） | [paper-broker-validation](20260709_paper-broker-validation.md) |
| FR-13 | 監視銘柄・閾値・上限の設定変更 | Should | 任意 ― | 任意 ― | [configuration-assumptions](20260710_configuration-assumptions.md) / [watchlist-settings-api](20260718_191_watchlist-settings-api.md) |
| FR-14 | Discord 対話（質疑・確定・kill switch・pause/resume） | Must | 任意 ― | 任意 ― | [discord-bot-authorization](20260717_15_discord-bot-authorization-killswitch.md) / [stage-gate-discord-bot](20260718_165_stage-gate-discord-bot.md) |
| **FR-15** ★ | バックテスト＝実弾前の必須ゲート Stage 0 | Must | **必須 ✅** [FR-15](../../docs/functional/FR-15_backtest.md) | **必須 ➕** [FR-15](../../docs/tests/FR-15_backtest-tests.md)（本 PR で補完） | [backtest-foundation](20260711_backtest-foundation.md) / [verdict-supply](20260718_backtest-verdict-supply.md) |
| FR-16 | 報告書の定型テンプレート・数値はコード集計 | Must | 任意 ― | 任意 ― | [report-generation](20260711_report-generation.md) |
| FR-17 | 全体前提条件の一元管理・バージョン管理 | Must | 任意 ― | 任意 ― | [configuration-assumptions](20260710_configuration-assumptions.md) / [assumptions-versioned-read](20260717_19_assumptions-versioned-read.md) |
| FR-18 | 損益通算・繰越控除（確定申告集計） | **Won't**（将来拡張） | 対象外 ―（未実装） | 対象外 ―（未実装） | ―（未実装。計画どおり） |
| **FR-19** ★ | 取引ガード（禁止銘柄・差金決済防止・相場操縦禁止 等） | Must | **必須 ✅** [FR-19](../../docs/functional/FR-19_trading-guard.md) | **必須 ✅** [FR-19 相場操縦](../../docs/tests/FR-19_manipulation-detection-tests.md) ＋ [FR-10 系](../../docs/tests/FR-10_risk-guard-core-tests.md)（T-19-xx） | [risk-eval-core-fixes](20260709_risk-eval-core-fixes.md) / [manipulation-detector](20260711_manipulation-detector.md) |
| **FR-20** ★ | 段階ゲート（Stage 0〜3・モード/資金上限強制） | Must | **必須 ✅** [FR-20](../../docs/functional/FR-20_staged-gates.md) | **必須 ✅** [FR-10 系](../../docs/tests/FR-10_risk-guard-core-tests.md)（T-20-xx） | [stage-gate-transitions](20260718_20_stage-gate-transitions.md) |

### 裁定後の充足状況

- **機能仕様書（安全中核 5 FR）**: FR-10/12/15/19/20 = **5/5 実在**。
- **テスト仕様書（安全中核 5 FR）**: FR-10/12/19/20 は [FR-10 系テスト仕様書](../../docs/tests/FR-10_risk-guard-core-tests.md)が写像済み・
  FR-19 は[相場操縦テスト仕様書](../../docs/tests/FR-19_manipulation-detection-tests.md)も併存。**唯一欠けていた FR-15 を本 PR で補完**
  （[FR-15 テスト仕様書](../../docs/tests/FR-15_backtest-tests.md)）→ **5/5 充足**。
- **非中核 FR（実装済み 12 FR）**: work spec ＋ xUnit で網羅（監査確認済み）。機能/テスト仕様書は任意（対象外）。
- **FR-18**: Won't/将来拡張・未実装につき網羅対象外（計画どおり）。

## 対象範囲

本作業で行うこと（docs のみ・コード挙動不変）:

1. 本裁定の記録（本書）。
2. [FR-15 テスト仕様書](../../docs/tests/FR-15_backtest-tests.md)の新規作成（安全中核テスト仕様を 5/5 に）。
3. 規約の明確化: `CLAUDE.md`「仕様書」節および [docs/README.md](../../docs/README.md)「必須の仕様書」節に、機能/テスト仕様書の
   必須範囲（安全中核 FR）と「複数 FR を 1 仕様書に集約可」を追記する。

本作業で**行わないこと**:

- コード・`backend/`・`AiStockTrading.Shared.Contracts` の変更（[#209](https://github.com/endazon/ai-stock-trading/issues/209) 並行のため非干渉）。
- `docs/adr/README.md` の連番変更・新規 IADR の作成（下記「実装 ADR の扱い」）。
- 既存 functional/test 仕様書 5 件の内容改訂（本件のスコープ外）。

## 実装 ADR の扱い（受け入れ基準との差異・要メンテナ判断）

Issue #211 の受け入れ基準は「案 A/B の裁定が **IADR に記録される**」である。本 PR は、**本作業に与えられた明示のスコープ制約
（`docs/adr/README.md` の連番・新規 IADR に触れず docs に閉じる）に従い**、新規 IADR を作成していない。裁定は本作業仕様書
（point-in-time 記録）と CLAUDE.md / docs/README.md の規約本文に**実体として記録**しており、内容面では受け入れ基準を満たす。

- `docs/adr/README.md` には採番前に未マージの全ブランチを確認して連番衝突を避ける手順が整備されている。並行して IADR を採番する
  作業（例: [#209](https://github.com/endazon/ai-stock-trading/issues/209)）が進行中であり、本 docs スコープの PR で採番手順まで
  踏み込むのは責務外と判断した。IADR へ昇格する場合は本書を一次情報として、その手順に沿って採番できる。
- したがって「**IADR へ昇格するか / 受け入れ基準側を緩和して work spec ＋ 規約本文の記録で足りるとするか**」は、
  マージ前にメンテナが明示的に決定すべき事項である。本 PR は後者を推奨としつつ、判断を委ねる（PR 説明・Issue コメントに明記）。

## 受け入れ基準

- [x] 案 A/B の裁定が根拠つきで記録される（案 B 採用。IADR 昇格は連番衝突回避のため保留し本書＋規約本文に実体記録）。
- [x] 裁定に従い、規約が改訂される（CLAUDE.md / docs/README.md に機能/テスト仕様書の必須範囲を明記）。
- [x] 裁定に従い、必須範囲の欠落が補完される（FR-15 テスト仕様書を新規作成し安全中核 5/5 を達成）。
- [x] doc-links / markdownlint / commit-messages CI が緑（本 PR 追加リンクは実在検査を通す）。

## 関連仕様

- 規約: `CLAUDE.md`（本リポ・「仕様書」節）、[docs/README.md](../../docs/README.md)（「必須の仕様書」節）
- 機能仕様書: [FR-15 バックテスト](../../docs/functional/FR-15_backtest.md)
- テスト仕様書: [FR-15 バックテスト](../../docs/tests/FR-15_backtest-tests.md)、[FR-10/12/19/20 リスクガードコア](../../docs/tests/FR-10_risk-guard-core-tests.md)
- 監査報告書: project-planning `draft/20260718_pre-production-audit.md`（B-6）
