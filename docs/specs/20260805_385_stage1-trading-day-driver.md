---
title: 作業仕様書 — Stage 1 の稼働営業日を稼働監視の観測から集計する（期間カウントの供給元）
type: work
status: done
related_ids: [FR-20, FR-12, FR-05, UC-06, SC-03, ADR-0008, ADR-0022, IADR-0137, IADR-0140, IADR-0142, IADR-0148, IADR-0149, IADR-0150]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - ../../planning/projects/ai-stock-trading/INDEX.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
related_specs:
  - ../adr/IADR-0150_stage1-uptime-observation-and-session-hypotheses.md
  - ../adr/IADR-0137_stage1-trading-day-counting.md
  - ../adr/IADR-0142_stage1-simulate-only-aggregation.md
  - ../adr/IADR-0149_stage1-trade-count-supply.md
  - ../functional/FR-20_staged-gates.md
  - ../tests/FR-20_staged-gates-tests.md
  - 20260804_333_stage-gate.md
  - 20260805_386_stage1-trade-count.md
---

# 作業仕様書: Stage 1 の稼働営業日の供給（[#385](https://github.com/endazon/ai-stock-trading/issues/385)）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-20**（段階ゲート）／ **FR-12**（内蔵 `paper` はデバッグ用であり Stage 1 の実績に算入しない）／
  **FR-05**（発注執行＝ブローカ接続を持つ唯一のサービス）
- ユースケース（UC）: **UC-06**（段階の参照・承認）
- 画面（SC）: **SC-03**（Stage 1 進捗の表示）
- 関連 ADR: **ADR-0008**（段階ゲート）／ **ADR-0022 決定3**（**営業日カレンダーを保持しない**という別件の裁定）
- 実装 ADR: [IADR-0150](../adr/IADR-0150_stage1-uptime-observation-and-session-hypotheses.md)（本作業の決定）／
  [IADR-0137](../adr/IADR-0137_stage1-trading-day-counting.md) 決定1（**カレンダーを実装が持たない**・観測入力で受ける）／
  [IADR-0142](../adr/IADR-0142_stage1-simulate-only-aggregation.md)（観測は発注先を必須で伴う・算入は許可制）／
  [IADR-0149](../adr/IADR-0149_stage1-trade-count-supply.md)（観測ログによる供給・窓の区切り）
- 計画書リンク:
  [06_daytrading-review §4.2](../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md)（期間カウント規則）／
  [INDEX 決定 34](../../planning/projects/ai-stock-trading/INDEX.md)

## 目的・背景

計画 §4.2 は Stage 1 の「3 か月」を**実際に取引できた日数**（目標 60 営業日）で数えると定め、
1 日として数える条件を「**その日の実際の通常取引時間の 50% 以上が稼働**」、分母を「その日の実際の通常取引時間
（通常日 390 分／半日取引日 210 分）」、判定の基準時刻を**米国東部時間**、除外を
「**OpenD の停止・ブローカー側の障害・市場休場**」と確定している。

[#333](https://github.com/endazon/ai-stock-trading/issues/333)（PR #384・IADR-0137）は判定側
（`Stage1TradingDayObservation` / `Stage1DayQualification`）を作ったが、**観測を生む仕組みが無い**。
`Stage1QualifiedTradingDays` は常に 0 であり、Stage 1 → 2 の昇格は起こらない（fail-safe だがゲートが実効化していない）。

### #386 との違い — 本件は「観測を生む仕組みそのもの」を作る

[#386](https://github.com/endazon/ai-stock-trading/issues/386)（IADR-0149）は `OrderExecuted` という**既存のイベント**へ
発注先を足すだけで供給が作れた。本件は違う。

- **稼働分数**: heartbeat / uptime を表すイベントも probe も**存在しない**
  （`backend/Shared/AiStockTrading.Shared.Contracts/Events/` を全走査して確認した）。
- **その日の通常取引時間（分母）**: 半日取引日カレンダーの判定源は**計画側の裁定待ち**
  （[docs/blocked-tasks.md](../blocked-tasks.md) B-4・環流記録 `feedback/20260804_fr20-stage1-session-calendar.md`）。
  `MarketCalendar`（TradeDecisionService）は**週末＋構成注入の休場日のみ**（既定は空）で、半日取引日を知らない。

### 供給元の実地確認（issue の指示「使えるかは実装時に確認し、無ければ記録して先送りする」）

本リポジトリの moomoo 取引ポート `IMoomooTradeClient` は
`PlaceOrder` / `QueryOrder` / `CancelOrder` / `FindOrderByClientId` / `GetPositions` の 5 つだけを持ち、
**取引時間・市場状態を照会する口は無い**（`MMApiMoomooTradeClient` にも無い）。
PoC（[IADR-0144](../adr/IADR-0144_moomoo-short-selling-poc-outcomes.md)・#342）でも取引時間の照会は確認項目に入っていない。

したがって「**その日の実際の通常取引時間**」を外部から取得する手段は現時点で存在しない。
値を発明しないという IADR-0137 決定1 の方針を維持したまま、**発明せずに安全側へ倒す**設計を採る（後述）。

## 対象範囲

- 対象:
  - **稼働の観測を生む**: 発注執行サービスの定期 probe（常駐）→ `BrokerAvailabilityObserved` の発行
  - **稼働分数の記録**: リスク管理サービスが観測を購読し、**米国東部時間の取引日**ごとに稼働分数を積む（永続）
  - **算入の判定**: カレンダーを持たないまま §4.2 の 50% 規則を適用する（**取り得る通常取引時間の仮説すべてで満たす**）
  - `StagePerformance.Stage1QualifiedTradingDays` と `Stage1ExcludedInternalPaperDays` への供給（判定直前の重ね）
  - 観測窓を受理された段階遷移で区切る（起算点＝Stage 1 遷移日・§4.2。#386 / #387 と同条件）
  - 永続化（新テーブル）と、供給元が移って死んだ 2 列の削除
- 対象外:
  - **半日取引日・市場休場日のカレンダー**（計画側の裁定待ち。B-4）。本 PR でも**発明しない**
  - 停止理由（OpenD 停止／ブローカー障害／構成）の分類・記録。§4.2 は日報・月報への記載を求めるが、
    分類の定義が計画に無い。**稼働分数の減少としてのみ表す**（IADR-0137 決定1 の踏襲）
  - 日報・月報への稼働率の記載（04_report-templates。ReportService の担当・別 issue）
  - 段階の自動昇格（設計上すべて利用者承認を要する）
  - `LiveTradingGate.LiveTradingReleased` の閂（触れない）

## 設計

### 1. 稼働の観測をどう作るか — **定期 probe**（[IADR-0150](../adr/IADR-0150_stage1-uptime-observation-and-session-hypotheses.md) 決定1）

候補は (a) 定期 probe で接続状態を刻む／(b) 接続・切断イベントで区間を積む／(c) その他。**(a) を採る。**

- (b) は「切断イベント」が届く保証が無い。OpenD の異常終了・プロセス断・ネットワーク断では
  **切断イベントこそが最初に失われる**。開始イベントだけが残ると区間が閉じず、
  **稼働時間が無限に伸びる**（＝日数が水増しされる）。issue の否定形基準に真っ向から反する。
- (a) は**沈黙が「稼働していない」を意味する**。供給が途絶えれば分数が積まれず、日数は増えない（fail-safe）。

```
[OrderExecutionService]  BrokerAvailabilityProbeService（常駐・既定 5 分間隔）
      └ IBrokerAvailabilityProbe.IsOperationalAsync()
           ├ true  → BrokerAvailabilityObserved(Provider, ObservedAt, CoveredInterval) を発行
           └ false → **何も発行しない**（警告ログのみ。BrokerPositionsObserved と同じ作法）
[RiskManagementService]  BrokerAvailabilityObservedHandler
      └ 観測時刻を米国東部時間へ写し（取引日 + 当日 0 時からの分）ストアへ credit
```

- 「稼働」の定義は §4.2 の「**OpenD が接続され発注可能であった時間**」である。実装が観測できるのは
  「**照会を投げて応答が返ったこと**」までであり、**実際に発注してみることはしない**（試し発注は統制違反である）。
  この代理の限界は IADR-0150 に明記する。
- probe は**発注先を必ず伴う**（IADR-0142 決定1）。`IBrokerAdapter.Provider`（#386 で追加した実発注先の自己申告）を載せる。
  内蔵 `paper` の稼働日も観測として記録され、**算入されない除外日**として SC-03 に別掲される
  （`Stage1ExcludedInternalPaperDays`。IADR-0142 決定3 が用意した口を初めて満たす）。

### 2. 稼働分数の積み方 — **前回成功からの経過だけを credit する**（IADR-0150 決定2）

観測 1 件は「その時刻に稼働していた」ことしか語らない。区間へ広げる規則を次のとおり定める。

```
credit(観測分 m, 保証分 c):
    直前の成功観測 p が無い            → 0 分（初回は遡らない）
    m - p > c（＝ probe を 1 回でも落とした） → 0 分（落ちた区間は稼働と見なさない）
    それ以外                          → 区間 (p, m] を通常取引時間の窓と交差させた分を加算
```

- **落とした区間を credit しない**のが要点である。「前回成功から今回成功までを一律に埋める」と、
  その間に起きた停止が稼働として計上される（＝水増し）。
- `c` は観測が運ぶ巡回間隔（`CoveredInterval`）だが、**受け手が上限 30 分でクランプする**。
  供給側の設定ミス・改変で 1 件の観測が 1 日を埋めることを構造的に防ぐ。
- 逆行（再送・順序前後）は加算しない（`m <= p` は 0 分）。**冪等**であり、同じ観測を二重に credit しない。

### 3. 分母をどうするか — **カレンダーを発明せず、取り得る仮説すべてで満たすことを要求する**（IADR-0150 決定3）

計画 §4.2 は通常取引時間を **9:30〜16:00 ET（390 分）**、半日取引日を **9:30〜13:00 ET（210 分）**と
明記している。**この 2 つは計画の転記であってカレンダーではない**（どの日がどちらかを実装は知らない）。

そこで、ある平日について**両方の仮説を作り、両方で 50% 以上稼働していたときにだけ算入する**。

| 仮説 | 分母 | 稼働分数 | 算入に要する稼働 |
| --- | --- | --- | --- |
| 半日取引日 | 210 分 | 9:30〜13:00 ET のうち稼働した分数 | **105 分以上** |
| 通常日 | 390 分 | 9:30〜16:00 ET のうち稼働した分数 | **195 分以上** |

- **これは真の規則より必ず厳しい（＝算入される日が真に少ない）。** 実際の通常取引時間が 210 分なら
  真の条件はちょうど 1 行目であり、390 分なら 2 行目である。両方を課す以上、真の条件を満たさない日が
  算入されることは**原理的に起こらない**（休場日を除く。後述）。
- 判定そのものは既存の純関数 `Stage1DayQualification.Qualifies`（#333）を**仮説ごとに呼ぶだけ**である。
  50% の閾値も 390/210 の定数も**再実装しない**。
- **週末は分母 0 の仮説 1 つ**（`DayOfWeek` の算術であり、カレンダーではない）。既存の
  「市場休場日（分母 0）は算入しない」経路にそのまま乗る。

> **監査の見立て「常に 390 分と仮定すれば半日は算入されないから fail-safe」は算数が誤っている。**
> 半日（210 分）に満稼働した日の稼働率は 210 ÷ 390 ＝ **53.8%** であり、**閾値 50% を割らない**（＝算入される）。
> 「必ず除外される」という前提は成り立たない。方向（過少計上＝安全側）の見立ては正しいが、
> 390 分固定は**朝方の停止で誤って算入する穴**を残す——例えば 12:00〜16:00 だけ稼働した日は
> 240 ÷ 390 ＝ 61.5% で算入されるが、その日が半日取引日なら実際の稼働は 13:00 までの 60 分（28.6%）でしかない。
> 上表の「両仮説」方式はこの穴を塞ぐ。

### 4. 塞げない穴 — **市場休場日**（正直な記録）

**祝日（市場休場）を判別する手段が無い。** OpenD は市場が閉じていても接続を保つため、probe は成功し続ける。
週末は曜日の算術で外れるが、**米国市場の祝日（年 9 日前後）は外れない**。

- 帰結: 祝日に OpenD が稼働していると、その日が営業日 1 日として**算入され得る**（60 営業日あたり 2〜3 日の過大計上）。
- **これは発明で埋めない。** 埋めるには出典のない祝日表を実装が抱えることになり、
  IADR-0137 決定1・ADR-0022 決定3 の裁定に反する。
- 対応: [docs/blocked-tasks.md](../blocked-tasks.md) の B-4 と「実装済みだが実際には発動しない機能」の表を更新し、
  環流記録 `feedback/20260805_fr20-stage1-market-holiday-exclusion.md` を作成して計画へ判定源を求める。

### 5. 供給（結線）と永続化

```
BrokerAvailabilityObserved → BrokerAvailabilityObservedHandler
                               └ IStage1TradingDayObservationStore.CreditUptime（(取引日, 発注先) で 1 行）
StageGateService            → GetQualifiedTradingDayCount() / GetExcludedInternalPaperDayCount() を
                              判定直前に StagePerformance へ重ねる
                            → 受理された段階遷移で ResetWindow()（起算点＝Stage 1 遷移日）
```

- 新テーブル `stage1_session_uptime`。主キーは **(SessionDateEasternTime, Provider)** ＝「1 取引日 1 発注先 1 行」。
  列は `LastObservedMinuteOfDayEasternTime` / `OperationalMinutesBeforeEarlyClose` /
  `OperationalMinutesBeforeRegularClose` / `MeetsUptimeThreshold` / `QualifiesTowardStage1` / `UpdatedAtUtc`。
  末尾 2 列は**記録時に純関数が決めた結果**であり、算入規則を SQL 側へ写さない（IADR-0148 / IADR-0149 と同じ規律）。
- `stage_performance.Stage1QualifiedTradingDays` と `Stage1ExcludedInternalPaperDays` の 2 列は**削除**する。
  供給元が観測ログへ移った以上この列は死ぬ。死んだ列を残すと「まだ使う値」に見え、次の実装者が判定へ
  結線し直す余地が残る（IADR-0137 決定2 / IADR-0148 決定2 / IADR-0149 決定3 と同じ規律）。
- マイグレーション `AddStage1SessionUptime`。`Up` / `Down` を実 PostgreSQL・既存データで確認する。

## 受け入れ基準

計画 §4.2 および #385 より。

- [x] 日次の稼働分数が記録され、`Stage1QualifiedTradingDays` が**実測から**更新される
- [x] ET 基準で判定される（**DST 切替をまたいでも同じ現地時刻が同じ分に写る**）
- [x] 半日取引日で誤って算入されない（**両仮説**方式。朝方停止 + 午後稼働で算入されないこと）
- [x] OpenD 停止・ブローカー障害が除外される（probe が失敗した区間は credit されない）
- [ ] **未達（意図的に残す）**: issue #385 の「市場休場が除外される」は**週末のみ**満たし、**祝日は満たさない**。
      OpenD は市場が閉じていても接続を保つため probe が成功し続け、祝日が営業日として算入され得る
      （60 営業日あたり 2〜3 日の過大計上＝**本 PR で唯一の fail-open**）。塞ぐには出典の無い祝日表を
      実装が抱えることになり IADR-0137 決定1・ADR-0022 決定3・issue の「発明しない」に反するため埋めない。
      追跡: [#407](https://github.com/endazon/ai-stock-trading/issues/407)・
      環流記録 [20260805_fr20-stage1-market-holiday-exclusion](../../feedback/20260805_fr20-stage1-market-holiday-exclusion.md)・
      [docs/blocked-tasks.md](../blocked-tasks.md) B-4
- [x] **否定形**: 供給が途絶えたときに日数が水増しされないこと（観測が無い＝0 分＝算入されない）
- [x] **否定形**: probe を 1 回落とした区間が稼働として計上されないこと
- [x] **否定形**: 同じ観測を二重に credit しないこと（再送・逆行）
- [x] **否定形**: 内蔵 `paper` で稼働した日は算入されないこと（**除外日として別掲**されること）
- [x] **否定形**: 1 件の観測が上限（30 分）を超えて credit されないこと
- [x] **カレンダーを内蔵していないことを構造で確認できる**（下記テスト方針の「構造テスト」）
- [x] 受理された段階遷移で観測窓が区切られ、直後は 0 に戻る

## テスト方針

| 観点 | 層 | 種別 |
| --- | --- | --- |
| **構造テスト**: 3 年ぶんの全日付で同じ稼働を与えると、結果が**曜日だけ**で決まる | Domain | 構造（カレンダー不在の証明） |
| 満稼働の平日が 1 日として算入される | Domain | 正 |
| 半日相当（9:30〜13:00 のみ満稼働）は算入されない（通常日仮説を満たさない） | Domain | 否定形 |
| 午後だけ稼働（12:00〜16:00）は算入されない（半日仮説を満たさない＝監査の穴を塞ぐ） | Domain | 否定形 |
| 境界値: 両仮説をちょうど満たす（105 分 / 195 分）と算入される | Domain | 正（境界） |
| 週末は満稼働でも算入されない | Domain | 否定形 |
| probe を 1 回落とした区間は credit されない | Domain | 否定形 |
| 逆行・再送は credit されない（冪等） | Domain | 否定形 |
| 1 件の観測は 30 分を超えて credit されない | Domain | 否定形 |
| 時間外（9:30 前・16:00 後）の稼働は credit されない | Domain | 否定形 |
| DST 切替日をまたぐ観測が正しい ET 分へ写る（2026-03-09 EDT / 2026-11-02 EST の実 tz データ） | Infrastructure | 正（境界） |
| 観測が (取引日, 発注先) 1 行へ集約され、再起動後も積み上がる | Infrastructure（EF） | 正 |
| 内蔵 `paper` の稼働日が算入されず、除外日として数えられる | Infrastructure・Domain | 否定形 |
| 段階ゲートへ供給され、60 営業日で期間条件が満たされる | Application | 正 |
| 供給が無ければ 0 日（昇格しない） | Application | 否定形 |
| 段階遷移で窓が区切られる／受理されなければ区切られない | Application | 正・否定形 |
| probe 失敗時に観測を発行しない | Infrastructure（OrderExecution） | 否定形 |

## 計画書との差異

- 差異: **「その日の実際の通常取引時間」の判定源が計画に無い**（既知・B-4）。
  実装は分母を発明せず、**取り得る仮説すべてで 50% を満たすこと**を要求する（真の規則より厳しい側）。
- 差異: **§4.2 の除外「市場休場」を完全には満たせない**（週末のみ。祝日は判別手段が無い）。
  環流記録 `feedback/20260805_fr20-stage1-market-holiday-exclusion.md` で計画へ判定源を求める。
- 差異: §4.2 は「日次の稼働分数・稼働率・**停止理由**と算入されなかった日の一覧を記録する」と定めるが、
  本 PR が記録するのは稼働分数・稼働率（＝算入可否）までである。**停止理由の分類は計画に定義が無い**ため作らない。

## 未決事項

- **市場休場日（祝日）の判定源**（B-4・環流済み）。判明するまで祝日は算入され得る。
- **半日取引日の判定源**（B-4・環流済み）。判明するまで半日は「通常日仮説」を満たす稼働を要求する（過少計上）。
- **probe の巡回間隔と OpenD の照会レート制限**の兼ね合い。既定 5 分は実測ではなく保守的な見積りである。
- **観測ログの保持期間**（retention）。計画に記述が無いため上限を設けない（#386 / #387 と同じ扱い）。
