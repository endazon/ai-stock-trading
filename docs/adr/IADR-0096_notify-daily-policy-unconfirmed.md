---
title: IADR-0096 日報未確定による取引スキップは DailyPolicyUnconfirmed イベントで通知し、営業日単位の in-memory dedup で抑止する（既定 no-op / opt-in）
type: impl-adr
status: Accepted
related_ids: [UC-01, FR-09, FR-07, FR-11, ADR-0003, IADR-0020, IADR-0023, IADR-0079, IADR-0083, IADR-0085]
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - "../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md (UC-01: 定時取引サイクル)"
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0096: 日報未確定による取引スキップは DailyPolicyUnconfirmed イベントで通知し、営業日単位の in-memory dedup で抑止する（既定 no-op / opt-in）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **UC-01**（情報収集→判断・例外フロー「日報未確定」）、**FR-09**（Discord 通知）、
  **FR-07**（無応答時の既定動作）、**FR-11**（監査＝全イベントの時系列記録）、ADR-0003（取引判断）。
- 対象 Issue: [#210](https://github.com/endazon/ai-stock-trading/issues/210)（日報未確定による取引スキップ時に確定を促す通知を発行）。
- 関連 IADR: [IADR-0020]（通知・実送信は既定オフ）、[IADR-0023]（取引サイクル配線・両系統の合流）、
  [IADR-0079]（イベント契約の後方互換）、[IADR-0083]/[IADR-0085]（撤退通知の durable dedup — 本件との差異の根拠）。

## コンテキストと課題

実環境構築前監査（2026-07-18）で、`TradeDecisionService.DecideAsync` が確定済み日報の方針なし（`policy is null`）で
取引サイクルを中止する際、`LogInformation` のみで通知イベントを発行していないことが検出された。UC-01 の例外フローは
「日報の確定を促す通知」を要求しており、利用者が日報未確定に気づかず取引停止が意図せず継続するリスクがある。

`DecideAsync` は policy-null 以外にも Hold・数量0・採算不成立で `null` を返すが、これらは正常な AI 判断であり毎巡回で
起こり得る。通知対象は **policy-null（日報未確定）のみ** とする（他を通知するとスパムになり UC-01 の要求外）。

## 決定

### 決定1: 発火点は Application 層 `DecideAsync` の policy-null 分岐（consumer 側ではない）

定時（`InformationCollectedConsumer`）・価格変動（`PriceMovementDetectedConsumer`）の両系統は `DecideAsync` 経由で、
戻り値 `null` の理由（policy-null / Hold / 数量0 / 採算）を区別できない。policy-null を知るのは `DecideAsync` の当該分岐
だけなので、そこに通知フックを置く。両 consumer が単一経路を共有するため 1 箇所で両系統をカバーする（[IADR-0023]）。

### 決定2: ポート＋NoOp 既定で現行挙動を保持（opt-in で実発行）

`IDailyPolicyUnconfirmedNotifier` を Application ポートとして導入し、`TradeDecisionService` の ctor へ**任意引数**で注入する
（未指定＝`NoOpDailyPolicyUnconfirmedNotifier`＝何もしない＝現行のログのみ）。RAG（[IADR-0072]）・採算（[IADR-0076]）と
同型の「既定 no-op・Worker が opt-in で実装差し替え」パターン。実装は Worker の `PublishingDailyPolicyUnconfirmedNotifier`
（`IBus` で `DailyPolicyUnconfirmed` を publish）で、構成フラグ `TradeCycle:NotifyOnUnconfirmedPolicy`（既定 false）が
true のときだけ配線する。

**なぜ opt-in（常時 publish しない）か**: 既定では `PlaceholderDailyPolicyProvider` が常に null を返す（Reports サービス
未結線＝安全既定＝取引しない）。この状態で常時 publish すると「日報を確定してください」が毎営業日誤発火する（利用者は
何も忘れていない＝偽アラーム）。実 Reports が結線され null が真に「本日未確定」を意味する環境でのみ有効化する。既定・CI・
dev は publish せず現行挙動を完全維持する。

### 決定3: 新イベント `DailyPolicyUnconfirmed`（追加のみ・グローバル粒度）

`DailyPolicyUnconfirmed(DateOnly BusinessDay, DateTimeOffset OccurredAt)` を `Shared.Contracts.Events` に追加する
（後方互換＝追加のみ・[IADR-0079]）。日報方針はグローバル（銘柄非依存）のため銘柄・市場は持たせない。`AuditService` に
対応 consumer を追加し（`AuditConsumerCoverageTests` 緑）、中央監査台帳へ記録する（FR-11・受け入れ基準③）。相関は
`"daily-policy"` の決定的 GUID（同一原因を束ねる）。`NotificationService` は consumer＋`NotificationFormatter` で
Warning 通知として整形する（実送信は既定オフ・[IADR-0020]）。

### 決定4: dedup は営業日単位の in-memory singleton（durable にしない）

同一営業日内の重複抑止は `clock.Today` を鍵にした in-memory singleton（スレッドセーフ）で行う。定時サイクルは watchlist を
巡回し全銘柄で policy-null に当たるため、無抑止だと 1 巡回で N 件・巡回ごとに再発する。当日初回のみ publish し当日を記録、
以降は抑止、翌営業日で再通知する。

**なぜ durable（DB）にしないか**: `TradeDecisionService.Worker` は意図的にステートレス（DB なし）。リマインダ 1 件のために
EF/永続化層を新設するのは過剰（計画外の大規模化）。トレードオフ＝プロセス再起動時に当日リマインダが最大 1 回重複し得るが、
これは無害（利用者への「確定してください」の再掲）。撤退降格（[IADR-0085]）が durable を要したのは、握り潰すと降格提案を
恒久的に失う安全性の問題があったからで、本件（無害な再掲）とは性質が異なる。

**dedup 鍵・BusinessDay は UTC 暦日（JST 営業日ではない）**: 鍵は `clock.UtcNow` の UTC 暦日で、`IBusinessCalendar` の JST
営業日判定・週末判定は用いない（`ReportService` の `clock.UtcNow.UtcDateTime` 由来と同一の既存慣行）。「翌営業日で再通知」は
実質「翌 UTC 暦日で再通知」を意味する。JST 0:00〜9:00（UTC 前日 15:00〜24:00）の境界では表示・dedup 粒度が JST 営業日と
ずれ得るが、トリガー（定時・価格変動）は実運用上ほぼ市場営業時間内に発火するため実害は小さい。将来 `IBusinessCalendar` 経由へ
寄せる余地は残す（命名「BusinessDay」と実装「UTC 暦日」の乖離はコメント・イベント定義で明示済み）。

## 影響・波及

- 追加のみ: `Shared.Contracts`（イベント 1 件）／Application（ポート＋NoOp）／Worker（実 notifier＋DI＋フラグ）／
  Notification・Audit（各 consumer・整形・登録）。既存の型・挙動は不変。
- `event-schemas.baseline.json` を `UPDATE_EVENT_BASELINE=1` で再生成（新イベント承認・追加のみ）。

## 代替案と却下理由

- **`DecideAsync` の戻り値に skip 理由を持たせ consumer で通知**: 戻り型変更が全呼び出し・全テストに波及し破壊的。却下。
- **常時 publish（フラグなし）**: 未結線の Placeholder 環境で偽アラーム。却下（決定2）。
- **durable（EF）dedup**: ステートレス Worker への過剰な永続化層。却下（決定4）。
- **Hold/数量0/採算も通知**: 正常判断でスパム・UC-01 要求外。却下。
