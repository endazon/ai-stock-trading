---
title: IADR-0071 報告書サービス残スコープは ReportService に閉じ、実 LLM/実 KB を既定オフ・opt-in、対話的確定は状態機械の薄い HTTP 結線で実装する
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-07, FR-16, FR-08, FR-09, UC-03, UC-04, UC-05, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# IADR-0071: 報告書サービス残スコープは ReportService に閉じ、実 LLM/実 KB を既定オフ・opt-in、対話的確定は状態機械の薄い HTTP 結線で実装する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-06/07/16**（報告書の自動生成・対話的確定・数値のコード集計）、FR-08（確定報告書の KB 保存）、
  FR-09（確定通知）、UC-03〜05、**ADR-0003**（human-in-the-loop・完全無人での方針変更をしない）、FR-06/FR-07（利用者のみが確定）
- 対象 Issue: [#14](https://github.com/endazon/ai-stock-trading/issues/14)
- 関連する実装仕様書: [20260718_report-service-remaining](../specs/20260718_report-service-remaining.md)
- 関連 IADR: [IADR-0032](IADR-0032_report-generation.md)（散文 LLM ドラフトのポート `IReportNarrativeDrafter`＝本作業で実 LLM 実装を差し込む）、
  [IADR-0042](IADR-0042_report-review-state-machine-and-detail-rendering.md)（`ReportReviewStateMachine` 純ドメイン＝本作業で HTTP へ結線）、
  [IADR-0061](IADR-0061_llm-production-wiring.md)（実 LLM 接続の安全既定・fail-safe・全量ログの先例。本作業はこれに倣う）、
  [IADR-0069](IADR-0069_knowledge-base-rag-foundation.md)（`IKnowledgeBaseWriter` 既定 no-op・opt-in＝本作業で確定報告書保存に結線）、
  [IADR-0024](IADR-0024_report-confirmation-and-policy.md)（版番号付き冪等確定）

## 背景・課題

報告書サービスは確定管理・数値集計（`PnlAggregator`）・テンプレート生成（`ReportRenderer`）・対話的確定の純ドメイン
（`ReportReviewStateMachine`）まで実装済み。残るのは「実運用のための差し込み」であり、いずれも安全既定を保ったまま結線する:

1. 散文ドラフトが `PlaceholderReportNarrativeDrafter`（定型文）のまま＝実 LLM 未接続。
2. 無応答時の既定動作が未定義（翌営業日開場までに応答が無い場合の挙動）。
3. 確定報告書の KB 保存（FR-08）が未結線。
4. 初回月報ブートストラップ（初期監視銘柄の選定対話）が未実装。
5. `ReportReviewStateMachine` に HTTP/永続の結線が無い（純ドメインのみ）。

制約: **ReportService に閉じる**（#11=TradeDecision・#9=InformationCollection が並行で他サービスを触るため）。`Shared.Contracts`
は追加のみ、`Shared.KnowledgeBase`（#18 マージ済み）は変更せず利用のみ。Risk・他サービスの実装には触れない。

## 決定

### 1. 実 LLM 散文ドラフトは IADR-0061 と同形の安全既定・fail-safe で差し込む（S1）

`HttpReportNarrativeDrafter`（Worker）を追加し、platform LLM ゲートウェイ `POST /complete` を呼ぶ。プロンプトは純関数
`ReportNarrativePromptBuilder`（Application）で構築し「**散文のみ・数値は再計算/改変しない**（数値はコード集計が権威・FR-16）」を明示する。

- `LlmGateway:BaseUrl` 未設定/不正 URI → 現行 `PlaceholderReportNarrativeDrafter`（定型文）を維持（**既定オフ**）。設定時のみ実照会。
- fail-safe（IADR-0061 と一致）: 送信拒否（`Sent=false`）・非 2xx・タイムアウト・空/不正応答・例外は**プレースホルダ散文**へ倒す。
  取引判断の Hold と異なり、報告書は発注を伴わないため安全側＝「LLM 未接続の定型散文（捏造しない）」であり、数値は常にコード集計値を出す。
- `LlmGateway:TimeoutSeconds`（既定 30・非正値は既定へ）、`LlmGateway:LogPrompts`（既定オフ＝機微を既定でログへ流さない）、
  `LlmGateway:Confidentiality`（既定 internal）、`LlmGateway:Purpose`（既定 report-narrative）。
- `/complete` は匿名（platform 側）ゆえ s2s トークンは付けない。リトライはゲートウェイ側一元化（ADR-0010）に委ね呼び出し側で重ねない。
- **費用計測（`LlmCostIncurred`）は本作業では結線しない**（報告書生成は低頻度・スコープ外）。seam を残す（申し送り）。

### 2. 無応答時の既定動作は「直近の確定済み方針を継続」を安全既定とし、純ドメインで決定する（S2）

`ReportNoResponsePolicy.Decide(now, deadline, hasPendingReview, behavior)` を純関数で定義する。`NoResponseBehavior` は
`ContinueLastConfirmed`（既定）/ `Halt`。翌営業日開場（`deadline`）までに応答（承認）が無い場合:

- `ContinueLastConfirmed`: 直近の**確定済み**方針を継続する。これは既存の `GetConfirmedDailyPolicy`（Confirmed のみ返す）で
  **構造的に既に成立**している（未確定ドラフトは方針として返らない）。よって既定動作は enforcement 済み。
- `Halt`: 停止シグナル（方針を返さない）。**実強制**（期限検知→取引停止）はスケジューラ結線（#22）に委ねる seam とし、本作業は
  決定関数と設定 `Reports:NoResponseBehavior` の読み取りまで。設定は起動時に `ReportNoResponsePolicy.ParseBehavior` で解釈し
  ログへ出力する（オペレーターが反映を確認でき、値が実際に消費されていることを可視化する）。スケジューラ（#22）はこの設定と
  `Decide` を読み取り期限検知→停止を実強制する。

理由: ADR-0003（human-in-the-loop）は「完全無人での方針変更をしない」ことを求める。無応答時に**新方針を自動適用しない**（確定を経ない
方針は不適用）ことが本質であり、既に確定済みの（＝過去に人が承認した）方針の継続は安全側。停止も安全側だが、日次運用の連続性を優先し継続を既定とする
（計画 #14 本文の既定「直近の確定済み日報方針を継続」に一致）。

### 3. 確定報告書の KB 保存は確定遷移時に fail-safe で行う（S3・既定 no-op）

確定エンドポイントで Draft→Confirmed 遷移が起きたときのみ、`IKnowledgeBaseWriter.SaveAsync`（#18）へカタログ文書を保存する。

- 文書は純関数 `ReportKnowledgeMapper.ToDocument(report)` で写像（Title・Confidentiality=internal・Tags=[report, kind]・
  Attributes に periodKey/kind/assumptionsVersion/confirmedAt）。本文（Markdown 実体）は現行 `POST /documents` が受けないため送らない（IADR-0069）。
- 既定は no-op（`KnowledgeBase:Documents:BaseUrl` 未設定＝保存しない）。保存の失敗・例外は握りつぶし**確定を壊さない**（KB は best-effort）。
- 保存は確定イベント発行（`ReportConfirmed`）と独立。順序は「確定→イベント発行→KB 保存（best-effort）」。

### 4. 初回月報ブートストラップは「確定済み月報なし」を検知して初期監視銘柄ドラフトを提示する（S4）

`MonthlyBootstrap.BuildDraft(month, watchlist)`（純関数）で、確定済み月報が存在しないときに初期監視銘柄を選定したブートストラップ
月報ドラフト（`BasedOn=null`・PolicySummary に初期銘柄）を生成する。`GET /reports/monthly-bootstrap`（OwnerOnly）は、確定済み月報が
既にあれば 404（ブートストラップ不要）、無ければドラフトを返す。初期監視銘柄は `Reports:Bootstrap:Watchlist`（構成）から取り、未設定なら空。

### 5. 対話的確定は状態機械を薄い HTTP 層＋永続 ReviewState で結線する（S5）

`ReportReviewStateMachine`（IADR-0042）を HTTP へ結線する。`ReviewState` 列を報告書行に追加（Migration）。

- 新エンドポイント（OwnerOnly）: `POST /{key}/present`（Drafting/ChangesRequested→PendingApproval）・`POST /{key}/request-changes`
  （PendingApproval→ChangesRequested）。いずれも `ReportReviewStateMachine.Decide` で検証し、拒否は HTTP へ写像（版不一致・不正遷移・
  確定済み変更 = 409）。これらは内容不変のため Version を上げない（状態機械の `bumpVersion:false` と一致）。
- **改訂（Revise）は既存 PUT upsert が担う**（新ドラフト＝内容変更で Version+1）。upsert は ReviewState を Drafting へ戻す。
- **承認（Approve）は既存 confirm が担う**（Draft→Confirmed・Version+1・`ReportConfirmed` 発行）。confirm は ReviewState を Confirmed へ。
- 既存 confirm は後方互換のため任意の非確定状態から確定可能とする（既存フロー・テスト・#11 の daily-policy 経路を壊さない）。present/
  request-changes は**追加の対話局面**を表現する層。#15 Discord Bot はこれらの HTTP を呼んで提示・差し戻し・承認を駆動する（Bot 自体は
  NotificationService＝別サービスのため本作業では触れない）。

## 検討した代替案

- **A（S1）: 費用計測も同時結線** — 却下（スコープ外）。報告書生成は日/週/月次で低頻度。#11 の egress 計測に倣う seam を残すに留める。
- **B（S2）: 無応答既定を Halt** — 却下。日次運用の連続性を損なう。確定を経ない新方針の不適用が本質で、確定済み方針の継続は安全側。
  Halt は設定で選べる。
- **C（S3）: 確定報告書の Markdown 本文も KB へ送る** — 却下。現行 `POST /documents` は本文を受けず object storage 経路は
  platform 依存（IADR-0069）。カタログ登録に留め、本文取り込みは後続。
- **D（S5）: 承認を PendingApproval 限定にゲート** — 却下。既存 confirm（Draft から直接確定）を壊し #11 の実データ経路・既存テストに波及する。
  present/request-changes を**追加**し、confirm は後方互換を保つ。
- **E: 全結線を #22 スケジューラ側に置く** — 却下。ReportService に閉じる制約に反し、報告書固有ロジックが他サービスへ漏れる。

## 影響・リスク

- 既定構成（実 LLM/実 KB 未設定）では挙動は**完全に不変**（プレースホルダ散文・no-op 保存・継続既定）。既存 30+ テストプロジェクトは緑を維持。
- `Shared.Contracts` は不変・新イベント無し → 監査 Consumer 追随不要（`AuditConsumerCoverageTests` に影響なし）。
- `ReviewState` 列追加は Migration を伴う。既存行は既定 `Drafting`（確定済みは confirm 経路で `Confirmed` に更新される）。InMemory ストアも同期。
- 無応答 Halt の実強制・LLM 費用計測・KB 本文取り込みは後続（seam を明記）。CI は外部接続なしで緑（実 LLM/KB は #82 系 E2E）。
