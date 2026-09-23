---
title: 市場カレンダーを米国市場の通常取引時間＋休場日へ差し替え、閉場中は市場単位で巡回を止め、保護の空白を声に出す
type: spec
status: accepted
related_ids: [FR-03, FR-10, FR-01, UC-02, ADR-0040, ADR-0003, ADR-0023, IADR-0344, IADR-0365, IADR-0023, IADR-0245, IADR-0260, IADR-0380]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-03 市場監視 / FR-10 リスク統制 / FR-01 費用統制)
  - planning:projects/ai-stock-trading/04_workflows/01_daily-trading-cycle.md (§市場の時刻構造への対応)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (§2 市場閉場中は監視停止 / §損切りの実行機構)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S1)
---

# 仕様書: 市場監視の開場判定を米国市場の通常取引時間＋休場日にする（#909）

## 起点

- [#909](https://github.com/endazon/ai-stock-trading/issues/909)。稼働中の PoC（2026-09-22/23・SIMULATE・`stopLossMethod=1`）の運用観測。
- `WeekdayMarketSchedule.IsOpen` は **UTC の曜日が土日でないか**しか見ておらず、米東 16:00 の引けから 5 時間後
  （JST 10:22）でも Finnhub の `/quote` 照会が 60 秒ごとに続いていた（issue 本文のログ）。
- 関連: [#21](https://github.com/endazon/ai-stock-trading/issues/21)（正確な市場カレンダー・本 PR で前倒し）/
  [#902](https://github.com/endazon/ai-stock-trading/issues/902)・[PR #904](https://github.com/endazon/ai-stock-trading/pull/904)（S1 評価の生存ログ）/
  [#857](https://github.com/endazon/ai-stock-trading/issues/857)（決済が拒否されたときの扱い）/ IADR-0344（S1）。

## 🔴 実測（コードで確認・`origin/develop` = `232abc65`）

| 事実 | 出典 |
| --- | --- |
| 市場監視の開場判定は UTC 曜日のみ | `backend/Services/MarketMonitorService/Infrastructure/ExternalServices/WeekdayMarketSchedule.cs:9` |
| 閉場中は巡回そのものを早期 return している（ポートは `IsOpen(DateTimeOffset)` の 1 引数） | `Hosted/MonitorPollingService.cs:50` / `Features/MarketMonitor/IMarketSchedule.cs:7` |
| **取引判断サービスには既に市場ローカル時刻のカレンダーがある**（JST / US Eastern・半日取引日つき） | `TradeDecisionService/Infrastructure/ExternalServices/MarketCalendar.cs` / `Domain/MarketSessions.cs` |
| その休場日・半日取引日は**構成注入で、既定は空**。`deploy/` に設定は 1 件も無い（`grep -rn "Holidays\|HalfDays" deploy/` ＝ 0 件） | `TradeDecisionService/Program.cs:260` |
| 監視対象・取引対象は実配備では米国株だけ（`Monitor__SeedSymbols__0__Market` / `TradeCycle__Watchlist__0__Market` ＝ `UnitedStates`） | `deploy/helm/ai-stock-trading/values-local.yaml:129,374` |
| 共有カーネルは「サービスを跨いで共有する Domain 型」の置き場で、`Contracts` だけを参照してよい葉である | `backend/Shared/AiStockTrading.Shared.Kernel/*.csproj` / `SharedKernelIsLeafTests` |

→ **「市場の時刻構造」を写した純関数はリポジトリに既にある**（`MarketSessions`。米国 9:30–16:00 ET・半日 13:00 ET、
東証 9:00–11:30 / 12:30–15:30 JST）。**市場監視だけがそれを使っていない。** よって本作業の中心は
「新しい時刻表を発明すること」ではなく、**既存の単一情報源を共有カーネルへ上げ、両サービスから同じものを引くこと**である。

## 射程

1. **`MarketSessions` を共有カーネルへ移設**し、`MarketHours`（新設・純関数）と `MarketHolidays`（新設・規則計算）を同居させる。
   共有カーネルは `Contracts` しか参照しないため、葉の制約は保たれる。
2. **`IMarketSchedule` に市場を渡す**（`IsOpen(Market, DateTimeOffset)` / `NextOpen(Market, DateTimeOffset)`）。
   ポートの継ぎ目（サービス自己所有）は維持する。実装は `MarketHoursSchedule`（`WeekdayMarketSchedule` を置き換え）。
3. **閉場中の市場は巡回の中で丸ごと飛ばす**（価格照会もしない・評価記録も作らない・到達も出さない）。
   すべての市場が閉場している巡回は従来どおり早期 return する。
4. **保護の空白を声に出す**。閉場へ移った最初の巡回で、市場ごとに 1 回だけ、保有ごとの
   「ライン・最終観測値・次の開場時刻」を出す。**最終観測値がラインを越えていた保有は Critical**、
   そうでなければ Warning。開場して最初に評価できた巡回で Information を 1 回出す（保護の再開）。
5. **取引判断サービスの `MarketCalendar` も同じ `MarketHours` へ寄せる**（構成の休場日は「足す」側として残す）。
   2 つのサービスが「今は開場か」で食い違う状態を作らない。
6. **S1 が通常取引時間しか保護しないことを残余リスクとして開示する**: IADR-0344 の追記（13）、
   `docs/functional/FR-10_risk-controls.md`、**日報 §3（ポジション一覧）の注記**、
   **S1 配置通知（`SoftwareStopArmed`）の文面**。

### 射程外

- **半日取引日・休場日の「構成での撤回」**（規則計算を構成で**外す**経路）。足す側（臨時休場）だけを持つ。
- 東証の休場日表（規則計算は米国のみ。日本は週末＋構成注入のまま。`MarketHours` の継ぎ目で後から足せる）。
- 時間外（プレ / アフター）の気配に基づく保護（#21 でも扱わない。ADR が要る）。
- 閉場中に到達したかどうかの**検知**（閉場中は照会しない。到達は次の開場の最初の巡回で判定される）。
- メトリクス（Prometheus）化・アラートルール（#891 の射程）。
- `deploy/` の値（休場日は規則計算がコードに入るため、配備で設定しなくても効く）。

## 🔴 母集合（走査したファイルと除外理由）

`grep -rn "IMarketSchedule\|IsOpen(\|閉場\|開場" --include=*.cs --include=*.md --include=*.yaml .`
（`obj/` `bin/` `CHANGELOG.md` `.ai-context/`（凍結記録）を除く）で 30 ファイル。ここから取捨した。

- **採る（実装）**: `MarketMonitorService` の `IMarketSchedule.cs` / `WeekdayMarketSchedule.cs`（置換）/
  `MonitorPollingService.cs` / `MarketMonitorAppService.cs` / `MonitorRoundResult.cs` / `StopLossLivenessReporter.cs` /
  `MonitorOptions.cs` / `Program.cs` / `.csproj`（共有カーネル参照）。
  `TradeDecisionService` の `MarketCalendar.cs` / `Domain/MarketSessions.cs`（移設）/ `Program.cs`。
  `Shared.Kernel/Trading/`（`MarketSessions` / `MarketHours` / `MarketHolidays`）。
- **採る（開示）**: `NotificationService/Features/Notifications/NotificationFormatter.cs`（S1 配置の文面）/
  `ReportService/Domain/ReportRenderer.cs`（日報 §3 の注記）/ `docs/functional/FR-10_risk-controls.md` /
  `docs/tests/FR-10_risk-controls-tests.md` / `.ai-context/adr/IADR-0344...md`（追記 13）/ `.ai-context/adr/README.md`（索引）。
- **除外**: `ReportService/Domain/ReportSchedule.cs` / `ReportNoResponsePolicy.cs`（報告書の締め時刻であって市場の開場ではない。
  `grep` の「開場」に当たらず「営業日」を扱う別概念）/ `TradeDecisionService/Tests/FxCalendarIndependenceTests.cs`
  （為替カレンダーの独立性。市場カレンダーへ寄せてはならないことを固定する否定形テストであり、**触ると意味が壊れる**）/
  `docs/migration/20260903_cutover-and-retention.md` `docs/infra/infra.md` `docs/observability/observability.md`
  （閉場を「巡回が止まる時間帯」として述べる運用記述。時刻表の値を持たないため追随不要）/
  `deploy/helm/.../values.yaml` `templates/deployment.yaml`（`PollIntervalSeconds` の描画のみ）。
- **是正で新たに誤りになる自分の記述（規則 10 の引き直し）**: 「閉場＝土日」と読める記述を
  `IMarketSchedule.cs` / `MonitorPollingService.cs` / `StopLossLivenessReporter.cs`（`OnMarketClosed` の XML doc の
  「週末をまたいだ」）/ `docs/tests/FR-10_risk-controls-tests.md`（T-10-628 の行）で引き直し、すべて書き換える。

## 決定（IADR-0380 に記録する）

| # | 問い | 決定 |
| --- | --- | --- |
| a | 閉場中は**評価ごと止める**のか**到達だけ止める**のか | **評価ごと止める**（照会もしない）。閉場中の価格は終値で凍り新しい情報が無く、成行は翌寄りまで約定せず、照会は FR-01 の費用になる |
| b | 閉場中にラインを越えていた保護をどう扱うか | **黙って忘れない。** 閉場へ移った最初の巡回で市場ごとに 1 回、保有ごとのライン・最終観測値・次の開場時刻を出す。最終観測値がラインを越えていたら **Critical**、そうでなければ Warning。次の開場の最初の巡回で従来どおり判定する（そこで到達すれば成行が出る＝寄りで約定し得る） |
| c | 休場日はハードコード表か構成か | **規則計算（コード）＋構成の追加分。** NYSE / Nasdaq が休場とする 10 日（連邦休日 9 ＋ Good Friday。コロンブスデーと復員軍人の日は開く）＋ 半日 3 規則を計算する（**期限切れ表を持たない＝refresh 計画が要らない**）。構成（`Monitor:Holidays:<Market>` / `TradeCycle:Holidays:<Market>`）は臨時休場（服喪等）を**足す**ためだけに使う |

## 受け入れ基準

1. 米東 16:00 ちょうど以降・9:30 未満は `IsOpen(UnitedStates, …)` が false。9:30 ちょうどは true（開始は包含・終了は排他）。
2. 夏時間の両端（3 月の開始日・11 月の終了日）で、同じ現地時刻が同じ判定になる（固定オフセット換算をしない）。
3. 独立記念日（曜日による振替を含む）・感謝祭・Good Friday が休場、感謝祭翌日とクリスマスイブの 13:00 以降が閉場。
4. 閉場中の市場の銘柄は **1 回も価格照会されない**（`FakeMarketDataSource` の呼び出し記録で固定）。
5. 閉場へ移った最初の巡回で、保有ごとの行が 1 回だけ出る。到達済みは Critical・未到達は Warning。次の巡回では出ない。
6. 開場して最初に評価できた巡回で、保護の再開が Information で 1 回出る。
7. 日報 §3 の注記と S1 配置通知の文面に「通常取引時間だけ保護する」が出る。
8. `dotnet build` / `dotnet test`（MarketMonitorService・TradeDecisionService・Shared.Kernel.Tests・NotificationService・
   ReportService・Architecture.Tests）と `dotnet format --verify-no-changes`、node の文書検査が緑。

## テスト ID

- **FR-10 の予約枠 T-10-690〜T-10-699 を使う。**
- 🔴 **FR-03 の採番は存在しない**（`grep -rhoE "T-[0-9]{2}-[0-9]+" docs/tests/` の名前空間は `T-10` / `T-12` / `T-15` /
  `T-17` / `T-19` / `T-20` の 6 つだけで、**`T-03-*` はリポジトリ全体で 0 件**）。よって「FR-03 の現在の最大値」は
  **無い（採番自体が未開始）**。本 PR で新設しない —— テスト仕様書は FR-10 側にあり、本作業の表明はすべて
  S1（FR-10）の保護に帰着するためである。

| ID | 対象 |
| --- | --- |
| T-10-690 | 米国の通常取引時間の境界（9:30 包含 / 16:00 排他 / 週末） |
| T-10-691 | 夏時間の両端（EDT 開始日・EST 復帰日）で同じ現地時刻が同じ判定になる |
| T-10-692 | 休場日（独立記念日の振替・感謝祭・Good Friday）と半日取引日（感謝祭翌日 13:00） |
| T-10-693 | `NextOpen` が週末・休場日を跨いで次の寄り付きを返す |
| T-10-694 | 構成の臨時休場日が**足される**（規則計算は外せない） |
| T-10-695 | 閉場中の市場の銘柄は照会も評価も到達もされない（開場中の市場はされる） |
| T-10-696 | 全市場が閉場の巡回は従来どおり何もしない |
| T-10-697 | 閉場へ移った最初の巡回だけ保護の空白を出す（到達済み＝Critical・未到達＝Warning・次の開場時刻つき） |
| T-10-698 | 開場して最初に評価できた巡回で保護の再開を 1 回出す |
| T-10-699 | 日報 §3 の注記・S1 配置通知の文面に「通常取引時間だけ保護する」が出る |

## 残余リスク（IADR-0344 追記 13 と IADR-0380 へ書く）

- 🔴 **S1 は通常取引時間しか保護しない。** 夜間・寄り前の急落からは守られない。閉場中に気配がラインを割っても
  決済は翌寄りまで起きない（成行がその場で約定しないため、**発動させないことが正しい**）。
- 規則計算の誤り（臨時休場・規則改定）は「開場と読んで終値で回す」側ではなく「閉場と読んで保護が止まる」側へも倒れ得る。
  **閉場と読んだ最初の巡回は必ずログに出る**ため、誤りは無音にならない。
- 再起動が閉場中に起きると、その閉場期間の保護の空白は出せない（保有を照会していないため何も知らない）。
- 未知の市場（`Market` の将来の値）は `MarketSessions` の既定どおり閉場と読む（現在の enum は 2 値で到達不能）。
