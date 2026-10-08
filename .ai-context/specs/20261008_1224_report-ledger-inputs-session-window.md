---
title: 監査台帳を JST の暦日で引く報告書の入力（借株料・損切りの手法とその解決・強制買戻しの推定・自動縮小・為替の状態）を市場ごとのセッションの窓に揃え、LLM 利用実績は JST の暦日のまま「集計したセッション」の行で明記する（#1224）
type: spec
status: accepted
related_ids: [FR-06, FR-10, FR-16, UC-03, UC-04, UC-05, ADR-0053, ADR-0027, ADR-0040, ADR-0016, ADR-0022, ADR-0017, IADR-0492, IADR-0516]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0053 (決定 2「前回の同種の報告書の生成の後から今回の生成までに閉場したセッションを集計」・フォローアップ 3「監査台帳を JST の暦日で引く入力が窓と揃っていない残余を扱う」)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates (日報 §4・月報 §6 / §6.1 / §7 の各入力・集計したセッションの範囲)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions (§6 / §6.1 月次 LLM 費用上限 15,000 円・月報に消費率を記載)
  - planning:projects/ai-stock-trading/07_adr/ADR-0027 (借株料: 決定 3 計上日の属する日・月へ帰属・按分しない)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases (UC-03〜05)
---

# 監査台帳を JST の暦日で引く報告書の入力をセッションの窓に揃える（#1224）

## 背景（issue の観測）

- IADR-0492 / 計画 ADR-0053 で、約定・乖離の取り込み・稼働率は市場ごとのセッションの窓
  `(前の営業日の DailyAt, 期間の最終営業日の DailyAt]` に大引けを迎えたセッションで引くようになった。
- 期間の集計として監査台帳（と強制買戻しの推定台帳）を **JST の暦日 `[PeriodStart, PeriodEnd]`** で引く入力
  （LLM 利用実績・借株料・損切りの手法とその解決・強制買戻しの推定・自動縮小・為替の状態）は窓に揃っていない
  （IADR-0492 §残余リスク）。米国のセッション（ET D ＝ JST D 22:30〜D+1 05:00）の記録は、約定（daily-D+1）と
  別の日報に載るか、**どの日報にも載らない**（daily-D は D 16:00 JST に生成されるため D 16:00〜24:00 の記録はまだ無く、
  daily-D+1 は JST D+1 だけを引く）。

## 実測（origin/develop 8c7205cc）

- `ReportAutoGenerator.CollectInputsAsync`（自動生成と `/report regenerate` で 1 本）が各供給元へ `due.PeriodStart, due.PeriodEnd` を渡す
  （`SafeReductionsAsync`・`SafeBuyInInferencesAsync`・`SafeFxSourceStatusAsync`・`SafeLlmUsageAsync`・`SafeBorrowFeesAsync`・
  `SafeStopLossMethodsAsync`・`SafeStopLossMethodResolutionsAsync`）。
- 監査台帳の供給元（REST / gRPC）は `[from, to]` を `AuditPeriodRange.JstHalfOpen`（JST の 0 時の半開区間）で台帳の記録時刻へ写す。
  損切りの解決だけは前後 1 日を足す（承認と DecisionId で突き合わせる）。強制買戻しの推定はリスク管理の推定台帳（`/risk-controls/buy-in-inferences`）。
- 各記録の持つ時刻と市場:

| 入力 | 記録（イベント） | 市場 | 時刻 |
| --- | --- | --- | --- |
| 借株料 | `BorrowFeeAccrued` / `BorrowFeeAccrualUnavailable` | あり | `TradingDay`（計上日）・`AccruedAt` / `ObservedAt` |
| 損切りの手法（承認時点） | `OrderApproved` | あり（`Intent.Market`） | `ApprovedAt` |
| 損切りの手法の解決 | `StopLossMethodResolved` | あり | `OccurredAt`（**承認と DecisionId で突き合わせる**） |
| 強制買戻しの推定 | `BuyInInferred` | あり | `InferredAt` |
| 自動縮小 | `MaintenanceMarginReductionExecuted` | 明細ごと（`Items[].Market`） | `ExecutedAt` |
| 為替の状態 | `FxRateSourceFellBack` ほか 4 種 | なし（`PositionClosedWithStaleFxRate` だけあり） | `OccurredAt` |
| LLM 利用実績 | 監査台帳の LLM 計上 | なし | 記録時刻 |

- 自動縮小の供給元は既定 `NoMarginReductionRecordSource`（空列。発火元が未結線）。借株料の計上（`BorrowFeeAccrualService.Accrue`）も呼び出し元が未結線。

## 計画の確認

- 計画 ADR-0053 決定 2: 各報告書は前回の同種の報告書の生成の後から今回の生成までに閉場したセッションを集計する。週報・月報は日報の和。
  「月報の税金レビューも同じ範囲で集計する」。**費用（LLM）の月次の扱いは書いていない。**
- 05_trading-assumptions §6 / §6.1: 月次 LLM 費用上限 15,000 円（取引判断サイクルのみ）・**月報に消費率を記載**。上限は「月」単位であり、
  費用統制サービスの月次カウンタは暦の月（`CostControlAppService.MonthKey`）である。
- ADR-0027 決定 3（借株料）: 計上日の属する日・月へ帰属させる（按分しない）。計上日（`TradingDay`）は市場の現地の日であり、
  その日のセッションを集計する報告書へ帰属させれば按分は起きない。
- 結論: 市場を持つ記録は窓へ揃えて計画に反しない（ADR-0053 決定 2 の帰結）。LLM 利用実績を窓へ寄せるかは「費用の月次」を窓に揃えるかの問いになり、
  計画の消費率（暦の月の上限に対する比率）と食い違う。**本 PR では据え置き（JST の暦日）とし、計画の裁定は要しない**（現状維持であり、issue の受け入れ基準 3 が
  市場を持たない入力を JST の暦日のままにする選択を明示的に許している）。

## 決定（IADR-0516 の要約）

1. **報告可能になる瞬間**で数える。市場を持つ記録は「そのセッション（市場の現地取引日）の大引け」と「記録の時刻」の遅いほう、
   市場を持たない記録は記録の時刻。その瞬間が窓 `(ClosedAfter, ClosedUntil]` に入る報告書にちょうど 1 回載る
   （窓は端で接し隙間も重なりも無い。報告可能になる瞬間は記録ごとに 1 つ）。生成は `ClosedUntil` 以後なので、窓に入る記録は生成時点で必ず台帳にある。
2. 入力ごとの扱い:

| 入力 | 扱い | 報告可能になる瞬間 |
| --- | --- | --- |
| 借株料（計上・未計上） | 窓 | (市場, `TradingDay`) の大引けと記録時刻の遅いほう |
| 損切りの手法（承認時点） | 窓 | (承認の市場, 承認時刻の現地取引日) の大引けと承認時刻の遅いほう |
| 損切りの手法の解決 | 承認に従う | 承認と DecisionId で突き合わせる（解決の時刻では数えない） |
| 強制買戻しの推定 | 窓 | (市場, 推定時刻の現地取引日) の大引けと推定時刻の遅いほう |
| 自動縮小 | 窓 | 明細の市場ごとの上記の瞬間の最も早いもの（明細が無ければ執行時刻） |
| 為替の状態 | 窓 | 発生時刻（市場を持たない）。鮮度切れのレートでの決済は市場を持つので約定と同じ形 |
| LLM 利用実績 | **JST の暦日のまま** | 費用の上限が暦の月。市場を持たない。行で明記する |

3. 照会の契約は変えない。供給元へは窓を覆う JST の暦日の外包
   `[min(PeriodStart, 窓の照会範囲の始まり, ClosedAfter の JST 日付), max(PeriodEnd, ClosedUntil の JST 日付)]` を渡し、受け取った後に
   報告書サービスが純関数で絞る（約定と同じ「外包で引いて絞る」形・IADR-0492 決定 3）。判断根拠の照会範囲も同じ外包を使う（値は従来と同じ）。
4. 損切りの月報 §6 の「日数」は承認の**セッションの日**（承認の市場の現地取引日）で数える（JST の暦日では米国の 1 セッションが 2 日に割れる）。
5. 「集計したセッション」の行に、窓に揃えない入力を書き足す: 日報・月報（LLM 利用実績を使う種別）は
   `集計したセッション: 米国 2026-10-05（ET）／東証 2026-10-06（JST）・LLM 利用実績は JST の暦日 2026-10-06`。週報は LLM 利用実績を使わないので変えない。

## 範囲

1. `ReportSessionWindow`（`Contains`・`ReportableAt`・`Counts`）と絞り込みの純関数 `ReportLedgerWindowing`（Domain・新設）。
2. `StopLossMethodApproval` に市場を持たせ（`StopLossMethodUsage.From` が承認から写す）、`StopLossMethodComparison` の日をセッションの日にする。
3. `FxSourceStatus` のクレジットの導出を Domain へ移す（絞り込み後に引き直すため。REST/gRPC 供給元の結果は変わらない）。
4. `ReportAutoGenerator`: 上記 6 入力を外包で引き窓で絞る。LLM 利用実績は据え置き。`DraftRequest` / `ReportView` に LLM 利用実績の暦日の範囲を足し、
   `ReportRenderer` が「集計したセッション」の行へ書き足す。
5. 文書: IADR-0516（新設）・索引・IADR-0492 への日付つき追記（残余リスクの解消を指す）・データ仕様書（報告書）・本仕様書。
   テンプレートのゴールデン（日報・月報 4 本）は行の書き足しだけが差分（意図した更新）。

範囲外:
- 約定・乖離の取り込み・稼働率の窓（IADR-0492 のまま。約定は現地取引日で絞る `Includes` であり、既定の構成では本 PR の「報告可能になる瞬間」と同じ結果）。
- 供給元の契約（REST・gRPC・proto）・監査台帳・リスク管理の照会。
- 飛ばされた日報（生成の予定は最新の日報だけ・従来どおり）。
- LLM 利用実績の暦日の集計が持つ既存の欠落（生成後〜24:00 JST の記録がどの日報にも載らない・月報が最終営業日の生成後〜月末を含まない）。残余リスクとして記録し、窓へ寄せるかは計画の裁定の対象として報告する。

## 母集合（規則 9。誤りの側＝「期間の集計として `due.PeriodStart, due.PeriodEnd` を JST の暦日のまま供給元へ渡す」箇所から引く）

引き方（origin/develop 8c7205cc）: `git grep -n "due.PeriodStart, due.PeriodEnd" -- backend/Services/ReportService`。

| 箇所 | 扱い |
| --- | --- |
| `SafeReductionsAsync`（自動縮小） | **直す** |
| `SafeBuyInInferencesAsync`（強制買戻しの推定） | **直す** |
| `SafeFxSourceStatusAsync`（為替の状態） | **直す** |
| `SafeBorrowFeesAsync`（借株料） | **直す** |
| `SafeStopLossMethodsAsync`（損切りの手法） | **直す** |
| `SafeStopLossMethodResolutionsAsync`（解決） | **直す**（承認と同じ外包で引く。承認に DecisionId で従う） |
| `SafeLlmUsageAsync`（LLM 利用実績） | 変えない（JST の暦日。行で明記） |
| `SafeRegenerationTally`（作り直しの回数・自リポの台帳） | 変えない（1 日の回数上限と同じ JST の暦日で数える運用の記録） |
| `SafePeriodEndFxRateAsync`（期末レート・期末日の観測） | 変えない |

規則 10（この変更で新たに誤りになる自分の記述）: 各供給ポートの「JST 取引日 [from, to] の…」の要約（照会の範囲としては正しいが、報告書に載る範囲ではなくなる）・
`StopLossMethodComparison` の「承認の JST 暦日に数える」・`ReportRenderer` の損切りの日数の注記・IADR-0492 §残余リスクの 1 行目・
データ仕様書（報告書）の「本規則の対象外」の行。いずれも本 PR で直す（IADR-0492 は凍結記録のため本文を書き換えず日付つき追記）。

## 受け入れ基準 → 試験

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-06-062 | 市場を持つ記録は「大引けと記録時刻の遅いほう」が窓に入る報告書に載る。米国のセッション中・閉場後の記録は約定と同じ日報、東証の生成境界後の記録は翌日報 | `ReportLedgerWindowingTests` |
| T-06-063 | （否定形・ちょうど 1 つ）連続する日報（休場日・夏時間の切替を含む 2 か月）の窓で、どの記録もちょうど 1 つに入る（市場あり・なしの両方） | 同上 |
| T-06-064 | 借株料は計上日（`TradingDay`）のセッションで帰属し、週末の計上日は月曜の日報に入る | 同上 |
| T-06-065 | 損切りの承認は窓で絞り件数を引き直す。月報の日数はセッションの日で数える（米国の 1 セッションが JST の 2 日に割れない） | `ReportLedgerWindowingTests`・`StopLossMethodComparisonTests` |
| T-06-066 | 為替の状態は発生時刻で絞り、クレジットは残った記録から引き直す | `ReportLedgerWindowingTests` |
| T-06-067 | （再現・自動生成）ET 10-05 の米国セッションの借株料・自動縮小・損切りの手法とその解決・強制買戻しの推定・為替の状態は日報 10-06 に載り、日報 10-05・10-07 には載らない | `ReportLedgerSessionWindowTests` |
| T-06-068 | 東証のセッションの記録は従来どおり同じ日付の日報に載る | 同上 |
| T-06-069 | LLM 利用実績は JST の暦日のまま引き、日報・月報の「集計したセッション」の行に暦日の範囲を書く（週報・窓を持たない経路は書かない） | `ReportLedgerSessionWindowTests`・`ReportSessionRangeTests` |
| T-06-070 | 供給元へ渡す照会の範囲は窓を覆う JST の暦日の外包 | `ReportLedgerSessionWindowTests` |

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format --verify-no-changes`・`ReportService.Tests`。
- `node scripts/scripts.test.js`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-test-traceability`・`check-adr-index-sync`・
  `check-adr-index-addendum-loss`・`check-cross-repo-refs`・`check-commit-messages`。
