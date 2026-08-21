---
title: IADR-0115 報告書の自動生成は「ドラフト生成→提示」までで停止し、確定は OwnerOnly の対話経路に残す
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-07, FR-16, UC-03, UC-04, UC-05, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0115: 報告書の自動生成は「ドラフト生成→提示」までで停止する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-29
- 決定者: endazon（利用者・設計承認）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-06（報告書生成）、FR-07（対話的確定）、FR-16（数値集計）、UC-03〜05、
  ADR-0003（計画リポ）（**Accepted**）、
  `04_workflows/03_reporting-cycle.md`（**fixed**）
- 対象 Issue: [#280](https://github.com/endazon/ai-stock-trading/issues/280)・傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279) ギャップ #2
- 関連する実装仕様書: [20260729_280_report-auto-generation-scheduler](../specs/20260729_280_report-auto-generation-scheduler.md)
- 関連 IADR: [IADR-0024](IADR-0024_report-confirmation-and-policy.md)、[IADR-0032](IADR-0032_report-generation.md)、
  [IADR-0042](IADR-0042_report-review-state-machine-and-detail-rendering.md)、[IADR-0071](IADR-0071_report-service-remaining.md)、
  [IADR-0095](IADR-0095_watchlist-authoritative-wiring.md)、[IADR-0103](IADR-0103_observed-drawdown-supply.md)、
  [IADR-0107](IADR-0107_base-currency-conversion.md)

## 背景・課題

report-service には `AddHostedService` が 1 つも無く、報告書の生成は OwnerOnly の HTTP（`draft` → `present` →
`confirm`）を人手で叩く経路しか存在しない。その結果、確定方針は 1 件で更新が止まり、取引は
`Reports:NoResponseBehavior` の既定（`ContinueLastConfirmed`）によって「最後に確定した方針の惰性」で回り続けている。
週報・月報は集計・描画のコードがあるだけで、駆動する側が無い。

`04_workflows/03_reporting-cycle.md`（fixed）は「閉場後にスケジューラが起動 → 集計 → AI がドラフト生成 →
利用者へ提示 → 対話 → 承認 → 確定」というシーケンスを確定済みで、**欠けているのは前半（スケジューラ〜提示）だけ**である。

ここで決めるべきは「自動化をどこで止めるか」である。運用の実感としては確定まで自動化したくなるが、
ADR-0003 は「方針の確定には必ず**利用者との対話**を要する。**完全無人での方針変更は行わない**」と Accepted で定めている。

## 検討した選択肢

1. **ドラフト生成 → 提示（`PendingApproval`）までを自動化し、確定は OwnerOnly の既存経路に残す**
2. **SIMULATE 限定の opt-in 自動確定フラグを足す（既定 false）** — dogfood で方針が日々更新される状態を作れる
3. **現状維持（外部 CronJob から HTTP を叩く）** — サービスにコードを足さない

## 決定

**選択肢 1** を採る。加えて以下を確定する。

### 決定 1: 自動化の終点は `Present`。`Confirm` は自動経路から呼ばない

自動生成の生成物は `ReviewState.PendingApproval` / `ReportState.Draft` で停止する。`ReportReviewStateMachine`・
`AiStockTradingAuthPolicies.OwnerOnly`・版番号付き楽観排他は**一行も変更しない**。`GetLatestConfirmed(Daily)` が
返す方針は利用者が確定するまで変化しないため、取引に効く方針は自動では動かない。

選択肢 2 は棄却する。既定 false・SIMULATE 限定であっても、「フラグ 1 つで無人確定が成立する」構造そのものが
ADR-0003 の否定であり、実装リポジトリの IADR で上書きしてよい範囲を超える。必要になった場合は `/plan-feedback` で
計画リポジトリへ提起し、ADR 改訂を経てから別 issue で扱う。

`ApplyReview(Present)` の actor は `report-scheduler` とする。これは HTTP の認可を迂回するものではない
（HTTP エンドポイントは OwnerOnly のまま）。提示は計画書のシーケンスで**システム側の動作**として定義されており、
in-process のドメイン操作として正しい。状態機械が要求する「actor 必須」（`ActorRequired`）も満たす。

提示の結果（`ReviewDecision`）は捨てない。生成直後の遷移のため通常は必ず受理されるが、拒否されると
「報告書は存在するのに承認待ち一覧に並ばない」＝利用者が気付けない状態になり、しかも次巡回は `PeriodKey` 一致で
スキップされるため自動回復しない。よって受理されなかった期間を `NotPresented` として返し、常駐が警告ログに残す。

### 決定 2: 生成境界は JST 固定・営業日基準。バックフィルは当期のみ

時刻境界は JST（UTC+9）に固定し、`PortfolioProjection.TradingDayOffset` と揃える。市場別の取引日境界の解釈は
[#249](https://github.com/endazon/ai-stock-trading/issues/249) の管轄であり、本 IADR では扱わない。

判定は純関数 `ReportSchedule.Due(instant, ReportScheduleOptions)` に閉じる（`BackgroundService` から分離してテスト可能にする）。
日報は「閉場境界を過ぎた**直近の営業日** 1 件」、週報・月報は「当 ISO 週・当月の**最終営業日**の境界を過ぎていれば当期 1 件」。

日報だけ直前の営業日まで遡るのは非対称に見えるが、意図的である。**確定した日報が翌営業日の取引方針になる**ため、
夜間の再起動で前営業日ぶんを取りこぼすと運用が止まる。一方で週・月をまたぐ遡りは行わない（長期停止からの復帰で
古い期間の報告書が一斉に湧くのは、レビュー負荷の割に価値が無い）。この制約は既知として仕様書に明記する。

「その瞬間」に依存しない設計にすることで、`PeriodicTimer`（既定 300 秒）の粗さ・巡回の遅延・プロセス再起動を
すべて同じ機構（境界を過ぎている＆未生成なら生成する）で吸収する。cron 的な発火時刻の一致は要求しない。

### 決定 3: 冪等の唯一の根拠は `PeriodKey` の存在。プロセス内に「生成済み」を持たない

`store.Get(periodKey) is not null` を唯一の判定にする。専有 DB（`report_svc`）が状態の単一情報源であるため、
プロセス再起動・多重レプリカのいずれでも二重生成しない。新規生成は `UpsertDraft(expectedVersion: 0)` で、
競合したレプリカは主キー衝突で負け、例外を捕捉してその期間をスキップする。

`#210`（IADR-0096）の in-memory dedup と異なり durable なストアを根拠にするのは、報告書が**永続オブジェクト**であり
「作られたかどうか」が DB を見れば確定するためである（通知イベントのように「送ったかどうか」が外部にしか無い場合と違う）。

### 決定 4: 自動ドラフトの `PolicySummary` は上位方針の継続案とし、LLM に新方針を提案させない

散文（振り返り・評価）は従来どおり `IReportNarrativeDrafter` が生成して Markdown 本文に入る。しかし
`PolicySummary` は「確定すると取引に効く」フィールドであるため、自動生成では**上位方針（`BasedOn` の確定済み報告書）の
継続案 ＋ 要確定である旨**に留める。

理由は、機械生成された新方針が `PendingApproval` に並ぶと、利用者のレビューが「読んで承認するだけ」に退化しやすいこと。
継続案であれば、利用者が能動的に書き換えない限り方針は変わらず、ADR-0003 の「対話を要する」が実質的に保たれる。
LLM による方針提案は、Discord 経由の対話（PR 2/2 以降）と合わせて別途設計する。

上位方針が未確定のときは `BasedOn = null` とし、その旨を方針文に明記する（`03_reporting-cycle.md`「上位方針の欠落」）。

> **改訂（[IADR-0125](./IADR-0125_report-policy-carryover-substance.md) / #310）**: 本決定のうち「**要確定である旨**を方針文へ書く」部分は撤回した。
> 前置き（`（自動生成ドラフト・未確定）` / `…継続する案です。確定前に内容を見直してください。`）を本文へ載せると、
> 継続のたびに世代累積し、確定後も「未確定」を名乗る本文が取引方針（`GET /reports/daily-policy`）として渡るため。
> 方針文は**方針の実体だけ**を持ち、状態は `ReportState` / `ReviewState` と提示通知（[IADR-0116](./IADR-0116_report-draft-discord-notification.md)）が持つ。
> 「機械に新方針を提案させない」という本決定の骨子（継続に留める）は不変である。
> 上位方針の欠落の明記も残す（文言のみ「未確定のため」→「確定済みのものがないため」へ改める）。

### 決定 5: 期間の約定は権威源への s2s 同期照会で取得し、供給不達は空＝数値 0 に倒す

report-service には約定の取得経路が無く、スケジューラを足しただけでは数値が常に 0 になる。権威源は
risk-management の取引台帳（`approved_orders` × `trade_fills`）であり、`GET /risk-controls/fills?from&to` を
`OwnerOrService` で追加して同期照会する（IADR-0095 と同型・Database per Service を跨いだ DB 直参照はしない）。

ポート `IPeriodFillSource` の既定は no-op（空列）で、`RiskManagement:BaseUrl` 設定時のみ HTTP 実装を選ぶ。
非 2xx・timeout・例外・不正応答はすべて空列へ倒し、**生成そのものは止めない**。報告書は発注判断を行わないため、
欠測が過大発注へ繋がる経路が無く、「数値 0 のドラフトを提示して利用者に気付かせる」ほうが「何も出さない」より安全である
（`#210` の「未確定を黙って続けない」と同じ方向）。

台帳（`LedgerFill.Price`）はローカル通貨（IADR-0107）、`PeriodTradeFill.Price` は基準通貨（円）建てのため、
照会結果は `Price × FxRateToBase` で基準通貨へ換算して渡す（`LedgerFill.PriceInBase` と同じ規則を、
別サービスの型を参照せずに一次フィールドから導出する）。

実約定が台帳に入るかどうかは [#270](https://github.com/endazon/ai-stock-trading/issues/270) 依存であり、
本 IADR は構造の結線までを担当する。

### 決定 6: 既定無効（opt-in）。Helm / values / デプロイ資材は触らない

`Reports:AutoGeneration:Enabled` が true のときだけ `AddHostedService` する（`ObservedDrawdownRefreshService`・
IADR-0103 と同型）。未設定＝現行挙動とバイト等価。稼働中環境への有効化は本 PR の範囲外とし、chart・values は変更しない。

## 理由

- ADR-0003 と `03_reporting-cycle.md`（fixed）に**そのまま従う**のが最短で、逸脱の根拠を作る必要が無い。
  欠けているのは自動化の前半だけであり、後半（対話・確定）は既に実装済みで動いている。
- 「生成の自動化」と「確定の自動化」は安全上まったく別物である。前者は失敗しても提示が増えるだけだが、
  後者は資産を動かす方針を無人で切り替える。両者を同じ PR で扱わないことで、レビューの焦点も分離できる。
- 判定を純関数に切り出すことで、JST 境界・休場日・月末週末の重なりという間違えやすい部分を、
  常駐プロセスを起動せずにテストで固定できる。

## 結果

- 良い影響: 閉場後に日報・週報・月報が自動で並び、利用者は「確定する」操作だけで運用できる。
  方針が 1 件で止まる状態（#279 ギャップ #2）が解消する。確定の信頼モデルは変わらない。
- 悪い影響 / トレードオフ: 利用者が確定しない限り報告書は溜まる一方になる（`PendingApproval` の滞留）。
  滞留の可視化・催促は `#210`（`DailyPolicyUnconfirmed`）と PR 2/2 の Discord 投稿が担う。
  週・月をまたぐ長期停止からの復帰では、その期間の報告書は生成されない。
- フォローアップ: Discord 投稿（PR 2/2）／LLM による方針提案の設計／市場別の取引日境界（#249）／
  実約定の伝播（#270）／有効化のための values 変更（別途・稼働中環境の作業）。

## 関連

- 実装仕様書: [20260729_280_report-auto-generation-scheduler](../specs/20260729_280_report-auto-generation-scheduler.md)
- 計画書: `04_workflows/03_reporting-cycle.md`（fixed）、
  ADR-0003（計画リポ）（Accepted）
