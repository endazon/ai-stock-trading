---
title: 一次スクリーニングの根拠文の文字化け（「監視銘柄」の「柄」）を、固定文の言い換えと化けの検出・目印で抑える（#1290）
type: spec
status: accepted
related_ids: [FR-04, FR-11, FR-15, FR-06, ADR-0003, IADR-0525, IADR-0440, IADR-0521, IADR-0523, IADR-0498, IADR-0248, IADR-0104]
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
漢字に挟まれた英語の固有名（「米国Apple社」）は誤検出する（目印が付くだけで action は変えない）。［2026-10-10 追記］監査で 30 文中 5 文（約 17%）。規則は変えない（末尾の監査対応の節）。

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
Stage 0 の記録であり、そこへ目印を付けた。本判断の根拠文（`TradeDecisionMade`）への検出の拡張は裁定の範囲外として扱わない（残余リスク）。［2026-10-10 追記］この拡張は追記の指示で範囲に入れた（末尾の追記の節）。

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

## ［2026-10-10 追記 / #1290］本判断の根拠文への拡張と PR #1298 のレビューへの対応

指示（コーディネータ・2026-10-10）: 裁定 (c) の趣旨は「台帳・日報・Discord に化けた根拠文が載ったら目印を付ける」である。そこへ届くのは本判断の根拠文
（`TradeDecisionMade.Rationale`）であるため、同じ検出器を本判断の解析の直後に 1 回だけ当て、Warning と目印を付ける。action は変えない。

- 検出の地点: `DecisionOrchestrator` の多数決の直後。採った根拠文（下流へ渡る 1 本）だけに当てる。各票の生の根拠文は対象外。
- 運び方: `OrchestratedDecision.DecisionRationaleGarbleSuspected`（印）と、根拠文の先頭に前置した目印。判断の記録に `decisionRationaleGarble` を足す。
- **契約は変えず、前置を採る**（`TradeDecisionMade` に真偽の項目を足さない）。消費者はどれも根拠文をそのまま転記するため、改修なしで目印が表示される。
  監査台帳の要約の切り詰め（200 文字）でも先頭の目印は消えない。理由の詳細は IADR-0525 決定 4。
- レビュー（AI レビュー・head 2dac1fd8）の 🟡: 一次が本判断へ進んだときの一次の根拠文の化けは警告ログと `screeningRationaleGarble` にしか残らない。
  Loki の検索語（警告ログの固定文言・構造化の属性名）と「どの化けがどこに印として出るか」の表を IADR-0525 とログ・可観測性仕様書に置いた。
- レビューの 🟢: 「一次スクリーニングで見送り」の Info の根拠文も `LogSanitizer.Sanitize` を通す（新しい警告ログと揃える）。
- レビュー: 判定の外にある化けの量を PoC の再計測で数えてもらう旨を IADR-0525 の残余に足した。
- 採番: 初版（head 2dac1fd8）の IADR-0524 は、並行の PR #1299 が同じ番号を取ったため IADR-0525 へ改番した（ファイル名・本文・索引・trace ブロック・コードのコメントをすべて追随。`git grep IADR-0524` は改番の記録の 2 行だけ）。

### `TradeDecisionMade.Rationale` の消費者（規則 9）

走査: `git grep -ln "TradeDecisionMade" -- 'backend/**/*.cs' ':!backend/**/Tests/**'`（イベントの受け手）と、
`git grep -n "\.Rationale\b\|rationales" -- 'backend/**/*.cs' ':!backend/**/Tests/**'`（根拠文の読み手）を突き合わせた。

| 消費者 | 根拠文の扱い | 目印の表示 | 固定する試験 |
| --- | --- | --- | --- |
| 監査台帳 `AuditEntryFactory.From(TradeDecisionMade)` | 要約の末尾に転記（全体を 200 文字で切り詰め）・本文（JSON）に全量 | 要約・本文とも出る（先頭にあるため切り詰めで消えない） | T-10-2521（AuditService） |
| 報告書 `HttpTradeRationaleSource`・`GrpcTradeRationaleSource`（監査台帳から `TradeDecisionMade` を引く） → `TradeHistoryViewBuilder` → `TradeHistoryRenderer` | 日報の明細「判断根拠（要約）」へそのまま転記（パイプのエスケープ・改行の畳みだけ） | 出る | T-10-2522（ReportService） |
| 報告書 `FillPnlAttributionBuilder` → `ReportRenderer`（最良／最悪の行） | そのまま転記（改行の畳みだけ） | 出る | （同じ転記の経路。T-10-2522 と同形のため試験は足さない） |
| 判断の記録 `LLM 判断:`（TradeDecisionService） | rationale にそのまま | 出る＋ `decisionRationaleGarble` | T-10-2520 |
| Stage 0 の記録 `MajorityRationale` | 多数決の根拠にそのまま（各票の生の根拠は別に持つ） | 多数決の根拠に出る・生の票には出ない | T-10-2523 |
| リスク管理（`TradeDecisionMadeHandler`・`OrderScreeningService` ほか）・市場監視（基準値） | 根拠文を読まない（発注意図・価格だけ） | — | 対象外 |
| 通知（Discord） | 判断の根拠文を受け取らない（`NotificationGrpcWire` の `Rationale` は方針の改訂の説明。日報の通知は集計値と散文の要約だけで、本文は閲覧リンク） | — | 対象外（契約を変えないため直す所が無い） |
| 数量の突合 `RationaleQuantityReconciler` | 末尾へ注記を追記 | 前置の目印と衝突しない（目印は数字を含まない） | 既存の試験（変更なしで緑） |

## ［2026-10-10 追記 / #1290］PR #1298 のフェーズ末監査（head de71db27）への対応

1. 🟡 **誤検出（英語の固有名・略語）**: 監査の現実的な根拠文 30 文中 5 文（「米国Apple社」「同社iPhone需要」「前年比YoYで」「米Microsoft社のAzure」
   「決算後gapupした」。約 17%）に目印が付いた。締め方を、実測の化け 4 形（HeaderItem・româ・gl・he）と誤検出 5 文へ当てて実測した
   （判定を Python で再現した。表は IADR-0525 決定 2 の追記）。候補の「大文字を含む並びを疑わない」は実測の「監視銘HeaderItem」（#1290 の 10/09）を
   取りこぼし、しかも小文字だけの「gapup」は残す。先頭だけ大文字の語を除く案は誤検出を 2/5、camelCase まで除いても 3/5 しか減らさず、1 語の化けや camelCase の
   コード断片（HeaderItem と同じ類）を新たに見逃す。「gapup」「YoY」は「gl」「he」と字の形で区別できない。**規則は変えず、限界として IADR と
   本仕様書に記録し、5 文を既知の誤検出として試験 T-10-2524 で固定した**（規則を締めれば赤になる＝意図して直す合図）。
2. 🟡 **方針の「監視銘柄」と固定文の「ウォッチリスト」の対応**: 両者を明示で結ぶ文が無く、別の一覧と読まれ得る（#1034 と同じ種類の読み違え）。
   固定文に「監視銘柄」とは書けない（決定 1）。IADR-0525 の残余リスクに足した。
3. 🟡 IADR-0525「影響」の構造化の値を 2 つ（`screeningRationaleGarble`・`decisionRationaleGarble`）へ直した。
4. 🟡 「どの化けがどこに印として出るか」の表に **KB（RAG）** の列を足した（IADR-0525 とログ・可観測性仕様書）。確定した日報は `report` タグで KB へ入り
   （`ReportKnowledgeMapper`）、`RetrievalSourcePolicy.Default` は `report` を許可するため、目印付きの本判断の根拠文が後の判断のプロンプトへ RAG で
   入り得る。化けを隠さず明示して渡すため害は小さい。表は行（化けの種類）× 列（行き先）の形であり、行き先である KB は列として足した。

母集合（規則 9・10）: 誤検出の限界の記述は `git grep -n "固有名" -- backend docs .ai-context` で引き、検出器の注記・IADR-0525 決定 2・本仕様書・
テスト仕様書（FR-10）の残余リスク・ログ・可観測性仕様書の「判定の限界」の 5 か所を同じ内容へ揃えた。表は `git grep -n "監査台帳・日報 |" -- docs .ai-context`
の 2 か所（IADR-0525・ログ・可観測性仕様書）。構造化の値の数は IADR-0525「影響」の 1 か所だけが「1 つ」と書いていた。

## 受け入れ基準

- [x] 本判断・一次の固定文に「監視銘柄」が出ない（ウォッチリストの全形 × 出口専用の有無）。方針の本文は書き換えない（T-10-2517）。
- [x] 実測の化けの形（U+FFFD・HeaderItem・româ・キリル文字・gl・he）を疑う（T-10-2512）。
- [x] 銘柄・略語・出力形式の語・URL・数値・全角英数字・空白で区切った英語は疑わない（T-10-2513）。
- [x] 目印は前置だけで原文を変えず、二重に付けない（T-10-2514）。
- [x] 一次の根拠文の化けで Warning が 1 行・印が立ち、見送りなら根拠に目印が付く。🔴 action は変えない（関心ありなら本判断へ進む）（T-10-2515）。
- [x] 判断の記録（`LLM 判断:`）の rationale に目印、`screeningRationaleGarble` に印が載る（T-10-2516）。
- [x] Stage 0 の記録の一次の根拠・多数決の根拠に目印が付く（T-10-2518）。
- [x] 本判断の根拠文の化けで Warning が 1 行・印が立ち、根拠文に目印が付く。action・票数は変えない（T-10-2519）。
- [x] `TradeDecisionMade.Rationale` に目印が付き、判断の記録に `decisionRationaleGarble` が載る。発注意図（売買・数量）は変えない（T-10-2520）。
- [x] 監査台帳の要約（切り詰め後）と本文に目印が残る（T-10-2521）。日報の明細に目印がそのまま出る（T-10-2522）。
- [x] Stage 0 の記録は化けた層の根拠にだけ目印を付け、生の票には付けない（T-10-2523）。
- [x] 英語の固有名・略語・言い回しの既知の誤検出 5 文に目印が付く（限界として固定。規則を締めれば赤）（T-10-2524）。
- [x] `dotnet build`（警告 0）・試験・`dotnet format --verify-no-changes`・repo の node 検査が通る。

## 範囲外

- 用途別の temperature（裁定 (b) は採らない）。
- 方針の改訂のプロンプト・初回月報の方針文の語（上の母集合の表）。
- ~~本判断の根拠文（`TradeDecisionMade`）の化けの検出~~ → 追記の指示で範囲に入れた。

## 検証

- `dotnet build backend/backend.slnx`・`dotnet test`（TradeDecisionService.Tests）・`dotnet format backend/backend.slnx --verify-no-changes`。
- node 検査（`check-trace-blocks`・`gen-knowledge-graph --check`・`check-commit-messages`・`check-test-traceability`・`check-adr-index-sync`・`check-cross-repo-refs`・`check-plan-id-qualification`）。
- 変異（1 本ずつ当てて戻した）: テスト仕様書（FR-10）の本件の節の表。
