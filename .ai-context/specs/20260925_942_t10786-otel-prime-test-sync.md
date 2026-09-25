---
title: T-10-786（起動時の 0 の計上が OTel の exporter まで届く）の不安定を、試験の同期と Meter 名の隔離で根から直す
type: spec
status: accepted
related_ids: [FR-10, NFR-07, IADR-0395]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10「逆指値なしの建玉を持たない」・NFR-07 可観測性)
---

# 仕様書: T-10-786 の不安定の根本原因と是正（#942 の追随）

## 起点

- PR #951（#942 / IADR-0395）で develop に入った T-10-786
  `Programは起動完了後に打ち切りのカウンタを0で計上し_OTelのexporterまで届く` が、マシン高負荷時に不安定。
- 失敗の形（実測）: `Expected points.Select(p => p.Reason) {empty} to contain {"positions-unknown", "positions-query-failed"}`
  ——exporter に当該計器の点が **1 つも届いていない**。
- 🔴 skip・無効化・再試行の追加は禁止（"flake" で片付けない）。本番の配線のバグなら本番を、試験の同期・分離の問題なら試験を直す。

## 再現（是正前・実測）

| 条件 | 結果 |
| --- | --- |
| `dotnet test backend/Services/OrderExecutionService/Tests`（全 815 件）× 8 回、別に CPU を回し続ける負荷 6 本（4 コア） | **8 回中 1 回赤**（`provider: "moomoo"`。上の失敗文面と同じ） |
| 本番の起動時の計上のコールバックの先頭に `Thread.Sleep(500)` を入れる（**計上は ApplicationStarted の後のまま**＝本番の意味は同じ） | **paper・moomoo とも決定的に赤**（同じ文面） |
| 同じ `Sleep(500)` を、計上のコールバックより**先に登録した別のコールバック**へ入れる | 緑（下の「LIFO」の裏付け） |

## 根本原因（仮説ごとの判定）

### ② ApplicationStarted の発火と試験のスレッドの競走 ——**これが原因（試験側）**

- `WebApplicationFactory` の `factory.Services` はホストの開始を `DeferredHost.StartAsync` で待つが、その待ちは
  `ApplicationStarted.UnsafeRegister(_ => _hostStartedTcs.TrySetResult())`（`RunContinuationsAsynchronously`）である
  （Microsoft.AspNetCore.Mvc.Testing の `DeferredHostBuilder.cs`）。**トークンが発火した瞬間に待ちが解ける**だけで、
  Program.cs が同じトークンへ登録したコールバックの**完了は待たない**。
- `CancellationToken` のコールバックは**後に登録したものから**走る。Program.cs の登録（`app.Run` の前・起動のスレッド）は
  ファクトリの登録（ファクトリ側のスレッドが `Build` の戻りを受けてから行う）より先に済むのが普通なので、
  **ファクトリの解放 → Program.cs の 0 の計上** の順になり、試験のスレッド（スレッドプールで再開）の `ForceFlush` と
  起動のスレッドの `PrimeDriftAdoptionFollowUpAbandoned()` が競走する。負荷で前者が勝つと exporter は空。
- 上の再現表の 2 行目（計上を 500ms 遅らせると決定的に赤）・3 行目（先に登録した側を遅らせても緑）がこれを裏付ける。

### 本番の配線にバグはあるか ——**無い**

- 本番では `ApplicationStarted` の発火（`Host.StartAsync` の `NotifyStarted`）は全 `IHostedService.StartAsync` の後で、
  OTel の `TelemetryHostedService.StartAsync` が MeterProvider を立てた後である。計上はその発火の中で同期に走るので、
  **MeterProvider が立った後に計上する**という IADR-0395 決定 3 の性質は成り立つ。落ちうるのは「計上の完了を待たずに
  観測する」試験の側だけである。本番コードは変えない。

### ① 同名の Meter と重複計器 ——**空の原因ではないが、偽の緑の原因になり得る（あわせて塞ぐ）**

- OTel 1.16 は同じ識別（Meter 名・計器名・種類…）の計器を 1 つの `Metric` に束ねる（`MetricReaderExt.TryGetExistingMetric`）。
  束ねた相手（並走する別の Program ホストの Meter）が破棄されると `Metric.Active=false` になるが、**次の collect で
  スナップショットを取ってから外す**（`GetMetricsBatch`）ので、それまでの点は export される。空の原因にはならない。
- 🔴 ただし既定の Meter 名はプロセス全体で共有されるため、**並走する別の Program ホストの 0 の計上がこの試験の provider にも届く**。
  「自分のホストの計上を見届けた」も「exporter に届いた」も他人の計上で満たされ得る（変異②を見逃す偽の緑）。
  xUnit v3 はテストクラスを単位に並列に走らせ（同じクラスの中は直列）、本プロジェクトには Program を組む他のクラス
  （`ForgoneReplayCompositionTests` / `HealthEndpointTests` / `ExecutionWorkerWebApplicationFactory` を使う試験）がある。

### ③ ForceFlush の期限 ——**これも原因（試験側・2 つ目）**。Delta/Cumulative は原因ではない

- 🔴 当初は「原因ではない」と読んだが、**②の是正後に 22 回中 1 回、門（計上を見届けた）を通ったうえで exporter が空**の赤が出て
  （`provider: "paper"`）、読み違いと判った。OTel 1.16 の `MeterProvider.ForceFlush(10_000)` は `CompositeMetricReader.OnCollect` で
  子の reader を**登録順**に回し、`Stopwatch.Remaining` で**残り時間**を次へ渡す。子の `MetricReader.ProcessMetricsCollection` は
  **バッチを集めた後で残り時間が 0 以下なら export せずに false を返す**（"OnCollect failed timeout period has elapsed."）。
- 先に回るのは本番構成の OTLP の reader（`AddAiStockTradingObservability` が先に登録）で、otel-collector が居ないので送信は失敗する。
  高負荷ではこの 1 回の ForceFlush に**最大 4.7 秒**かかった（実測。全 828 件 × 14 回・並列度 16・CPU 負荷 6 本、28 回の ForceFlush の最大値。
  負荷なしでは最大 0.7 秒）。期限 10 秒の尾で足した reader が残り 0 になる。
- 決定的な再現: 期限を 1 ms にすると paper・moomoo とも `exported=0` で同じ文面の赤。11 秒眠る exporter を持つ reader を
  足した reader の**前に**挟むと、旧形（`ForceFlush(10_000)`）は赤、是正後（足した reader だけを `Collect()`）は緑。
- temporality は既定（Cumulative）で、0 の点も出る（原因ではない）。

## 是正（試験だけ）

1. **計上そのものを見届けてから吐き出させる。** OTel と独立の `MeterListener`（`PrimeLatch`）をホストの開始**前**から張り、
   打ち切りのカウンタに 2 つの理由の計上が届いたら開く。待つのは 1 つの出来事で、再試行ではない（上限 30 秒は変異①のときだけ効く）。
2. **Meter 名を試験ごとに隔離する。** 既存の `BusinessMetrics.WithMeterName` ＋ `MeterCapture.NewIsolatedMeterName()` を使い、
   試験の `ConfigureServices` で `BusinessMetrics` の登録だけを差し替え、足した exporter の provider に同じ名前を `AddMeter` する。
   **計上の呼び出し（Program.cs の `ApplicationStarted`）は本番のまま通る。** 本番の `AddMeter(BusinessMetricNames.MeterName)` の
   配線は `BusinessMetricsWiringTests`（肯定形・否定形の対）が引き続き固定する。
3. **足した reader だけを期限なしで吐き出させる**（`reader.Collect()`。`MeterProvider.ForceFlush(10_000)` をやめる）。OTLP の送信の
   成否・所要と切り離す。足した exporter は常に成功を返すので戻り値も `true` を表明する。期限なしで固まり得るのは同じ reader の
   collect が並行しているときだけで、本試験では起きない（試験終了時の provider の破棄より前に呼ぶ）。
4. 元の試験の意図（殺す変異）を保つ:
   - 変異①（起動時の計上を消す）→ 門が開かず赤。
   - 変異②（計上を `ApplicationStarted` より前＝ホストの開始前へ動かす）→ 門は開く（リスナは開始前から聞いている）が、
     OTel の provider は未だ無く、exporter が空で赤。

## 🔴 母集合（規則 9〜11。走査したファイルと除外理由）

走査に使った語: `ApplicationStarted` / `T-10-786` / `ForceFlush`（`git grep`・`CHANGELOG.md` を除く追跡ファイル全件）。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/OrderExecutionService/Tests/ProtectiveStopDriftAdopterCompositionTests.cs` | **直す**（T-10-786 の同期と Meter 名の隔離） |
| `backend/Services/OrderExecutionService/Program.cs` | **直さない**（本番の順序は正しい。上の「本番の配線にバグはあるか」） |
| `docs/tests/FR-10_risk-controls-tests.md`（T-10-786 の行） | **直す**（手順の欄に「計上を見届けてから」「Meter 名は試験ごとに隔離」を足す） |
| `.ai-context/adr/IADR-0395_*.md` / `.ai-context/adr/README.md` の索引行 | **追記ブロックだけ足す**（決定 3 の「T-10-786 が確かめる」の読み方。本文は書き換えない） |
| `.ai-context/adr/IADR-0370_*.md`（208 行・試験番号の列挙のみ） | 直さない（試験番号の範囲は変わらない） |
| `.ai-context/specs/20260925_942_drift-followup-abandoned-alert.md` | 直さない（確定済みの凍結記録） |
| `ForceFlush` を呼ぶ他の試験（`backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests/**`） | 直さない（`WebApplicationFactory` を使わず、`ServiceCollection` を自分で組む。起動のスレッドとの競走が無い） |
| `ApplicationStarted` を使う他の本番コード・試験 | **無し**（本件の 1 箇所だけ） |

- 規則 10（この変更で新たに誤りになる自分の記述）: 試験の旧コメント「`_ = factory.Services; // ホストを開始する（ApplicationStarted が発火する）`」は
  「発火したので計上も済んだ」と読める。コメントを「コールバックの完了は待たない」へ改めた。
- 規則 11（窓）: 窓は「起動完了の通知の発火（＝MeterProvider が立った後）」から「0 の計上の完了」まで。
  プローブは 増える側＝計上を 500ms 遅らせる（窓が広がる。本番の意味は同じなので**緑が正しい**）／
  減る側＝計上をホストの開始前へ前倒し（窓の前へ出る。**赤が正しい**）、あわせて計上を消す（**赤が正しい**）。
  形は 3 通り。升目は「期待どおりか」。採用の行は 3 列とも実測、他の行は注記のとおり。

  | 形 \ プローブ | 計上を 500ms 遅らせる（緑が正） | 計上を開始前へ前倒し（赤が正） | 計上を消す（赤が正） |
  | --- | --- | --- | --- |
  | 前の端だけ（発火を待って即 ForceFlush。**是正前**） | ✗ 赤（paper・moomoo とも決定的。実測） | ✓ 赤（#942 の PR の変異試験。本件では再測せず） | ✓ 赤（同左） |
  | 後の端だけ（計上の完了を `MeterListener` で見届けるだけで、OTel の exporter を見ない） | ✓ 緑 | ✗ 緑（前倒しでも門は開く。実測: 前倒しの変異で門を通って exporter の表明で落ちた） | ✓ 赤（門が開かない） |
  | **両端**（発火を待ち、計上の完了も見届けてから、OTel の exporter で「立った後に聞かれた」ことを見る。**採用**） | ✓ 緑 | ✓ 赤（exporter が空） | ✓ 赤（門が開かない） |

## 検証

- 変異の実測（是正後・`--filter ProtectiveStopDriftAdopterCompositionTests`）:
  - 計上のコールバックを 500ms 遅らせる → 5/5 緑（是正前は paper・moomoo とも赤）。
  - 計上を消す → T-10-786 の 2 件が門で赤（`primed.Wait(...)` が False）。
  - 計上をホストの開始前（`builder.Build()` の直後）へ動かす → T-10-786 の 2 件が exporter の表明で赤（`{empty}`）。
  - （③の是正の確認）11 秒眠る exporter の reader を前に挟む → 是正後は緑、旧形は赤。
- 並列・高負荷（`dotnet test backend/Services/OrderExecutionService/Tests -- xUnit.MaxParallelThreads=16`、CPU を回し続ける負荷 6 本〔4 コア〕と
  他エージェントの並行ビルド・試験の下）:
  - 是正前: 全 815 件 × 8 回（並列度は既定）のうち 1 回 T-10-786 が赤。
  - ②だけ是正（`ForceFlush(10_000)` のまま）: 全 822 件 × 22 回は 22/22 緑だったが、rebase 後の全 828 件 × 22 回で **1 回赤**（③）。
  - ②③とも是正: 下の「最終の連続実行」（PR 本文に回数と結果を書く）。
- `dotnet build backend/backend.slnx`（0 警告）・`dotnet format --verify-no-changes`・文書検査器。

## 射程外

- 本番コードの変更（不要と判断。上記）。
- `MeterCapture` への「待つ」API の追加（本件 1 箇所だけの用途。同型の必要が 2 回目に出たら共通化する）。
