---
title: 自動生成した報告書ドラフトの Discord 投稿（確定依頼・PR 2/2）
type: spec
status: review
related_ids: [FR-06, FR-07, FR-09, FR-11, UC-01, UC-03, UC-04, UC-05, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 自動生成した報告書ドラフトの Discord 投稿

> 利用者確認（2026-07-29）で **(a) 自動で生成 → 提示 → Discord 投稿／確定は owner が人手** に確定。
> 完全無人の自動確定は行わない（ADR-0003 準拠のまま）。
>
> 本 PR（**2/2**）は投稿経路。生成スケジューラは PR 1/2（[#283](https://github.com/endazon/ai-stock-trading/pull/283)・
> [IADR-0115](../adr/IADR-0115_report-auto-generation-scheduler.md)）で、本 PR はその上に積む。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-09（通知）、FR-06（報告書生成）、FR-07（対話的確定）、FR-11（監査）
- ユースケース: UC-03〜05（月報・週報・日報）、UC-01（取引サイクルの通知）
- 業務フロー: `04_workflows/03_reporting-cycle.md`（**fixed**）
  — 「`REP->>DC: ドラフト提示（要約＋閲覧リンク）`」が本 PR の実装対象
- ADR: [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)（Accepted）
- 関連 IADR: [IADR-0115](../adr/IADR-0115_report-auto-generation-scheduler.md)（生成スケジューラ・PR 1/2）／
  [IADR-0020](../adr/IADR-0020_notification-safe-outbound.md)（通知サービス・実送信は安全既定無効）／
  [IADR-0062](../adr/IADR-0062_discord-bot-gateway-and-authorization.md)（Discord Bot・空＝全拒否）／
  [IADR-0022](../adr/IADR-0022_information-collection-safe-sourcing.md)（プロンプト安全化＝`PromptSafetySanitizer`）／
  [IADR-0079](../adr/IADR-0079_event-backward-compat-contract-test.md)（イベント契約テスト）／
  [IADR-0096](../adr/IADR-0096_notify-daily-policy-unconfirmed.md)（新イベントによる通知・#210）／
  本作業で新規 [IADR-0116](../adr/IADR-0116_report-draft-discord-notification.md)
- 対象 Issue: [#280](https://github.com/endazon/ai-stock-trading/issues/280)（`Refs #280`）・
  傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279) ギャップ #2/#3

## 現状（この変更の直前）

| 面 | 実態 |
| --- | --- |
| 提示の通知 | **無い**。PR 1/2 でドラフトは `PendingApproval` に並ぶが、利用者に届く経路がログだけ |
| 確定の通知 | `ReportConfirmed` → `ReportConfirmedNotificationConsumer` で**既にある**（1 行の Info 通知） |
| Discord 送信 | `DiscordWebhookNotificationSender` / `DiscordNetBotGateway` はあるが、URL・ID 未設定で no-op（#279 ギャップ #3） |
| 監査 | `AuditConsumerCoverageTests` が「Events 名前空間の全イベントに監査 Consumer がある」ことを CI で強制 |

## 目的

1. 自動生成されたドラフトの**要約が Discord に届き**、利用者が確定操作へ進める。
2. 送信経路の追加に留め、**Discord 未設定なら従来どおり no-op**（実送信は利用者が ID/Webhook を入れて初めて発火）。
3. 投稿本文は**サニタイズ**を通す（制御文字・Discord メンション・データ境界語の無害化）。
4. 確定の信頼モデルは PR 1/2 と同じく不変（通知は「確定依頼」であって確定ではない）。

## 設計

### 1. 新イベント `ReportDraftPresented`（`Shared.Contracts/Events`）

```csharp
public record ReportDraftPresented(
    string PeriodKey,      // "daily-2026-07-29"
    string Kind,           // "Daily"/"Weekly"/"Monthly"（ReportConfirmed と同じく列挙を契約に晒さない）
    string PeriodLabel,    // "2026-07-29" / "2026-W31" / "2026-07"
    string Summary,        // サニタイズ済みの要約（数値＋散文抜粋）
    int Version,           // 提示時点の版番号（確定時の expectedVersion に使える）
    DateTimeOffset OccurredAt);
```

`ReportConfirmed`（確定）と対になる「提示」イベント。既存イベントは変更しない（後方互換の**追加のみ**・IADR-0079）。
契約テストの追随は 3 点: `event-schemas.baseline.json` の再生成／`EventMessageUrnTests` の `[InlineData]` 追加／
`AuditEventConsumers` への監査 Consumer 追加（`AuditConsumerCoverageTests` が CI で強制する）。

### 2. 発行点（report-service Worker）

`ReportAutoGenerator`（PR 1/2）が**提示（`Present`）が受理された直後にだけ**通知する。
未提示（`NotPresented`）・失敗した期間は通知しない（届く通知と実状態を食い違わせない）。

- 発行は Application ポート `IReportDraftPresentedNotifier` 経由（`#210`／IADR-0096 の通知ポートと同型）。
  既定実装は no-op で、Worker が MassTransit アダプタ（`IBus` で `ReportDraftPresented` を発行）を選ぶ。
  **Application 層は MassTransit に依存しない**（`ReportAutoGenerator` は単体テスト可能なまま）。
- **fail-safe**: 通知の失敗・例外は生成側で捕捉し、**生成・提示を巻き戻さない**（報告書は既に永続化され承認待ちに
  並んでいる。通知不達で作り直すほうが害が大きい）。ただし**黙って捨てない**: 失敗した期間を結果の
  `NotificationFailed` に載せ、常駐が警告ログに残す（`Failed`・`NotPresented` と同じ扱い）。
- 冪等: 生成自体が `PeriodKey` で冪等（IADR-0115 決定3）のため、同じドラフトの提示通知は 1 回だけ発生する。
- 構成 `Reports:AutoGeneration:NotifyOnDraftPresented`（既定 **true**）が false のときは DI が no-op 実装を選ぶ
  ＝イベントを 1 件も発行しない。既定 true とするのは、常駐そのものが既定無効（opt-in）であり、有効化した
  利用者にとって「生成したのに何も届かない」ほうが危険なため（#279 が問題視した「無言で止まっている」状態を
  新たに作らない）。既定挙動のバイト等価は常駐の既定無効が担保する。

### 3. 要約の組み立て（純関数・`ReportService.Domain`）

`ReportSummary.Build(kind, periodLabel, pnl, narrative)`:

```
日報 2026-07-29（承認待ち）
実現損益（税引後・費用込み）: +12,300 円 ／ 費用: 450 円 ／ 取引: 4 件（決済 2・勝ち 1）

（散文・サニタイズ済み）
```

金額表記は `ReportAmountFormat.Yen` に単一化し、本文テンプレート（`ReportRenderer`）と同じ表記にする
（同じ数値が経路によって違って見えないようにする）。散文は要約の残り枠に収める（数値行は切り落とさない）。

- 数値は**コード集計値**（`PnlSummary`）のみを使う。LLM に数値を語らせない（FR-16・IADR-0032 の踏襲）。
- 散文は LLM 出力のため**必ずサニタイズを通し**、全体長を上限で丸める（Discord の実務上の長さと監査の可読性）。
- サニタイズは `Build` の内部で必ず適用する（呼び出し側が忘れられる形にしない）。

`ReportDraft` に `Narrative` を追加して散文を取り出せるようにする（Markdown からの再抽出はしない）。

### 4. サニタイズ（純関数・`ReportSummarySanitizer`）

| 対象 | 処置 | 理由 |
| --- | --- | --- |
| 制御文字（`\n` 以外） | 除去 | 端末・ログ・Discord の表示を壊さない |
| `@everyone` / `@here` | `@` の直後に U+200B | 通知の一斉送信を LLM 出力から起こさせない |
| `<@…` / `<@!…` / `<@&…` / `<#…` | `<` の直後に U+200B | ユーザー/ロール/チャンネルの mention 化を防ぐ |
| `<<<UNTRUSTED_DATA` / `UNTRUSTED_DATA>>>` | 除去 | 収集情報の境界語（IADR-0022）を本文が偽装できないようにする |
| 3 行以上の連続改行 | 2 行へ畳む | 投稿の可読性 |
| 長文 | 上限で切り詰め、末尾に `…` | Discord の長さ制約・監査の可読性 |

**`PromptSafetySanitizer` は共有化せず、同じ防御思想の別関数を置く**（判断の理由は IADR-0116 決定 3）。
`PromptSafetySanitizer.Sanitize` は本文を `<<<UNTRUSTED_DATA … UNTRUSTED_DATA>>>` で**囲う**関数であり、
LLM プロンプトに埋める用途には正しいが、人間が読む Discord 投稿に境界語を被せるのは誤りである。

### 5. 通知（notification-service）

- `NotificationFormatter.From(ReportDraftPresented)` — Title「報告書ドラフト（承認待ち）」、
  本文は要約 ＋ 確定を促す 1 行（`PeriodKey` と版番号を明示）、Severity は `Info`。
- `ReportDraftPresentedNotificationConsumer` を追加し `Program.cs` へ登録する。
  クラス名はサービスを跨いで一意（IADR-0106・`check-consumer-endpoint-names.js` が CI で検査）。
- **Discord 未設定時は従来どおり no-op**。`NotificationSenderFactory` の既定（ログのみ）に変更を加えない。

### 6. 監査（audit-service）

`AuditEntryFactory.From(ReportDraftPresented)` ＋ `ReportDraftPresentedAuditConsumer` を追加する
（FR-11「全イベントの時系列記録」・`AuditConsumerCoverageTests` の要求）。相関は報告書系として `PeriodKey` から導出する。

## 影響範囲

| 対象 | 変更 |
| --- | --- |
| `Shared.Contracts` | `ReportDraftPresented`（**新規イベント 1 件・既存は不変**） |
| `Shared.Contracts.Tests` | `event-schemas.baseline.json` 再生成、`EventMessageUrnTests` へ `[InlineData]` 追加 |
| `ReportService.Domain` | `ReportSummary`／`ReportSummarySanitizer`（新規・純関数）、`ReportAmountFormat`（金額表記を本文と要約で単一化） |
| `ReportService.Application` | `IReportDraftPresentedNotifier` ＋ `NoOpReportDraftPresentedNotifier`（新規ポート・既定 no-op）、`ReportDraft.Narrative` 追加、提示成功時の通知（fail-safe） |
| `ReportService.Worker` | `MassTransitReportDraftPresentedNotifier`（新規アダプタ）、構成 `NotifyOnDraftPresented` による実装選択、introspection 自己申告 |
| `NotificationService` | `NotificationFormatter.From` ＋ Consumer ＋ 登録 |
| `AuditService` | `AuditEntryFactory.From` ＋ Consumer ＋ 登録 |
| Helm / values / compose | **不変**（稼働中環境へは触れない） |

## テスト（受け入れ基準の写像）

| # | 観点 | テスト |
| --- | --- | --- |
| 1 | 要約の内容 | 数値（実現損益・費用・件数）が入る／散文が抜粋される／種別ごとの見出し |
| 2 | サニタイズ | `@everyone`・`<@123>` が mention として成立しない／制御文字が消える／境界語が消える／上限で切り詰める |
| 3 | サニタイズ必須 | `ReportSummary.Build` が未サニタイズの散文をそのまま通さない |
| 4 | 発行 | 提示まで到達した報告書だけ発行される／未提示・失敗は発行されない |
| 5 | fail-safe | 発行が例外でも生成・提示が壊れない／失敗は `NotificationFailed` に載り常駐が警告する |
| 6 | 構成 | `NotifyOnDraftPresented=false` で発行しない |
| 7 | 通知整形 | `NotificationFormatter.From(ReportDraftPresented)` が要約と確定依頼を含む |
| 8 | 監査 | `AuditEntryFactory.From` が `PeriodKey` 相関で記録する／`AuditConsumerCoverageTests` が green |
| 9 | 契約 | 後方互換テスト・URN 固定テストが green（新イベントの追随漏れが無い） |
| 10 | no-op | Discord 未設定時に実送信が起きない（既存の `NotificationSenderFactory` テストで担保済み） |

## 受け入れ基準

- [x] 自動生成されたドラフトの要約が `ReportDraftPresented` として発行され、通知サービスが Discord へ整形する
- [x] 提示まで到達していない報告書は通知されない
- [x] 投稿本文がサニタイズを通っている（メンション・制御文字・境界語・長さ）
- [x] Discord 未設定時は従来どおり no-op（送信経路の追加のみ）
- [x] 新イベントに監査 Consumer があり、契約テスト（後方互換・URN）が green
- [x] 発行失敗が生成・提示を壊さない（fail-safe）
- [x] `dotnet build` / `dotnet test` / `dotnet format` が green・CI / gitleaks が green

## スコープ外

- **Discord からの確定コマンド**（`/confirm-report` 等）。制御コマンドは OwnerAuth を要する別の面であり、
  kill switch・pause と同じ導線設計が要る（IADR-0062/0098）。確定は当面 SC-01（フロント）と
  `POST /reports/{periodKey}/confirm`（OwnerOnly）で行う。follow-up として IADR-0116 に記録する。
- 確定通知（`ReportConfirmed`）の本文拡充。既存の通知は変更しない。
- Discord の GuildId / ChannelId / Webhook URL の実投入（#279 ギャップ #3・環境固有値の運用作業）。
- 自動確定（ADR-0003 に反するため実装しない）。
