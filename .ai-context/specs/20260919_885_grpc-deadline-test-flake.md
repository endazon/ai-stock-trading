---
title: gRPC deadline 結合試験の Calls 観測を決定的にする（#885 のフレーク）
type: spec
status: accepted
related_ids: [FR-17, IADR-0063, IADR-0264, IADR-0284, IADR-0328, IADR-0331, IADR-0364]
author: claude (Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-17 全体前提条件の一元管理)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: gRPC deadline 結合試験の `Calls` 観測を決定的にする（#885）

## 起点

- **#885**（test）。`dotnet test backend/backend.slnx`（全ソリューション並列実行）で
  `GrpcAssumptionsClientIntegrationTests.提供側が黙れば_構成した_deadline_で安全側既定へ倒れる` が
  まれに落ちる。**単体実行では 100% 緑。**

```
Expected host.Stub.Calls to be 1 because 既定は再試行しない, but found 0 (difference of -1).
```

- 起点 ID: **FR-17**（全体前提条件の一元管理）。当該テストが固定しているのは
  **[IADR-0331](../adr/IADR-0331_assumptions-grpc-transport-and-proto-contract-checks.md) 決定 6**
  （「呼び出し元ごとの timeout / retry が効く」を実 Kestrel h2c の結合試験で固定する）である。
  段の親は IADR-0284 決定 5（段 1）・IADR-0328。
- 起点 ID に `NFR-xx` は採らない。計画の非機能要件は `NFR-01`〜`NFR-18` で、
  **「テストの安定性・保守性」に当たる番号が無い**（実測: planning
  `02_requirements/01_requirements.md` の NFR 表）。無い番号を推測で書かない。

## 🔴 再現の実測（**着手前の実測。再現しなかった事実を含めてそのまま残す**）

### 第 1 巡: 素の全ソリューション実行 ×12（`8107ff33`＝当時の `origin/develop`）

`dotnet test backend/backend.slnx` を逐次 12 回。**当該テストは 12 回とも緑。**

```
 1  成功! -失敗: 0、合格: 145 ... 期間: 33 s - CostControlService.Tests.dll (net10.0)
 2  成功! -失敗: 0、合格: 145 ... 期間: 27 s
 3  成功! -失敗: 0、合格: 145 ... 期間: 11 s
 4  成功! -失敗: 0、合格: 145 ... 期間: 28 s
 5  成功! -失敗: 0、合格: 145 ... 期間: 22 s
 6  成功! -失敗: 0、合格: 145 ... 期間: 10 s
 7  成功! -失敗: 0、合格: 145 ... 期間:  9 s
 8  成功! -失敗: 0、合格: 145 ... 期間: 22 s
 9  成功! -失敗: 0、合格: 145 ... 期間: 22 s
10  成功! -失敗: 0、合格: 145 ... 期間: 14 s
11  成功! -失敗: 0、合格: 145 ... 期間: 14 s
12  成功! -失敗: 0、合格: 145 ... 期間: 15 s
```

全 12 回を通して、Docker 不在で落ちる `AiStockTrading.IntegrationTests` の 8 件**以外**の
失敗は **0 件**であった。

### 第 2 巡: CPU 飽和下の全ソリューション実行 ×1

8 論理 CPU に対しスピンループ 16 本をぶつけた状態で 1 回。
`成功! -失敗: 0、合格: 145 ... 期間: 44 s - CostControlService.Tests.dll` ——**緑**
（無負荷時 9〜33 s に対し 44 s。負荷は効いていた）。
残りの回は中止した（スイート全体が 1 回 >1 時間へ落ち、8/21 アセンブリで 22 分を要したため）。

🔴 **この巡は機序を突けていなかった。** スピンループは CPU を食うだけで、
**スレッドプールの枯渇・JIT・ディスク I/O・ポート確保**が同時に効く形ではない。
報告時の実環境は**並行する `dotnet test` プロセス 8 本**であった。

### 第 3 巡: **並行する `dotnet test` プロセス**の下で ×6（`d8264233`）

別ワークツリー（`.claude/worktrees/wt-885-load`）で 6 本のテストプロジェクトを
`--no-build` で回し続けながら、対象ワークツリーで `dotnet test backend/backend.slnx` を 6 回。

```
=== real run 1 rc=1 ===  成功! -失敗: 0、合格: 145 ... 期間: 44 s - CostControlService.Tests.dll
                         成功! -失敗: 0、合格: 711 ... 期間: 40 s - TradeDecisionService.Tests.dll
=== real run 2 rc=1 ===  成功! -失敗: 0、合格: 145 ... 期間: 31 s / 成功! 711 ... 34 s
=== real run 3 rc=1 ===  成功! -失敗: 0、合格: 145 ... 期間: 15 s / 成功! 711 ... 58 s
=== real run 4 rc=1 ===  成功! -失敗: 0、合格: 145 ... 期間: 22 s / 成功! 711 ... 37 s
=== real run 5 rc=1 ===  成功! -失敗: 0、合格: 145 ... 期間: 15 s / 成功! 711 ... 28 s
=== real run 6 rc=1 ===  成功! -失敗: 0、合格: 145 ... 期間: 13 s / 成功! 711 ... 25 s
```

負荷は効いていた（1 回あたり 4〜10 分。無負荷時は 3〜5 分）。
`rc=1` はいずれも Docker 不在の `AiStockTrading.IntegrationTests` 8 件によるもので、
**それ以外の失敗は 6 回を通して 0 件**であった。

### 再現率

🔴 **0 / 19。**（素の全ソリューション 12 ＋ CPU 飽和 1 ＋ 並行 `dotnet test` 下 6）

報告された再現率は 6 回中 3 回（＝ 50%）である。真の率が 50% なら 0/12 の確率は約 0.02% であり、
**本 PR の実行環境では再現率が違う**と言える。**「再現しなかった」は「起きない」ではない** ——
起票者と別作業（PR #874）の 2 つの独立した観測が実在する。

## 🔴 診断の実測（一時的な計装。最後に外す）

再現しないまま案を選ぶことはできないので、**機序を直接測った。**

計装（`GrpcAssumptionsClientIntegrationTests.cs` へ一時的に入れ、**最後に外した**）:

- `app.Use(...)` ミドルウェアで **HTTP 要求が ASP.NET Core のパイプラインへ到達した時刻**
- `StubAssumptionsService.Get` の**ハンドラ入場時刻**
- クライアント側の **RPC 開始時刻**（`GetCurrentAsync()` の直前）と **総経過時間**

計測は `CostControlService.Tests` の当該テストのみを `--filter` で繰り返し実行して採った
（2 コピーはバイト同一なので片方で足りる）。負荷は**別ワークツリーで 6 本の `dotnet test` を
`--no-build` で回し続ける**形（スピンループではない）。

### 是正前

| 区間 | 無負荷 10 回 | 並行 `dotnet test` 下 20 回 |
| --- | --- | --- |
| RPC 開始 → HTTP 要求が提供側へ到達 | 64.7〜68.1 ms | 66.3〜**764.4** ms |
| 到達 → ハンドラ入場 | 14.3〜15.6 ms | 14.9〜45.4 ms |
| **RPC 開始 → ハンドラ入場（＝1 秒の予算の消費分）** | **79.1〜83.4 ms** | **81.4〜809.7 ms** |

最悪の 1 本（生の計測行）:

```
hostReady->rpcStart=24.0ms rpcStart->reqArrived=764.4ms reqArrived->handler=45.4ms
rpcStart->handler=809.7ms totalElapsed=1118.9ms calls=1 reqArrived=yes
```

🔴 **1000 ms の予算のうち 809.7 ms を、提供側のハンドラに入るまでに食っていた。残余は 190 ms。**
食い切れば `Calls` は 0 のままになる。**仮説は裏付けられた。**
食っている区間は圧倒的に `rpcStart -> reqArrived`（DI 構築・**TCP 接続・HTTP/2 preface**・要求送出）である。

### 是正後（同じ負荷・同じ 20 回）

| 区間 | 並行 `dotnet test` 下 20 回 |
| --- | --- |
| RPC 開始 → HTTP 要求が提供側へ到達 | 50.6〜53.3 ms |
| 到達 → ハンドラ入場 | 14.4〜16.4 ms |
| **RPC 開始 → ハンドラ入場** | **65.4〜69.6 ms** |

🔴 **是正後の最悪値（69.6 ms）が、是正前の最良値（81.4 ms）よりも小さい。** 裾が消えた。

> 🔴 ［2026-09-19 追記 / #885］**「裾が消えた」は撤回する。**
> **20 サンプルで観測されなかったことを「消えた」と一般化していた。** PR #896 の監査が
> **スピンループ 16 本**（8 論理 CPU）という**より強い枯渇**で測り直したところ、
> 無負荷でも範囲は重なり（warm 最大 104.1 ms ＞ cold 最小 97.2 ms）、
> **負荷下では warm-up 後も 988〜991 ms を消費し 1/3 が失敗**した。
> **本 PR の負荷（並行 `dotnet test`）は機序に到達していなかった**（最悪 809.7 ms でも `calls=1`＝成功例）。
> **スピンループのほうが強かった** —— 「スピンは CPU を焼くだけ」という見立ては誤りであった。
> 追試の数値と是正（案 B の併用）は次節。

### 🔴 この計測が案の選択を変えた

**案 A（`TaskCompletionSource` で到達を待つ）だけでは直らない。**
機序は「ハンドラに**入らない**」ことであり、**入らないものは待っても来ない**
（30 秒待って別の理由で赤くなるだけ）。よって**接続確立を予算の外へ出す**ことが本体の是正であり、
案 A はその上に重ねる観測点の確定として採る。詳細は
[IADR-0364](../adr/IADR-0364_grpc-deadline-test-excludes-connect-from-budget.md)。

### 🔴 ［2026-09-19 追記 / #885］案 B の再評価（監査の指摘を受けた追試）

初版は案 B を「2 秒でも同じ競合が起き得る」として退けたが、**これは未検証の推論であった**
（残余を測っていなかった）。**監査と同じスピンループ 16 本**（8 論理 CPU）で、
**1 秒予算と 3 秒予算を別プロセス・交互に 8 回ずつ**（プロセス内の JIT 暖機バイアスを避けるため）
測り直した。**いずれも決定 1 の暖機は適用済み**である。

| 構成した予算 | RPC 開始 → ハンドラ入場（ms） | 予算超過 | 未到達 | 判定 |
| --- | --- | --- | --- | --- |
| **1 秒** | 833.4 / 837.7 / 941.1 / 1009.3 / 1159.6 / 1205.2 / 1287.9 ＋ 未到達 1 | **4 / 8** | **1 / 8** | 🔴 赤が出る |
| **3 秒** | 897.8 / 926.1 / 985.1 / 1012.0 / 1020.8 / 1133.4 / 1186.1 / 1378.2 | **0 / 8** | **0 / 8** | ✅ 最悪でも 1.6 秒の余裕 |

生の計測行（1 秒予算・未到達の 1 本と、待ちが救った 1 本）:

```
budget=1s rpcStart->reqArrived=-1.0ms   rpcStart->handler=-1.0ms   totalElapsed=1522.5ms callsAtReturn=0 callsFinal=0 arrived=NEVER
budget=1s rpcStart->reqArrived=1050.3ms rpcStart->handler=1159.6ms totalElapsed=1538.6ms callsAtReturn=0 callsFinal=1 arrived=yes
budget=3s rpcStart->reqArrived=1267.5ms rpcStart->handler=1378.2ms totalElapsed=3768.5ms callsAtReturn=1 callsFinal=1 arrived=yes
```

**よって案 B を併用する**（[IADR-0364](../adr/IADR-0364_grpc-deadline-test-excludes-connect-from-budget.md) 決定 5）。
構成する deadline を **1 秒 → 3 秒**へ上げ、**経過時間の上界（10 秒）は比例させない**
（比例させると `AddSeconds(30)` の変異を捕まえられない。据え置いた結果、上界は予算の 10 倍から
3.3 倍になった）。

> 🔴 ［2026-09-19 追記 / #885］**この「厳しくなった」を一般化しない。**
> **絶対値をハードコードする変異の検出力は変わらない**（`AddSeconds(5)` / `AddSeconds(8)` は
> どちらの予算でも素通りし、境界は `BeLessThan(10s)` が決める）。**厳しくなったのは比例的逸脱の検出だけ**で、
> `k ≥ 10` → `k ≥ 3.34` になった（実測: `Add(timeout * 4)` は 1 秒予算では緑・3 秒予算では赤）。
> 監査の実測。**「裾が消えた」と同じ過度な一般化を繰り返さないために明記する。**


🔴 **決定 2（待ち）の効き目も訂正する。** 1 秒予算の 8 サンプルのうち **3 本は `callsAtReturn=0` だが
`callsFinal=1`** であった —— **ハンドラが遅れて入り、待ちがそれを拾った**。
待ちが無ければこの 3 本は `found 0` で赤である。**待ちが救えないのは「一度も届かない」1 本だけ**である。

## 対象範囲

- 対象: **2 ファイル**。
  - `backend/Services/CostControlService/Tests/Infrastructure/ExternalServices/GrpcAssumptionsClientIntegrationTests.cs`
  - `backend/Services/TradeDecisionService/Tests/GrpcAssumptionsClientIntegrationTests.cs`
  - 🔴 **この 2 本はバイト同一である**（`diff` の実測: 差分は `using` 1 行と `namespace` 1 行の 2 hunk ＝ 4 行だけ）。
    **片方だけ直すとフレークは `TradeDecisionService.Tests` に残る。**
- 対象外: 製品コード（`GrpcAssumptionsClient` / `AssumptionsClientExtensions`）。
  本件はテストの観測点の問題であり、製品の縮退の向き（IADR-0331 決定 3）は変えない。
- 対象外: 他の実時間依存テストの是正（一覧のみ #885 へコメントで残す。後述）。

## 母集合（走査したファイルと除外理由）

| 走査 | コマンド | 結果 |
| --- | --- | --- |
| 同型のテスト本体 | `grep -rl "提供側が黙れば" --include=*.cs backend` | 2 件（上記 2 ファイル）。両方を対象にする |
| 実時間依存のテスト全般 | `grep -rnE "Task\.Delay\(\|Thread\.Sleep\(\|Stopwatch" --include=*.cs backend` を `/Tests?/` で絞り、`Timeout.Infinite` / `TimeProvider` / `FakeTime` を除外 | 28 箇所。うち**実時間の締切が合否を決める**のは後述の表。**本 PR では直さない**（射程外・#885 へコメント） |
| 当該テストを参照する文書 | `grep -rn "提供側が黙れば" docs/ .ai-context/` | 0 件。追随する文書は無い |
| テスト ID（`T-NN-NNN`）の帯 | `grep -rnoE "\bT-[0-9]{1,2}-[0-9]{1,4}\b" --include=*.cs backend` | 実在する帯は `T-10` / `T-15` / `T-17` / `T-19` のみ。**`CostControlService.Tests` と `TradeDecisionService.Tests` は `T-` ID を 1 つも持たない**。FR-17 帯の最大は `T-17-04`（`AiStockTrading.Shared.Kernel.Tests/Trading/CostCalculatorTests.cs:161`）。**本作業はテストケースを増やさない**（既存ケースの観測を決定的にするだけ）ため、**新規採番はしない** |
| 計画 NFR の該当番号 | planning `02_requirements/01_requirements.md` の NFR 表 | `NFR-01`〜`NFR-18`。**テストの安定性に当たる番号は無い**。よって起点 ID に NFR を採らない |

## 設計

### 何を守るのか（**弱めてはならない性質**）

当該テストのコメントが宣言している性質は 2 つある。**どちらも残す。**

1. **経過時間の assert** —— 「**呼び出し元が構成した秒数で**諦めること。ハードコードされたもっと長い
   deadline ではないこと」。実測として `CallOptions.Deadline` を `AddSeconds(30)` へ変える変異が
   弱い版のテストをすり抜けた実績がある。**消さない・緩めない。**
2. **`Calls` の assert** —— 「**既定は再試行しない**」の対照。同型の否定形が同ファイルの
   `既定では再試行しない` / `恒久的な失敗は再試行しない` / `再試行し切っても例外を出さない` にもある。
   **消さない。**

### 採る形（案 D ＋ 案 A ＋［2026-09-19 追記 / #885］案 B。**計測で決めた**）

1. **接続確立を予算の外へ出す**（本体）。`ResolveAsync` の中で、`IAssumptionsProvider` を呼ぶ前に
   `sp.GetRequiredService<GrpcChannel>().ConnectAsync(...)` を実行する。
   構成した 1 秒が測るものを「黙っている提供側を待つ時間」だけにする。
2. **到達の観測を同期点で確定させる**（案 A を上に重ねる）。スタブは初回呼び出しで
   `TaskCompletionSource` を完了させ、テストは `WaitForFirstCallAsync(30 秒)` で待ってから数える。
   届かなければ `found 0` ではなく理由付きで赤くなる。
3. ［2026-09-19 追記 / #885］**構成する deadline を 1 秒 → 3 秒へ**（案 B。前節の追試で決めた）。
   **経過時間の上界（10 秒）は比例させない。**

- ~~案 B（構成する deadline を 2 秒へ）は**採らない**。2 秒でも同じ競合は起き得るうえ、
  **固定したい値そのものを動かす**。~~
  > 🔴 ［2026-09-19 追記 / #885］**撤回し、案 B を併用する（1 秒 → 3 秒）。**
  > 「2 秒でも起き得る」は**未検証の推論**であった。前節の追試のとおり、暖機後の残余は
  > **1 秒予算で 8 回中 4 回超過・1 回未到達／3 秒予算で 0 回超過**である。
  > 固定したいのは「構成した秒数で諦めること」であって**秒数が 1 であること**ではない。
- 案 C（`Calls` の assert を削る）は**不可**。「再試行しない」の対照そのものである。
- `Calls` のメモリ可視性は**既に手当て済み**であった（`Interlocked.Increment` で書き、
  `Volatile.Read` で読む）。この枝は読みで否定した。
- **製品コードは変えない**（`GrpcAssumptionsClient` / `AssumptionsClientExtensions`）。

## 受け入れ基準

- [x] `CostControlService.Tests` / `TradeDecisionService.Tests` の当該テストが、全ソリューション実行
      6 回以上で全回緑（素 6 回 ＋ 並行 `dotnet test` 下 3 回。さらに案 B 併用後に再実行）
- [x] `CallOptions.Deadline` を `AddSeconds(30)` 相当のハードコードへ変える変異で**両方赤**
      （`but found 30s, 74ms` / `30s, 66ms`）
- [x] 再試行を入れる変異（`Calls` が 2 以上になる形）で**両方赤**（`but found 2`）
- [x] 経過時間の assert と `Calls` の assert が**両方残っている**
- [x] 計装を外し `git status` が clean
- [x] ［2026-09-19 追記 / #885］**待ちが本物の欠落を隠さない**: `deadline` を `AddMilliseconds(1)` へ
      変える変異で、**当該テストを単体実行したとき**は**両方赤**
      （`System.TimeoutException : 提供側のスタブは 30 秒以内に呼び出しを 1 回も観測しなかった。`。5/5）。
      🔴 **このプローブは実行順序に依存する** —— **クラス一括実行（先行テストでプロセスが暖まっている状態）では
      当該テストが緑になる**（監査の実測）。**無条件の言い切りにしない。**
- [x] ［2026-09-19 追記 / #885］案 B を併用し、負荷下で残余が消えることを実測（上表）

## テスト方針

既存のテストケースは増やさない。観測点（`Calls` を読む時点）だけを決定的にする。

## 計画書との差異

- 差異: なし。IADR-0331 決定 6 の「呼ばれた回数を数える」を**覆さず**、数える時点を確定させるだけである。

## 🔴 実装 ADR を書くか（判断と根拠）

**書く。[IADR-0364](../adr/IADR-0364_grpc-deadline-test-excludes-connect-from-budget.md)。**

「テストの安定化だけで設計判断が無い」とは言えない。本件で決めたのは次の 3 点であり、
いずれも**後から読む人が同じ判断に至れなければ壊す**種類のものである。

1. **構成した deadline の予算に何を含め、何を含めないか**（接続確立は含めない）。
   これは IADR-0331 決定 6 の「結合で固定する」の**射程の確定**である。
2. **案 A 単独では直らない**という否定的な知見。これを残さないと、次に同型が出たときに
   また同期点だけを入れて「直った」と言うことになる。
3. **2 コピーは常に同時に直す**（IADR-0264 決定 1 が生んだ複製の帰結）。

## 未決事項

- **是正前の再現率を測れていない**（0/19）。したがって**是正前後の再現率の差は示せない**。
  根拠は①機序の実測②予算の内訳の前後比較③変異注入の 3 つである。**「直った」とは書かない。**
- 同型（実時間の締切・スリープに依存するテスト）の一覧は **#885 へコメントで残す**。本 PR では直さない。
  ~~最も #885 と形が近いのは `InformationCollectionService/Tests/Hosted/CollectionPollingServiceTests.cs:161`
  （`Start → Task.Delay(300) → Stop` して周期が実際に publish したことを assert する）である。~~

  > 🔴 ［2026-09-19 追記 / #885］**上の記述は事実と異なるので取り消す。**
  > `RunBrieflyAsync` の使用箇所は**全リポジトリで 1 箇所だけ**（同ファイル `:150`、
  > `External_モードでは_in_process_巡回を行わない`）で、その assert は
  > **`session.Sent.MessagesOf<InformationCollected>().Should().BeEmpty()`
  > ＝「publish しなかった」ことを確かめる否定的表明**である。
  > **CPU 枯渇下ではこの assert はむしろ通りやすくなり、#885 とは形が逆**である。
  > よって「最も形が近い」「`InformationCollectionService.Tests` の失敗の正体である可能性が高い」は
  > **成り立たない**。**grep の行番号だけを見て呼び出し元を読まずに書いた誤りである**
  > （規約「追随する文書を記憶で挙げない・走査してから挙げる」を自分の推論にも当てるべきだった）。
  > 同ファイルが実時間に依存すること自体は事実だが、**理由は Wolverine の
  > `ExecuteAndWaitAsync` のトラッキング・タイムアウト**であって `Task.Delay(300)` の assert 競争ではない。
  > **#885 のコメントにも同じ誤りを投稿したため、訂正コメントを投稿した。**
- 接続確立が異常に遅いこと自体は、本テストではもう捕まらない。捕まえるなら別のテストを立てる。
- 🔴 ［2026-09-19 追記 / #885］**#885 は閉じない**（PR は `Refs #885`）。是正は部分的である ——
  **十分な CPU 枯渇下では warm-up 後も予算を食い切り得る**（実測 988〜991 ms / 1000 ms〔監査〕・
  最大 1287.9 ms / 1000 ms〔本 PR の追試〕）。案 B（3 秒）で余裕の桁を取ったが、「起きない」保証ではない。
- 🔴 ［2026-09-19 追記 / #885］**残余の失敗の形が変わった** —— 即座の `found 0` から
  **30 秒待ってからの `TimeoutException`** になる。診断は読みやすくなるが **CI の失敗コストは増える**。
