---
title: 一次スクリーニングの根拠文の文字化け（「監視銘柄」の「柄」）を、固定文の言い換えと化けの検出・目印で抑える（#1290）
type: spec
status: accepted
related_ids: [FR-04, FR-11, FR-15, ADR-0003, IADR-0524, IADR-0440, IADR-0521, IADR-0523, IADR-0498, IADR-0248, IADR-0104]
author: claude (Claude Code)
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04, FR-11)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 一次スクリーニングの根拠文の文字化けを、固定文の言い換えと化けの検出・目印で抑える（#1290）

## 起点

- [#1290](https://github.com/endazon/ai-stock-trading/issues/1290)。PoC（2026-10-06〜09 US）の `LLM 判断:` 行で、一次スクリーニング（claude-haiku-4-5）の根拠文の
  「監視銘柄」の「柄」の位置だけが化けた（監視銘牌・監視銘姓・監視銘牘・置換文字 U+FFFD・監視銘HeaderItem・監視銘româ内・監視銘събњект）。
  10/09 は 96 行中 45 行。本判断（claude-sonnet-5）には出ていない。action は壊れていない。
- 切り分け（issue のコメント・コード読み）: AST とゲートウェイの経路に文字を書き換える処理・切り詰める処理は無い。化け方がバイト破損と合わない
  （有効な別の漢字・1 文字の位置に複数のトークン）。モデル側のトークンのサンプリングと見る。
- 利用者裁定（2026-10-10）:
  - (a) プロンプトの固定文の「監視銘柄」を別の語へ言い換える。利用者が書いた方針の本文は変えない。
  - (c) 一次スクリーニングの根拠文の化け（U+FFFD、漢字の並びの中に唐突に入るラテン文字・キリル文字）を検出して警告ログを出し、
    台帳・日報・Discord への転記時に目印を付ける。action は変えない（Hold に倒さない）。
  - (b) 用途別の temperature は採らない。
- 直列化: #1292（PR #1293・`HeldExitAlwaysJudgedRule`）の後（origin/develop 09c1c42c から着手）。
- 並行: Claude 5.5 系への移行の PR（`LlmAssignments`・単価表・helm の値）は触らない。一次は近く claude-haiku-5-5 へ移るため、検出はモデルに依存させない。

## 計画の確認

- FR-04・ADR-0003: AI 判断は方針とリスク制約の範囲内。判断（action）の決め方は本件で変えない。
- FR-11: 判断の根拠・プロンプト・出力を記録する。Hold は `TradeDecisionMade` を発行しないため、判断の記録（`LLM 判断:` のログ）が唯一の監査記録である（IADR-0104 決定 6）。
- 計画に語の定め（「監視銘柄」をプロンプトで使うこと）は無い。言い換えは実装（プロンプトの文言）の選択であり、計画への環流は要らない。

## 決めること

### 語: 「ウォッチリスト」

- 「監視対象」は採らない。プロンプトは「判断対象」「この銘柄は対象外」を使っており、「監視対象の外」「対象外」が紛れる。
- 「ウォッチリスト」はプロンプトの他の語（方針の銘柄の列挙・判断対象・保有銘柄）と重ならない。コードの名前（`Watchlist`）とも揃う。
- #1292 の固定文（`HeldExitAlwaysJudgedRule`）は「方針の銘柄の列挙」と書いており、市場監視の一覧（ウォッチリスト）とは別物として読める。
- 利用者が書いた方針の本文に「監視銘柄」があっても書き換えない（方針は IADR-0351 決定 3 で書き換えない）。節の冒頭の文が
  「市場監視に登録されている銘柄の一覧（ウォッチリスト）」と言い、方針の本文の語と対応づける。

### 検出（`RationaleGarbleDetector.IsSuspected`。純関数・モデル非依存）

1. U+FFFD を含めば疑う。
2. キリル文字の並びが漢字・かなに直接接していれば疑う。
3. ラテン文字（英字・数字・`._-/:` の連なり）が、直前に漢字・直後に漢字かかなを、どちらも空白なしで持ち、次のいずれでもなければ疑う:
   小文字を含まない（銘柄・略語）／英字 2 文字未満／URL／既知の語（Buy・Sell・Hold・null・true・false・出力形式の項目名）。

限界: 有効な別の漢字への化け（監視銘牌）は検出できない（(a) で出現を減らす）。化けた英字列が句読点・文末の前にあると検出しない。
漢字に挟まれた英語の固有名（「米国Apple社」）は誤検出する（目印が付くだけで action は変えない）。

### 検出の地点と目印の運び方

- **受け取った地点で 1 回だけ**検出する: `DecisionOrchestrator.DecideAsync` が一次の出力を `ParseScreening` した直後。解析不能（安全既定の定型文）は検出しない。
- 疑いがあれば Warning を 1 行出し（`action` と 1 行へ正規化した根拠文）、`OrchestratedDecision.ScreeningRationaleGarbleSuspected` で印を運ぶ。
- 一次で見送る（Hold）とき、下流へ渡る `Decision.Rationale` に目印「⚠ 判断理由に文字化けの疑い: 」を前置する（原文は書き換えない）。
- 一次が関心あり（本判断へ進む）なら action・本判断の根拠文は変えない。印は判断の記録に残る。

## 母集合（規則 9・10）

### (a) 固定文の「監視銘柄」

走査: `git grep -n '"[^"]*監視銘柄' -- 'backend/**/*.cs' ':!backend/**/Tests/**'`（引用符の後に語がある行。origin/develop で 118 行・是正後 110 行）を、LLM へ送るプロンプトかで仕分けた。

| 箇所 | 扱い |
| --- | --- |
| `TradeDecisionPromptBuilder` の `WatchlistSectionTitle`・`WatchlistIsNotPolicyRule`・`WatchlistUnknownLine`・`WatchlistContainsSuffix`・`WatchlistNotContainsSuffix`・`ExitOnlyLine`・件数の行 2 つ（本判断・一次で共用） | **是正**（「ウォッチリスト」へ。8 箇所） |
| `TradeDecisionAppService` のログの文（出口専用の新規建ての見送り・照会の失敗）・`HttpWatchlistProvider` 等の Warning | **除外**（ログであり LLM へ送らない） |
| ReportService `PolicyRevisionPromptBuilder` の 4 行（方針の改訂のプロンプト） | **除外**（裁定の対象は取引判断のプロンプト。方針の改訂は別の用途・別のモデルで、化けは観測されていない。出力は利用者が確定する方針の本文になり、その語を変えるのは裁定の外） |
| ReportService `MonthlyBootstrap.PolicySummary`（初回月報の方針文） | **除外**（方針の本文として利用者が確定する。判断が読むのは日報の方針） |
| MarketMonitor・Notification・InformationCollection・Shared の文字列（画面・Discord・例外・ログの文） | **除外**（LLM へ送らない） |

コメントのうちプロンプトの文言を引用していた 2 箇所（`TradeDecisionAppService` の「監視銘柄: 不明」「監視銘柄なし」）は新しい語へ直した。

### (c) 一次の根拠文の行き先

走査: `git grep -n 'Rationale' -- 'backend/**/*.cs' ':!backend/**/Tests/**'` を `ParseScreening` の結果から辿った。

| 行き先 | 一次の根拠文が載るか | 扱い |
| --- | --- | --- |
| `DecisionOrchestrator` の Info「一次スクリーニングで見送り」 | 載る（見送り） | **目印**（前置済みの根拠文を出す）＋ Warning を別に 1 行 |
| `TradeDecisionAppService` の Info「LLM 判断:」（FR-11。Hold の唯一の監査記録） | 載る（見送り） | **目印**（`rationale` に前置済み）＋ 構造化の値 `screeningRationaleGarble` |
| `Stage0DecisionRecorder` → `Stage0DecisionRecord.Screening.Rationale`・`MajorityRationale`（Stage 0 の記録・永続） | 載る | **目印**（印を使う。検出し直さない） |
| `TradeDecisionMade.Rationale` → 監査台帳（`AuditEntryFactory`）・報告書（`ITradeRationaleSource`・日報の取引履歴） | **載らない**（本判断の根拠文だけ。一次の見送りは `TradeDecisionMade` を出さない） | 対象外（印を運ぶ先が無い） |
| `TradeDecisionHeld`（市場監視の基準値・監査台帳） | **載らない**（理由の列挙値だけ） | 対象外 |
| Discord（NotificationService） | **載らない**（判断の根拠文を送る経路は無い。根拠文を扱うのは方針の改訂だけ） | 対象外 |
| ログからの Discord・台帳への転記（helm・ops の設定） | **無い**（`git grep 'LLM 判断\|一次スクリーニングで見送り' -- docs ops deploy helm` が 0 件） | 対象外 |

issue の「台帳・日報・Discord にそのまま載る」は、コード上は一次の根拠文の経路ではない（上表）。一次の根拠文が残るのは判断の記録（ログ）と
Stage 0 の記録であり、そこへ目印を付けた。本判断の根拠文（`TradeDecisionMade`）への検出の拡張は裁定の範囲外として扱わない（残余リスク）。

### プロンプトの文言を固定する試験

走査: `git grep -nE '監視銘柄（判断時点|この監視銘柄に含まれ|監視銘柄: 不明|- 監視銘柄: |監視銘柄の外にある保有銘柄|「監視銘柄なし」' -- backend docs`、
および縮退の予算の境界 `git grep -n 'WatchlistSection(' -- 'backend/**/Tests/**'`。

| 箇所 | 扱い |
| --- | --- |
| `WatchlistInDecisionPromptTests.GoldenSixWithMeta`（節の全文） | **是正**（意図した golden の更新） |
| 同 `- 監視銘柄: 0 件`（否定形・肯定形）・`- 監視銘柄: 80 件` | **是正**（否定形は語を直さないと空振りで緑になる） |
| `Stage0DecisionRecorderTests` の `- 監視銘柄: 2 件`（否定形・肯定形） | **是正**（同上） |
| `ScreeningContextAssemblerTests`・`ScreeningContextDegradationTests`・`WatchlistInDecisionPromptTests`・`HeldExitAlwaysJudgedInPromptTests` の予算の境界 | **据え置き**（予算は `WatchlistSection(...).Length` から実際の文字数で計算しており、言い換えで不明の形は 82 → 91 文字（+9）に伸びるが境界は同幅で動く。緩めていない。実行して緑） |
| const を直接参照する試験（`HeldOutsideWatchlistExitOnlyTests` ほか） | **据え置き**（const の参照で追随する） |
| `HeldExitAlwaysJudgedInPromptTests` の方針の本文「監視銘柄: NVDA, …」 | **据え置き**（利用者が書く方針の本文の例） |
| テスト仕様書（FR-10）の T-10-1535〜T-10-1537 の文言の引用 | **是正**（節の冒頭に言い換えの注記を置いた） |

文字数の差（UTF-16）: 見出し +3・`WatchlistIsNotPolicyRule` +10・不明の行 +6・含まれる／含まれない +3・件数の行 +3・`ExitOnlyLine` +3。

## 受け入れ基準

- [x] 本判断・一次の固定文に「監視銘柄」が出ない（ウォッチリストの全形 × 出口専用の有無）。方針の本文は書き換えない（T-10-2517）。
- [x] 実測の化けの形（U+FFFD・HeaderItem・româ・キリル文字・gl・he）を疑う（T-10-2512）。
- [x] 銘柄・略語・出力形式の語・URL・数値・全角英数字・空白で区切った英語は疑わない（T-10-2513）。
- [x] 目印は前置だけで原文を変えず、二重に付けない（T-10-2514）。
- [x] 一次の根拠文の化けで Warning が 1 行・印が立ち、見送りなら根拠に目印が付く。🔴 action は変えない（関心ありなら本判断へ進む）（T-10-2515）。
- [x] 判断の記録（`LLM 判断:`）の rationale に目印、`screeningRationaleGarble` に印が載る（T-10-2516）。
- [x] Stage 0 の記録の一次の根拠・多数決の根拠に目印が付く（T-10-2518）。
- [x] `dotnet build`（警告 0）・試験・`dotnet format --verify-no-changes`・repo の node 検査が通る。

## 範囲外

- 用途別の temperature（裁定 (b) は採らない）。
- 方針の改訂のプロンプト・初回月報の方針文の語（上の母集合の表）。
- 本判断の根拠文（`TradeDecisionMade`）の化けの検出（観測されておらず、裁定は一次の根拠文を指定した）。

## 検証

- `dotnet build backend/backend.slnx`・`dotnet test`（TradeDecisionService.Tests）・`dotnet format backend/backend.slnx --verify-no-changes`。
- node 検査（`check-trace-blocks`・`gen-knowledge-graph --check`・`check-commit-messages`・`check-test-traceability`・`check-adr-index-sync`・`check-cross-repo-refs`・`check-plan-id-qualification`）。
- 変異（1 本ずつ当てて戻した）: テスト仕様書（FR-10）の本件の節の表。
