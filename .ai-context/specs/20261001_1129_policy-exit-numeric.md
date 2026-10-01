---
title: 日報の方針の利確条件を数値で求め、欠けたら確定の前に警告し、達した保有は判断へ明示する（#1129）
type: spec
status: accepted
related_ids: [FR-04, FR-07, FR-09, FR-14, UC-03, ADR-0003, ADR-0048, ADR-0050, IADR-0470, IADR-0351, IADR-0467, IADR-0431, IADR-0116, IADR-0352, IADR-0119, IADR-0125]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §7・粒度の対応表・§日報の追加記載要件の先頭)
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md (決定 4)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 日報の方針の利確条件を数値で求め、欠けたら確定の前に警告し、達した保有は判断へ明示する（#1129）

## 背景

監査からのオーナー起票（#1129）。確定済みの方針の利確の条件は「含み益が十分に出た段階で…利確」だけで数値が無い。判断はこれを 173 回引用し、
保有中の銘柄の一晩の判断は Hold 17・Buy 1・Sell 0。判断のプロンプトの規則（`TradeDecisionPromptBuilder.ExitFollowsPolicyRule`）は
「方針に出口の基準があれば従い、無ければ Hold」であり、「十分に」は判断が確かめられない（計画 ADR-0048 決定 4 と同じ類）。
損切りは機械的（S1・幅の下限 2%）なのに利確は実現しない。計画のテンプレートは翌営業日の売買条件を「銘柄別の具体値」とする（粒度の対応表）が、
確定済みの日報（daily-2026-09-30）には数値の売り条件が無い。

### オーナー裁定（#1129 のコメント・2026-10-01）

1. 日報の方針（初稿と `/policy` の改訂）を作るとき、保有中の銘柄と新規建ての対象には、利確の条件を数値（% か価格、一部利確の割合を含む）で書くことを求める。数値が無い方針は**確定の前に警告する（ブロックしない）**。
2. 判断は今と同じく LLM が方針を見て行う。方針に数値の利確条件があり、それに達しているときは、プロンプトで「方針の利確条件に達している」と明示する。**数値はコードで計算する**（含み益の率と条件との比較）。
3. 機械的な自動の利確（S1 と対になる自動売り）は**採らない**。
4. 計画側の環流: 「利確条件を数値で書く」を日報テンプレート §7 の必須要件にするかを planning に記録する（起票はコーディネータ。本書は起票案を置く）。

## 範囲

1. 方針の改訂 LLM（`PolicyRevisionPromptBuilder`）に、日報だけ、利確の条件を数値で書く案内の節を足す（`判断へ渡る材料` の節〔#1118〕の直後）。
2. 方針の文から数値の利確条件を取り出す純関数を共有カーネルに置く（`PolicyTakeProfitConditions`）。報告書と判断が同じ部品を引く。
3. 数値の利確条件が 1 件も無い日報の方針を、確定の前に警告する（自動生成の提示の要約・`/policy` の案内文と Discord の見出し・本文の改訂の記録・警告ログ）。確定は止めない。方針の本文へは入れない。
4. 判断の保有状況の節（本判断・一次）に、到達した利確条件と系が計算した含み益の率を 1 行で明示する。一次の縮退の予約を足す。
5. 計画への環流の起票案（本書の末尾）。

範囲外: 確定のブロック（計画の水準の統制）・自動の利確・判断の既定の Hold の規則の変更・`TradeDecisionAppService`（LLM の前の見送り。並行レーン #1130）・一部利確の実行（全量決済のまま）・承認の Web 画面。

## 前提（読んだ制約）

- 計画 ADR-0048 決定 4（判断が確かめられない条件を方針に書かない・方針を生成する LLM へ材料を示す・確定は利用者との対話）。ADR-0003（数値はコードで計算する）。
- 04_report-templates 日報 §7 の表（買い条件・売り条件の列）と粒度の対応表「翌営業日の売買条件・銘柄別の具体値」。§7 に数値を必須とする記述は無い（環流の対象）。
- IADR-0351 決定 3（出口の既定は Hold。本件は変えない）・IADR-0119 決定 1（手仕舞いは全量）。
- IADR-0467 決定 7（材料の節）。IADR-0125（初稿は継続案。LLM が方針を作らない）。IADR-0431 決定 5（表示と確定される原文を食い違わせない）。
- IADR-0352 決定 5・#866（要約の警告行と契約アセンブリの印で通知を Warning へ上げる形）。

## 設計の選択（IADR-0470 に記録）

| 論点 | 選んだ形 | 理由 |
| --- | --- | --- |
| 「初稿」の案内 | 初稿は継続案（LLM が方針を作らない）なので、初稿に効くのは警告だけ。案内は改訂プロンプトにだけ足す | 散文の LLM（`ReportNarrativePromptBuilder`）は方針を作らない。そこへ足すと方針と散文が食い違う |
| 検出器の置き場 | 共有カーネル `AiStockTrading.Shared.Kernel.Trading.PolicyTakeProfitConditions` | 警告と判断の到達が同じ読み方をする（片方だけが読める方針を作らない） |
| 警告の出しどころ | 実在する確定の前の経路 3 つ（提示の要約→Discord〔Warning〕／`/policy` の案内文→Discord の見出し〔承認待ちでも警告の行だけ出す〕／本文の改訂の記録） | 提示の要約の警告行と印は既存の形（IADR-0352 決定 5）。`/policy` は承認待ちのとき案内文を出していなかったため、印の行だけを拾う |
| 契約 | 新しい項目は足さない。印 `ReportSummaryMarkers.PolicyTakeProfitMissingPrefix` を本文に埋める | 契約の形を変えない（旧版との読み分けが要らない） |
| 判断への明示 | 到達したときだけ 1 行（3 件まで）。未到達・不明は何も足さない | 裁定は「達しているときは明示」。未到達でも書くと全保有のプロンプトが変わる |
| 一次 | 短縮版にも同じ行。縮退の保護分に +400 | 一次は門（Hold で本判断が走らない） |

## 母集合（規則 9。origin/develop 23b73f35）

誤りの側の文字列・触る部品で全文書を走査してから挙げた。

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| 方針（`PolicySummary`）を書く経路 `PolicySummary =`（ReportService・テスト以外） | `ReportAutoGenerator`（初稿＝継続案）・`ReportPolicyRevisionService`（改訂＝LLM 案・新規の当日日報の土台）・`UpsertReportDraft` / `DraftReport` の口（利用者が書く）・`MonthlyBootstrap`（月報）・`ReportOwnerWriteGrpcService`（応答の写し）・EF の写し | 初稿と改訂に警告。**利用者が自ら書く口と月報は対象外**（裁定は「方針を作るとき」の AI の生成と初稿。利用者の手書きは確定の対話そのもの） |
| 方針を作る LLM のプロンプト `PolicyRevisionPromptBuilder.Build` / `ReportNarrativePromptBuilder.Build` | 改訂（`LlmReportPolicyReviser`）と散文（`HttpReportNarrativeDrafter`） | 改訂にだけ案内。散文は方針を作らない |
| 提示の要約 `ReportSummary.Build(`（本体） | `ReportAutoGenerator` の 1 か所 | 引数を足す（省略時は従来と同じ） |
| 改訂の Discord 表示 `PolicyRevisionMessage.Build(`（本体） | `PolicyRevisionCommandHandler` の 1 か所（REST・gRPC の受け手は同じ処理器） | 印の行を見出しへ |
| 提示の通知の重大度 `ReportSummaryMarkers` の参照（本体） | `NotificationFormatter` の 1 か所 | 印を足す |
| 判断の保有状況の節 `AppendHeldPositionSection(Short)?(sb` | 本判断・一次の各 1 か所 | 両方に行を足す |
| 縮退の予約を予算に足し込む試験 `NewsStatusReserveChars` | `ScreeningContextAssemblerTests`（2 か所）・`ScreeningContextDegradationTests`・`WatchlistInDecisionPromptTests` | 予約の追加で予算を同幅ずらす（実測で 4 件が赤→追随） |
| 「十分に」（本体の文言） | 本体に 0 件（テストの注記のみ） | 方針の文はデータであり、コードに曖昧語の出どころは無い |
| 「利確」（本体） | `TradeDecisionPromptBuilder.ExitFollowsPolicyRule` の 1 件（他は「権利確定」） | 規則は変えない（裁定） |

除外の理由: 週報・月報の方針は銘柄別の売買条件の粒度を持たない（粒度の対応表）ため警告しない。

## 窓の表（規則 11）

**N/A。** 本件は方針の文の読み方と、ある時点の取得単価・現在値の比較だけで、時間差（窓の前後の端）を扱わない。
評価価格は既存の保有状況の節と同じ値（急変はトリガーの価格・定時は現在値）を使い、新しい時間の窓を作らない。

## 実装

### 共有

- `Shared.Kernel/Trading/PolicyTakeProfitConditions.cs`: `Extract` / `HasAny` / `ForSymbol` / `Reached` / `GainPercent` / `Describe`。
- `Shared.Contracts/Events/ReportDraftPresented.cs`: `ReportSummaryMarkers.PolicyTakeProfitMissingPrefix`。

### 報告書

- `PolicyRevisionPromptBuilder`: 日報だけ「売り条件（利確）の書き方」の節（`NumericTakeProfitHeading` / `NumericTakeProfitRule` / `VagueTakeProfitRule`）。
- `Domain/PolicyTakeProfitCheck.cs`: `WarningFor(kind, policy)`（日報で 1 件も無ければ警告の全文）。
- `ReportSummary.Build`: 省略可能な `policyTakeProfitWarning`（数値行の後・散文の前）。`ReportAutoGenerator` が初稿の方針で判定して渡す。
- `ReportPolicyRevisionService`: 案の方針で判定し、案内文の末尾の行・改訂の記録（案の方針の直前）・警告ログ。

### 通知

- `NotificationFormatter`: 印があれば提示の通知を Warning。
- `PolicyRevisionMessage.Build`: 承認待ちにできた案でも、案内文のうち印で始まる行を見出しの通へ出す。

### 取引判断

- `TradeDecisionPromptBuilder`: 保有状況の節（本判断・一次）に `TakeProfitReachedLine`（`TakeProfitReachedLinePrefix`）。
- `ScreeningContextAssembler`: `TakeProfitReachedReserveChars = 400` を銘柄の保護分へ。

## 試験（T-10-1880〜1891。docs/tests/FR-10_risk-controls-tests.md の末尾の節）

| ID | ファイル |
| --- | --- |
| T-10-1880〜1883 | `backend/Shared/AiStockTrading.Shared.Kernel.Tests/Trading/PolicyTakeProfitConditionsTests.cs` |
| T-10-1884〜1887 | `backend/Services/ReportService/Tests/Features/Reports/PolicyTakeProfitWarningTests.cs` |
| T-10-1888 | `backend/Services/NotificationService/Tests/Features/Notifications/PolicyTakeProfitWarningNotificationTests.cs` |
| T-10-1889〜1891 | `backend/Services/TradeDecisionService/Tests/Features/TradeDecision/DecideTrade/TakeProfitReachedInPromptTests.cs` |

既存の試験の追随: 縮退の予算を予約の分だけずらした 4 件（上の母集合）。

## 自己変異の結果

1 つずつ入れて対象の試験を走らせ、元へ戻した（実測 2026-10-01）。

| 変異 | 落ちた試験 |
| --- | --- |
| 🔴 改訂プロンプトの利確の案内を外す（`if (false)`） | T-10-1884（1 件） |
| 🔴 検出器を常に「ある」（`HasAny => true`） | T-10-1885・1886・1887（報告書 3 件）・T-10-1881（11 件） |
| 🔴 警告を出さない（`WarningFor => null`） | T-10-1885・1886・1887（3 件） |
| 判断の到達の行を出さない | T-10-1889（2 件）・T-10-1891 |
| 到達をしきい値と比べない（`true ||`） | T-10-1890（未到達）・T-10-1883（4 件） |
| ショートの符号を反転しない | T-10-1889（ショート）・T-10-1891・T-10-1883（2 件） |
| 承認待ちの改訂案で警告の行を出さない | T-10-1888（1 件） |
| 提示の通知を印で Warning へ上げない | T-10-1888（1 件） |
| 損切り・含み損の節を除外しない | T-10-1881（含み損の 1 件。負号の「-2%」は符号の規則でも落ちるため生存しない） |
| 名指しの条件を優先しない | T-10-1882・T-10-1890（他の銘柄） |

## 残余リスク

- 自由文のすべては読めない（「取得単価の 1.05 倍」・分数・英語）。読めない方針は警告され、判断には何も足さない（安全側）。
- 「+5%」を前日比の意味で書いても、同じ節に「前日」等が無ければ取得単価からの率と読む（案内で取得単価からの率を求めて抑える）。
- 警告は「1 件も無い」だけを見る。銘柄ごとの有無は見ない（確定の経路が保有・新規建ての対象の一覧を持たない）。
- 承認の Web 画面の要約欄・初稿の本文には出さない。自動生成の経路は警告ログを持たない（通知の Warning で代える）。
- 一部利確は実行できない（全量決済）。

## 計画への環流（起票案）

起票はコーディネータが行う（本書は案のみ）。重複の確認: project-planning で「利確 条件 数値 日報 §7 売り条件」を検索し、該当なし（2026-10-01）。

**タイトル**: `[feedback] 日報 §7 の売り条件（利確）を数値（% か価格・一部利確の割合）で書くことを必須要件にする（endazon/ai-stock-trading#1129 のオーナー裁定）`

**本文**:

```markdown
### フィードバック元（実装リポジトリ）
ai-stock-trading

### 実装側の根拠
endazon/ai-stock-trading#1129（オーナー裁定 2026-10-01 のコメント）／作業仕様書 `.ai-context/specs/20261001_1129_policy-exit-numeric.md`／IADR-0470

### 起点となる計画書の ID
FR-07, FR-04, ADR-0048, ADR-0003

### 種別
要求の不足

### 現状（計画書の記述 / As-Is）
- `06_technical/04_report-templates.md` の粒度の対応表は、日報の目標値を「翌営業日の売買条件・銘柄別の具体値」とする。
- 日報 §7 の表（銘柄 / 方針 / 買い条件 / 売り条件 / 上限数量/金額）は、売り条件の書き方を定めていない。
- §日報の追加記載要件の先頭（ADR-0048 決定 4）は「判断へ渡される材料だけを条件に書く」と定めるが、**条件が数値で確かめられるか**は定めていない。

### 問題点 / あるべき姿（To-Be）
- 確定済みの方針（daily-2026-09-30）の利確の条件は「含み益が十分に出た段階で…利確」だけで数値が無く、判断は条件に達したかを確かめられない。判断の規則（方針に出口の基準が無ければ保有継続）に倒れ、保有中の銘柄の一晩の判断は Hold 17・Buy 1・Sell 0 だった。損切りは機械的に執行されるのに利確は実現せず、損益の分布が負の側へ歪む。
- 「十分に」は ADR-0048 決定 4 と同じ類（判断が確かめられない条件）である。
- To-Be（オーナー裁定 2026-10-01）: 日報 §7 の**保有中の銘柄と新規建ての対象**の売り条件に、**利確の条件を数値**（平均取得単価からの含み益の率か価格。一部利確ならその割合）で書くことを必須要件にする。数値の無い語（「十分に」「適切に」）だけの利確の条件は書かない。
- 確定は止めない（数値が無い方針は確定の前に警告するに留める）。機械的な自動の利確は採らない。

### 背景 / 経緯
- 実装は裁定に沿って次を入れた（IADR-0470）: ① 方針の改訂 LLM へ数値で書く案内（日報だけ）② 数値の利確条件が 1 件も無い日報の方針を確定の前に警告（提示の要約・`/policy` の Discord 表示・本文の改訂の記録。確定は止めない）③ 判断は LLM のまま、方針の数値の利確条件に達した保有だけ、コードで計算した含み益の率とともにプロンプトへ明示。
- 統制の 3 点セット（CLAUDE.md の規律）: **定める統制**＝§7 の利確の条件を数値で書く／**現在の実現手段**＝改訂 LLM への案内と確定の前の警告（IADR-0470）／**配備までの暫定手段**＝不要（実現手段は配備済み。ただし確定はブロックしないため、最終の担保は利用者の確定の対話）。

### 反映案
- `04_report-templates.md` §日報の追加記載要件へ 1 項を足す: 「§7 の売り条件は、保有中の銘柄と新規建ての対象に、利確の条件を数値（平均取得単価からの % か価格。一部利確ならその割合）で書く。数値の無い語だけの条件は書かない。数値の利確条件が無い方針は確定の前に警告される（確定は止めない）」。変更履歴に本裁定を根拠として残す（本書は `fixed`）。
- 必要なら ADR-0048 決定 4 の補足として、または新しい ADR として記録する（判断が確かめられる条件＝渡る材料であり、かつ数値で比べられる条件）。
- 判断の側の扱い（達したときの明示・自動の利確は採らない）を計画書へ写すかは、計画側の判断に委ねる。
```
