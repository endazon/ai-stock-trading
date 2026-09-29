---
title: T-10-786 の門（MeterListener）と OTel の集計の競走を、足した reader の Collect を揃うまで繰り返して塞ぐ
type: spec
status: accepted
related_ids: [FR-10, NFR-07, IADR-0395]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値なしの建玉を持たない」・NFR-07 可観測性)
---

# 仕様書: T-10-786 の 3 つ目の競走（門と OTel の集計）の是正（#1108）

## 起点

- #1108: `ProtectiveStopDriftAdopterCompositionTests.Programは起動完了後に打ち切りのカウンタを0で計上し_OTelのexporterまで届く`（T-10-786）が
  ローカルの全件実行で断続的に赤（#1107 の head で 6 回中 2 回）。
- 失敗の形: `Expected points.Select(p => p.Reason) {"positions-unknown"} to contain {"positions-unknown", "positions-query-failed"} ... could not find {"positions-query-failed"}`
  ——門（`PrimeLatch`）は通り、exporter には**1 つ目の理由だけ**が届いている。
- IADR-0395（2026-09-25 追記）が塞いだ 2 つの競走（①ファクトリの解放と計上の完了 ②`ForceFlush` の期限）とは別の形である。
- 🔴 本番コードは変えない（本番の起動時の 0 の計上に欠陥は無い。下記）。skip・無効化・失敗を握りつぶす再試行はしない。

## 原因の裏付け（実測）

### 1. `MeterListener` のコールバックは登録（開始）順に、同じスレッドで直列に呼ばれる

一時的な探査試験（コミットしない）で、`MeterListener` A を開始 → OTel の MeterProvider（reader 付き）を構築 → `MeterListener` B を開始 →
計器を作り `Add(0, r=x)`・`Add(0, r=y)` を順に呼び、各リスナのコールバックの中から同じ reader を `Collect()` して export された系列を記録した。

```
A-before-otel[x] otel-sees=
B-after-otel[x]  otel-sees=x
A-before-otel[y] otel-sees=x
B-after-otel[y]  otel-sees=x|y
```

- OTel の集計（MeterProvider の内部の `MeterListener`）は**開始順の中の自分の位置**で呼ばれる。OTel より先に開始したリスナのコールバックの
  時点では、**その計上はまだ OTel に入っていない**（A が y を受けた瞬間の Collect は x しか出さない）。
- T-10-786 の `PrimeLatch` はホストの開始**前**に開始する（変異②を殺すために必須）ので、必ず OTel より先に呼ばれる。
  2 つ目の計上（`positions-query-failed`）で門が開いた瞬間、その計上は OTel の集計へまだ入っていない。
  試験のスレッドの `reader.Collect()` は起動のスレッドの「OTel のコールバック」と競走し、負荷で前者が勝つと 1 系列だけが export される。

### 2. 決定的な再現

- 試験の `PrimeLatch` のコールバックで、門を開いた直後に `Thread.Sleep(500)` を入れる（起動のスレッドが OTel のコールバックへ進むのを遅らせる。
  本番の意味は変わらない）→ **旧形は T-10-786 だけの実行 22 回中 6 回、#1108 と同じ文面で赤**（`{"positions-unknown"}` だけ。paper・moomoo の両方で出た）。
  決定的にならないのは、計上が試験のスレッドそのものの上で走る回がある（`factory.Services` の中で起動完了の通知が同期に走る）ためで、
  そのときは試験のスレッドが OTel のコールバックの完了まで戻らず、競走が起きない。眠りが効いていることは試験の所要（各 500ms 余り増える）で確かめた。
- 本番: 計上は `ApplicationStarted` の中で MeterProvider が立った後に同期で走り、同じ `Add` の中で OTel のコールバックも済む。
  次の収集（OTLP の周期 export）はその後なので欠けない。**本番に欠陥は無い。**

## 是正（試験だけ）

1. 門が開いた後、足した reader の `Collect()` を**短い間隔（20ms）で繰り返し**、2 つの理由の系列がそろった時点で表明へ進む。期限は 10 秒。
   待つのは「OTel が 2 つ目の計上を集計した」という 1 つの出来事で、失敗を握りつぶす再試行ではない（期限切れは赤になる）。
2. exporter は累積（Cumulative）で、Collect のたびに同じ点を再び出す。**各 Collect の前に sink を空にし、判定と表明は最後の Collect の点だけで行う**
   （重複した点で「値がすべて 0」の表明が水増しされない。そろわないまま期限を迎えたら最後の点で表明し、既存と同じ文面で赤になる）。
3. `Collect()` の戻り値 `true` の表明は各回で保つ（足した exporter は常に成功を返す）。
4. 殺す変異（引き続き赤であることを実測する）:
   - 変異①（Program.cs の起動時の計上を消す）→ 門で赤。
   - 変異②（計上をホストの開始前＝`builder.Build()` の直後へ動かす）→ 門は開くが OTel には届かず、10 秒の期限の後に exporter の表明で赤。

## 🔴 母集合（規則 9〜11）

走査に使った語: `PrimeLatch` / `.Collect()` / `BaseExportingMetricReader` / `ForceFlush` / `T-10-786`（`git grep`・`CHANGELOG.md` を除く）。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/OrderExecutionService/Tests/ProtectiveStopDriftAdopterCompositionTests.cs` | **直す**（T-10-786 の収集を揃うまで繰り返す。コメントに 3 つ目の競走を足す） |
| `backend/Services/OrderExecutionService/Program.cs` | 直さない（本番に欠陥は無い） |
| `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests/BusinessMetricsWiringTests.cs`・`CredentialBearingUriTraceRedactionTests.cs` | 直さない（計上を試験のスレッド自身が同期に行ってから収集する。別スレッドの計上と門を持たないので同型の競走が無い） |
| `docs/tests/FR-10_risk-controls-tests.md`（T-10-786 の行） | **直す**（手順の欄に「足した reader の収集を 2 つの系列がそろうまで繰り返し、最後の収集で判定する」を足す。表示テキストに ID は書かない） |
| `.ai-context/adr/IADR-0395_*.md` / `.ai-context/adr/README.md` の索引行 | **追記ブロックを足す**（`［2026-09-30 追記 / #1108］`。本文は書き換えない） |
| `.ai-context/adr/IADR-0370_*.md`（試験番号の列挙のみ） | 直さない（試験番号は変わらない） |
| `.ai-context/specs/20260925_942_*.md` | 直さない（確定済みの凍結記録） |

- 規則 10（この変更で新たに誤りになる自分の記述）: 試験の旧コメント「計上を見届けてから吐き出させる」は「見届けた＝OTel に入った」と読める。
  「門が開いた時点では OTel の集計はまだ」と改めた。テスト仕様書の手順の欄も同じ。
- 規則 11（窓）: 窓は「門が開く（2 つ目の計上を試験のリスナが受けた）」から「OTel がその計上を集計する」まで。
  プローブは 増える側＝門を開いた直後に 500ms 眠る（窓が広がる。本番の意味は同じなので**緑が正**）／
  減る側＝計上をホストの開始前へ前倒し（**赤が正**）・計上を消す（**赤が正**）。

  | 形 \ プローブ | 門の直後に 500ms 眠る（緑が正） | 計上を開始前へ前倒し（赤が正） | 計上を消す（赤が正） |
  | --- | --- | --- | --- |
  | 前の端だけ（門が開いたら即 1 回 Collect。**是正前**） | ✗ 赤（22 回中 6 回。実測） | ✓ 赤 | ✓ 赤（門） |
  | 後の端だけ（門を持たず、Collect を揃うまで繰り返すだけ） | ✓ 緑 | ✓ 赤（期限切れ） | ✓ 赤（期限切れ。ただし門が無いので理由の区別がつかない） |
  | **両端**（門で計上を見届け、さらに OTel の集計を Collect の繰り返しで見届ける。**採用**） | ✓ 緑 | ✓ 赤（期限切れ） | ✓ 赤（門） |

  「後の端だけ」も緑・赤の升目は満たすが、門を外すと 2 つの変異の落ち方（門／exporter）が区別できなくなる。既存の門は残す。

## 検証

コマンド（素の `dotnet`）:

- `dotnet build backend/backend.slnx`（0 警告）
- `dotnet format backend/backend.slnx --verify-no-changes`
- `dotnet test backend/Services/OrderExecutionService/Tests/OrderExecutionService.Tests.csproj`（全件 × 10 回）
- 文書検査器（`check-test-traceability` / `check-trace-blocks` / `check-doc-links` / `check-adr-index-sync` / `check-adr-index-addendum-loss` /
  `check-cross-repo-refs` / `check-plan-id-qualification` / `gen-knowledge-graph --check` / `check-commit-messages`）

結果（実測）は下の「実測の記録」。

## 実測の記録

| 項目 | 結果 |
| --- | --- |
| 門の直後に 500ms 眠るプローブ（T-10-786 を含むクラスだけの実行） | 旧形: 22 回中 6 回赤（#1108 と同じ文面）／是正後: 13 回とも緑 |
| 変異①（`Program.cs` の `ApplicationStarted` の計上を消す） | paper・moomoo とも赤（`primed.Wait(...)` が False。門で落ちる） |
| 変異②（計上を `builder.Build()` の直後＝ホストの開始前へ動かす） | paper・moomoo とも赤（10 秒後に `{empty}` で exporter の表明で落ちる） |
| OrderExecutionService.Tests 全件（1246 件。基点 cc87ceb1＝#1107 の head）× 10 回（是正前 → 是正後） | **是正前 10 回中 2 回赤**（paper 1・moomoo 1。#1108 と同じ文面 `{"positions-unknown"}`）／**是正後 10/10 緑（0 失敗）** |
| 同 全件（1211 件。基点 87c40e1f）× 10 回・負荷なし（是正後。rebase 前） | 10/10 緑（0 失敗） |
| 同（1211 件）× 10 回・CPU を回し続ける負荷 6 本（4 コア）（是正前 → 是正後） | 是正前 10/10 緑（develop では再現せず。#1108 の実測 develop 8 回中 0 回と同じ）／是正後 10/10 緑 |
| `dotnet build backend/backend.slnx` | 0 警告・0 エラー |
| `dotnet format backend/backend.slnx --verify-no-changes` | 差分なし |

- 作業中に develop が #1106・#1107 へ進んだため、基点を cc87ceb1 へ rebase した。変異①②は rebase 後にも再測し、同じ落ち方で赤だった。
- 変異は `git checkout <基点SHA> -- backend/Services/OrderExecutionService/Program.cs` で戻した。探査試験・プローブはコミットしていない。
- 1211 件の基点では是正前も全件の繰り返しで赤が出なかった（#1108 の develop 8 回中 0 回と同じ）。1246 件の基点で是正前 2/10 赤・是正後 0/10。

## 射程外

- 本番コードの変更（不要。上記）。
- `MeterCapture` 等の共通部品への「集計を待つ」API の追加（本件 1 箇所の用途。同型が 2 回目に出たら共通化する）。
