---
title: T-10-786 で足した reader の Collect が相乗りの false を返して赤になる 4 つ目の競走を、戻り値を表明しない形で塞ぐ
type: spec
status: accepted
related_ids: [FR-10, NFR-07, IADR-0395]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値なしの建玉を持たない」・NFR-07 可観測性)
---

# 仕様書: T-10-786 の 4 つ目の競走（Collect の相乗り）の是正（#1507）

## 起点

- #1507: Integration E2E（`workflow_dispatch`・`72e993b4`・attempt 1）で
  `OrderExecutionService.Tests.ProtectiveStopDriftAdopterCompositionTests.Programは起動完了後に打ち切りのカウンタを0で計上し_OTelのexporterまで届く(provider: "paper")`
  （T-10-786）が赤。再実行（attempt 2）とその後の develop の run は緑。同じ形の赤は #1504 でも 1 回出ている（オーケストレーターの記録）＝**2 回目**。
- 失敗の形: `Expected reader.Collect() to be True because 足した exporter は常に成功を返す（OTLP の送信の成否とは切り離す）, but found False.`
  試験の所要は 81 ms で、**1 回目の `Collect()`** で赤になっている（門は通っている）。

## 原因の読み（OTel 1.16 の実装で確かめた）

`MetricReader.Collect(timeout)` が false を返すのは次のどれかに限られる（`MetricReader.cs` `Collect` / `ProcessMetricsCollection`、
`BaseExportingMetricReader.ProcessMetrics`）:

1. exporter が失敗を返す・投げる → 足した `SumCapturingExporter` は常に `Success` を返し、投げる箇所が無い。**当たらない。**
2. 期限切れ（`ProcessMetricsCollection` の残り時間 ≤ 0）→ 試験は期限なしで呼ぶ。**自分の呼び出しでは当たらない。**
3. **同じ reader で収集が既に走っていると、それに相乗りしてその結果を返す**（`!shouldRunCollect` の枝。`Shutdown` と重なれば待ちの打ち切りでも false）。
   プロバイダの `ForceFlush(期限)`・`Shutdown` は合成の reader 越しに足した reader の収集も回し、期限を先の OTLP の reader が食えば期限切れの false になる。

1・2 が当たらないので 3 である。CI でどの呼び出しと重なったかは特定していない（手元で 15 回 × 51 件を流して再現せず）。
いずれにせよこの false は「足した exporter が成功したか」ではなく「この呼び出しが自分の収集を完了したか」であり、**試験の守る性質
（起動しただけで 2 つの理由の系列が 0 で exporter に届く）ではない**。

## 母集合（規則 9）

`git grep -n "\.Collect()" -- 'backend/**/*Tests*.cs'` と `git grep -n "reader.Collect\|ForceFlush" -- backend` で、`Collect` / `ForceFlush` の戻り値を表明する試験を引いた。
- `Collect()` の戻り値を表明するのは本件の 1 か所だけ。
- `ForceFlush(10_000).Should().BeTrue()` が `TestSupport.PlatformShim.Tests/BusinessMetricsWiringTests.cs:97` に 1 か所ある。OTLP の reader を持たない
  自前の MeterProvider での表明で、相乗りする他の収集が無い（並走するホストと provider を共有しない）。**対象外**（赤の記録も無い）。

## 是正（試験の側だけ。本番コードは変えない）

- `reader.Collect()` の戻り値を表明しない。false の回は点を判定に使わず、20 ms 置いて次の回へ進む（期限 10 秒は従来どおり）。
- 期限切れは従来どおり exporter の表明（2 つの系列・値 0）で赤にする。表明の文面に失敗した `Collect` の回数を出す（次に赤になったときの手がかり）。
- 変異①（起動時の計上を消す）は門で、②（ホストの開始前へ動かす）は期限切れの後の exporter の表明で引き続き赤（判定の形は変えていない）。
- IADR-0395 に 2026-10-30 追記を足す。

## red → green（再現の注入は一時的で、コミットしない）

同じ reader に期限 0 の `Collect(0)` を 4 本並走させる（300 ms）注入を試験に一時的に入れて、相乗りの false を起こした。

| 形 | 結果 |
| --- | --- |
| 旧形（`Collect().Should().BeTrue()`） | 3 回中 1 回、#1507 と同じ文面で赤 |
| 是正後 | 6 回とも緑 |
| 是正後・注入なし | 緑（`ProtectiveStopDriftAdopterCompositionTests` 5 件） |

## 受け入れ基準

- [x] 相乗りの false で赤にならない（注入で確認）。
- [x] 性質（2 つの系列が値 0 で exporter に届く）の表明は残る。
- [x] OrderExecutionService.Tests 全件が緑、format 差分なし。
