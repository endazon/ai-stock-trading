---
title: 種別ごとの打ち切りの試験を壁時計の競争から同期点へ移す（#900 のフレーク）
type: spec
status: accepted
related_ids: [FR-06, FR-16, IADR-0071, IADR-0120, IADR-0123, IADR-0364, IADR-0366]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06 日次/週次/月次の報告書)
---

# 仕様書: 種別ごとの打ち切りの試験を壁時計の競争から同期点へ移す（#900）

## 起点

- **[#900](https://github.com/endazon/ai-stock-trading/issues/900)**（test）。`dotnet test backend/backend.slnx`
  （全ソリューション実行）で `ReportService.Tests` の
  `HttpReportNarrativeDrafterTests.タイムアウトは報告書種別ごとに効く_日報は打ち切られ週報は通る` が稀に落ちる
  （起票者の実測 6 回中 1 回）。

```
Expected (drafter.DraftNarrativeAsync(Ctx with { Kind = ReportKind.Daily })) to be the same string, but they differ at index 0:
 ↓ (actual)
"週次の所感です。"
"（本節は LLM 未接続のため自動ドラフトされていません。…"
 ↑ (expected)
```

- 起点 ID: **FR-06**（日次/週次/月次の報告書）。当該テストが固定しているのは
  [IADR-0123](../adr/IADR-0123_report-narrative-timeout-by-kind.md) 決定 1（種別ごとの打ち切り。#308）。
- 先行事例: [#885](https://github.com/endazon/ai-stock-trading/issues/885) / PR #896 /
  [IADR-0364](../adr/IADR-0364_grpc-deadline-test-excludes-connect-from-budget.md)
  （同型の壁時計依存を、予算の外出しと同期点で是正した）。
- 起点 ID に `NFR-xx` は採らない（「テストの安定性」に当たる計画の非機能要件番号は無い。IADR-0364 と同じ扱い）。

## 対象範囲

- 対象: `backend/Services/ReportService/Tests/Infrastructure/ExternalServices/HttpReportNarrativeDrafterTests.cs`
  の当該テストと、そこでしか使っていないフェイクハンドラ。
- 対象外: **製品コードは一切変更しない**（縮退の向き・種別ごとの上限の実装は正しく動いている）。
  同ファイルの他のテスト、`#901`（別 PR）、同型の棚卸しと検査器の検討（#885 / #900 / #901 をまとめて別途）。

## 🔴 再現の実測（着手前）

### 第 1 巡: 当該テスト単体の反復（スピンループ 16 本・8 論理 CPU）

`ReportService.Tests.exe -method <当該テスト>` を **30 回**。**30 回とも緑**
（`RESULT target_fail=0 other_fail=0 of 30 (spinners=16)`）。CPU を焼くだけでは再現しない。

### 第 2 巡: 計装して機序を測る（一時的な診断テスト・コミットしない）

製品の `HttpReportNarrativeDrafter` をそのまま呼び、ハンドラ側で
①入場時刻 ②要求トークンのキャンセル通知が走った時刻 ③`Task.Delay(600ms)` が完了した時刻 を記録した。
要求を投げた**直後に**スレッドプールのワーカーを全部塞ぐ（`Thread.Sleep` の作業項目 1024 本 × 1.5 秒）。
スピンループ 16 本を併走。

```
  0 cut       entry=  31.3 cancelCb= 1543.3 delayDone=   -1.0 total= 1545.9
  1 RESPONDED entry=   0.2 cancelCb=   -1.0 delayDone= 1502.0 tokenCancelledAtDelayDone=False total= 1521.1
  2 cut       entry=   0.2 cancelCb= 1503.8 delayDone=   -1.0 total= 1504.1
  3 cut       entry=   0.1 cancelCb=  104.5 delayDone=   -1.0 total=  104.6
  …（以降はスレッド注入が進み、打ち切りは 100〜200 ms 台で発火する）
SUMMARY responded=1/20 blockers=1024 holdMs=1500
```

🔴 **読み取れること**: プールが塞がると、**期限 100 ms のキャンセルも 600 ms の遅延も、どちらも
「ワーカーが空くまで」配送されない**。空いた時点では **2 つとも期限切れ**であり、
**どちらが先に走るかは保証されない**。サンプル 0・2 ではキャンセルが先（テストは緑）、
サンプル 1 では遅延が先（＝**日報が応答を受け取る**＝ issue の失敗そのもの）。

### 第 3 巡: **実テストそのもの**を同じ条件で反復（是正前）

一時ハーネス（`ZzStarver`。実テストのメソッドを直接呼び、呼び出しの 2 ms 後に上記の枯渇波を起こす）で
**同一プロセス内 20 回**。スピンループ 16 本。

```
iter 1,15,16,17,18,19 FAIL XunitException: Expected (drafter.DraftNarrativeAsync(Ctx with { Kind = ReportKind.Daily }))
  to be the same string, but they differ at index 0: ↓ (actual) "週次の所感です。" …
SUMMARY pass=14 fail=6 of 20 blockers=1024 holdMs=1500
```

**是正前 6/20。** 失敗の文言は issue の報告と同一である。

> 🔴 **冷えたプロセスでは再現しない**（40 プロセス × 1 回で 0/40、60 プロセス × 1 回で 0/40 も別途実測）。
> 初回の要求は JIT で 100 ms を食い、打ち切りが先に立つためだと**見込まれる**が、**計測していない**ので断定しない。
> 再現には「暖まったプロセス」＋「要求直後の枯渇」が要る。

## 設計

**遅延（壁時計）を同期点へ置き換え、順序で同じ命題を固定する。** 秒数・上限値は一切動かさない。

- ハンドラ `HeldRespondingHandler`: 要求を受けたら `Channel` へ載せ、**テストが解放するまで応答しない**。
  終わり方は「解放」か「打ち切り」のどちらかだけで、時間では終わらない。
- 手順:
  1. **週報**を投げ、ハンドラ到達を待つ（飛行中にする）。
  2. 同じドラフタ・同じハンドラで**日報**を投げる。解放しないのに戻ってくれば、戻した経路は**種別ごとの打ち切り**しかない。
     `HttpClient.Timeout` は `Timeout.InfiniteTimeSpan` にして**多層防御の上限を外す**（従来は 30 秒で、
     これも理屈上は日報を切り得た）。
  3. その時点で**週報の要求トークンは未発火**であることを見る。週報は日報より前から飛んでおり、
     日報の上限（100 ms）は既に発火しているので、**週報は「日報の上限より長く飛んでいても切られない」**と言える
     —— 時刻の比較ではなく**前後関係**で言える。
  4. 解放すると週報は本文（`週次の所感です。`）を受け取る。

- `Guard`（30 秒）は「打ち切りが効かない」場合に黙って固まらないための上限であり、**合否の基準ではない**
  （IADR-0364 決定 2 と同じ役割）。

## 受け入れ基準

- [x] 「日報は打ち切られ、週報は通る」**両方**の assert が残っている（片方を消していない）。
- [x] 同一インスタンス・同一ハンドラで種別差が出ることを見ている。
- [x] 合否を決める 2 つの事象に**壁時計どうしの競争が無い**。
- [x] 是正後、第 3 巡と同じ条件で 40 回連続緑。
- [x] 変異注入で弱まっていないことを示す（種別ごとの上限を無効化・週報へ日報の上限を適用）。

## テスト方針

上記のとおり、テストそのものが対象である。製品コードは変更しないので、**弱めていないことは変異注入で示す**
（IADR-0364 決定 4 と同じ作法）。

## 計画書との差異

- 差異: なし（計画 ID の受け入れ基準は変わらない。試験の観測方法だけを変える）。

## 未決事項

- **同型の棚卸しと検査器**（「合否を決める 2 つの時刻がどちらも壁時計か」の軸で全テストを引き直す）は
  #885 / #900 / #901 をまとめて別途行う。本 PR の射程は #900 の 1 本に限る。
