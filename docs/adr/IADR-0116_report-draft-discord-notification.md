---
title: IADR-0116 提示したドラフトは新イベント ReportDraftPresented で通知し、投稿本文は Discord 向けの専用サニタイザを通す
type: impl-adr
status: Accepted
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

# IADR-0116: 提示したドラフトの Discord 投稿

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-29
- 決定者: endazon（利用者・(a) のシーケンスを確定）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-09（通知）、FR-06/07（報告書生成・対話的確定）、FR-11（監査）、UC-01・UC-03〜05、
  `04_workflows/03_reporting-cycle.md`（**fixed**・`REP->>DC: ドラフト提示（要約＋閲覧リンク）`）、
  [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)（Accepted）
- 対象 Issue: [#280](https://github.com/endazon/ai-stock-trading/issues/280)・傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279) ギャップ #2/#3
- 関連する実装仕様書: [20260729_280_report-draft-discord-notification](../specs/20260729_280_report-draft-discord-notification.md)
- 関連 IADR: [IADR-0115](IADR-0115_report-auto-generation-scheduler.md)（生成スケジューラ・PR 1/2）、
  [IADR-0020](IADR-0020_notification-safe-outbound.md)、[IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)、
  [IADR-0022](IADR-0022_information-collection-safe-sourcing.md)、[IADR-0079](IADR-0079_event-backward-compat-contract-test.md)、
  [IADR-0096](IADR-0096_notify-daily-policy-unconfirmed.md)、[IADR-0106](IADR-0106_consumer-endpoint-name-uniqueness.md)

## 背景・課題

IADR-0115（PR 1/2）で報告書のドラフトは閉場後に自動生成され `PendingApproval` へ並ぶようになった。しかし
**利用者に届く経路が常駐のログしか無い**。計画書のシーケンス（fixed）は生成の直後に
「`REP->>DC: ドラフト提示（要約＋閲覧リンク）`」を置いており、ここが欠けたままだと (a) のフロー
（自動生成 → 提示 → **利用者が確定**）が成立しない。確定を待つ報告書が誰にも気付かれず溜まるだけになる。

確定通知（`ReportConfirmed`）は既にあるが、**提示（確定依頼）に対応するイベントが無い**。

## 検討した選択肢

1. **新イベント `ReportDraftPresented` を追加し、通知サービスが購読して整形する**（既存の通知経路を再利用）
2. **report-service が通知サービスの HTTP を直接叩く** — s2s 依存が増え、通知の宛先・整形の責務が分散する
3. **既存 `ReportConfirmed` にフィールドを足して提示でも流す** — 「確定した」という意味を持つイベントの意味が壊れる

## 決定

**選択肢 1** を採る。加えて以下を確定する。

### 決定 1: 提示は `ReportDraftPresented`（新規イベント）で表し、既存イベントは変更しない

`ReportConfirmed`（確定）と対になる「提示」イベントを `Shared.Contracts.Events` に追加する。
既存イベントには手を触れないため、後方互換の**追加のみ**（IADR-0079 の契約テストが許容する変更）に収まる。

新イベント 1 件の追加には、リポジトリの契約ガードに対する追随が 3 点必要である（いずれも CI が強制する）。

| ガード | 追随 |
| --- | --- |
| `EventBackwardCompatibilityTests` | `event-schemas.baseline.json` を `UPDATE_EVENT_BASELINE=1` で再生成 |
| `EventMessageUrnTests` | 正準 URN の `[InlineData]` を追加（母集合との完全一致を別テストが検査する） |
| `AuditConsumerCoverageTests` | 監査 Consumer を追加（FR-11「全イベントの時系列記録」） |

`Kind` は `ReportConfirmed` と同じく **文字列**で持つ（列挙型を wire 契約に晒さず、値の追加で消費側が壊れないようにする）。
`Version` を載せるのは、利用者が確定する際の `expectedVersion`（版番号付き冪等・IADR-0024）を通知だけで得られるようにするため。

### 決定 2: 発行は「提示まで到達したものだけ」。best-effort で生成を壊さない

通知点は `ReportAutoGenerator` で、**提示（`Present`）が受理された直後だけ**通知する。IADR-0115 決定 1 で導入した
`NotPresented`（提示が受理されなかった期間）は通知しない。承認待ち一覧に並んでいないものを「確認してください」と
通知すると、利用者が探しても見つからない状態になるためである。

Application 層を MassTransit へ依存させないため、通知はポート `IReportDraftPresentedNotifier`（既定 no-op）を挟む
（`#210`／IADR-0096 の通知ポートと同型）。実装は Worker 側の `MassTransitReportDraftPresentedNotifier` が `IBus`
（singleton）で `ReportDraftPresented` を発行する。常駐は巡回ごとにスコープを作るが、発行はスコープに依存しないため
scoped な `IPublishEndpoint` は引かない。通知の失敗・例外は生成側で捕捉し、**生成・提示は成功のまま**とする
（通知は best-effort・IADR-0020 の方針と同じ。報告書は既に永続化され承認待ちに並んでおり、通知不達で作り直すほうが
害が大きい）。

重複送信の抑止は新たに作らない。生成そのものが `PeriodKey` で冪等（IADR-0115 決定 3）であり、
提示は 1 回しか起きないため、`#210`（IADR-0096）のような営業日単位の dedup を持つ必要がない。

構成 `Reports:AutoGeneration:NotifyOnDraftPresented`（既定 **true**）が false のときは DI が no-op 実装を選び、
イベントを 1 件も発行しない。

既定を true にするのは発行点の位置が違うためである。`#210`（IADR-0096）が既定 false（opt-in）だったのは、
発行点が**常時稼働の取引サイクル上**にあったからで、本件の発行点は**既定無効の常駐の内側**にある。
有効化した利用者にとって「報告書は作られているのに何も届かない」は #279 が問題視した「無言で止まっている」状態
そのものであり、二段目の opt-in を要求するほうが危険である。既定挙動のバイト等価は常駐の既定無効が担保する。

### 決定 3: `PromptSafetySanitizer` は共有化せず、Discord 向けの専用サニタイザを置く

投稿本文には LLM 生成の散文が含まれるため、サニタイズを通す。ただし既存の
`PromptSafetySanitizer`（IADR-0022）を共有物へ移して再利用することは**しない**。

`PromptSafetySanitizer.Sanitize` は本文を `<<<UNTRUSTED_DATA … UNTRUSTED_DATA>>>` で**囲って返す**関数である。
これは「LLM プロンプトに埋め込む値を、命令ではなくデータとして構造的に分離する」ための正しい設計だが、
**人間が読む Discord 投稿に境界語を被せるのは誤り**であり、そのまま流用すると読めない投稿になる。
共有物へ移すには公開 API（`Sanitize` が囲う）を分解する必要があり、情報収集サービスの本番経路
（FR-01 の取得テキスト正規化）に回帰リスクを持ち込む。得られる利益に見合わない。

したがって `ReportService.Domain` に `ReportSummarySanitizer` を置き、**同じ防御思想（データを命令にしない・
制御文字を落とす・境界を偽装させない）を投稿先の脅威モデルに合わせて実装**する。

| 対象 | 処置 | 理由 |
| --- | --- | --- |
| 制御文字（`\n` 以外） | 除去 | 表示・ログの破壊を防ぐ |
| `@everyone` / `@here` | `@` の直後に U+200B | LLM 出力から一斉通知を起こさせない |
| `<@…` / `<@!…` / `<@&…` / `<#…` | `<` の直後に U+200B | ユーザー・ロール・チャンネルの mention 化を防ぐ |
| `<<<UNTRUSTED_DATA` / `UNTRUSTED_DATA>>>` | 除去 | 収集情報の境界語を本文が偽装できないようにする |
| 3 行以上の連続改行 | 2 行へ | 可読性 |
| 上限超過 | 切り詰め＋`…` | Discord の長さ制約・監査の可読性 |

境界語の 2 定数は `PromptSafetySanitizer` と重複するが、型の共有はしない（上記の理由）。値が動くのは
プロンプト仕様の変更時のみで、その際は本 IADR とあわせて見直す。現状 report-service の散文プロンプト
（`ReportNarrativePromptBuilder`）は収集情報を含まないため、境界語の除去は将来 RAG 文脈を入れたときの多層防御である。

### 決定 4: 要約の数値はコード集計値のみ。サニタイズは組み立て関数の内側で必ず適用する

要約（`ReportSummary.Build`）は純関数で、数値は `PnlSummary`（コード集計・FR-16）から埋め、散文は LLM 出力を
**必ずサニタイザに通してから**差し込む。呼び出し側が「サニタイズし忘れる」余地を作らないため、`Build` の外に
サニタイズを置かない。散文を取り出せるよう `ReportDraft` に `Narrative` を追加する（Markdown からの再抽出はしない）。

### 決定 5: Discord からの確定コマンドは本 PR に含めない

`/confirm-report` のような制御コマンドは OwnerAuth（IADR-0062/0098）を要し、kill switch・pause と同じ
認可・確認・冪等の導線設計が必要になる。通知（一方向）と制御（双方向）は別の面であり、同じ PR で扱うと
レビューの焦点がぼやける。確定は当面 SC-01（フロント）と `POST /reports/{periodKey}/confirm`（OwnerOnly）で行う。

## 理由

- 既存の通知経路（`INotificationSender` → Discord provider）をそのまま再利用でき、**Discord 未設定なら no-op**
  という安全既定（IADR-0020/0062）を新たに壊さない。送信経路の追加であって、送信の有効化ではない。
- 提示イベントを別に立てることで、`ReportConfirmed`（確定＝方針が取引に効く）の意味を保てる。
  監査台帳でも「提示」と「確定」が別種として時系列に残り、確定までのリードタイムが追える。
- サニタイズを共有せず投稿先に合わせて書くことで、情報収集の本番経路に触れずに済む。

## 結果

- 良い影響: (a) のシーケンスが端から端まで成立する。利用者は Discord で要約を読み、そのまま確定操作へ進める。
  #279 ギャップ #2 の残り（届かない）と #3 の一部（報告書系の通知経路が無い）が解消する。
- 悪い影響 / トレードオフ: 境界語の定数が 2 箇所に重複する（意図的・上記）。Discord から直接確定はできず、
  フロント/HTTP へ遷移する必要がある。実送信は環境固有 ID の投入（#279 ギャップ #3）が済むまで発火しない。
- フォローアップ: Discord の確定コマンド／確定通知（`ReportConfirmed`）の本文拡充／
  報告書本文の閲覧リンク（フロントの URL 体系が定まってから）。

## 関連

- 実装仕様書: [20260729_280_report-draft-discord-notification](../specs/20260729_280_report-draft-discord-notification.md)
- 計画書: `04_workflows/03_reporting-cycle.md`（fixed）、
  [ADR-0003](../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md)（Accepted）
