---
title: 定時サイクルの判断対象に監視銘柄の外の保有銘柄を足し、出口専用（決済か Hold）で判断する（#1286）
type: spec
status: accepted
related_ids: [FR-02, FR-04, FR-03, FR-10, UC-01, ADR-0003, ADR-0041, ADR-0044, ADR-0051, IADR-0521, IADR-0023, IADR-0119, IADR-0358, IADR-0374, IADR-0440, IADR-0475, IADR-0490]
author: claude (Claude Code)
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-01 基本フロー 3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02・FR-03・FR-04)
  - planning:projects/ai-stock-trading/07_adr/ADR-0051_policy-take-profit-numeric-line-warn-not-block.md (決定 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0044_watchlist-in-decision-prompt-and-stage0-asof.md (実測 3)
---

# 定時サイクルの判断対象に監視銘柄の外の保有銘柄を足す（#1286）

## 起点

- [#1286](https://github.com/endazon/ai-stock-trading/issues/1286)。利用者裁定（2026-10-09）「保有銘柄も判断対象に」 —— 監視銘柄の外の保有銘柄も、損切り（S1）だけでなく
  LLM の取引判断（売り・利確）の対象にする。
- PoC（2026-10-09）: 監視銘柄 NVDA・META・TSLA・COIN・MARA・MSTR・SMCI・PLTR、保有 AAPL・MSFT・AMZN・GOOGL（いずれも監視銘柄の外）。

## 計画の確認（実装の不足か、計画の変更か）

project-planning `origin/main` の `projects/ai-stock-trading/` を読んだ。

| 計画書 | 記述 | 読み |
| --- | --- | --- |
| UC-01 基本フロー 3 | 取引判断サービスが「日報の方針・**保有ポジション**・収集情報・過去の判断（RAG）」を文脈に LLM で判断を生成する | 判断対象を監視銘柄に限っていない |
| 04_workflows 01（定時） | 「AI判断 日報方針＋保有状況＋収集情報＋過去判断RAG」 | 同上 |
| FR-03 | 「監視銘柄の価格変動を監視し…取引サイクルを即時起動できる」 | 監視銘柄は急変の監視対象。定時の判断対象の定めではない |
| FR-02・FR-04 | 定時トリガーで判断。判断は方針とリスク制約の範囲内 | 判断対象の範囲の定めは無い |
| ADR-0051 決定 3 | 利確は LLM が判断し、系は書式の条件に達した**保有**を計算して判断へ示す | 保有が判断に掛かることを前提にしている |
| ADR-0044 実測 3 | 「定時サイクルは監視銘柄を読み、どの銘柄を判断するかを決める」 | 実装の現状の記述（実測）であり、決定ではない |
| 監視銘柄の外への新規建て | 定めが無い | 本件は出口（決済・Hold）に限る |

結論: **計画は保有銘柄の判断を求めており（少なくとも明確に許しており）、監視銘柄だけを巡回するのは実装の不足である**（分岐 A）。
新規建ての可否は計画に定めが無いため、監視銘柄の外の保有銘柄は出口専用にする（IADR-0521 決定 2）。計画への環流は要らない。

## 現況（origin/develop 586902eb）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | 定時サイクルは `IWatchlistProvider.GetWatchlistAsync` の一覧だけを巡回し、銘柄ごとに `DecideAsync` を呼ぶ | `Infrastructure/Steps/InformationCollectedHandler.cs` |
| 2 | 保有の照会（`IHeldPositionProvider`）は銘柄単位で、判断の入力（プロンプトの保有状況・建玉効果）に使う。全保有銘柄を返す口は無い | `Features/TradeDecision/IHeldPositionProvider.cs` |
| 3 | 保有の口は `GET /risk-controls/open-positions`（gRPC は `GetOpenPositions`）で、応答は全建玉の一覧 | `HttpHeldPositionProvider.cs`・`GrpcHeldPositionProvider.cs` |
| 4 | 建玉効果は保有から決まり（IADR-0119）、決済は保有全量・統制で止めない | `TradeDecisionAppService.cs` |
| 5 | 市場監視は保有と監視銘柄の和集合の現在値を取る（#1190）。保有のみの銘柄にも現在値はある | 市場監視 |

## やること

1. `IHeldSymbolsProvider.GetHeldSymbolsAsync`（保有中の (銘柄, 市場)。不明は null）を足し、Http・gRPC・NoOp の保有照会の 3 実装に実装する（解釈は 1 つ）。
2. `ScheduledJudgmentTargets.Build`（監視銘柄 ∪ 保有。外の保有だけ出口専用）を足し、`InformationCollectedHandler` が使う（保有の供給口は必須依存）。
3. `DecisionTrigger.ExitOnly` を足し、`DecideAsync` で出口専用の判断の新規建てを判断後の見送り `ExitOnlyOpenOutsideWatchlist` にする（語彙の末尾へ）。
4. プロンプトの監視銘柄節の末尾に出口専用の行を足す（出口専用の判断だけ）。
5. `Program.cs` で `IHeldSymbolsProvider` を保有照会と同じ実装に配線する。
6. 試験・テスト仕様書・観測と監査の文書（見送り理由の語彙）を更新する。

## 受け入れ基準

- [x] 定時サイクルは監視銘柄に加え、監視銘柄の外の保有銘柄も判断する（T-10-2480・T-10-2482）。
- [x] 保有が不明（照会失敗・解釈不能・未結線）なら監視銘柄だけを判断する。不明を空へ倒さない（T-10-2481・T-10-2482）。
- [x] 保有のみの銘柄の判断で LLM が決済を返せば保有全量の決済になる（T-10-2483）。新規建てを返せば発注せず見送る（T-10-2484）。
- [x] 監視銘柄の判断・プロンプトは変えない（T-10-2485）。見送り理由の語彙は 19 値で末尾へ足す（T-10-2486）。
- [x] リスク統制・取引ガードの既存試験は変更なしで緑（`TradingDefaults` の試験を含む）。
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と repo の node 検査が通る。

## 母集合（規則 9・10）

誤りの側の文字列で走査した（`.ai-context/specs`・`.ai-context/adr`・`CHANGELOG.md` を除く）:
`git grep -nE "監視銘柄（watchlist）を巡回|watchlist 巡回|監視銘柄を巡回|監視銘柄だけを(判断|巡回)|判断対象は監視銘柄|語彙 ?18|18 値|18 種"`、
および見送り理由を列挙する文書の走査 `git grep -n AddOnBlockedByRiskControls -- docs deploy`。

| 箇所 | 扱い |
| --- | --- |
| `InformationCollectedHandler.cs` 冒頭の注記（監視銘柄を巡回） | **是正**（保有銘柄を足した） |
| `docs/observability/observability.md`（見送りの理由「ほか 18 種」） | **是正**（19 種・新しい理由を列挙） |
| `docs/data/audit-events.md`（判断後の見送りの理由の説明） | **追記**（新しい理由） |
| `docs/tests/FR-10_risk-controls-tests.md` T-10-2322・T-10-2386 の行（17 値・18 値） | **除外**（その時点の試験の記述。T-10-2486 の行を足した。既存の版の行を書き換えない慣行に従う） |
| `InformationCollectedConsumerTests.cs` 冒頭の注記（watchlist 巡回） | **除外**（試験の筋書きの要約。T-10-2482 が保有を足した形を固定する） |
| `deploy/observability/README.md`・ダッシュボードの説明 | **除外**（`AddOnBlockedByRiskControls` を個別に説明する行であり、語彙の総数・全列挙を持たない） |

## 範囲外

- 価格変動の即時判断（FR-03）を保有のみの銘柄へ広げること。
- 保有のみの銘柄の情報収集（ニュース）。
- 監視銘柄の外への新規建て（計画に定めが無い）。

## 検証

- `dotnet build backend/backend.slnx`・`dotnet test backend/backend.slnx`・`dotnet format backend/backend.slnx --verify-no-changes`。
- repo の node 検査（`scripts/`）。
- 変異 H1（出口専用の見送りを外す）→ T-10-2484・T-10-2482 が赤。H2（保有を足さない）→ T-10-2482 が赤。いずれも戻して緑。
