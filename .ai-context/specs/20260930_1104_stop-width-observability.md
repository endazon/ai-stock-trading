---
title: 新規建ての損切り幅を観測できるようにし、「ATR 連動」の誤った主張を実際の挙動へ直す（#1104）
type: spec
status: accepted
related_ids: [FR-10, FR-04, FR-02, FR-03, ADR-0003, ADR-0018, IADR-0460, IADR-0003, IADR-0030, IADR-0035, IADR-0099, IADR-0451]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 新規建ての損切り幅の観測と「ATR 連動」の主張の是正（#1104）

## 背景

計画（FR-10・05_trading-assumptions §5「1取引あたりリスク」）は「資金の 1%、**ATR 連動でサイズ算出**」と定める。
実装は ATR を**どこでも計算していない**（テスト以外の backend コードに ATR の算出は無い。先行調査で確認済み）。

- 損切り幅は LLM の出力項目 `stopLossDistancePerShare` をそのまま使う（`TradeDecisionPromptBuilder` の出力形式・`TradeDecisionParser`）。
- 検証は `0 < 幅 < 参照価格` だけ（`TradeDecisionParser.cs` 46-47 行・`TradeDecisionAppService`・`Stage0DecisionRecorder.cs` 351-352 行）。
- 損切りライン＝アンカリング済みの現在値 − 幅（ロング。ショートは＋）。
- 株数＝`floor(equity × 1取引リスク × 縮小係数 ÷ 幅)` を、1 注文 25% と残枠（段階残枠・日次発注残枠の小さい方）で上から抑える（`PositionSizer.CalculateCappedQuantity`）。

それなのにコードのコメント・IADR・docs は「ATR 連動」と書いており、実装がそれを満たしているかのように読める。
また `TradingDefaults.DefaultStopLossRatio`（と実体の `StopLossApproximation.DefaultRatio`）は出典を「§5 の『損切り幅3%』目安」と書くが、
**現行の §5 にその記述は無い**（隣接クローン `../project-planning` aa068ac で `grep -rn 損切り幅 projects/ai-stock-trading` を実測。
該当は 2026-07-08 の環流記録 `10_feedback/20260708_trading-defaults-derived-values.md` が引用した、当時の「初期投入資金」行の備考
「1取引リスク1%=1,000円 → 損切り幅3%なら…」だけで、資金の USD 化で同行ごと書き換わっている）。

損切り幅の数値の下限は計画側へ裁定を依頼済み（planning#703）。**本件では数値の下限を強制しない。**

## 範囲

1. 「ATR 連動」の誤った主張を、実際の挙動（幅は LLM の出力・サイジングの式）へ直し、planning#703 を裁定待ちとして引く。
   凍結記録（`.ai-context/adr/`）は本文を書き換えず、日付つき追記ブロック `［2026-09-30 追記 / #1104］` を足す。
   `DefaultStopLossRatio` の出典を直す。
2. 新規建ての損切りラインを算出する地点（`TradeDecisionAppService`）で、損切り幅の観測値を構造化 Information ログへ出す。
3. （任意）プロンプトへの指針 1 行 —— **採らない**（下記「決定」D4）。

範囲外: 数値の下限の強制（planning#703 の裁定待ち）、ATR の算出、`Stage0DecisionRecorder`（Stage 0 の記録は発注しない）。

## 母集合（規則 9: 誤りの側の文字列で全文書を走査してから挙げる）

### 「ATR」

`git grep -n "ATR" -- backend docs .ai-context/adr`（2026-09-30・origin/develop 87c40e1f）。英大文字の偶然一致（`ATTR` 等）は無かった。
範囲外の走査として `git grep -n "ATR" -- ':!backend' ':!docs' ':!.ai-context/adr'` も引いた（下表の末尾）。
`git grep -n -i "\batr\b" -- backend` は上と同じ 5 件のみ（小文字の別表記は無い）。

| # | 箇所 | 主張 | 分類 | 扱い |
| --- | --- | --- | --- | --- |
| 1 | `backend/Services/RiskManagementService/Domain/PositionSizer.cs:3` | 「ATR連動を想定した損切り幅入力」 | 誤り（実装の主張） | 実際の入力（LLM の幅）とサイジングの式へ直す |
| 2 | `backend/Services/RiskManagementService/Domain/TradingDefaults.cs:43` | 「取引判断の ATR 連動 stopLossDistancePerShare」 | 誤り | LLM の出力値である旨へ直す |
| 3 | `backend/Services/RiskManagementService/Domain/TradingDefaults.cs:64` | 「ATR 連動サイジングの基礎」 | 誤り（計画の要求を実装の性質として書いている） | 「計画は ATR 連動を定めるが実装は ATR を計算しない」へ直す |
| 4 | `backend/Services/RiskManagementService/Domain/RiskLimitSettings.cs:35` | 「ATR連動サイジングの基礎」 | 誤り | 同上 |
| 5 | `backend/Shared/AiStockTrading.Shared.Contracts/Trading/OrderIntent.cs:9` | 「損切り価格（ATR 連動・…）」 | 誤り | コメントだけを直す（契約の型は変えない） |
| 6 | `backend/Services/RiskManagementService/Tests/Domain/TradingDefaultsTests.cs:29` | 「1%（ATR 連動）」 | 誤り（テストのコメント） | 直す |
| 7 | `backend/Services/RiskManagementService/Tests/Domain/PositionSizerTests.cs:7` | 「1%・ATR 連動サイジング」 | 誤り（テストのコメント） | 直す |
| 8 | `backend/Services/RiskManagementService/Tests/Features/RiskManagement/GetOpenPositions/OpenPositionsServiceTests.cs:47` | 「ATR 連動の実損切り価格 950」 | 誤り（テストのコメント） | 「取引判断が決めた実損切り価格」へ直す |
| 9 | `docs/functional/FR-10_risk-controls.md:177` | 表「1 取引リスク 1%（ATR 連動サイジング）」 | 誤り（生きた文書が実装を説明している） | 実際の挙動へ直す。起点 ID・issue は trace ブロックへ |
| 10 | `docs/functional/FR-10_risk-controls.md:190` | 既定値表「資金の 1%（ATR 連動サイジング）」 | 同上（計画の値の転記だが「実装」列と並ぶ） | 計画の定めと実装の差を併記する |
| 11 | `docs/data/risk-management-aggregates.md:79` | `PerTradeRiskRatio`「equity 比・ATR 連動」 | 誤り | 直す |
| 12 | `.ai-context/adr/IADR-0003_position-sizing-responsibility.md:62` | 「損切り幅（ATR）といった判断側の入力」 | 凍結記録の誤り | 本文は残し、追記ブロックを足す |
| 13 | `.ai-context/adr/IADR-0030_position-store-sync-api.md:36` | 「`stopLossDistancePerShare`（ATR 連動）」 | 凍結記録の誤り | 同上（#14・#23 と 1 つの追記にまとめる） |
| 14 | `.ai-context/adr/IADR-0030_position-store-sync-api.md:74` | 「ATR 連動の実値ではない」 | 凍結記録の誤り | 同上 |
| 15 | `.ai-context/adr/IADR-0035_stop-loss-authoritative.md:32-33` | 「（ATR 連動）」「近似は ATR 連動の実際の…」 | 凍結記録の誤り | 同上 |
| 16 | `.ai-context/adr/IADR-0035_stop-loss-authoritative.md:56` | 「ATR 連動の実値で動き」 | 凍結記録の誤り | 同上 |
| 17 | `.ai-context/specs/` 6 ファイル 7 件（20260708_risk-guard-core・20260711_position-store-wiring ×3・20260711_stop-loss-authoritative・20260804_329_risk-control-core・20260919_846_entry-and-stop-price-precision） | 同趣旨 | 凍結記録（作業仕様書・point-in-time） | **除外**。当時の記述と食い違わせないため書き換えない（traceability.repo.md §除外とその理由と同じ規律）。後から読む人は本仕様書と IADR-0460 から辿る |
| 18 | `frontend/package-lock.json` 2 件 | integrity ハッシュ内の偶然一致 | 無関係 | 除外 |

### 「損切り幅3%」の出典（`DefaultStopLossRatio` の是正に付随）

`git grep -n "損切り幅 \?3%\|損切り幅3\|「損切り幅" -- ':!.ai-context/specs'`。

| # | 箇所 | 主張 | 分類 | 扱い |
| --- | --- | --- | --- | --- |
| a | `backend/Services/RiskManagementService/Domain/TradingDefaults.cs:42` | 「§5 の『損切り幅3%』目安」 | 誤り（現行 §5 に無い） | 出典を当時の §5 備考（現行に無い）と環流記録へ直す |
| b | `backend/Shared/AiStockTrading.Shared.Kernel/Trading/StopLossApproximation.cs:20` | 同上 | 誤り（#a の実体側） | 同上。**#a だけ直すと実体側が古いまま残る**（規則 10） |
| c | `.ai-context/adr/IADR-0030_position-store-sync-api.md:45` | 「前提条件 §5 の『損切り幅 3%』注記」 | 凍結記録の誤り | #13 と同じ追記ブロックで扱う |
| d | `.ai-context/adr/IADR-0002_trading-defaults-derivation.md:39`・`IADR-0108_simulator-risk-profile.md:64,68` | 「1 取引リスク 1% × 損切り幅 3% の目安」から逆算 | 当時の導出の記録であり、§5 に今あるとは言っていない | 対象外（誤りではない） |
| e | `.ai-context/adr/IADR-0042_…:45` | 「損切り幅を広げたい」（対話の例） | 無関係 | 対象外 |
| f | `backend/Services/RiskManagementService/Tests/Domain/SimulatorTradingDefaultsTests.cs:100` | 検算の中の「損切り幅 3%」 | テストの入力値の説明 | 対象外 |

## 決定（IADR-0460）

- **D1: 観測はログだけにする（監査イベントの項目は足さない）。**
  判断の記録 `TradeDecisionMade` / `OrderIntent` は共有契約（`AiStockTrading.Shared.Contracts`）であり、
  すでに `Price`（アンカリング済みの参照価格）と `StopLossPrice` を運んでいる（幅＝両者の差として台帳から復元できる）。
  足りないのは LLM の参照価格と日中の高安だが、これらは判断時の観測量であって発注・統制の入力ではない。
  共有契約に足すと、発注執行・リスク管理・監査・報告書の購読側の互換性（直列化・台帳の列）を巻き込む（契約の変動）。
  planning#703 の裁定で下限を強制するときに、その統制の記録として契約へ載せるかを改めて決める。
- **D2: 出す地点は新規建ての損切りラインを算出した直後の 1 か所。** 決済（Close）・見送りでは出さない（損切り幅を使わない／発注意図を作らない）。
- **D3: 出す値。** 銘柄・売買方向・株数・LLM の参照価格・アンカリング済みの価格・差（アンカリング済み − LLM）・1 株あたり幅・
  幅の比率（アンカリング済みの価格に対する %、小数 4 桁・四捨五入）・日中の値幅（高値 − 安値）・幅の値幅に対する倍率（小数 4 桁）・損切り価格。
  日中の値幅は IADR-0451 の日中文脈（`IntradayPriceContext.High` / `Low`）から取る。現在値の供給が無い構成（既定 NoOp）では日中文脈が無い。
  **高値・安値のどちらかが不明、または高値 ≦ 安値のときは値幅を「不明」とし、倍率も「不明」にする**（0 を書かない。0 除算もしない）。
  計算は純関数 `StopWidthObservation.Of` に置き、ログはその値を出すだけにする（試験で式を固定するため）。
- **D4: プロンプトへの指針（任意項目）は採らない。** 1 行でも LLM への指示を変える変更であり、判断の挙動（幅の出し方）を変え得る。
  本件は「観測できるようにする」ことが目的で、観測値が無いまま指示を変えると効果を測れない。指示の要否は planning#703 の裁定と観測値を見て決める。
- **D5: 「ATR 連動」の是正は、コメントでは「計画は ATR 連動を定めるが、実装は ATR を計算せず LLM の幅をそのまま使う（planning#703 裁定待ち）」と書く。**
  計画の側の「ATR 連動」は計画の定めであり、本リポジトリからは書き換えない（計画の要求と実装の差は planning#703 で扱う）。

## 試験（FR-10 テスト仕様書へ登録。T-10-1747〜1751）

| ID | 内容 | 種別 |
| --- | --- | --- |
| T-10-1747 | `StopWidthObservation.Of`: LLM 100・アンカリング 102・幅 2・高安 104/99.5 → 差 +2・比率 1.9608%・値幅 4.5・倍率 0.4444 | 自動 |
| T-10-1748 | 日中文脈なし／高値不明／安値不明／高値＝安値／高値＜安値 → 値幅・倍率とも null（0 にしない） | 自動（否定形） |
| T-10-1749 | 判断サービス（現在値の供給あり・日中文脈あり）で新規建てを出すと、Information の観測ログが 1 件出て、構造化値が上と一致し、損切り価格と株数が発注意図と一致する | 自動 |
| T-10-1750 | 現在値の供給なし（NoOp）では、アンカリング済みの価格＝LLM の参照価格・差 0、値幅と倍率は「不明」と出す | 自動（否定形） |
| T-10-1751 | Hold の判断・損切り幅が不正で見送る判断では観測ログを出さない | 自動（否定形） |

## 検証

（実行結果は下の「検証結果」に記す）

- `dotnet build backend/backend.slnx`（警告 0）
- `dotnet test` —— TradeDecisionService.Tests・RiskManagementService.Tests
- `dotnet format --verify-no-changes --include` 変更したサービスのディレクトリ
- `node scripts/check-test-traceability.js` / `check-trace-blocks.js` / `check-doc-links.js` / `check-adr-index-sync.js` /
  `gen-knowledge-graph.js --check` / `check-commit-messages.js` / `check-cross-repo-refs.js` / `check-plan-id-qualification.js`
- 自己変異: 比率の式（`× 100` を外す・分母を LLM の参照価格にする）と値幅の不明判定（`h > l` を外す）を壊して、落ちる試験を確かめる

## 検証結果

2026-09-30・origin/develop 87c40e1f 起点の作業ブランチで実施。

| 検証 | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx` | `0 Warning(s)` / `0 Error(s)` |
| `dotnet test` TradeDecisionService.Tests | `Passed! - Failed: 0, Passed: 1050, Total: 1050` |
| `dotnet test` RiskManagementService.Tests | `Passed! - Failed: 0, Passed: 2083, Total: 2083` |
| `dotnet format backend/backend.slnx --verify-no-changes --include`（TradeDecisionService・RiskManagementService・Shared の Contracts/Trading・Kernel/Trading） | exit 0 |
| `node scripts/check-test-traceability.js` | exit 0（採番の最大値 T-10-1751） |
| `node scripts/check-trace-blocks.js` | OK: 53 件に違反なし |
| `node scripts/check-doc-links.js` | OK: 1019 件に破損リンクなし |
| `node scripts/check-adr-index-sync.js --range=origin/develop..HEAD` | OK: 変更された実装ADR 4 件すべてで索引行も変更 |
| `node scripts/check-adr-index-addendum-loss.js --range=origin/develop..HEAD` | OK: 追記ブロック 185 件すべて残存・重複なし |
| `node scripts/gen-knowledge-graph.js --check` | OK: in-repo 参照に実在しないもの無し |
| `node scripts/check-commit-messages.js --range origin/develop..HEAD` | ✓ すべてのコミットが規約に適合 |
| `node scripts/check-cross-repo-refs.js` | OK（初回は FR-10 機能仕様書の trace ブロックで `planning#646, #1104` の列挙形の修飾漏れを検出。裸の番号を修飾つきの前へ並べ替えて解消） |
| `node scripts/check-plan-id-qualification.js` | OK: 3021 件に違反なし |

自己変異（`StopWidthObservationTests` 15 件に対して。各変異の後に原状へ戻した）:

| 変異 | 結果 |
| --- | --- |
| 比率の `× 100` を外す | 12 件以上が落ちる（出力を 12 行で切った。T-10-1747〜1750） |
| 比率の分母を LLM の参照価格にする | 11 件が落ちる（T-10-1747〜1749） |
| 値幅の条件 `high > low` を外す | 2 件が落ちる（T-10-1748 の高値＝安値・高値＜安値） |
| ログの呼び出しを外す | 2 件が落ちる（T-10-1749・1750） |
| 不明を `0m` と書く | 1 件が落ちる（T-10-1750） |

## 監査の指摘への対応（2026-09-30）

監査は途中で中断した。調整側が同じ作業ツリーで、監査の変異をコミットに取り込んだためである（別のコミットで戻した）。中断までの所見に 🔴 は無かった。🟡 の 2 件は次のとおり対応した。
- 決済（Close）でログが出ないことを固定する試験が無かった。T-10-1751 に、保有 10 株・LLM が幅つきで売る（決済）ケースを足した。決済の分岐でログを出す変異で落ちることを確かめた。
- 丸め方式を偶数丸めに変える変異が生き残っていた。T-10-1747 の Theory に中間値（1 ÷ 128 × 100 ＝ 0.78125 → 0.7813）を足し、変異で落ちることを確かめた。
