---
title: IADR-0483 取引判断の最中の例外は、再試行の後の最終の失敗だけを TradeDecisionFailed として 1 件、監査台帳へ残す。載せるのは型名・起点・銘柄・時刻だけで、発行はランタイムの MessageBus から行う
type: impl-adr
status: Accepted
related_ids: [NFR, FR-04, FR-11, FR-02, UC-01, UC-02, IADR-0462, IADR-0129, IADR-0395, IADR-0023, IADR-0163, IADR-0079, IADR-0254]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs: []
---

# IADR-0483: 取引判断の最中の例外は最終の失敗だけを台帳へ残す（#1111）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-02
- 決定者: 利用者（残すかどうかと語彙。[#1111](https://github.com/endazon/ai-stock-trading/issues/1111) の裁定 2026-10-02「最小限で残す」）。Claude Code（実装の形）。

## 起点・関連

- 起票: [#1111](https://github.com/endazon/ai-stock-trading/issues/1111)（#1092 段 2〔AST#1110〕の対象外とした「判断中の例外」）
- 関連する計画書 ID: FR-11（判断のイベントを時系列で記録し後から監査できる）・FR-04（判断）・FR-02 / UC-01（定時）・UC-02（価格変動）。NFR（運用。無採番のメタ作業）
- 計画 ADR: 新たな制約なし（監査台帳の保持は計画の非機能要件の 7 年保持に従う。秘密を台帳へ載せないのはその保持期間の長さによる）
- 関連する実装仕様書: [`.ai-context/specs/20261002_1111_decision-final-failure-record.md`](../specs/20261002_1111_decision-final-failure-record.md)（母集合・窓の表・試験・自己変異）
- 前提: [IADR-0462](IADR-0462_ledger-position-query-status-and-pre-llm-skips.md) 決定5（別件とした）・[IADR-0129](IADR-0129_wolverine-messaging-topology.md) 決定5（再試行 2s/10s/30s → `<queue>_error`）・
  [IADR-0395](IADR-0395_drift-followup-abandoned-metric-and-alert.md)（配送回数から「最後の配送か」を決める形）・[IADR-0023](IADR-0023_trading-cycle-scheduling-and-merge.md)（定時は銘柄ごとに捕まえて続ける）

## 背景

取引判断の最中の例外は、ログにしか残らず Pod の再起動で消えた。経路で扱いが違う。

- 定時（`InformationCollectedHandler`）: 銘柄ごとに捕まえてログを出し、次の銘柄へ進む（IADR-0023）。その巡回で同じ銘柄はやり直さない。
- 価格変動（`PriceMovementDetectedHandler`）: 握らずに投げ、共通の再試行（2s/10s/30s）の後に `<queue>_error`（RabbitMQ・永続）へ移る。
- ハンドラが例外で終わると、その処理中にハンドラの `IMessageBus` で発行したメッセージは捨てられる（IADR-0129）。

裁定（2026-10-02）:「最小限で残す」。最終の失敗だけを 1 件、型名・発生源・銘柄・時刻だけ、ランタイムの MessageBus から出し、夜間の要約で数える。

## 決定

1. **事実 `TradeDecisionFailed`**（Shared.Contracts。追加のみ・baseline 登録。IADR-0079）: `EventId`・`Symbol`・`Market`・`CycleTrigger`（`scheduled` / `price-movement`。
   `TradeDecisionForgoneBeforeLlm` と同じ語彙）・`ExceptionType`・`OccurredAt` の 6 欄だけ。
   - 🔴 **メッセージ・スタック・内側の例外の欄を持たない**（秘密情報・口座 ID を含み得る。台帳は 7 年残る）。欄の集合は試験と baseline が固定する。
   - `ExceptionType` は名前空間つきの型名。総称型は定義の名前（`Ns.Foo`1`）にする（閉じた総称型の `FullName` は型引数のアセンブリ名・版まで含む）。外側の例外の型だけを見る。
2. **最終の失敗だけ・1 件**。
   - 定時: 銘柄ごとの `catch` が最終である。そこで 1 回だけ報告する。
   - 価格変動: `catch (Exception) when (!本判断のキャンセル && Envelope.Attempts >= WolverineExtensions.MaxDeliveryAttempts)` で報告し、**投げ直す**（再試行と退避の挙動は変えない）。
     判定は IADR-0395 と同じ形（`>=` は退避先から配送回数を引き継いで戻された場合も、規則の枠の外で再び退避先へ送られるため）。最大配送回数は再試行間隔の配列から導出される（数字を 2 箇所に持たない）。
   - 本判断のキャンセル（停止）は失敗として出さない。判断が成功した配送は出さない。
3. **ポートと発行**: `ITradeDecisionFailureReporter.ReportFinalFailureAsync(cycleTrigger, symbol, market, exception)`。本番は `PublishingTradeDecisionFailureReporter`（singleton）。
   - 🔴 **発行はランタイムの MessageBus**（Program.cs が `new MessageBus(IWolverineRuntime)` の発行を委譲で渡す。`PositionQueryHealthReporter` と同じ形）。価格変動のハンドラは報告の直後に投げ直して失敗で終わるため、scoped の `IMessageBus` で出すと捨てられる。
   - 例外から取るのは型名だけ。報告口のログにも型名しか載せない（発行の失敗のログも、発行の失敗の型名だけ）。
   - 🔴 **例外を投げない**（呼び出し元の挙動を変えない）。発行に失敗したら警告のログを出し、出し直さない（再送の状態を持たない）。
   - 報告口は両ハンドラの**必須依存**（省略可能にすると Program.cs から配線が消えても試験が緑のまま記録だけが止まる。IADR-0163 決定2）。
4. **監査**: `AuditEntryFactory.From(TradeDecisionFailed)`・`TradeDecisionFailedAuditHandler`。相関は `EventId`（発注チェーンを持たない。`TradeDecisionForgoneBeforeLlm` と同じ）。
   要約は「{銘柄} 取引判断の最中の例外（最終の失敗）: {型名}・{起点}」。通知はしない（台帳だけ。価格変動の失敗は退避先にも残る）。
5. **夜間の要約** §14: `TradeDecisionFailed` を起点 × 型名で数え、銘柄を並べる（窓は他の節と同じ半開区間）。§13 だけ別の DB を読むので、§14 は audit_svc の照会の末尾に置き、出力は 12 → 14 → 13 の順になる（節の番号は振り直さない。既存の試験と手順書が番号で引く）。

## 採らなかった案

| 案 | 採らない理由 |
| --- | --- |
| メッセージも載せる（伏せてから） | 裁定（最小限）。伏せ方は既知の秘密の形にしか効かず、取りこぼすと 7 年残る |
| 再試行ごとに出す | 1 件の失敗が再試行の回数だけ数えられる（裁定） |
| Wolverine の失敗規則（`Then.CustomAction` 等）で退避の時点に出す | 規則は全ハンドラ共通の配線（TestSupport の共通ヘルパ）にあり、取引判断のためだけに分けると規則の一致の試験（T-10-784）と配線の単一情報源を崩す。配送回数での判定は IADR-0395 で実績がある |
| ハンドラの `IMessageBus` で出す | ハンドラが例外で終わると捨てられる（IADR-0129） |
| 退避先（`<queue>_error`）の件数を数える | 定時の経路は退避先へ行かない。要約は台帳だけを読む（IADR-0254 決定3） |
| 内側の例外の型も載せる | 裁定（型名だけ）。外側の型で十分に分類でき、欄を増やすと秘密の経路を作りやすい |

## 結果

- 翌朝、台帳だけで「判断の最中の例外が、どの起点で・どの型で・どの銘柄で・何件、最終の失敗になったか」を数えられる（Pod の再起動の後も）。
- 定時の巡回の継続、価格変動の再試行と退避、判断・見送りの計上は変えない。
- **残余**:
  - 定時の購読のメッセージそのものが（銘柄ごとの捕捉の外で）失敗して再試行されると、銘柄ごとの失敗は再試行のたびに報告される
    （捕捉の外で投げ得るのは、巡回の前の監視銘柄の照会・ニュースの状態の記録と、巡回の中の市場カレンダーの判定・停止である。巡回の前の失敗は
    判断の前なので重ならず、停止は報告しない。巡回の途中でカレンダーが投げたときだけ、それまでの銘柄の失敗が再試行で重ねて出る）。
  - 退避先から手で戻したメッセージが再び最後の配送で失敗すると、もう 1 件出る（新しい最終の失敗として数える）。
  - 発行が失敗した最終の失敗は台帳に無い（警告のログだけ。価格変動は退避先には残る）。
  - 例外の型名は外側だけ。`AggregateException` 等で包まれると中身の型は分からない（本文はログを見る）。
  - 取引判断を複数台で動かしても重複はしない（最終の失敗は配送ごと・銘柄の判断ごとに 1 回）。
