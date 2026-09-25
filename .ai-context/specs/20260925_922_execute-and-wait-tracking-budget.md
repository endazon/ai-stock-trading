---
title: ExecuteAndWaitAsync の既定 5 秒の追跡窓を 30 秒予算へ寄せ、形 (c) を検査器で止める（#922）
type: spec
status: accepted
related_ids: [NFR, IADR-0168, IADR-0379]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs: []
---

# 仕様書: `ExecuteAndWaitAsync` の既定 5 秒の追跡窓（#922）

## 起点

- **#922**（#885 の全走査で見つかった形 (c)。IADR-0379 の残余リスク「形 (c) は手つかず」）。
- 予算の意味（ハングの検知であって性能の表明ではない）と単一情報源 `TrackedSessionBudget`（既定 30 秒・環境変数で上書き可）は
  **IADR-0168 が既に決めている**。本作業はその射程を `IServiceProvider` の短縮入口へ広げるだけで、新しい判断は持ち込まない
  （細目は IADR-0168 と IADR-0379 へ日付つき追記で残す。新しい IADR は作らない）。
- メタ作業（試験の待ち方と検査器）であり、計画の非機能要件表に当たる番号は無い → 無採番 `NFR`。

## 問題

Wolverine の短縮入口 `IServiceProvider.ExecuteAndWaitAsync(Func<Task> action, int timeoutInMilliseconds = 5000)` は、
素の `TrackActivity()` と同じ **5 秒の壁時計**で打ち切る。`check-tracked-session-timeout.js` は `TrackActivity` の綴りだけを見るため
この overload は素通りする。`WebApplicationFactory` を使うテストは `IHost` を直接持たず、`factory.Services.ExecuteAndWaitAsync(...)` を書いていた。

## 作るもの

| 項目 | 内容 |
| --- | --- |
| 予算つきの入口 | `WolverineTrackingExtensions.ExecuteAndWaitForTestAsync(this IServiceProvider, Func<Task>)`。**同じ overload** を `timeoutInMilliseconds: TrackedSessionBudget.Current` で呼ぶだけ（追跡の範囲・例外の扱い・返す `ITrackedSession` は不変） |
| ミリ秒への変換 | `TrackedSessionBudget.ToTimeoutMilliseconds(TimeSpan)`。端数は切り上げ・下限 1・`int.MaxValue` で頭打ち（予算を縮める向きに倒さない） |
| 呼び出しの置換 | 下の 35 か所を `Services.ExecuteAndWaitAsync(` → `Services.ExecuteAndWaitForTestAsync(` へ（引数・表明は不変）。`using AiStockTrading.TestSupport.Messaging;` を足す |
| 検査器 | `scripts/check-wall-clock-timeout-tests.js` の `SHAPES` に形 (c) を 1 行足す（#921 が用意した拡張点）。allowlist は空のまま |
| 一覧 | `scripts/README.md` の `wall-clock-timeout-tests` 行に形 (c) を書き足す |

### どちらの検査器を広げるか

#922 本文は `check-tracked-session-timeout.js` の拡張を案に挙げていたが、**`check-wall-clock-timeout-tests.js` の `SHAPES` へ足す**。
①#921 がこの拡張のために形ごとの表を用意した（母集合・コメント潰し・allowlist・報告の書式を共用できる）
②形 (c) は `TrackActivity` の綴りでは検出できず、**受け手の式を読む**別の検出規則が要る（綴りの禁止とは性質が違う）
③母集合（ディレクトリ名が `Tests` で終わる配下）が形 (c) の置き場と一致する。
`check-tracked-session-timeout.js` は素の `TrackActivity()` の禁止として従来どおり残す（`services.TrackActivity()` もこちらが止める）。

## 検出規則（形 (c)）

Wolverine の待ちヘルパ `ExecuteAndWaitAsync` / `ExecuteAndWaitValueTaskAsync` / `InvokeMessageAndWaitAsync` /
`SendMessageAndWaitAsync` / `PublishMessageAndWaitAsync` の呼び出しで、**受け手（`.` の左の式）が予算の中でなければ**落とす。

- 名前は `TrackedSessionConfiguration` の同名メソッドと区別できない。**受け手の式を左へ読む**（識別子・`.`・`?.`・括弧・型引数・改行をたどる）。
- 予算の中とみなすのは: 受け手の式が `TrackActivityForTest(` を含む／受け手が単独の識別子で、同じファイルでそこへ
  `TrackActivityForTest(` を含む式が代入されている（`var tracking = host.TrackActivityForTest(); … tracking.ExecuteAndWaitAsync(…)`）。
- `timeoutInMilliseconds:` を明示した呼び出しも落とす（予算の単一情報源〔環境変数で上書き可〕を迂回する。IADR-0168 決定 2）。
- `ExecuteAndWaitForTestAsync` は別名なので当たらない。コメント・文字列は `stripComments` で潰してから見る（#921 と同じ）。

## 母集合（規則 9〜11）

### 規則 9: 走査してから挙げる（develop `08c53b91`）

| 走査 | 手段 | 結果 |
| --- | --- | --- |
| 素の `IServiceProvider` 入口 | `git grep -c "Services\.ExecuteAndWaitAsync" -- '*.cs'` | **15 ファイル・35 か所**（下表） |
| 検査器の形 (c)（置換前） | `node scripts/check-wall-clock-timeout-tests.js` | **35 件**。上の grep と**ファイル・行まで一致**（待ちヘルパ 5 種の呼び出しは全 329 か所〔`git grep -oE` で `.<名前>Async(` を数えた〕のうち残り 294 か所は予算つきの入口経由で 0 件＝偽陽性 0） |
| `IHost` の短縮入口（`host.InvokeMessageAndWaitAsync(msg)` 等） | 同上 | **0 件** |
| テスト樹形の外 | `git grep -nE "(ExecuteAndWait\|MessageAndWait)(ValueTask)?Async" -- '*.cs'` のうち `Tests/` 以外 | 本 PR の入口の実装とその注記だけ |

| ファイル | か所 |
| --- | --- |
| `ConfigurationService/Tests/Features/Assumptions/AssumptionsEndpointsTests.cs` | 1 |
| `CostControlService/Tests/Features/CostControl/CostControlEndpointsTests.cs` | 1 |
| `ReportService/Tests/Features/Reports/ReportConfirmActorTests.cs` | 2 |
| `ReportService/Tests/Features/Reports/ReportEndpointsTests.cs` | 1 |
| `RiskManagementService/Tests/.../AdoptPositionDrift/PositionDriftAdoptionEndpointTests.cs` | 3 |
| `RiskManagementService/Tests/.../CancelPositionClose/PositionCloseCancellationEndpointTests.cs` | 1 |
| `RiskManagementService/Tests/.../ClearGoodFaithViolations/GoodFaithViolationClearEndpointTests.cs` | 2 |
| `RiskManagementService/Tests/.../ClosePosition/PositionCloseEndpointTests.cs` | 1 |
| `RiskManagementService/Tests/.../RequestStageTransition/StageTransitionApproverTests.cs` | 1 |
| `RiskManagementService/Tests/Features/RiskManagement/StageGateEndpointsTests.cs` | 4 |
| `RiskManagementService/Tests/Hosted/MaintenanceMarginEvaluationServiceTests.cs` | 5 |
| `RiskManagementService/Tests/Hosted/WithdrawalEvaluationServiceTests.cs` | 9 |
| `RiskManagementService/Tests/StopOutReentryWiringTests.cs` | 2 |
| `TradeDecisionService/Tests/LlmPricingWiringTests.cs` | 1 |
| `TradeDecisionService/Tests/LlmPurposeWiringTests.cs` | 1 |
| **計** | **35** |

- **#922 本文の数え（約 30 テスト）は転記しない**（規則 10）。本文の表に無かった 11 か所
  （`AssumptionsEndpointsTests` / `CostControlEndpointsTests` / `StageTransitionApproverTests` / `StopOutReentryWiringTests` ×2 /
  `GoodFaithViolationClearEndpointTests:99` / `WithdrawalEvaluationServiceTests:176,190` / `MaintenanceMarginEvaluationServiceTests:99,120,135` /
  `StageGateEndpointsTests:183`）も同じ窓であり、否定形（`BeEmpty()` 等）も含めて全部寄せる（否定形も `TimeoutException` で落ちる余地は同じ）。

### 除外とその理由

- **除外なし。** 否定形の呼び出しも寄せる。allowlist も空のまま。
- 他の open PR（#956 / #968 / #970 / #972 / #973）が触るテストファイルとの重なりは **0 件**（各 PR のファイル一覧で確認）。

### 規則 10: この変更で新たに誤りになる自分の記述

- `check-wall-clock-timeout-tests.js` の冒頭注記「形 (c)（#922 の担当）は検出しない」→ 書き換えた。出力文言の「壁時計どうしの競争」は形 (a) 固有だったため、
  形 (a) 固有の説明は形 (a) の検出があるときだけ出す。
- IADR-0379 の残余リスク「形 (c) は手つかず」／IADR-0168 の決定 3「入口は `TrackActivityForTest()` の 1 つだけ」→ **日付つき追記**で記録する（本文は書き換えない）。索引行も同じ。
- 確定済みの作業仕様書（`20260923_885_…` / `20260925_921_…`）の「形 (c) は #922」→ point-in-time の記録であり書き換えない。
- `docs/ai-workflow.md` の必須 check 名 → 変わらない（既存の `static-checks` の step が同じスクリプトを走らせる）。

### 規則 11: 窓を扱う是正か

**当たらない。** 規則 11 は「照会の前後で値が変わり得る量」（業務データの時間差）を扱う是正の規則である。本件の 5 秒 → 30 秒は
**待ちの上限**であり、`TrackedSession` は**すべての活動が終わった時点で戻る**（上限まで待たない）。
よって合格するテストの所要は変わらず（否定形も活動が無ければ直ちに終わる）、変わるのは「本当にハングしたときに落ちるまでの時間」（5 → 30 秒）だけである
（IADR-0168 が記録済みの代償）。

## 受け入れ基準

- [x] 35 か所すべてを予算つきの入口へ寄せ、表明・引数・対象メッセージを変えない（壁時計の sleep を足さない）
- [x] `check-wall-clock-timeout-tests.js` に形 (c) を足し、allowlist 空で develop 相当の木が 0 件
- [x] 置換前の木で 35 件を検出し、`TrackActivityForTest()` 経由の呼び出しを誤検出しない（自己試験で両方向を固定）
- [x] `TrackedSessionBudget.ToTimeoutMilliseconds` の変換規則をテストで固定する
- [x] `dotnet build`（0 警告）・対象 6 テストプロジェクト・`dotnet format --verify-no-changes`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑

## テスト方針

- `scripts/scripts.repo.test.js`: 形 (c) の赤（#922 前の実形・短縮入口 5 種・改行つき受け手・型引数・`timeoutInMilliseconds:` 明示・代入していない変数）と
  緑（`TrackActivityForTest()` 連鎖・改行・代入した変数・`ExecuteAndWaitForTestAsync`・コメント・文字列）、模擬ツリーの CLI 終了コードと案内文。
- `TrackedSessionBudgetTests`: 既定 30 秒が 30000 ms に直る／端数の切り上げと下限 1 ms／`int.MaxValue` での頭打ち。
- 置換した 35 か所は既存テストそのものが入口の実走になる。

## 計画書との差異

なし（試験の待ち方と検査器であり、製品コードは変えない）。

## 残余リスク

- 検出は受け手の式の字面に依る。`TrackActivityForTest()` を返すヘルパメソッド経由の受け手（`Track(host).ExecuteAndWaitAsync(…)`）は赤になる
  （安全側の誤検出。現に 0 件）。逆に、予算つき入口を代入した変数へ後から別の値を代入する形は見逃す（現に 0 件）。
- 予算を伸ばしただけであり、30 秒を超える飽和は再び落ちる（IADR-0168 の残余リスクと同じ）。
