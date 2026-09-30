---
title: K 線の取得枠の検証口に、K 線を 1 本も取らず取得枠だけを読むモード（--quota-only）と requestTime の時刻帯の明記を足す（回復周期の追試用。#1125）
type: spec
status: accepted
related_ids: [FR-02, FR-15, UC-01, ADR-0048, ADR-0023, IADR-0464, IADR-0300]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
---

# 仕様書: K 線の取得枠の検証口の quota-only モード（#1125）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（判断へ渡す出来高）／FR-15（日足 OHLC の履歴源）
- ユースケース（UC）: UC-01（手順 3）
- 画面（SC）: なし
- 関連 ADR: ADR-0023 決定 5（確認 1: 取得枠の単位と**回復周期**）／ADR-0048 決定 3
- 関連する実装ADR: IADR-0464（検証口の置き場所と形。本作業は同 IADR への追記 `［2026-09-30 追記 / #1125］`）／IADR-0300（検証口の作法）
- 起票: [#1125](https://github.com/endazon/ai-stock-trading/issues/1125)（前作業 #1117・関連 #1118）

## 目的・背景

利用者が 9/30 に `--probe-kline-quota` を 2 回（23:25 JST ごろ・23:43 JST ごろ）打った。分かったこと:

1. **同じ銘柄を取り直すと `delta.used=0` だが、枠の詳細一覧のその銘柄の `requestTime` は取り直した時刻へ更新される**（AAPL: `22:25:53` → `22:42:36`）。
   回復が「その銘柄の最後の取得から」数える仕組みなら、追試で K 線を取り直すたびに時計が戻り、回復を観測できない。
   既存の手順書の回復周期の手順（「同じ銘柄だけを取る」打ち直し）はこの性質の下では**誤り**になる。
2. **`requestTime` は JST ではなく UTC+8（moomoo のサーバ時刻）で返る**（23:25 JST の実行で `22:25:53`）。

よって、K 線を 1 本も取らずに取得枠（used / remain / 詳細）だけを読むモードを足し、`requestTime` に時刻帯を明記する。

## 母集合（規則 9・10: 誤りの側の文字列で走査する）

走査: `grep -rn "requestTime\|RequestTime\|回復周期\|probe-kline-quota" --include=*.md --include=*.cs --include=*.yaml --include=*.json .`（bin/obj を除く）。

| 当たり | 本作業で偽になるか | 扱い |
| --- | --- | --- |
| `docs/operations/kline-quota-probe-runbook.md` §回復周期の判定（「日を置いて打ち直し」「同じ銘柄だけを取る」「`requestTime=` の時刻帯は確かめていない」） | **偽になる**（取り直しで時計が戻る・時刻帯は UTC+8 と判明） | 書き直す（quota-only の追試手順・性質・UTC+8） |
| 同 §手順・§出力の読み方・§頻度制限（モードが 1 つだけの前提の記述） | 追記が要る | quota-only の 1 行と出力を足す |
| `KLineQuotaProbeCommand.cs` の冒頭コメント・`usage=` 行 | 追記が要る | モードを足す |
| `IADR-0464` 決定 4（「日を置いて打ち直したときの変化から読む」）・残余（`RequestTime` の時刻帯） | 凍結記録。本文は書き換えない | 末尾に `［2026-09-30 追記 / #1125］` を足し、索引行にも同じ追記ブロックを足す |
| `.ai-context/specs/20260930_1117_kline-quota-probe.md` 残余（時刻帯） | 凍結記録 | 据え置く（本仕様書が後続） |
| `docs/blocked-tasks.md` A-3 の 2026-09-30 追記（道具を足した・確認は未了・手順は runbook） | **偽にならない**（回復周期は未了のまま。手順の所在も runbook のまま） | 据え置く |
| BacktestService の「取得枠の照会は実装していない」「回復周期が未確認」の各所（`MMApiMoomooHistoryKLineClient.cs`・`BarDataOptions.cs`・`MoomooHistoricalBarSource.cs` 等）・IADR-0157・FR-15 文書 | 偽にならない（アダプタの記述。回復周期は依然未確認） | 据え置く |
| `Program.cs` の分岐コメント（`--probe-kline-quota [オプション]`） | 偽にならない（オプションの 1 つが増えるだけ） | 据え置く（試験が固定する文字列も変えない） |
| 既存の試験の `requestTime=… requestTimeStamp=…` の期待値 | 偽にならない（時刻帯の欄は `requestTimeStamp=` の**後ろ**へ足すため部分一致が保たれる） | 据え置く |

規則 10（自分の記述で新たに誤りになるもの）: runbook の「既定の 1 回で最大 3 銘柄ぶん」「要求 12 回」は既定モードの記述として正しいまま。
quota-only は「要求 1 回・枠の消費 0」と別行で書く。

## 窓の扱い（規則 11）

規則 11 は「時間差（窓）を扱う是正」の形を決める前に両側のプローブで実測せよ、と定める。**本作業は窓を扱う是正ではない**:
検証口は 1 回の照会で得た `requestTime` を**同じ時点のまま**別の時刻帯へ換算して並べるだけで、2 つの時点の差（窓）を計算しない。
回復周期（前回の取得から今までの差）は利用者が手順書に従って記録を並べて読むものであり、検証口は差を計算・判定しない。
よって「前の端／後の端／両端」の 3 形の表は作らない（該当する形が無い）。換算の正しさは日付をまたぐ場合を含む固定値の試験で担保する。

## 設計

1. **旗**: `--probe-kline-quota --quota-only`。値を取らない旗である。
   - **他のオプション（`--symbols` / `--count` / `--split-*`）と併用したら使い方の誤り**（終了コード 2・接続しない）。
     併用は「K 線も取りたい」のか「取りたくない」のかが曖昧であり、取り直しで回復の時計が戻る事故を黙って起こさないため。
   - 重複（`--quota-only --quota-only`）・`--quota-only=…` の形も使い方の誤り。旗は先頭の `--probe-kline-quota` の後ろならどこでもよい（オプションは位置を問わない既存の作法）。
2. **手順（quota-only）**: 枠の照会（詳細つき）を **1 回だけ**撃って終わる。`RequestDailyKLinesAsync` を呼ばない（枠を消費せず、どの銘柄の `requestTime` も更新しない）。
   要求 1 回なので待ち（自制レート）は入らない。全体の打ち切り・例外時の扱い・伏せ・終了コードは既定モードと同じ。
3. **出力（quota-only）**:
   - 見出し `probe=kline-quota mode=quota-only market=US requestTime.tz=UTC+8`
   - `section=quota-only` の後に既定モードと同じ整形の `quota[0] label=quota-only retType=… used=… remain=…` と `quota[0].detail.count=` / `quota[0].detail[j] …`
   - `quota.used=… quota.remain=… quota.total=…`（total = used + remain。欄が無ければ `(不明)`）
   - `requests.sent=1 requests.failed=…`・`result=`・`exitCode=`
4. **requestTime の時刻帯（両モード共通）**: 詳細の各行の末尾（`requestTimeStamp=` の後ろ）に
   `requestTime.tz=UTC+8 requestTime.jst=yyyy-MM-dd HH:mm:ss requestTime.utc=yyyy-MM-dd HH:mm:ss` を足す。
   - 換算は `requestTime` の文字列を UTC+8 の壁時計として読み（書式 `yyyy-MM-dd HH:mm:ss`、小数秒つき 1〜3 桁も可）、UTC = −8 時間、JST = +1 時間。小数秒は入力にあれば同じ桁で出す。
   - 読めない・欄が無い場合は `requestTime.jst=(換算不可) requestTime.utc=(換算不可)`（`tz=UTC+8` は出す）。
   - `requestTimeStamp`（int64）は単位が未確認のため換算しない（値はそのまま出す）。
   - **既定モードの出力の既存の欄・順序・要求の順序は変えない**（欄を行末へ足すだけ）。
5. **読み取り専用**: ポート `IKLineQuotaQuery`（2 メソッド）・検証口の引数型・実装の型は変えない。quota-only は同じポートの `QueryQuotaAsync` だけを使う。
   「K 線を取らない」をポートを分けて型で閉じることは採らない（ポートを 3 つ目のメソッドや別型へ広げると、IADR-0464 決定 2 の「2 メソッドに保つ」固定を崩す。
   quota-only が K 線を撃たないことは偽の照会口の呼び出し記録と、偽の OpenD の送信記録で固定する）。

## 対象範囲

- `backend/Services/OrderExecutionService/Features/OrderExecution/ProbeKLineQuota/KLineQuotaProbeCommand.cs`（旗・解釈・手順・出力・換算）
- 試験: `Tests/Features/OrderExecution/ProbeKLineQuota/KLineQuotaProbeCommandTests.cs`・`Tests/Infrastructure/ExternalServices/KLineQuotaProbeEndToEndTests.cs`（追加）
- `docs/operations/kline-quota-probe-runbook.md`（追試の手順・requestTime の性質・UTC+8。trace ブロックに #1125 と本仕様書を足す）
- `.ai-context/adr/IADR-0464_…md` の末尾追記と `.ai-context/adr/README.md` の索引行の追記ブロック
- 対象外: #1118 のレーン（日足の経路）・BacktestService・Program.cs の分岐（変更不要）・`docs/blocked-tasks.md`（上の母集合のとおり偽にならない）

## 受け入れ基準

- [x] `--quota-only` では K 線の取得を 1 本も撃たず、枠の照会（詳細つき）を 1 回だけ撃つ（偽の照会口・偽の OpenD の双方で固定）
- [x] 出力に used / remain / 詳細（銘柄・requestTime）が出る
- [x] requestTime に時刻帯 UTC+8 を明記し、JST と UTC の換算値を並べる（日付をまたぐ換算を含む）
- [x] `--quota-only` と他のオプションの併用・重複・`=` の形は使い方の誤り（終了コード 2・接続しない）。旗の位置は問わない
- [x] 既定モードの要求の順序・回数・出力の既存の欄は変わらない
- [x] 読み取り専用のポート・型は変えない（既存の構造の試験が緑のまま）
- [x] 手順書: 追試の手順（quota-only を数日おき・9/30 に消費した 3 件が戻るか）、requestTime が取り直しで更新される性質と既定モードを追試に使わない理由、UTC+8 の注記
- [x] IADR-0464 追記と索引行の追記ブロック

## テスト方針

- `KLineQuotaProbeCommandTests`: quota-only の呼び出し記録（`quota(detail)` 1 件だけ・待ち 0 回）、出力（used / remain / total / 詳細・時刻帯）、
  換算（通常・日付をまたぐ・小数秒・読めない・欄なし）、引数（単独・位置違い・併用・重複・`=` 形）、既定モードの呼び出し順が不変・詳細行に時刻帯の欄が付く。
- `KLineQuotaProbeEndToEndTests`: 実物のクライアント ＋ 偽の OpenD で、quota-only は `RequestHistoryKLQuota`（詳細つき）1 件だけを送り `RequestHistoryKL` を 0 件、
  出力に換算値（偽の OpenD の `2026-09-29 22:10:05` → JST `23:10:05`・UTC `14:10:05`）。
- テスト ID: 振らない。#1117 の試験は ID を持たない（FR-02 は網羅裁定 #211 の必須範囲外でテスト仕様書が無い。`docs/tests/` に FR-02 の系列は無い）。同じ扱いに揃える。
- 変異（自己変異）で赤を確認する: quota-only でも K 線を取る／時刻帯のずれを誤る／旗を無視する。

## 変異（自己変異）の実測

`KLineQuotaProbe` の試験（79 件）を各変異で流した。各変異は戻して再ビルドし、79 件が緑に戻ることを確かめた。

| 変異 | 内容 | 結果 |
| --- | --- | --- |
| M1 | quota-only でも K 線を取る（`ExecuteQuotaOnlyAsync` の後に既定の手順も流す） | **赤 3 件**（quota-only の呼び出し記録・非成功時・E2E の送信記録） |
| M2 | 時刻帯のずれを誤る（`RequestTimeOffset` を +9 時間＝JST とみなす） | **赤 9 件**（換算 5 件・定数・既定モードの詳細行・quota-only の詳細行・E2E） |
| M2b | 時刻帯のずれを誤る（`RequestTimeOffset` を 0＝UTC とみなす） | **赤 9 件**（同上） |
| M3 | 旗を無視する（解釈はするがモードを立てない＝既定の手順へ落ちる） | **赤 3 件**（quota-only の呼び出し記録・非成功時・E2E） |
| M3b | 旗を無視し、他のオプションとの併用も通す | **赤 7 件**（併用 4 件・quota-only 3 件） |

## 計画書との差異

無し（検証口に観測の手段を足すだけで、判断へ出来高を流さない）。

## 採番

IADR は新規に起こさない（IADR-0464 の形〔読み取り専用・1 回だけ・伏せ・終了コード〕の中でモードを足すだけであり、新しい決定ではない）→ IADR-0464 へ追記。

## 検証

`dotnet build backend/backend.slnx -warnaserror`（警告 0・エラー 0）／`dotnet test`（OrderExecutionService.Tests 全 1,390 件緑。うち `KLineQuotaProbe` 79 件＝既存 56 ＋ 追加 23）／
`dotnet format backend/backend.slnx --verify-no-changes`（差分なし）／node の検査器（`check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・
`check-plan-id-qualification`・`check-test-traceability`・`check-doc-links`・`check-adr-index-sync`・`check-adr-index-addendum-loss`・`check-reading-budget`・
`check-commit-messages`）と `scripts.test.js`（490 件）。

［2026-10-01 追記 / #1125］独立監査の 🟡（形式として読める極端な requestTime ― `0001-01-01 05:00:00`・`9999-12-31 23:30:00` ― で換算が `ArgumentOutOfRangeException` を投げ、検証口が result=error で打ち切られる）を是正した。換算先（UTC は −8h、JST は +1h）が `DateTime` の範囲外なら「(換算不可)」に倒す。試験は「読めない requestTime」の Theory に 2 件を足した。変異（範囲の判定を外す）で 2 件が赤になることを確かめた。
