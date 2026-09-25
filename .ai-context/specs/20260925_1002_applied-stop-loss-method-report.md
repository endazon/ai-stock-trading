---
title: 日報に「実際に適用された損切り手法」の行と食い違いの理由を足し、月報 §6 に日数ベースの内訳と食い違いの日数を足す（#1002）
type: spec
status: accepted
related_ids: [FR-06, FR-10, FR-11, FR-12, SC-03, ADR-0040, IADR-0342, IADR-0422, IADR-0420, IADR-0254, IADR-0352, IADR-0129, IADR-0429]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §4「損切りの実行機構（当日）」・月報 §6「当月の損切りの実行機構」・記載要件。2026-09-25 追加・b54da86)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1)
  - planning:projects/ai-stock-trading/10_feedback/20260925_stop-loss-method-display-fields.md (planning#644 の裁定 1〜3)
---

# 仕様書: 日報に「実際に適用された損切り手法」の行、月報 §6 に日数ベースの内訳（#1002）

## 起点

- #1002（planning#644 の裁定・PR planning#645 / `b54da86` の「実装側の残作業」1・2）。
- 割り当て: ブランチ `feat/FR-10-1002-applied-stop-loss-method-report`・**IADR-0429**・テスト ID **T-10-1080〜T-10-1099**
  （FR-10 のテスト仕様書に載せる。origin/* 6 本〔develop・main・進行中の 4 ブランチ〕で `git grep -o "T-10-1[0-9]{3}"` の最大は `T-10-1059`）。
- やらないこと: 残作業 3（SC-03 へ「S1・S3 は未実装で S0 として執行」の併記）。前提が現行実装と違う
  （`StopLossMethodPolicy.cs` の解決は S1 → `SoftwareStop`、S3 → `AlternativeBrokerOrderType`）。計画側の訂正は planning#646。
  週報には出さない（裁定 2）。

## 計画書の確認（隣接クローン `C:/10_SourceCode/worktree/project-planning`・`b54da86`（origin/main）・読み取り専用）

- 04_report-templates 日報 §4（2026-09-25 追加）:
  - `選ばれていた手法（承認時点）: <なし（当日の新規建ての承認は 0 件） / 計 n 件 — S0 … a 件 / S2 … b 件>`
  - `実際に適用された手法（発注執行の解決結果）: <なし / 計 n 件 — …>`
  - 2 行が食い違う場合は理由（未実装の手法・実際の発注先の不一致・空売りの新規建て）を併記する。
  - 数えるのは当日の新規建ての承認だけ（同一の判断 ID は 1 件）。承認 0 件も「なし」と明記。照会できなかった場合は「なし」と書かない（文言は実装に委ねる）。
- 月報 §6: `当月の損切りの実行機構: <S0 n 日 / S2 m 日>／選択と実際が食い違った日数: <n 日>`（承認が無い月も「なし」と明記。個々の日は日報を参照）。
- 計画の記載要件は「S1・S3 が未実装である間は S0 として執行される」を 2 行並記の理由に挙げるが、**この前提は現行実装と違う**（上記。planning#646）。
  本 PR は理由の列挙を**実装の解決規則から**作る（下記「食い違いの理由」）。計画の「未実装の手法」に当たる現行の分岐は「未知の手法の値 → S0」である。

## develop で既に在るもの・残るもの（develop `a35ae3f7`・`git rev-parse --is-shallow-repository` → `false`）

| 項目 | 状態 | 根拠 |
| --- | --- | --- |
| 承認時点の手法の集計（日報の 1 行目） | 済（#994） | `StopLossMethodUsage.From`・`HttpStopLossMethodUsageSource`（監査台帳の `OrderApproved`） |
| 解決結果の算出 | 済 | `StopLossMethodPolicy.Resolve`（`OrderExecutionAppService.ResolveStopLossMethod` から Open のときだけ呼ばれる） |
| **解決結果の記録・照会の経路** | **無い** | 解決結果はログ（Refused=Error・未知=Warning）にしか出ない。`OrderDispatchForgone.Reason=StopLossMethodNotPermitted` は拒否だけを表し、空売りの S0・未知の S0・一致は区別できない |
| 日報の 2 行目・食い違いの理由 | 無い | `ReportRenderer.AppendStopLossMethods` は 1 行目だけ |
| 月報 §6 の日数ベースの内訳 | 無い | `AppendStopLossMethods` は日報だけ |

## 設計

### 1. 契約（Shared.Contracts）— 新イベント `StopLossMethodResolved`

既存イベントへ項目を足さず、**新しいイベント**にする（IADR-0429 決定 1 の比較）。

```
StopLossMethodResolved(
  Guid DecisionId, string Symbol, Market Market, ProductType ProductType,
  StopLossExecutionMethod SelectedMethod,        // 承認が運んだ手法
  StopLossExecutionMethod? AppliedMethod,        // 実際に適用した手法。発注しない（拒否）なら null
  StopLossMethodResolutionReason Reason,         // 食い違いの理由（一致なら AsSelected）
  BrokerProvider Provider,                       // 実際に発注するアダプタの発注先
  DateTimeOffset OccurredAt)
```

`StopLossMethodResolutionReason`（序数固定）: `AsSelected=0` / `BrokerNotMoomooSimulate=1`（拒否・発注しない）/
`ShortSellEntry=2`（空売りの新規建て → S0）/ `UnknownMethod=3`（未知の手法の値 → S0）。

### 2. 発注執行（発行側）

- `StopLossMethodPolicy.ResolveWithReason`（解決と理由を 1 か所で決める）。`Resolve` はこれを呼ぶだけにする（判定の順序を 2 か所に持たない）。
  適用した手法は解決結果から写す（BrokerStopOrder/NotImplementedFallback → S0、ProtectiveStopWaived → S2、SoftwareStop → S1、
  AlternativeBrokerOrderType → S3、Refused → null）。
- `OrderExecutionAppService.ExecuteAsync` が、解決した回（Open の承認で、再配送の抑止より後）に限り結果へ `MethodResolved` を載せる
  （`OrderDispatchResult` の末尾の任意項目）。**発注した・見送ったのどちらでも載る**（解決の後の見送り〔逆指値価格なし等〕でも解決結果は事実である）。
  Close・完了済みの再配送・見送り済みの再配送では解決しないので載らない。
  例外で終わった回は結果を返さないので発行されない。**再試行で解決し直しても発行されるとは限らない**——送信後に届いたか不明（`BrokerDispatchIndeterminateException`）の回は予約を Reserved のまま残して例外で終わり、以後の再試行は解決の後の予約で競合（`OrderDispatchReservationConflictException`）して同じく例外で終わる。その承認の解決結果は一度も発行されず、日報・月報では「解決結果の記録が見つからない」に数えられる（予約より前・解決より後で例外が起きた回だけは、再試行が完了すればその回に載る）。発行そのものの失敗はハンドラが握ってログに残し、後続の発行（見送り・発注結果・保護逆指値）を止めない（その承認も「記録が見つからない」に数えられる）。
- `OrderApprovedHandler` が `MethodResolved` を発行する（再配送抑止の判定の直後・見送りの発行より前）。

### 3. 監査（購読側）

- `StopLossMethodResolvedAuditHandler`（全イベントを台帳へ記録する既存の形）＋ `AuditEntryFactory.From(StopLossMethodResolved)`
  （相関＝DecisionId・時刻＝OccurredAt・要約に「選択 → 適用（理由・発注先）」）。
- 購読キュー `ai-stock-trading.audit-service.StopLossMethodResolved`（共通配線の命名。IADR-0129 決定 1）。

### 4. 報告書（照会側）

- `IStopLossMethodResolutionSource` / `HttpStopLossMethodResolutionSource` / `UnsuppliedStopLossMethodResolutionSource`
  （監査台帳 `GET /audit/events/by-type?types=StopLossMethodResolved`・`audit-ledger` クライアント・`Audit:BaseUrl` 未設定/不正は Unsupplied）。
  照会の窓は報告期間の**前後 1 日**を足した JST 暦日の半開区間（承認の日の境界と解決の時刻がずれても拾う。照合は DecisionId）。
- `StopLossMethodUsage` に承認の明細（DecisionId・手法・承認時刻）を持たせる（件数は従来どおり）。
- 純関数 `StopLossMethodComparison`（承認と解決結果を DecisionId で突き合わせる）。
- `ReportInput.StopLossMethodResolutions`（表示名「損切りの実行機構（発注執行の解決結果）」）を末尾に足す。
  `StopLossMethods` と `StopLossMethodResolutions` は**日報と月報**に適用（週報は計画が求めない）。

### 5. 「日」と「食い違い」の定義（IADR-0429 決定 3 に同文）

- **日** ＝ 日報と同じ **JST の暦日**。承認の時刻（`OrderApproved.ApprovedAt`＝台帳の OccurredAt）で決める。
  解決結果は**その承認に属する**（解決した時刻の日付では数えない）。米国の取引時間は JST の日付を跨ぐため、同じ注文の承認と解決が
  別の暦日に落ち得る——解決の時刻で数えると 1 行目と 2 行目の母集合がずれる。
- **件数の母集合**（日報の 2 行）: 当日（JST 暦日）の新規建ての承認（DecisionId で重複を除く）。2 行目は、そのうち解決結果の記録が見つかったもの。
  見つからない承認は「解決結果の記録が見つからない承認 n 件」として別に書き、2 行目・食い違いのどちらにも数えない（不明を一致とも食い違いとも言わない）。
- **食い違い**（1 件）＝ 解決結果が見つかった承認のうち、適用した手法が承認の手法と違うもの（拒否は「適用なし」なので必ず食い違い）。
  理由は解決結果が運ぶ `Reason`。**食い違った日** ＝ 食い違いが 1 件以上あった暦日。
- **月報の日数ベースの内訳** ＝ 実際に適用された手法（拒否は「発注せず」）ごとに、それが 1 件以上あった暦日の数。
  1 日に複数の手法が適用された日は各手法に重複して数え、その日数を併記する。解決結果の記録が見つからない承認を含む日は別に数える。
- 同じ判断 ID の解決結果が複数あれば**時刻が最も遅いもの**を採る。**2 本目は通常生じない**——結果を返した回の後の再配送は、完了済み・見送り済みの判定で解決の前に戻るか、予約の競合で例外に終わる。生じ得るのは同じ承認が並行して配送され、双方が予約の前で見送りを返した場合など（解決結果の中身は同じになる）であり、時刻の遅いものを採るのはその場合の決め方である。

### 6. 描画

日報（§4 の子節 `### 損切りの実行機構（当日）`。計画の書式に揃える）:

- 1 行目 `- **選ばれていた手法（承認時点）**: 計 n 件 — …`／`なし（当日の新規建ての承認は 0 件）`／未供給は既存の文言
  `- **承認の記録を照会できませんでした（要確認）**: 「承認なし」とは区別しています。`（文言は変えない）
- 2 行目 `- **実際に適用された手法（発注執行の解決結果）**: 計 n 件 — S0 … a 件 / 発注せず（拒否） b 件`／`なし（当日の新規建ての承認は 0 件）`／
  解決結果が未供給なら `- **発注執行の解決結果を照会できませんでした（要確認）**: 「なし」とは区別しています（選択と実際の食い違いも判定できていません）。`／
  承認が未供給なら 2 行目は照合できない旨（「なし」と書かない）。
- 食い違い `- **選択と実際の食い違い: n 件** — <選択> → <適用>（<理由>） k 件 / …`／`なし`／一部が照合できなければ「照合できた承認の範囲ではなし」。
- 記録が見つからない承認・復元できなかった記録は件数があるときだけ書く。固定の注記は「件数は承認の件数であり発注・約定の件数ではない」を保つ。

月報（§6 の子節 `### 損切りの実行機構（当月）`。§6.1 より前＝計画の §6 本文の位置）:

- `- **実際に適用された手法の日数**: S0 … a 日 / S2 … b 日（新規建ての承認があった日 n 日…）`
- `- **選択と実際が食い違った日数: k 日**（個々の日の内訳と理由は該当日報を参照）`
- 承認なしの月は `なし（当月の新規建ての承認は 0 件）`。未供給はそれぞれ照会できなかった旨。

## 受け入れ基準 → テスト

| ID | 受け入れ基準 |
| --- | --- |
| T-10-1080 | 解決と理由は 1 か所で決まり、手法 × 発注先 × 商品種別の全組み合わせで `Resolve` と一致する。理由と適用した手法が解決規則どおり |
| T-10-1081 | Open の承認を処理すると結果に解決結果が載る（発注・拒否の見送り・解決後の見送り）。Close・完了済み/見送り済みの再配送では載らない |
| T-10-1082 | ハンドラが解決結果を発行する（本番と同じ共通配線・見送りの経路でも） |
| T-10-1083 | **本番の Program.cs（発注執行）**で承認を流すと、解決結果が `rabbitmq://exchange/…StopLossMethodResolved` へ送られる |
| T-10-1084 | 契約: 型名の固定・スキーマ基準の再生成・理由の序数の固定 |
| T-10-1085 | 監査: 写像（相関・時刻・要約）。**本番の Program.cs（監査）**が購読キューを持ち、流した解決結果が `by-type` 照会で返る |
| T-10-1086 | 報告書の供給: 窓・種別・未供給（非 2xx・例外・null・構成なし）・壊れた 1 件。**送り手の本物の型（`AuditEntryFactory`）の応答**を読める |
| T-10-1087 | 突き合わせ: DecisionId で照合・最も遅い解決結果・記録なし・食い違いの集計・暦日の決め方 |
| T-10-1088 | 日報の描画: 2 行・理由・なし・未供給（どちら側でも「なし」と書かない）・記録なし |
| T-10-1089 | 月報の描画: 日数ベースの内訳・重複日・食い違った日数・記録なしの日・承認なしの月・未供給。週報には出さない |
| T-10-1090 | 入力の語彙: 2 入力とも日報・月報に適用・週報に不適用。表示名 |
| T-10-1091 | 自動生成: 解決結果の供給を受け取り、未注入・失敗は未供給として記録する。月報でも両入力を引く |
| T-10-1092 | **本番の Program.cs（報告書）**: 台帳の応答（送り手の本物の型）から日報の 2 行目が出る。既定は Unsupplied |
| T-10-1093 | ゴールデン（日報・月報 × 供給あり/なし）が新しい行を含む |

## 是正・追随の母集合（規則 1〜6・9・10）

引いた軸と結果（`git grep`・パスの除外だけで引く。拡張子で絞らない）:

1. **新イベントの追随先**（誤りの側＝「全イベントを列挙する場所」）: 既存イベント `SoftwareStopArmed` を全ファイルから引いた（31 ファイル＝`.cs` 21・それ以外 10）。
   追随する: `AuditEntryFactory.cs`・`AuditEventHandlers.cs`・`AuditEntryFactoryTests.cs`・`AuditCycleCompletenessTests.cs`（標本）・
   `EventMessageTypeNameTests.cs`・`event-schemas.baseline.json`・`docs/api/events-and-ports.md`・`docs/data/audit-events.md`。
   除外: 通知（`NotificationFormatter`・`NotificationHandlers` と試験）＝解決結果は Discord へ出さない（拒否は既存の見送り通知が出る）。
   `ReportRenderer.cs:579`＝S1 の開示文の話。発注執行の S1 の試験群・`StopLossMethodContractTests`＝S1 固有。IADR/specs＝凍結記録。
2. **「日報の件数は見送り・空売りの S0 を反映しない」と述べる記述**（本 PR で偽になる。規則 10）: `git grep -n "反映しない\|空売りの S0 扱い"`（specs・adr・CHANGELOG を除く）
   → `docs/functional/FR-10_risk-controls.md:918`（書き換える）・`ReportRenderer` の固定注記（書き換える）。
   `docs/tests/FR-10_risk-controls-tests.md` の #823 節の残余リスク行は**その節の時点の記録**として残し、本 PR の節で解消を書く。
3. **「週報・月報には出さない」と述べる記述**（月報が出すようになるため偽になる）: `git grep -n "週報・月報には出さない\|月報には出さない"` →
   `StopLossMethodUsageTests.cs`（T-10-997 の週報・月報の否定形。月報の側を本 PR の T-10-1089 へ移す）・`docs/tests/FR-10_risk-controls-tests.md:2536`（T-10-997 の行）・
   `ReportInput.cs` の `StopLossMethods` の注釈・`ReportView.cs:125`「日報だけが描く」。
4. **日報の 1 行目の表示名**（計画の書式へ揃える）: `git grep -l "新規建ての承認（承認時点の手法）" a35ae3f7` → 6 ファイル。
   追随する: `ReportRenderer.cs`・`Golden/daily-supplied.md`・`StopLossMethodUsageTests.cs`・`ReportAutoGeneratorStopLossMethodTests.cs`。
   除外: `IADR-0422`・`20260925_823_stop-method-ui-and-daily-report.md`（凍結記録。当時の文言として真）。`docs/` には現れない。
5. **`ReportInput` の全数を使う試験**: `git grep -n "GetValues<ReportInput>\|ReportInput\." -- '*Tests*'` → `ReportInputsTests`・`ReportSummary` 系の全数試験を引き直す。
6. **基盤（`../microservices-platform`）**: AST 固有のイベント・報告書は持たない（`git grep StopLossMethod` 0 件）。

## 残余リスク

- 日報の日付境界は JST 暦日である（既存の日報と同じ）。米国の金曜の取引の JST 土曜未明の承認は、土曜に日報が無いため**どの日報にも出ない**
  （既存の全監査照会に共通する境界。本 PR は同じ境界に従う）。月報は暦月で引くので、その日も日数に数える（日報の無い日が月報に 1 日として現れ得る）。
- 解決結果は発注の成否ではない（解決の後の見送り〔逆指値価格なし・能力なし・帰属不明の建玉〕や、S3 の代替注文の拒否は反映しない）。注記で明示する。
- 解決結果の記録が無い承認（発注執行が未処理・送信後に届いたか不明で例外に終わった〔再試行も予約の競合で例外になる〕・解決結果の発行に失敗した）は判定しない（件数で見せる）。
- 監査台帳が解決結果を記録し損ねた場合（監査の購読が落ちている）も「記録が見つからない」になる（台帳の欠落を報告書は区別しない）。
