---
title: IADR-0168 Wolverine テストハーネスの壁時計予算を単一情報源にし、素の入口を機械的に禁止する
type: adr
status: Accepted
related_ids: [NFR, IADR-0129]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
---

# IADR-0168: TrackedSession の壁時計予算の単一情報源化

## コンテキストと課題

[#357](https://github.com/endazon/ai-stock-trading/issues/357) は「ソリューション全体を並列実行したときだけ 1 件落ちる」flaky test を報告していた。**失敗したテスト名が記録されておらず**、まず特定が要るとされていた。

### 特定（本作業で実測）

ソリューション全体を **15 回**反復実行し、**15 回目で再現**した。

```
Failed AiStockTrading.CostControl.Infrastructure.Tests.LlmCostIncurredConsumerTests.別_MessageId_はそれぞれ計上される [6 s]
System.TimeoutException : This TrackedSession timed out before all activity completed.
Activity detected:
| bcd99ef6-… | LlmCostIncurred |  81 ms | Sent     |
| bcd99ef6-… | LlmCostIncurred | 154 ms | Received |
   at Wolverine.Tracking.TrackedSession.AssertNotTimedOut()
```

**#357 が報告した `RiskManagementService.Worker.Tests` / `ReportService.Worker.Tests` とは別のプロジェクトである。** これが決定的な手がかりだった——**原因はプロジェクト固有ではない**。

### 原因

`Wolverine.Tracking.TrackedSession` は**壁時計で打ち切る**。既定は **5 秒**であり、リポジトリ内に `.Timeout(...)` の指定は **1 件も無かった**（全 131 か所が既定）。

失敗時のログでは `Sent`（81 ms）と `Received`（154 ms）は記録されているのに **`Executed` が窓内に現れていない**。メッセージは失われておらず、**ハンドラの完了が 5 秒以内にスケジュールされなかった**だけである。ソリューション全体の並列実行では 9 プロジェクトのホストが同時に動き、CPU が飽和する。

**ロジックの不具合ではなく、テストハーネスの壁時計への暗黙の結合である。** #357 が挙げた仮説のうち「実時刻依存」が当たりだった。

### 因果の確認（決定的な実験）

再現率 1/15 の事象を「出なくなった」で確かめることはできない。**予算そのものを動かして、同じ失敗を意のままに出し入れした。**

| 予算 | 結果（`CostControlService.Infrastructure.Tests`・31 件） |
| --- | --- |
| **0.05 秒**（極小） | **Failed: 4** —— すべて `System.TimeoutException : This TrackedSession timed out`（flake と同一の型・同一の文言） |
| **30 秒**（既定） | **Passed: 31** |

**同じテスト・同じコード・同じ機械で、予算だけが結果を決めている。** これで「壁時計の予算が操作変数である」ことが確定する。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A** | `Thread.Sleep` / リトライで凌ぐ | **棄却。** #357 の受け入れ基準が明示的に禁じている。原因を残したまま症状を隠す |
| **B** | 並列度を下げる（`dotnet test -m:1` 等） | **棄却。** #357 は「**並列度に依存しない形へ**」と要求している。並列度を下げるのは**依存したまま条件を避ける**ことであり、CI の実行時間も伸びる |
| **C** | 131 か所へ `.Timeout(...)` を書き足す | **棄却。** **次に書かれるテストに効かない。** `TrackActivity()` は Wolverine の標準 API であり、次に書く人は素直にそれを呼ぶ——同じ flake が静かに戻る |
| **D** | **予算つきの入口を 1 つ用意し、素の入口を機械的に禁止する** | **採用。** 単一情報源＋機械的強制。`check-banned-libraries.js` / `check-banned-settled-cash-sources.js` と同じ形 |

## 決定

### 決定1: 予算は「ハングの検知」であり「性能の表明」ではない

**この区別が本 IADR の要である。**

| | 意味 | 適切な値 |
| --- | --- | --- |
| 性能の表明 | 「N 秒以内に終わること」がテストの主張 | 厳しく |
| **ハングの検知**（本件） | 「終わらないなら永久に待たず落とす」 | **十分に緩く** |

**どのテストも「5 秒以内に完了すること」を要求していない。** 5 秒は Wolverine の既定がたまたま入っていただけである。

**ハングの検知に厳しい値を入れると、検知したいもの（永久に終わらない）ではなく、検知したくないもの（遅い）を拾う。** したがって**予算を伸ばすことは糊塗ではない**——意味づけに合った値へ直すことである。

### 決定2: 単一情報源 `TrackedSessionBudget`（既定 30 秒・環境変数で上書き可）

既定は **30 秒**（Wolverine の既定 5 秒の 6 倍。実測の失敗は 6 秒）。`AST_TEST_TRACKING_TIMEOUT_SECONDS` で上書きできる——**より遅い実行環境へ、コード改変なしで追随する**ため。

**読めない値では既定へ倒す**（0・負数・非数・空文字）。倒す先を既定にするのは、**設定ミスで予算が 0 になると全テストが即座に落ちる**ためである。**環境変数の誤りでテストが壊れるより、上書きが効かないほうが失敗モードとして軽い。**

小数点は**不変文化**で解釈する（ロケール次第で `0.5` が `5` と読まれると予算が 10 倍になる）。

### 決定3: 入口は `TrackActivityForTest()` の 1 つだけ。素の入口は CI で禁止する

`scripts/check-tracked-session-timeout.js` が、C# の**コードとしての** `TrackActivity` 参照を検出して落とす。

- **コメント・文字列リテラルは誤検出しない**（`check-banned-settled-cash-sources.js` と同じ規則）。**禁止の理由を散文で書けなくなっては、検査が自分の目的を殺す。**
- **例外は 1 ファイルだけ** —— 予算を適用している当の実装（`WolverineTrackingExtensions.cs`）。ここまで禁じると入口そのものを書けない。
- **検査自身の効きをテストで固定する**（正・否定形の両方向＋「許可ファイルを外すと実ツリーで検出される」）。**本検査が効かない方向に壊れると、CI は緑のまま flake だけが戻る。**

### 決定4: テストの表明・対象は変えない

差分は `host.TrackActivity()` → `host.TrackActivityForTest()` の**入口の置換のみ**（131 か所）。表明・待ち方・対象メッセージはいずれも不変である。`DoNotAssertOnExceptionsDetected()` 等の連鎖もそのまま書ける（返り値は Wolverine の `TrackedSessionConfiguration` そのもの）。

## 理由

- **flake は CI ゲートを構造的に無効化する。** 確率的に赤くなる CI は「また flake だろう」という再実行の習慣を育て、**本物の退行も同じ反応で流される**。#343 がカバレッジ floor と写像検査を CI に入れた直後であり、そのゲートが確率的に無意味になるのは受け入れられない。
- **決定3（機械的禁止）が無ければ、この修正は 1 回限りの掃除で終わる。** 131 か所を直しても、次の 1 か所が同じ穴を開ける。**再発の経路が「標準 API を素直に呼ぶ」である以上、規律では止まらない。**
- **決定1 の区別を書き残さないと、この予算はいずれ「遅すぎる」として縮められる。** 30 秒という値そのものより、**それが何のための値か**が失われることのほうが危険である。

## 結果

- **#357 の失敗モードが塞がる。** 予算が操作変数であることは実験で確定しており、飽和時の遅延（実測 6 秒）に対して 30 秒は十分な余裕がある。
- **次に書かれるテストにも効く**（決定3）。
- **予算が 1 か所に集まる。** 環境ごとの調整がコード改変を伴わない。

### 悪い影響（記録する）

- **本当にハングしたテストは 5 秒ではなく 30 秒かけて落ちる。** **ハングは稀であり flake は常時である**——取るべきトレードオフはこの向きだが、代償ではある。
- **予算を伸ばしただけであり、飽和が極端になれば再び超え得る。** 30 秒を超えるスケジューリング遅延は、テスト環境そのものの問題として別に扱う（本機構は隠さず `TimeoutException` で落ちる）。
- **検査は `TrackActivity` という綴りに依存する。** Wolverine が将来別名の入口を足せば検出できない。
- **文字列の走査は完全な C# 字句解析ではない。** 補間文字列の穴（`$"…{ ここはコード }…"`）は #447 のレビュー指摘を受けて**コードとして扱う**ようにしたが、**生文字列リテラル（`"""…"""`）は扱っていない**。同型の既存検査（`check-banned-settled-cash-sources.js`）も同じ簡略化を採っている。
- **本作業が塞いだのは壁時計依存だけである。** #357 が挙げた他の仮説（共有状態・固定ポート）が別に存在する可能性は否定できない——**ただし本作業で観測された失敗はすべて `TimeoutException` であった**。
- **新しいテスト専用プロジェクトが 2 つ増えた**（`AiStockTrading.TestSupport.Messaging` と同 `.Tests`）。

## 関連

- [IADR-0129](IADR-0129_wolverine-messaging-topology.md)（MassTransit → Wolverine 移行。`TrackActivity` はここで導入された）
- 起点 issue: [#357](https://github.com/endazon/ai-stock-trading/issues/357)。関連: [#343](https://github.com/endazon/ai-stock-trading/issues/343)（退行防止テスト基盤）・[#344](https://github.com/endazon/ai-stock-trading/issues/344)（全面再実装）
- 作業仕様書: [20260807_357_flaky-tracked-session-timeout](../specs/20260807_357_flaky-tracked-session-timeout.md)
