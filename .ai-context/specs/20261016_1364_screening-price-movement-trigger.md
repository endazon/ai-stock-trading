---
title: 急変が起点の一次スクリーニングへ価格変動トリガーの節（現在値・基準値・変動率）を出し、市場監視は急変の検知を INFO で 1 行残す（#1364）
type: spec
status: done
related_ids: [FR-03, FR-04, ADR-0003, IADR-0023, IADR-0039, IADR-0247, IADR-0313, IADR-0316, IADR-0440]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-03, FR-04)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 一次スクリーニングへ価格変動トリガーの節を出す（#1364）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は AST `origin/develop` `eabff721`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-04**（取引判断）、**FR-03**（市場監視・急変の検知）。計画 ADR: **ADR-0003**（判断のガードレール）。
- 起点 issue: **#1364**（同件の検索: open issue・open PR に同じ題の作業は無い。並行作業 #1365〔報告書の方針／取引判断のプロンプト〕・#1366〔RAG・情報収集〕とは `TradeDecisionPromptBuilder` が重なり得る）。
- 新規 IADR: **作らない**（判断は「本判断の既存の節を一次にも出す」「見積りは監視銘柄節〔IADR-0440 決定 5〕と同じく実際の文字数で数える」で、既存 IADR の規則の適用に留まる）。

## 症状と原因

- PoC の夜間監査（2026-10-15）: 急変 5 件（MARA・MSTR・SMCI・PLTR・RKLB）の一次の理由がすべて「C（急変追随）は急変の検知の基準値と変動率が提供されておらず判定できない」。その夜の判断 686 件のうち 318 件が急変の情報の欠如に触れた。
- 原因: 本判断 `TradeDecisionPromptBuilder.Build` は `# 価格変動トリガー`（現在値／基準値／変動率）を出すが、一次 `BuildScreening` は `trigger.Kind` を含み損益の評価価格にしか使わず、節を出さない。一次は門（Hold で本判断が走らない。IADR-0039）なので、急変追随の条件は構造的に成立しない。
- あわせて、市場監視（`MarketMonitorAppService`）は急変の検知をログに残さず、発行だけだった。検知の中身を監査で追えない。

## 方式

1. **節の共通化**: `TradeDecisionPromptBuilder.PriceMovementTriggerSection(DecisionTrigger)`（公開・純関数）を新設する。急変が起点で価格があるときだけ「見出し・銘柄／市場・現在値／基準値／変動率」の 3 行を返し、それ以外は空文字列。価格の単位は Build と同じ規則（基準通貨の市場は注記なし）。**Build の文言は一字一句変えない**（Build は同じ関数で同じ 3 行を出し、続けて従来どおり値動きの行を出す）。
2. **一次へ出す**: `BuildScreening` はニュースの状態の行と空行の後、保有状況の短縮版の前に同じ節（＋空行）を出す。定時が起点なら空文字列のため、**従来のプロンプトと一字一句同じ**。
   - 一次の `# 対象` 節の「- 現在値」（現在値の供給）とは別の節にする。急変の検知時の価格と供給の現在値は時刻がずれ得るため、片方を他方で上書きしない（本判断も急変の節では検知時の価格を出す）。現在値の供給が無い構成（NoOp）でも、検知時の価格・基準値・変動率は一次に届く。
3. **縮退の見積り**: `ScreeningContextAssembler.Assemble` の銘柄側の保護分へ、同じ関数の**実際の文字数**を足す（監視銘柄節〔IADR-0440 決定 5〕と同じ扱い。概算の予約定数は置かない）。定時は 0 で従来の見積りと同じ。Stage 0 の as-of 記録（`Stage0DecisionRecorder.BuildScreeningPrompt`）は同じ `Assemble` と `BuildScreening` を呼ぶため自動的に揃う。
4. **市場監視のログ**: 急変を検知して `PriceMovementDetected` を積んだ直後に、INFO で 1 行「急変を検知しました: 銘柄・市場・現在値・基準値・変動率・閾値」を残す。銘柄は監視銘柄の設定（利用者・提案の入力）由来のため `LogSanitizer.Sanitize` を通す（NFR, IADR-0316）。変動率・閾値はプロンプトと同じ書式（`P2`・InvariantCulture）。

## 母集合（規則 9）

引き方: `grep -rn "BuildScreening\|ScreeningContextAssembler.Assemble"`（呼び出しの全箇所）、`grep -rln "価格変動トリガー"`（誤りの側＝節の文言）を `backend/` と `docs/` に掛けた。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `TradeDecisionAppService`（一次 2 枝・`Assemble` 1 箇所） | 変更不要（`trigger` を既に渡している） |
| 2 | `Stage0DecisionRecorder.BuildScreeningPrompt`（一次 2 枝・`Assemble` 1 箇所） | 変更不要（同上） |
| 3 | 試験で予算の境界を内訳から組む箇所 | 急変を起点にするのは `ScreeningContextDegradationTests` の 1 件だけ（全試験の実走で赤になったのもこの 1 件）。節の長さぶん予算をずらした。他（`WatchlistInDecisionPromptTests`・`HeldExitAlwaysJudgedInPromptTests`・`ScreeningContextAssemblerTests`）は定時の起点で 0 |
| 4 | `ReportService/Domain/TradeHistoryView.cs`・`DecisionTrigger.cs` の文言 | 語の一致のみ（プロンプトではない）。変更不要 |
| 5 | `docs/` | 一次のプロンプトの節の一覧・市場監視のログの一覧を持つ文書は無い（`docs/observability/observability.md` に急変の行の記載なし）。変更不要 |

## 受け入れ基準と試験

| 基準 | 試験 |
| --- | --- |
| 急変が起点の一次のプロンプトに現在値・基準値・変動率が含まれる | `PriceMovementTriggerInScreeningTests.急変が起点の一次のプロンプトに現在値と基準値と変動率が含まれる` / `現在値の供給が無くても急変の価格と基準値は一次に届く` |
| 一次と本判断は同じ関数・同じ文言で出す（米国株・日本株） | `一次と本判断は同じ価格変動トリガーの節を出す`（Theory 2 件）/ `日本株の急変では価格に通貨の単位を付ける` |
| 定時が起点なら節が出ない（従来どおり） | `定時が起点の一次には価格変動トリガーの節が出ない_否定形` / `価格の無い急変では節を出さない_否定形` |
| 縮退の見積りが節の長さを保護分に数える | `縮退の見積りは急変の節の長さを保護分に数える_境界値`（定時でちょうどの予算・節の長さ −1・ちょうど） |
| 市場監視は急変の検知を INFO で 1 行残す（LogSanitizer の規律） | `MarketMonitorServiceTests.急変を検知したら銘柄と現在値と基準値と変動率をINFOで1行残す` / `急変を検知しない巡回では急変の行を出さない_否定形` / `急変の行は銘柄の改行を無害化して1行に保つ` |

- 変異: 一次への出力を外すと新試験 8 件中 4 件が赤、見積りへの加算を外すと境界値の 1 件が赤になることを確かめた。

## 残余

- 方針の「急変追随」の条件そのもの（何 % で追随するか）は方針の文言であり本件の範囲外。本件は判定の材料を一次へ届けるだけ。
