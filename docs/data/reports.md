---
title: 報告書（reports）データ仕様書
type: data-spec
status: review
created: 2026-07-10
updated: 2026-09-25
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-06, FR-07, FR-08, FR-14, FR-16, FR-17, UC-03, UC-04, UC-05]
adrs: [ADR-0001, ADR-0003]
iadrs: [IADR-0012, IADR-0024, IADR-0240, IADR-0352, IADR-0418]
specs: [20260710_report-confirmation, 20260919_774_report-confirmed-actor-on-behalf-of, 20260919_840_report-transient-dependency-retry, 20260925_843_report-period-keys-projection]
issues: [#14, #18, #19, #22, #63, #774, #840, #843]
-->


# データ仕様書: 報告書（reports）

> 報告書サービス（`ReportService`）が所有する報告書（日報/週報/月報）の永続化。取引方針の階層管理と対話的確定、
> 報告書テンプレートによる集計、全体前提条件のバージョン記録を支える。設計は「報告書サービスが確定管理と確定済み
> 日報方針を所有し、確定はイベントで通知する」、版番号確定は「単一行 JSON ＋バージョン列で永続化し楽観的排他制御する」
> を踏襲する。作業仕様は 仕様書: 報告書サービス Slice A（確定管理・方針の実体）。

## 本書が受け持つ範囲

- 機能要求: 取引方針の階層管理、報告書の対話的確定（確定前の方針は不適用）、報告書テンプレートによる集計、全体前提条件のバージョン記録
- ユースケース: 日報・週報・月報の対話的確定
- 計画 ADR: 基盤採用（Database per Service）、生成AIの売買判断の拘束（確定は利用者のみ・確定前方針は不適用）

## ドメイン型（`TradingReport`）

| 属性 | 型 | 説明 |
| --- | --- | --- |
| PeriodKey | string | 自然キー（"daily-2026-07-10" / "weekly-2026-W28" / "monthly-2026-07"） |
| Kind | ReportKind | Daily / Weekly / Monthly |
| PeriodStart | DateOnly | 対象期間の開始日（最新の確定済み日報照会に使用） |
| State | ReportState | Draft / Confirmed（確定した方針のみ取引に適用） |
| BasedOn | string? | 参照した上位方針の PeriodKey（daily→週報 / weekly→月報 / monthly→前月報） |
| AssumptionsVersion | int | 適用した全体前提条件のバージョン（#19） |
| PolicySummary | string | 翌期間の方針（確定で有効化） |
| UnsuppliedInputs | ReportInput の列 | 供給が届かないまま生成された入力（その種別が使うものだけ）。空＝欠けた入力なし。提示通知の要約とレビュー照会（`GET /reports/{periodKey}/review`）が表示名で返し、確定の前に欠落へ気付けるようにする |
| ConfirmedAt | DateTimeOffset? | 確定日時 |

## エンティティ定義（`reports`・永続）

| 属性 | 型 | 説明 |
| --- | --- | --- |
| PeriodKey | string(64) (PK) | 自然キー |
| Kind / State | 列挙（int 格納） | 種別・状態 |
| PeriodStart | date | 期間開始日 |
| BasedOn | string(64)? | 上位方針参照 |
| AssumptionsVersion | int | 前提条件バージョン |
| PolicySummary | string(8192) | 方針テキスト |
| UnsuppliedInputs | string(1024)? | 供給が届かないまま生成された入力（入力名のカンマ区切り・宣言順）。NULL＝欠けた入力なし（列の追加前に作られた行も NULL）。**本文に従う**——本文を差し替えない改訂では残し、確定しても残る |
| ConfirmedAt | timestamptz? | 確定日時 |
| Version | int（並行トークン） | 版番号付き冪等確定の楽観排他 |

- インデックス: `(Kind, State, PeriodStart)`（最新の確定済み日報の照会）。

## 照会・操作

- `GET /reports`、`GET /reports/{periodKey}`、`GET /reports/daily-policy`（確定済み日報方針＝Date/Summary/AssumptionsVersion・未確定は 404）。
- `GET /reports/period-keys`（会話キーと開始日だけの軽い一覧＝`[{periodKey, periodStart}]`・開始日の降順、同日は会話キーの降順・ページングなし）。Discord の `/report` の入力補完が打鍵ごとに読むため、本文・要約・状態を載せない。OwnerOnly。`/{periodKey}` より優先される（`daily-policy` と同じくリテラル一致）。
- `PUT /reports/{periodKey}`（ドラフト upsert・楽観排他）、`POST /reports/{periodKey}/confirm`（版番号付き冪等確定）。すべて OwnerOnly。
- **版番号付き冪等確定**: Draft→Confirmed の遷移時のみ `ConfirmedAt` 記録＋`ReportConfirmed` 発行（通知サービスが Discord 通知）。
  既に確定済みの再確定は冪等（状態変化なし・イベント重複なし）。版不一致は 409、確定済みの変更は 409、未認証 401/無権限 403。
- **確定者の解決**: 確定要求の本文は `expectedVersion` と任意の `onBehalfOf`（代理される利用者＝Keycloak 利用者名）。
  Discord Bot は機密クライアント（`client_credentials`）のトークンで確定を呼ぶため、トークンからは人を解決できない。
  `onBehalfOf` は **トークンの `azp` が構成 `Reports:DelegatedActor:TrustedClientIds`（カンマ区切り・既定は空＝誰も信じない）に
  一致するときだけ**確定者として採り、`ReportConfirmed` には `Actor`＝操作した利用者と `AuthorizedBy`＝認可の主体（クライアント ID）の
  両方を載せる。利用者本人のトークンや一覧外のクライアントが送った `onBehalfOf` は**無視**する（確定は通り、確定者はトークンの主体）。
  信頼クライアントが値域外（`[A-Za-z0-9._@+-]` の 1〜64 文字以外）の値を送ると 400 で確定しない。
  操作者が取れないとき（名前クレームの無いトークンで `onBehalfOf` も無い）は `client:<azp>` を確定者にする。

## 整合性・制約ルール

- PeriodKey ごとに 1 行。確定済みは不変（`UpsertDraft` で変更不可）。版番号（Version）で楽観排他しロストアップデートを防ぐ。
- 確定は利用者のみ（OwnerOnly・アクター必須）。生成AI・自動処理は確定できない。

## 永続化方針

| 集約 | 永続化 | 実装 issue | 備考 |
| --- | --- | --- | --- |
| TradingReport（`reports`） | PostgreSQL 1 行/PeriodKey＋Version（専有 DB `report_svc`） | #14（PR） | 版番号付き冪等確定（単一行 JSON ＋バージョン列の方式を踏襲） |

## 対象外（後続）

- 損益・費用・税の集計列（報告書テンプレートの集計。取引台帳 #63・前提条件 #19 を参照するコード集計）。LLM ドラフト・対話的確定・ナレッジベース保存（#18）。
- 無応答時の既定動作・階層（月報→週報→日報）参照の強制・取引判断の `IDailyPolicyProvider` 結線。

## 関連仕様

- 作業仕様書: 仕様書: 報告書サービス Slice A（確定管理・方針の実体）
- 実装ADR: 報告書サービスが確定管理と確定済み日報方針を所有し、確定はイベントで通知する／リスク管理設定は単一行 JSON ＋バージョン列で永続化し楽観的排他制御する（踏襲）
