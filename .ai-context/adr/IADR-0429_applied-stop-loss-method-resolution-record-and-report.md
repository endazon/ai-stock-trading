---
title: IADR-0429 損切りの実行機構の解決結果を新イベント StopLossMethodResolved として発注執行が発行し、監査台帳を経て日報の「実際に適用された手法」と月報 §6 の日数ベースの内訳を作る（日は承認の JST 暦日）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-10, FR-11, FR-12, SC-03, ADR-0040, IADR-0342, IADR-0422, IADR-0420, IADR-0254, IADR-0352, IADR-0129, IADR-0413, IADR-0199]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §4「損切りの実行機構（当日）」・月報 §6・記載要件。2026-09-25 追加・b54da86)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1)
  - planning:projects/ai-stock-trading/10_feedback/20260925_stop-loss-method-display-fields.md (planning#644 の裁定 1〜3)
---

# IADR-0429: 損切りの実行機構の解決結果の記録と、日報・月報での表示

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 [#1002](https://github.com/endazon/ai-stock-trading/issues/1002)。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #1002（planning#644 の裁定・PR planning#645 / `b54da86` の「実装側の残作業」1・2）。
- 関連する実装仕様書: [20260925_1002_applied-stop-loss-method-report](../specs/20260925_1002_applied-stop-loss-method-report.md)
- 関連 IADR: [IADR-0422](IADR-0422_stop-method-selection-display-sc02-sc03-daily-report.md)（日報の 1 行目＝承認時点の手法。本 IADR は覆さず 2 行目を足す。
  同 IADR の「計画が欄を定めたら書式を揃え直す」を本 IADR で行う）、[IADR-0342](IADR-0342_simulate-stop-loss-method-selection.md) 決定 4（解決規則）、
  [IADR-0254](IADR-0254_period-aggregation-authority-is-audit-ledger.md)（期間の集計の権威源は監査台帳）、[IADR-0420](IADR-0420_cross-service-read-contract-convention-and-guard.md)
  （送り手の本物の型による契約テスト）、[IADR-0129](IADR-0129_wolverine-messaging-topology.md)（共通配線の conventional routing）、[IADR-0352](IADR-0352_report-defers-on-transient-dependency-failure.md)（未供給の入力）。
- やらないこと: 残作業 3（SC-03 へ「S1・S3 は未実装で S0 として執行」の併記）。前提が現行実装と違う（`StopLossMethodPolicy` の解決は S1 → `SoftwareStop`、
  S3 → `AlternativeBrokerOrderType`）。計画側の訂正は planning#646。週報には出さない（裁定 2）。

## コンテキストと課題

計画 04_report-templates（2026-09-25 追加）は日報 §4 に「選ばれていた手法（承認時点）」と「実際に適用された手法（発注執行の解決結果）」の 2 行を、
月報 §6 に「当月の日数ベースの内訳」と「選択と実際が食い違った日数」を求めた。1 行目は #994（IADR-0422）で在る。**2 行目の供給経路が無い**
——解決結果（`StopLossMethodPolicy.Resolve` の戻り値）はログ（拒否＝Error・未知＝Warning）にしか出ない。見送りのイベント
（`OrderDispatchForgone.Reason=StopLossMethodNotPermitted`）は拒否だけを表し、空売りの S0・未知の S0・一致は区別できない。

## 検討した選択肢

### 解決結果の運び方

1. **既存の `OrderExecuted` / `OrderDispatchForgone` の末尾へ任意項目を足す**— 購読者が多い（監査・通知と、リスク管理の 5 本のハンドラ〔約定の台帳・GFV・Stage 1 の約定・注文活動の射影・見送りの台帳〕）うえ、
   `OrderExecuted` は完了済みの再配送（発注執行が記録から作り直す）・約定追跡でも作られ、そこでは解決結果を持たない（null が「解決していない」と
   「古い送り手」を混ぜる）。拒否以外の見送り（逆指値価格なし等）にも付けるには 2 型の両方を揃える必要がある。発注執行の並行 PR（#999・#1001）と
   衝突する面も広い。**却下**。
2. **報告書が承認と発注先から解決を再計算する**— 解決規則を 2 か所に持つ（発注執行の「手法を解釈するのはここ 1 か所」に反する）。報告書は
   **実際に発注したアダプタ**を知らない（設定上の発注先と実際の発注先は食い違い得る。IADR-0413 決定 1）。**却下**。
3. **新イベント `StopLossMethodResolved` を発注執行が発行し、監査台帳を経て報告書が引く**（採用）— 解決した回だけ 1 件出る。報告書の既存の作法
   （監査台帳を種別 × 期間で引く。IADR-0254・IADR-0422）にそのまま乗る。既存イベントの形・購読者は変えない。

### 報告書が直接購読するか

- 報告書は期間の事実を**監査台帳から引く**（IADR-0254）。直接購読すると報告書に第 2 の永続化が要り、生成の時点で台帳と食い違い得る。**監査台帳経由**にする。

## 決定

### 決定1: 新イベントを発注執行が発行する

- `StopLossMethodResolved(DecisionId, Symbol, Market, ProductType, SelectedMethod, AppliedMethod?, Reason, Provider, OccurredAt)`（Shared.Contracts）。
  `AppliedMethod` は実際に適用した手法で、**拒否（発注しない）なら null**。`Provider` は実際に発注するアダプタの発注先。
- `OrderExecutionAppService.ExecuteAsync` は、手法を解決した回（Open の承認で、完了済み／見送り済みの再配送の判定より後）に限り、
  結果（`OrderDispatchResult.MethodResolved`・末尾の任意項目）へ解決結果を載せる。**解決の後の戻り口は多いため、本体の各戻り口ではなく
  `ExecuteAsync` の外側で 1 回だけ載せる**（戻り口を足したときに載せ忘れる形を作らない）。サービスは Singleton なので回ごとの受け皿は引数で渡す。
- `OrderApprovedHandler` は再配送の抑止の判定の直後・見送り／発注の発行より前に発行する（見送りの経路は途中で return するため）。
- 解決の後に見送った回（逆指値価格なし・能力なし・帰属不明の建玉）でも載る。例外で終わった回（予約の競合等）は結果を返さないので発行されず、
  再試行で解決し直した回に載る。

### 決定2: 解決と理由は 1 か所で決める

- `StopLossMethodPolicy.ResolveWithReason` が解決結果と理由（`StopLossMethodResolutionReason`: `AsSelected=0` / `BrokerNotMoomooSimulate=1` /
  `ShortSellEntry=2` / `UnknownMethod=3`。序数固定）を同時に返し、`Resolve` はそれを呼ぶだけにする。理由を別の関数で導き直すと、判定の順序を
  変えたときに片方だけが古くなる（例: 空売りかつ SIMULATE 以外は「拒否」であり「空売り」ではない）。
- 適用した手法は解決結果から写す（BrokerStopOrder・未知の退避 → S0、ProtectiveStopWaived → S2、SoftwareStop → S1、AlternativeBrokerOrderType → S3、拒否 → 無し）。
  全組み合わせで「適用 ≠ 選択 ⇔ 理由 ≠ AsSelected」が成り立つ（T-10-1080）。

### 決定3: 「日」と「食い違い」の定義

- **日** ＝ 日報と同じ **JST の暦日**で、**承認の時刻**（`OrderApproved.ApprovedAt`＝台帳の OccurredAt。日報の期間は `AuditPeriodRange.JstHalfOpen`）で決める。
  解決結果は判断 ID で**その承認に属させ**、解決した時刻の日付では数えない——米国の取引時間は JST の日付を跨ぐため、解決の時刻で数えると
  1 行目と 2 行目の母集合がずれる。
- **日報の件数の母集合** ＝ 当日の新規建ての承認（判断 ID で重複を除く。1 行目と同じ）。2 行目はそのうち解決結果の記録が見つかったもの。
  **見つからない承認は「解決結果の記録が見つからない承認 n 件」として別に書き、2 行目にも食い違いにも数えない**（不明を一致とも食い違いとも言わない）。
- **食い違い**（1 件）＝ 解決結果が見つかった承認のうち、適用した手法が承認の手法と違うもの（拒否は「適用なし」なので必ず食い違い）。
  **食い違った日** ＝ 食い違いが 1 件以上あった暦日。
- **月報の日数ベースの内訳** ＝ 実際に適用された手法（拒否は「発注せず」）ごとに、それが 1 件以上あった暦日の数。1 日に複数の手法が適用された日は
  各手法に重複して数え、その日数を併記する。解決結果の記録が見つからない承認を含む日は別に数える（食い違った日数は照合できた承認だけで数えた値）。
- 同じ判断 ID の解決結果が複数あれば**時刻が最も遅いもの**を採る。2 本目は 1 本目の処理が例外で終わった（結果を返さず再試行された）ときにだけ生じ、
  発注に至ったのは後の処理である。

### 決定4: 報告書の供給

- `IStopLossMethodResolutionSource` / `HttpStopLossMethodResolutionSource`（`GET /audit/events/by-type?types=StopLossMethodResolved`・`audit-ledger`）/
  `UnsuppliedStopLossMethodResolutionSource`（`Audit:BaseUrl` 未設定・不正）。照会の窓は**報告期間の前後 1 日**を含む JST 暦日の半開区間
  （承認と解決は別サービスの時計で刻まれ、JST 0 時を跨ぎ得る。期間外の解決結果は判断 ID の照合で捨てる）。非 2xx・例外・null は未供給、壊れた 1 件は除いて数を返す。
- `StopLossMethodUsage` に承認の明細（判断 ID・手法・承認時刻）を持たせ、純関数 `StopLossMethodComparison` が突き合わせる。

### 決定5: 入力の語彙

- `ReportInput.StopLossMethodResolutions`（「損切りの実行機構（発注執行の解決結果）」）を末尾に足す。`StopLossMethods` と共に**日報と月報**に適用し、
  週報には適用しない（未供給の警告・見送りの判定に混ぜない）。

### 決定6: 描画

- 日報（§4 の子節 `### 損切りの実行機構（当日）`）: 計画の書式に揃え、1 行目を「選ばれていた手法（承認時点）: 計 n 件 — …」とする（#823 の
  「新規建ての承認（承認時点の手法）: N 件」を改めた）。**1 行目の未供給の文言は #823 のまま変えない**。2 行目「実際に適用された手法（発注執行の解決結果）」、
  食い違いは「選択 → 適用 件数（理由: …）」。どちらかを照会できなければ「なし」と書かない（承認を照会できなければ 2 行目は「照合できません」）。
  固定の注記は「新規建ての承認の件数であり、発注・約定の件数ではない。実際に適用された手法は解決結果（解決の後の見送り・約定の有無は反映しない）」。
- 月報（§6 の子節 `### 損切りの実行機構（当月）`・§6.1 より前＝計画の §6 本文の位置）: 手法ごとの日数・食い違った日数だけを書き、理由（明細）は書かない。
  承認の無い月は「なし」。

### 決定7: 配線の証明

- 発注執行: 本番の Program.cs（`WebApplicationFactory`・外部トランスポートだけ stub）で承認を流し、解決結果が
  `rabbitmq://exchange/AiStockTrading.Shared.Contracts.Events.StopLossMethodResolved` へ送られることを固定する（T-10-1083）。
- 監査: 本番の Program.cs で、ハンドラが発見され、共通配線の routing convention が `ai-stock-trading.audit-service.StopLossMethodResolved` の listener を作り、
  流した解決結果が `by-type` 照会で元のイベントへ戻る本文で返ることを固定する（T-10-1085）。stub の試験ホストは起動時に conventional routing の
  listener 発見を行わないため、構成済みの convention（Wolverine が公開していない一覧をリフレクションで 1 か所だけ読む）へ型を渡して発見させる。
- 報告書: 本番の Program.cs（台帳の HTTP の最下層と時計だけ差し替え）で自動生成を回し、送り手（監査）の本物の `AuditEntryFactory` で作った応答から
  日報の 2 行目と食い違いの行が出ること、JST 0 時を跨いで解決された承認も記録なしにならないことを固定する（T-10-1092）。受け手の契約は
  `AuditLedgerReadContractTests`（extern alias `AuditWorker`・IADR-0420）に 1 本足す（T-10-1086）。

## 理由

- 解決結果を発行するのは解決した本人（発注執行）であり、実際のアダプタの発注先を知っているのも発注執行だけである。報告書が再計算すると
  規則と発注先の両方を 2 か所に持つ。
- 母集合を承認に固定すれば、日報の 2 行は同じ承認の集合を数える（1 行目と 2 行目の差は「記録が見つからない承認」として明示される）。
- 既存イベントの形・購読者・並行 PR の面を変えずに済む（新しい型を 1 つ足し、監査の購読と報告書の照会を 1 本ずつ足すだけ）。

## 結果・残余リスク

- 良い影響: 日報で「選んだ手法」と「実際に走った手法」を読み分けられ、食い違いの理由が読める。月報 §6 で S0 の日と S2 の日を分けて §5 の三者比較を読める。
- **日報の日付境界は JST の暦日である**（既存の日報の全照会と同じ）。米国の金曜の取引のうち JST 土曜未明の承認は、土曜に日報が無いため**どの日報にも出ない**。
  月報は暦月で引くのでその日も 1 日として数える（該当する日報が無い日が月報の日数に現れ得る）。境界の解釈は既存の課題として本 IADR では変えない。
- 解決結果は**発注の成否ではない**。解決の後の見送り・S3 の代替注文の拒否・約定の有無は反映しない（注記で明示）。
- 解決結果の記録が無い承認（発注執行が未処理・処理が例外で終わり error キューへ落ちた・監査台帳が記録していない）は判定しない（件数で見せる。
  台帳の欠落と発注執行の未処理を報告書は区別しない）。
- 本 PR 以前の承認には解決結果の記録が無い。配備前の日の日報・その日を含む月報では、その承認が「解決結果の記録が見つからない」に数えられる。
- 監査のキューが実際の RabbitMQ で exchange に bind されることは、外部トランスポートを差し替えた試験では見ていない（統合試験の範囲）。
- 計画の記載要件の「S1・S3 は未実装で S0 として執行」という前提は現行実装と違う（planning#646）。本 IADR の理由の列挙は実装の解決規則から作った。
