---
title: 新規建ての損切り幅に下限（暫定は参照価格の 2%）を掛け、割ったら広げて監査に残す（#1120）
type: spec
status: accepted
related_ids: [FR-10, FR-04, FR-11, FR-15, ADR-0003, ADR-0018, ADR-0040, ADR-0048, ADR-0049, IADR-0465, IADR-0460, IADR-0003, IADR-0030, IADR-0035, IADR-0099, IADR-0107, IADR-0130, IADR-0318, IADR-0451]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0049_stop-width-floor-atr14-widen-no-ceiling.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5「損切り幅の下限」)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
---

# 新規建ての損切り幅に下限を掛ける（#1120）

## 背景

planning#703 の利用者裁定（計画 ADR-0049、2026-09-30）で損切り幅の扱いが決まった。

- 幅は AI が提案し、系が下限を掛ける。AI は下限を上書きできない（決定 1）。
- 下限は 1.0 × ATR(14, 日足)。ATR が得られないときは参照価格（アンカー後の現在値）の 2%（決定 2）。
- 下限を割った幅は下限まで広げ、見送らない。サイジングは広げた幅で行う（決定 3）。
- 監査には AI の幅・下限とその出所・適用した幅を残す（決定 3）。
- 上限は設けない。1 注文上限（equity の 25%）は緩めない（決定 4）。
- 配備までの暫定手段は、退避の 2% だけで下限を先に強制すること（決定 5）。

実装（origin/develop 79abb596）の実測:

- 検証は `0 < 幅 < 参照価格` だけ（`TradeDecisionParser.cs` 46-47 行＝LLM の参照価格・`TradeDecisionAppService.cs` 577 行＝アンカー後・
  `Stage0DecisionRecorder.cs` 351-352 行＝LLM の参照価格）。
- ライン＝アンカー後の現在値 ∓ LLM の幅（`TradeDecisionAppService.cs` 631-633 行）。
- 株数＝`PositionSizer.CalculateCappedQuantity`（1 取引リスク 1% と 1 注文 25%・残枠の min）。
- 幅は観測ログだけに出る（IADR-0460。監査イベントの項目は足さないと決めていた）。

PoC 2026-09-29 夜の META は幅約 0.6%（ADR-0049 の実測）で約 50 分後に刈られた。優先度が高い。

## 範囲

1. 新規建ての経路（`TradeDecisionAppService`）で下限を強制する（暫定は 2%。ATR の供給口 `IStopWidthFloorSource` を用意し、今は供給なし）。
2. 監査（`TradeDecisionMade`）に AI の幅・下限・出所・適用した幅・広げたかを残す。
3. 観測ログ（`StopWidthObservation`）に下限・出所・適用した幅・広げたかを足す。
4. 本判断のプロンプトに「損切り幅は当日の値動きより広く」の案内を足す（数値は強制しない）。
5. Stage 0 の記録（`Stage0DecisionRecorder`）のサイジングにも同じ下限（2%）を掛ける（本番と同じ `PositionSizer` を使う規律）。
6. `IADR-0003`・`IADR-0030`・`IADR-0035`・`IADR-0460` に追記ブロック、コード・docs の「ATR 連動」「planning#703 の裁定待ち」を ADR-0049 の定義へ揃える。
7. `.claude/rules/traceability.repo.md` の計画 ADR レンジを実測で引き直す（0047 → 0050）。

範囲外: ATR(14) の計算と日足の経路（ADR-0048 の条件・#1117・#1118 の後の別 issue）、幅の呼値への丸め、決済（Close）の経路、
1 注文上限の変更。

## 前提（読んだ制約）

- 計画 ADR-0049 決定 1〜5（上記）。§5「損切り幅の下限」の行（2026-09-30 追記。`fixed`）。
- 計画 ADR-0003: AI は判断し、リスク管理の制約は決定的なコードで強制する（AI は上書きできない）。
- 計画 ADR-0018 決定 1: §5 の確定単一値は `TradingDefaults` をテストで固定する。
- IADR-0003: サイジングは取引判断サービスが行う（下限を掛ける位置もここ）。
- IADR-0099 決定 2: 参照価格は権威ある現在値へアンカリング済みの値を使う。
- IADR-0107: サイジングは基準通貨、ライン・幅はローカル通貨。
- IADR-0460 決定 1: 監査イベントの項目は足さない（下限を強制するときに改めて決める）→ 本件で改める（IADR-0465）。
- IADR-0318（Stage 0 記録）: サイジングは本番と同じ `PositionSizer` を使う（複製しない）。

## 母集合（規則 9。origin/develop 79abb596）

### 「ATR」（誤りの側の文字列）

`git grep -n "ATR" -- ':!.ai-context/specs' ':!*package-lock.json'` の全件（英大文字の偶然一致は無い）。

| # | 箇所 | 現在の記述 | 扱い |
| --- | --- | --- | --- |
| 1 | `.ai-context/adr/IADR-0003_position-sizing-responsibility.md:62,97-101` | 本文「損切り幅（ATR）」と #1104 の追記（「planning#703 の裁定待ち」） | 凍結。新しい追記ブロック `［2026-09-30 追記 / #1120］` で ADR-0049 の定義と本件の強制を書く |
| 2 | `.ai-context/adr/IADR-0030_position-store-sync-api.md:36,74,83-89` | 同上 | 同上 |
| 3 | `.ai-context/adr/IADR-0035_stop-loss-authoritative.md:32-33,56,80-85` | 同上 | 同上 |
| 4 | `.ai-context/adr/IADR-0460_stop-width-observability-log-only.md` 全体 | 「監査イベントの項目は足さない」「プロンプトへの指針は足さない」「planning#703 の裁定待ち」 | 同上（決定 1・4 を IADR-0465 で改める旨） |
| 5 | `.ai-context/adr/README.md:65,92,97,475` | 索引の各行 | 追記に合わせて各行に `［2026-09-30 追記 / #1120］` を足し、IADR-0465 の行を足す |
| 6 | `backend/Services/RiskManagementService/Domain/PositionSizer.cs:5-6` | 「LLM の出力をそのまま受け取る…planning#703 の裁定待ち」 | 下限を掛けた幅を受け取る旨へ直す |
| 7 | `backend/Services/RiskManagementService/Domain/RiskLimitSettings.cs:36-37` | 同上 | 直す |
| 8 | `backend/Services/RiskManagementService/Domain/TradingDefaults.cs:45,66-67` | 同上 | 直し、下限の退避比率 `StopWidthFloorFallbackRatio = 0.02` を足す |
| 9 | `backend/Services/RiskManagementService/Tests/Domain/PositionSizerTests.cs:7` | 「LLM の出力・ATR は計算しない」 | 直す |
| 10 | `backend/Services/RiskManagementService/Tests/Domain/TradingDefaultsTests.cs:29` | 同上 | 直す（下限の値のテストを足す） |
| 11 | `backend/Services/RiskManagementService/Tests/Features/RiskManagement/GetOpenPositions/OpenPositionsServiceTests.cs:47` | 「ATR 連動ではない」 | テストの入力値の説明（実損切り価格 950）であり、下限の有無と無関係。**据え置く**（誤りではない） |
| 12 | `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/StopWidthObservation.cs:4-5` | 「ATR は計算していない…裁定待ち」 | 直す（観測値を足す） |
| 13 | `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/TradeDecisionAppService.cs:636` | 同上 | 直す（下限の適用へ置き換える） |
| 14 | `backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/StopWidthObservationTests.cs:17` | 同上 | 直す |
| 15 | `backend/Shared/AiStockTrading.Shared.Contracts/Trading/OrderIntent.cs:10` | 「参照価格 ∓ LLM が出した 1 株あたり幅。ATR 連動ではない」 | 「下限を掛けた幅」へ直す（型は変えない） |
| 16 | `docs/data/risk-management-aggregates.md:79` | 「数値の下限は計画側の裁定待ち」 | 下限（2%・ATR は未供給）へ直す |
| 17 | `docs/functional/FR-10_risk-controls.md:190` | 同上 | 直し、下限の節を足す |
| 18 | `docs/tests/FR-10_risk-controls-tests.md:4020,4043` | 観測の節の前文・残余「下限は強制しない」 | 凍結しない生きた文書のため、前文と残余を現在の挙動へ直し、末尾に本件の節を足す |
| 19 | `.ai-context/specs/` の作業仕様書（20260930_1104 ほか） | 同趣旨 | **除外**（point-in-time の記録。traceability.repo.md §除外とその理由） |

### 「planning#703」「裁定待ち」（規則 10: 本件で新たに誤りになる記述）

`git grep -n "planning#703\|裁定待ち" -- ':!.ai-context/specs'` のうち損切り幅に関わる行は上表 #1〜#8・#12〜#18 と同じ集合だった
（追加の該当なし）。docs の trace ブロックの `planning#703` は出典として正しく、据え置く。

### 監査イベントの消費者（契約に項目を足すことの追随先）

`git grep -l "TradeDecisionMade" -- backend` の非テスト 20 ファイル。本件は `TradeDecisionMade` に**既定 null の末尾項目**を足すだけで、
位置引数で組み立てる既存の呼び出し（決済・owner 手仕舞い・自動縮小・リプレイ）はそのままコンパイルし、値は null になる。

| 消費者 | 扱い |
| --- | --- |
| `AuditService/Domain/AuditEntryFactory.cs` | 要約に幅の要約を足す（null なら従来と同じ文字列）。本文（Detail JSON）はイベント全量なので自動で載る |
| `AuditService/Infrastructure/Steps/AuditEventHandlers.cs` | 変更なし（型は同じ） |
| `Shared.Contracts.Tests/event-schemas.baseline.json` | `TradeDecisionMade.StopWidth` を足す（追加のみ。`UPDATE_EVENT_BASELINE=1` で再生成し差分を確認） |
| `Shared.Contracts.Tests/EventMessageTypeNameTests.cs` | 新イベントを作らないので変更なし |
| RiskManagement（`TradeDecisionMadeHandler` ほか）・MarketMonitor（基準値）・Report（根拠文の読み出し） | 項目を読まない。JSON の未知の項目は無視される。変更なし |
| `docs/api/events-and-ports.md:45` | 項目の列挙に足す |

## 設計

### D1: 下限の適用位置と検証の順序（`TradeDecisionAppService` の新規建て経路）

1. 既存の検証 `0 < AI の幅 < アンカー後の参照価格` をそのまま先に行う（AI の出力が壊れているのは「狭い」とは別。見送り `StopLossDistanceInvalid`）。
2. 下限を求める: `IStopWidthFloorSource.GetFloorAsync(symbol, market, アンカー後の参照価格)` が正の値を返せばそれ（出所つき）。
   null・0 以下・例外（キャンセルを除く）は**参照価格（アンカー後）× 2%**（出所 `Fallback2Pct`）。既定の供給口は常に null。
3. 適用する幅 ＝ max(AI の幅, 下限)。広げたか ＝ AI の幅 ＜ 下限（ちょうど下限は広げない）。
4. 🔴 **適用する幅 ≧ アンカー後の参照価格なら見送る**（`StopLossDistanceInvalid`。ロングのラインが 0 以下になる）。2% の退避では起こらず、
   将来の ATR が価格以上を返した極端な場合だけに当たる。ADR-0049 の「見送らない」は「狭いことを理由に捨てない」であり、
   ラインが成立しない幅は IADR-0035 の不変量で止める（下限を割らない向きに倒す＝価格未満へ縮めることはしない）。
5. 以降（サイジング・採算ゲート・ライン・発注意図・観測ログ・監査）はすべて**適用する幅**を使う。
6. 下限の端数は丸めない（2% の厳密値）。ブローカーへ送る発火価格の丸め（`MoomooPriceRounding`）は 1 刻み未満だけ幅を縮め得る（残余）。

### D2: 監査の形（IADR-0465 決定 2）

- `TradeDecisionMade` に既定 null の末尾項目 `StopWidth`（`StopWidthFloorApplication`: `AiWidthPerShare`・`FloorPerShare`・
  `FloorSource`・`AppliedWidthPerShare`・`Widened`）を足す。新規建てだけが値を持ち、決済・他経路は null。
- `StopWidthFloorSource` は共有契約の列挙（`Unspecified = 0`・`Fallback2Pct = 1`・`Atr14 = 2`。0 を有効値にしない）。
- `OrderIntent` には載せない（発注執行・台帳〔approved_orders の明示写像〕へ流れ、発注に不要）。新イベントにしない
  （判断 1 件に 1 行で `DecisionId` と数量・ラインが同じ行に載る。新イベントは購読・キュー・型名・監査の網羅を増やす）。
- 監査の要約: 値があるとき `・損切り幅 <適用>（AI <AI の幅>・下限 <下限> <出所>[・下限まで拡大]）` を根拠文の前に足す。

### D3: 観測ログ（IADR-0460 決定 3 の拡張）

`StopWidthObservation` に `FloorPerShare`・`FloorSource`・`AppliedWidthPerShare`・`Widened` を足し、ログに `floor`・`floorSource`・
`appliedWidth`・`widened` を出す。既存の `stopWidth`・比率・倍率は **AI の幅**のまま（AI の提案の傾向を測る。IADR-0460 の意味を変えない）。
`stopLossPrice` は適用した幅から引いたライン。

### D4: プロンプト

本判断（`Build`）のリスク制約節に `StopWidthBeyondDailyRangeRule`（「損切り幅は当日の値動き〔日中の高値と安値の差〕より広く取る。
システムが下限を掛け、下回る幅は下限まで広げる」）を足す。一次スクリーニングは幅を返さないので足さない。

### D5: Stage 0

`Stage0DecisionRecorder.SignedQuantity` で、同じ純関数に**LLM の参照価格 × 2%**の下限を渡してから `PositionSizer` を呼ぶ
（Stage 0 には現在値のアンカーが無く、記録の参照価格が判断時点の価格である）。記録の型は変えない（各票の生の幅は従来どおり残る）。

## 窓の表（規則 11）

窓 ＝ LLM が参照価格を見た時点（前の端・LLM の参照価格）と、系がアンカーした時点（後の端・アンカー後の現在値）の間。下限（2%）をどちらの価格で求めるか。

- **増える側**: 窓の間に価格が上がる（例: LLM 100 → アンカー 110。ラインはアンカー 110 から引く）。
- **減る側**: 窓の間に価格が下がる（例: LLM 110 → アンカー 100）。

| 形 | 増える側（100 → 110・AI の幅 1） | 減る側（110 → 100・AI の幅 1） |
| --- | --- | --- |
| 前の端だけ（LLM の参照価格 × 2%） | ✗ 下限 2.0（ラインの基準 110 の 1.82%）＝ 下限を割る | △ 下限 2.2（100 の 2.2%）＝ 必要以上に広げる |
| **後の端だけ（アンカー後 × 2%。本件）** | ✓ 下限 2.2（110 の 2%） | ✓ 下限 2.0（100 の 2%） |
| 両端 max(前, 後) | ✓ 下限 2.2 | △ 下限 2.2（ADR-0049 の定義〔アンカー後〕を外れる） |

ADR-0049 決定 2 は「参照価格（アンカー後の現在値）の 2%」と定める。ラインはアンカー後の価格から引くので、下限も同じ価格で求める形だけが
両側で定義どおりになる。**採るのは後の端だけ**。実測は T-10-1799（増える側・減る側の 2 件。前の端で求める変異で赤）。

## 試験（FR-10 テスト仕様書へ登録。T-10-1797〜1809）

| ID | 内容 | 種別 |
| --- | --- | --- |
| T-10-1797 | 純関数: AI の幅 ＜ 下限 → 下限へ広げる（広げた）／＞ → そのまま／＝ → そのまま（広げない） | 自動 |
| T-10-1798 | 退避の下限 ＝ 参照価格 × 2%（出所 `Fallback2Pct`）。`TradingDefaults.StopWidthFloorFallbackRatio` ＝ 0.02（§5） | 自動 |
| T-10-1799 | 判断: アンカー後の価格で下限を求める（増える側 100→110・減る側 110→100） | 自動 |
| T-10-1800 | 判断（買い）: 幅 0.5 ＜ 下限 2 → ライン 98・株数は広げた幅で算出・監査の値 {0.5, 2, Fallback2Pct, 2, 広げた} | 自動 |
| T-10-1801 | 判断: 幅 3 ＞ 下限 → そのまま（ライン 97・広げない）／幅＝下限 2 → そのまま | 自動（否定形） |
| T-10-1802 | 判断（空売りの建て増し）: 幅 0.5 → ライン ＝ 価格 ＋ 下限（対称） | 自動 |
| T-10-1803 | 下限の供給口: 正の値は出所つきでそのまま（ATR）。null・0・負・例外は 2% へ退避。キャンセルは伝える | 自動（否定形） |
| T-10-1804 | 極端: 下限（供給口）≧ 参照価格 → 見送り（`StopLossDistanceInvalid`・発注意図なし） | 自動（否定形） |
| T-10-1805 | 25% 上限: equity 3,000・価格 100 で幅 0.5 と 2 と 4 の株数が同じ（7 株）。幅 5 は 6 株（1% 側） | 自動 |
| T-10-1806 | 監査: 要約に幅の要約（広げた／広げない）、本文に 5 項目（出所は名前）。null は従来の要約。決済は null。契約の基準に登録 | 自動 |
| T-10-1807 | 観測ログ: 下限・出所・適用した幅・広げたか。AI の幅と比率は AI の幅のまま。ラインは適用した幅から | 自動 |
| T-10-1808 | プロンプト: 本判断のリスク制約節に案内がある（一次には無い） | 自動 |
| T-10-1809 | Stage 0: 幅 0.5 の買いの記録の株数は下限 2% で算出（本番と同じ） | 自動 |

既存の T-10-1749（アンカー 102・幅 2）は下限 2.04 で広がるため、ラインの期待値を 99.96 に直す（観測ログの `stopWidth` は AI の 2 のまま）。

## 受け入れ基準

- [ ] 幅が下限未満なら広げ、サイジング・ライン・発注意図・監査・観測ログが広げた幅を使う。見送らない。
- [ ] 下限以上・ちょうど下限はそのまま。空売りも対称。下限はアンカー後の価格で求める。
- [ ] 広げた幅が参照価格以上なら見送る（2% では起こらない）。
- [ ] 1 注文 25% の上限は変わらず、幅 4% 以下で株数が変わらない。
- [ ] 監査の本文と要約に AI の幅・下限・出所・適用した幅・広げたかが残る。決済では残さない（null）。
- [ ] プロンプトに案内がある。Stage 0 も同じ下限でサイジングする。
- [ ] 自己変異（下限を外す・見送りにする・アンカー前の価格で求める・出所を取り違える・広げた判定を ≦ にする）で赤。

## 検証

（結果は下の「検証結果」）

- `dotnet build backend/backend.slnx`（警告 0）
- `dotnet test` —— TradeDecisionService.Tests・RiskManagementService.Tests・AuditService.Tests・Shared.Contracts.Tests・（念のため）ReportService.Tests・MarketMonitorService.Tests
- `dotnet format backend/backend.slnx --verify-no-changes`
- `node scripts/check-test-traceability.js` / `check-trace-blocks.js` / `check-doc-links.js` / `check-adr-index-sync.js --range=origin/develop..HEAD` /
  `check-adr-index-addendum-loss.js --range=origin/develop..HEAD` / `check-cross-repo-refs.js` / `check-plan-id-qualification.js` /
  `gen-knowledge-graph.js --check` / `check-commit-messages.js --range origin/develop..HEAD` / `check-reading-budget.js`

## 検証結果

2026-09-30・origin/develop 79abb596 起点の作業ブランチで実施。

| 検証 | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx` | `0 Warning(s)` / `0 Error(s)` |
| `dotnet test` TradeDecisionService.Tests | Passed 1158 / Failed 0 |
| `dotnet test` RiskManagementService.Tests | Passed 2108 / Failed 0 |
| `dotnet test` AuditService.Tests | Passed 250 / Failed 0 |
| `dotnet test` Shared.Contracts.Tests | Passed 519 / Failed 0（`UPDATE_EVENT_BASELINE=1` の差分は `TradeDecisionMade.StopWidth` の 1 行追加だけ） |
| `dotnet test` OrderExecutionService / ReportService / MarketMonitorService / Architecture / PlatformShim | Passed 1293 / 1484 / 332 / 199（Skipped 1）/ 188、Failed 0 |
| `dotnet test` IntegrationTests | 環境に Docker が無く実行できない（`Failed to connect to Docker endpoint`。本件と無関係） |
| `dotnet format backend/backend.slnx --verify-no-changes` | exit 0 |
| node 検査器（test-traceability・trace-blocks・doc-links・cross-repo-refs・plan-id-qualification・reading-budget・gen-knowledge-graph --check） | すべて OK（採番の最大値 T-10-1809） |
| `check-adr-index-sync` / `check-adr-index-addendum-loss` / `check-commit-messages`（`origin/develop..HEAD`） | OK（変更した実装ADR 6 件すべてで索引行も変更・追記ブロック 191 件すべて残存・コミット 1 件が規約に適合） |

自己変異（各変異の後に原状へ戻した。落ちた件数）:

| 変異 | 結果 |
| --- | --- |
| 下限を外す（適用する幅 ＝ AI の幅） | 20 件（T-10-1797・1799・1800・1802・1803・1804・1807・1809・1749） |
| 下限を割ったら見送る | 21 件 |
| 下限を LLM の参照価格で求める | 4 件（T-10-1799 × 2・1803・1749） |
| 退避の出所を `Atr14` と書く | 15 件 |
| 広げた判定を「以下」にする | 2 件（T-10-1797・1801） |
| 広げた幅が参照価格以上でも見送らない | 2 件（T-10-1804） |
| サイジングに AI の幅を渡す | 2 件（T-10-1800） |
| Stage 0 の下限を外す | 1 件（T-10-1809） |
| 監査の要約から幅を外す | 2 件（T-10-1806） |
| プロンプトの案内を外す | 2 件（T-10-1808・従来のプロンプトとの一致） |
| 供給口の答えを検めない | 4 件（T-10-1803） |
| 判断の記録に下限の結果を載せない | 14 件 |
