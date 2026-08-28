---
title: 為替の情報源の「日次の使用記録」を残し、静かな期間の出典を証拠づける
type: spec
status: approved
related_ids: [FR-06, FR-10, FR-11, FR-17, UC-06, ADR-0022, IADR-0096, IADR-0196, IADR-0198, IADR-0199, IADR-0225]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
---

# 仕様書: 為替の出典を「静かな期間」にも証拠づける（#513）

> 本仕様書は実装着手前に作成する。

## 起点

- 起点 issue: [#513](https://github.com/endazon/ai-stock-trading/issues/513)（為替の出典が「静かな期間」に出せない）
- 起点 ID: **FR-06**（報告）・**FR-10**（統制）・**FR-11**（監査）・**FR-17**／**UC-06**
- 計画: ADR-0022（計画リポ） **決定1**（出典の明記）・**決定2**（黙って劣化させない）・**§利用条件**（2026-08-05 利用者裁定）
- 直接の原因決定: [IADR-0196](../adr/IADR-0196_fx-source-visibility.md) 決定1（遷移で発行する）・決定4（使っていない源のクレジットを出さない）／
  [IADR-0198](../adr/IADR-0198_fx-expired-visibility.md) 決定2（暦日の抑止）／
  [IADR-0199](../adr/IADR-0199_fx-status-supply-wiring.md) 決定1（権威源は台帳）・決定5（証拠のある源だけを出す）
- 本 PR の実装ADR: [IADR-0225](../adr/IADR-0225_fx-source-daily-usage-record.md)

## 課題 —— **平常時こそ出典が出ない**

`FxSourceStatusTracker.OnSourceUsed` は**遷移でしかイベントを返さない**（IADR-0196 決定1。銘柄ごと・巡回ごとに
呼ばれるため、呼び出しごとに出すと 1 巡回で N 件飛ぶ）。したがって台帳には次の 4 通りしか残らない。

| 台帳にある記録 | 証明できること |
| --- | --- |
| `FxRateSourceFellBack(SourceName)` | **その源を使った** |
| `FxRateSourcePrimaryRestored(SourceName)` | **第一の源へ戻して使った** |
| `FxRateStale` / `PositionClosedWithStaleFxRate` | レートを使った（**源の名前は分からない**） |
| **記録なし** | 🔴 **何も分からない**（静かに第一の源を使った／為替を一度も使わなかった、の区別が付かない） |

**そして「記録なし」が平常時の姿である。** `HttpFxSourceStatusSource.Credits()` は FellBack / Restored の
2 種からしかクレジットを導けないため、日報の為替欄は平常時に
「出典: **記録からは特定できません**」としか書けない（IADR-0199 決定5 が、証拠の無い出典を書くよりは
正直だと判断して現在そう出している）。

## 本 PR で実施する判断の根拠（「やらない選択」を採らない理由）

issue は「**やらない選択もあり得る**」と明記している。ADR-0022 §利用条件（2026-08-05 利用者裁定）により
**日銀 API 側のクレジット義務は「サービスを公開した場合」の条件付きであり、私的運用では発生しない**ため、
出典が平常時に出ないこと自体は法的な不履行ではない。**それでも本 PR で解く。**

1. **同 裁定は「報告書への出典明記の方針は維持する」と明記している。** 方針として掲げたものが
   **平常時に一度も満たされない**状態は、方針を掲げていないのと区別が付かない。
2. **「特定できません」が毎日出る文面は、慣れると読み飛ばされる。** IADR-0196 / IADR-0199 が
   残余リスクとして 2 度予告した形（「正直ではあるが、慣れると読み飛ばされる」）であり、
   **読み飛ばされる警句は、劣化を知らせる本来の行（要確認・鮮度警告）まで一緒に読み飛ばさせる。**
3. **issue が follow-up として起票された残件であり**（IADR-0199 残余リスク）、
   **公開運用へ移る前に解いておく**という条件が issue 自身に書かれている。
4. **費用が小さい。** 抑止の形は既に `OnStale`（IADR-0198 決定2 の暦日抑止）に前例があり、
   追随箇所も既存イベント追加の前例（`PositionClosedWithStaleFxRate`）と同一である。

## 母集合の引き直し（`.claude/rules/traceability.repo.md` 規則 9・10）

**「イベントを 1 種増やしたときの追随箇所」を記憶で挙げない。** 直近に追加されたイベント
（`PositionClosedWithStaleFxRate`・IADR-0198）と、同じ族の既存イベント
（`FxRateSourcePrimaryRestored`・IADR-0196）**の 2 つの文字列でツリー全体を走査**して母集合を引いた。
軸を 2 本にしたのは規則 5（軸を 1 本で終わらせない）による。

走査コマンド（生の出力に対して判断した。`head` で切らない・整形しない＝規則 7）:

```
grep -rn "FxRateSourcePrimaryRestored" . | grep -v "^\./\.git/"        # 36 行 / 21 ファイル
grep -rln "PositionClosedWithStaleFxRate" . | grep -v "^\./\.git/"     # 22 ファイル
grep -rn "FxRate" docs/                                                # 9 行（いずれも FxRateToBase 等で無関係）
grep -rln "EventTypeDiscovery" --include=*.cs .                        # 6 ファイル（母集合の単一情報源）
grep -rn "UPDATE_EVENT_BASELINE" -l .                                  # 契約基準の再生成手順
```

### 引いた結果 —— 手を入れる箇所

| # | 箇所 | 何を足すか | 強制する機械 |
| ---: | --- | --- | --- |
| 1 | `Shared.Contracts/Events/FxRateSourceUsed.cs` | **新イベントの契約** | —（起点） |
| 2 | `Shared.Contracts.Tests/EventMessageTypeNameTests.cs` | **メッセージ識別子の固定**（`[InlineData]` 1 行） | `識別子固定の対象はイベント型の母集合と完全に一致する` |
| 3 | `Shared.Contracts.Tests/event-schemas.baseline.json` | **契約基準の再生成**（`UPDATE_EVENT_BASELINE=1`） | `全イベントが基準に登録されている_追加は許容するが記録漏れは許容しない` |
| 4 | `AuditService.Application/Services/AuditEntryFactory.cs` | **台帳エントリへの写像** | （5 のハンドラがコンパイルで要求する） |
| 5 | `AuditService.Infrastructure/.../AuditEventHandlers.cs` | **監査ハンドラ**（新設 1 本） | `AuditConsumerCoverageTests`（**全イベント**が母集合） |
| 6 | `AuditService.Application.Tests/AuditEntryFactoryTests.cs` | 写像のテスト | —（前例に倣う） |
| 7 | `AuditService.Infrastructure.Tests/AuditEventConsumersTests.cs` | **ハンドラ実行テスト**（Wolverine ホスト実走） | —（issue が名指し） |
| 8 | `TradeDecisionService.Infrastructure/.../FxSourceStatusTracker.cs` | **(通貨, 源, 暦日) の抑止と発行** | —（起点） |
| 9 | `TradeDecisionService.Infrastructure.Tests/FxSourceStatusTrackerTests.cs` | 抑止・境界・巻き戻しのテスト | —（受け入れ基準） |
| 10 | `ReportService.Domain/FxSourceStatus.cs` | **`Usages` の追加**（`IsClean` には**入れない**） | —（下記「なぜ集計型へ足すか」） |
| 11 | `ReportService.Domain/ReportRenderer.cs` | 出典の 3 分岐・平常時の文言 | —（下記） |
| 12 | `ReportService.Infrastructure/.../HttpFxSourceStatusSource.cs` | **引く種別への追加とクレジットの導出** | —（受け入れ基準の中心） |
| 13 | `ReportService.*.Tests`（3 ファイル） | 上記のテストと既存呼び出しの追随 | コンパイル |
| 14 | `.ai-context/adr/IADR-0225_*.md` ＋ `.ai-context/adr/README.md` | 実装ADR と索引 | `check-adr-index-sync.js` |

### 引いた結果 —— **手を入れない箇所と、その理由**（規則 6: 除外の理由を残す）

| 除外した箇所 | 理由 |
| --- | --- |
| `NotificationService`（`NotificationFormatter` / `NotificationHandlers`） | **通知は「選んだ事象だけ」を出す**（IADR-0198 副次②が監査との母集合の違いを明記）。**平常運転の記録を Discord へ毎日流すのは、警告を埋もれさせるだけである。** `NotificationConsumerCoverageTests` の母集合はアセンブリ内のハンドラであり、**足さなくても赤にならない**（＝規約上も不要） |
| `scripts/check-consumer-endpoint-names.js` / `ConsumerEndpointNameTests` | キュー名は `<ServiceName>.<メッセージ型名>` で**サービス単位**に一意化される（IADR-0129 決定1）。イベントの追加で不変条件は動かない |
| `docs/tests/FR-10_*.md` / `docs/functional/FR-10_*.md` | 走査の結果、**FX の可視化イベントを列挙している docs は 1 つも無い**（`grep -rn "FxRate" docs/` の 9 行はいずれも `FxRateToBase` 等の換算レートの話）。先行 3 PR（IADR-0196 / 0198 / 0199）も docs/ を触っていない。**列挙が無い表へ 1 行だけ足すと、次の追加者が気づかず片肺になる** |
| `IFxSourceStatusNotifier`（ポート） | `ReportSourceUsedAsync` は**既に毎回呼ばれている**。呼び出し側（`FallbackFxRateSource`）も変更不要 |
| `PublishingFxSourceStatusNotifier` | `PublishIfAny` は `object?` を publish するため、判定器が新しい型を返しても**そのまま流れる**。`Rollback` の分岐は**判定器の側**にある |
| `.ai-context/specs/` の既存記録・`IADR-0196` 〜 `IADR-0199` の本文 | **凍結記録**（`traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」）。**旧 ID を付け替えず、後継 IADR-0225 の側から旧決定を引く** |

## 決めること

### 決定A: **(通貨, 源, 暦日) を鍵に、1 日 1 件の「使用記録」を発行する**

`FxRateSourceUsed(Quote, SourceName, Rank, TotalSources, OccurredAt)` を新設し、
`OnSourceUsed` が遷移を返さないときに**当日・当該源で未発行なら 1 件だけ**返す。
**抑止の形は `OnStale`（IADR-0198 決定2）と同じ暦日抑止**であり、暦日は**UTC**（IADR-0096 決定4 からの慣行）。

**鍵に源の名前を含める。** フォールバック中に源が入れ替わっても（rank 2 → rank 3）遷移イベントは出ないため、
源を鍵に含めないと**実際に使った源が台帳に残らない**。

### 決定B: **遷移イベントは、その日の使用記録を兼ねる**

`FxRateSourceFellBack` / `FxRateSourcePrimaryRestored` は **`SourceName` を運ぶ＝使用の証拠そのもの**である。
遷移を発行したときに (源, 暦日) を記録済みとして印を付け、**同じ日に使用記録を重ねて出さない**。
（`OnSourceUsed` の戻り値は従来どおり **1 件または `null`** のままにする。2 件返す形にすると
呼び出し側〔`PublishIfAny` の巻き戻し〕まで作り替えることになる。）

### 決定C: **巻き戻しは「多めに出る」側へ倒す**

発行に失敗したときの `Rollback` は印を消す。**印を消しすぎると当日に使用記録が 1 件余分に出るだけ**だが、
消し損ねると**その日の証拠が永久に欠ける**。**欠測より重複を採る**（IADR-0096 の巻き戻しと同じ向き）。

### 決定D: **集計型（`FxSourceStatus`）へ `Usages` を足す。ただし `IsClean` には入れない**

**使用記録は劣化ではない。** `IsClean` に入れると**平常運転の日が「劣化あり」と読める**日報になる。

### 決定E: **出典は 3 分岐にする**（新たに誤りになる自分の記述を引き直す＝規則 10）

現在の文面「出典: **記録からは特定できません**（情報源の記録は**切替時にのみ残る**ため…）」は、
**本 PR 後は事実と食い違う**（切替が無くても記録は残る）。さらに **FRED だけを使った期間**では
「使った源は分かるが、その源はクレジット表記を求めていない」状態が生じるため、
**「特定できません」は端的に誤りになる。** よって次の 3 分岐にする。

| 台帳の状態 | 出す文面 |
| --- | --- |
| クレジットを要求する源を使った証拠がある | `- 出典: <クレジット文言>`（従来） |
| 使った源は分かるが、その源はクレジットを要求しない | `- 出典: <源名>（クレジット表記を求めていない情報源です）` |
| **使用記録が 1 件も無い** | `- 出典: **記録からは特定できません**（期間内に情報源の使用記録が残っていません）` |

あわせて、**第一の源（rank ≤ 1）の使用記録がある平常日**に限り、劣化なしの文へ
「**第一の情報源（<源名>）から取得できており**」を戻す（IADR-0199 決定5 が
「証拠が支えていない」として外した文言を、**証拠ができたので戻す**）。**証拠が無ければ従来の文のまま**。

## 受け入れ基準 → テスト写像

| # | 受け入れ基準 | テスト |
| ---: | --- | --- |
| 1 | 静かな期間でも `Credits()` が**根拠のある出典**を返す | `HttpFxSourceStatusSourceTests.静かな期間でも_使用記録から出典を導ける` |
| 2 | **1 日 1 件・通貨ごと**（同一暦日に何度呼んでも 1 件） | `FxSourceStatusTrackerTests.第一の源が使えている間は_日次の使用記録を1件だけ発行する` / `使用記録は通貨ごとに独立して1件ずつ出す` |
| 3 | **暦日が変われば再び 1 件**（境界） | `FxSourceStatusTrackerTests.使用記録は日をまたげば再び発行する` |
| 4 | **使っていない源のクレジットを出さない**（否定形・IADR-0196 決定4） | `HttpFxSourceStatusSourceTests.フォールバック先しか使っていなければ_日銀の出典を出さない`（既存）／`使用記録がフォールバック先だけなら_日銀の出典を出さない` |
| 5 | 新イベントの CI 強制追随（識別子・監査ハンドラ・契約基準・ハンドラ実行） | `EventMessageTypeNameTests` / `EventBackwardCompatibilityTests` / `AuditConsumerCoverageTests` / `AuditEventConsumersTests.為替の情報源の使用記録は_通貨ごとの相関で台帳へ記録される` |
| 6 | 使用記録で「劣化あり」にならない（否定形） | `ReportRendererFxSourceStatusTests.使用記録だけの期間は_劣化ありとは書かない` |
| 7 | 遷移が出た日に使用記録を重ねない（洪水の否定形） | `FxSourceStatusTrackerTests.遷移を発行した日は_同じ源の使用記録を重ねない` |

## やらないこと

- **Discord への通知**（決定 除外表のとおり。平常運転を毎日流さない）。
- **状態の永続化**。判定器は従来どおり in-memory・プロセスごとであり、**再起動した日は使用記録が
  もう 1 件出る**（重複側へ倒す・決定C と同じ向き）。権威源は台帳である（IADR-0199 決定1）。
- **日報への使用記録の明細行**。出典 1 行で足り、明細は月報を押し流す（IADR-0196 決定7 と同じ理由）。

## 残余リスク

- **暦日は UTC** であり JST の日付境界とずれる（IADR-0196 から不変）。
- **抑止はプロセスごと**。水平展開すると台帳にインスタンス数ぶんの使用記録が入る（重複であり欠測ではない）。
- **台帳の行数が 1 日あたり「通貨 × 使った源」ぶん増える**（現状 USD/JPY のみで日 1〜2 件）。
