---
title: 壁時計どうしの競争で合否が決まる試験の棚卸しと、同型 10 本の一括変換（#885）
type: spec
status: accepted
related_ids: [FR-01, FR-04, FR-06, FR-17, NFR, IADR-0031, IADR-0168, IADR-0364, IADR-0366, IADR-0379]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01 情報収集 / FR-04 取引判断 / FR-06 報告)
---

# 仕様書: 壁時計どうしの競争で合否が決まる試験の棚卸しと同型の一括変換（#885）

## 起点

- **#885**（集約 issue）。個別の是正はすでに 3 本走っている:
  **#885 本体＝PR #896（IADR-0364・マージ済み）／#900＝PR #906（IADR-0366・本作業中にマージ）／
  #901＝PR #907（IADR-0367・本作業時点で未マージ）**。
- 本作業の射程は #885 に残った **(a) 全走査と一覧、(b) 検査器を足すかの判断、(c) 明らかに安全な変換だけの実施**である。

## 判別の軸（#885 が自ら訂正した軸を使う）

**「テストが遅延を自分で渡したか」ではない。「合否を決める 2 つの時刻が、どちらも壁時計か」である**
（#885 の 2026-09-19 コメントが旧い軸を明示的に撤回している。旧い一覧はその軸で絞ったもので、使えない）。

形は 3 つに分かれる。

| 形 | 内容 | 例 |
| --- | --- | --- |
| **(a)** | 実時間の打ち切り（`HttpClient.Timeout` / `CancelAfter` / gRPC deadline）が、実時間の遅延（`Task.Delay` / `DelayingHandler`）と競争し、**どちらが先に発火したかで判定が変わる** | #885 / #900 / #901 |
| **(b)** | `StartAsync → await Task.Delay(N) → StopAsync` のあと「その N の中で仕事が起きた」ことを**肯定形**で表明する | 本リポジトリには**該当なし**（常駐の試験はすべて `TaskCompletionSource` の同期点を使っていた） |
| **(c)** | 待ちヘルパの**上限が仕事量に対して小さい** | Wolverine の `IServiceProvider.ExecuteAndWaitAsync`（**既定 5 秒**） |

🔴 **否定形（「起きなかった」）の表明は同型ではない。** CPU 枯渇下ではむしろ通りやすくなる。
#885 はこの取り違えで `CollectionPollingServiceTests` を誤って名指しし、訂正している。**本書は呼び出し元と
表明を読んでから分類した**（grep の行だけで推定しない）。

## 母集合（走査と除外理由）

**記憶で挙げず走査した**（`traceability.repo.md` 規則 9）。

| 走査 | 手段 | 結果 |
| --- | --- | --- |
| 候補ファイル | `grep -rlE "Task\.Delay\(|Thread\.Sleep\(|CancelAfter\(|\.Wait\(|WaitAsync\(|Stopwatch|Timeout *=|Deadline|SpinWait" --include=*.cs backend` のうちテスト樹形 | **89 ファイル** |
| 呼び出し元と表明の読み取り | 上記 89 を 4 分割し、別々の文脈で全件読んだ（計 **437 箇所**の一致を呼び出し元まで確認） | 下表 |
| 形 (a) の機械的な絞り込み | 同一ファイル内で「有限の実時間の打ち切り」＜「実時間の遅延」になっているもの | **632 テストファイル中 12 件** |

🔴 **母集合の取り方を 1 度誤り、是正した。** 最初の機械走査はパスに `/Tests/` を含むものだけを見ており、
`backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests/`（ディレクトリ名が `Tests` で終わるが
`Tests` ではない）を**取りこぼしていた**。実際にそこへ 1 件あった（`ClientCredentialsTokenProviderTests`）。
**ディレクトリ名が `Tests` で終わるもの**へ引き直した。

## 形 (a) の 12 件と扱い

| # | ファイル | 是正 |
| --- | --- | --- |
| 1 | `InformationCollectionService/Tests/.../HttpCostControlGateTests.cs` | **PR #907**（#901。作業中に未マージ）。本 PR では触らない |
| 2 | `ReportService/Tests/.../HttpReportNarrativeDrafterTests.cs` | 🔴 **PR #906（#900）は同ファイルの 1 ケースだけを是正しており、残り 3 ケースは同型のまま残っていた**（下記）。**本 PR で残りを是正する** |
| 3 | `MarketMonitorService/Tests/.../HttpPositionStoreTests.cs` | **本 PR** |
| 4 | `ReportService/Tests/.../HttpBuyInInferenceRecordSourceTests.cs` | **本 PR** |
| 5 | `ReportService/Tests/.../HttpOpenDUptimeSourceTests.cs` | **本 PR** |
| 6 | `ReportService/Tests/.../HttpPeriodFillSourceTests.cs` | **本 PR** |
| 7 | `ReportService/Tests/.../HttpStageProgressSourceTests.cs` | **本 PR** |
| 8 | `TradeDecisionService/Tests/.../HttpDailyPolicyProviderTests.cs` | **本 PR** |
| 9 | `TradeDecisionService/Tests/.../HttpLlmCompletionClientTests.cs` | **本 PR** |
| 10 | `TradeDecisionService/Tests/.../HttpSizingContextProviderTests.cs` | **本 PR** |
| 11 | `TradeDecisionService/Tests/.../HttpWatchlistProviderTests.cs` | **本 PR** |
| 12 | `TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests/ClientCredentialsTokenProviderTests.cs` | **本 PR** |

12 件のほとんどは **`HttpClient.Timeout = 50 ms` 対 `DelayingHandler(2〜5 秒)`** という同一の形である。
**同型の複製が 12 ファイルにあった。**

### 🔴 作業中に #906 が develop へマージされ、母集合を引き直した

`HttpReportNarrativeDrafterTests.cs` は **#906 のマージ後も走査に当たり続けた**。読み直すと、
**#906 が是正したのは同ファイルの「種別ごとの打ち切り」1 ケースだけ**で、同型が 3 ケース残っていた。

| 残っていたケース | 形 |
| --- | --- |
| `タイムアウト_応答遅延_は_プレースホルダ散文へ倒す` | 50 ms 対 2 秒 |
| `タイムアウト縮退のログに種別と発火した秒数を残す` | 種別ごと 500 ms 対 30 秒 |
| `呼び出し側のキャンセルは縮退せず伝播する` | 即時キャンセル 対 20 秒／30 秒（余裕は大きいが実時間どうし） |

**本 PR でこの 3 件も同じ形へ移す。** 走査はファイル単位のため、**「1 ケース直したファイル」を
『済み』と数えると取りこぼす**——issue の記述ではなく走査の出力で数え直した結果である。

**是正後、走査に当たるのは `HttpCostControlGateTests.cs`（PR #907）の 1 件だけになった**（実測）。
［2026-09-23 追記 / #885］**その #907 も本作業中に develop へ入り、取り込み後の走査は 0 件になった**（633 テストファイル）。

## 変異注入で分けた「赤くなる」と「黙って検査しなくなる」

遅延を勝たせた状態（`Timeout` を 30 秒・遅延を 0）を作って走らせ、**実測で**分けた。

| 判定 | テスト |
| --- | --- |
| 🔴 **赤くなる**（遅延が勝つと表明が破れる） | `HttpStageProgressSource` / `HttpOpenDUptimeSource` / `HttpDailyPolicyProvider` / `HttpWatchlistProvider` / `HttpSizingContextProvider` |
| **緑のまま**（＝打ち切り経路を**黙って検査しなくなる**） | `HttpPositionStore` / `HttpPeriodFillSource` / `HttpLlmCompletionClient` / `ClientCredentialsTokenProvider` |

後者も是正する。**偽の赤は出ないが、代わりに「タイムアウトを検査している」という名前が嘘になる**
（本 PR の変換後は、上限を無効化する変異で 5 件すべてが赤になることを実測した）。

## 設計（是正の形）

**PR #907（IADR-0367）の形をそのまま複製する。** 新しい判断を持ち込まない。

1. `DelayingHandler` を **`NeverRespondingHandler`**（`Task.Delay(Timeout.InfiniteTimeSpan, ct)`＝
   打ち切られるまで決して応答しない）へ置き換える。「上流が上限より遅い」という前提は変わらず、
   **応答が上限に勝つ余地だけが消える**。
2. 呼び出しを `Guard`（30 秒）で包む。**合否の基準ではなく**、打ち切りが効かないときに黙って固まらないための上限。
3. **応答ではなく打ち切りで終わったこと**を、ハンドラ側の `TaskCompletionSource` で観測して確定させる。
4. **上限値（50 ms）は動かさない。** 固定したい性質は「上限に達したら安全側へ倒す」であり、その秒数ではない。

## 検査器を足すか（#885 の (b)）

**足すべきである。ただし本 PR では足さない。** 判断と理由は [IADR-0379](../adr/IADR-0379_wall-clock-timeout-test-discriminator-and-never-responding-upstream.md) 決定 4。
要点:

- 条件は満たしている。**同型の事故は実際に 3 回観測されている**（#885 / #900 / #901）。
- 形 (a) は**機械的に検出できる**（同一ファイル内で 有限の打ち切り ＜ 実時間の遅延）。試作の的中率は
  **632 ファイル中 12 件**で、**12 件すべてが真陽性**（偽陽性 0）。
- ［2026-09-23 追記 / #885］🔴 **本作業中に #907 も develop へマージされた。**
  取り込み後に走査し直したところ、**本ブランチでは検出 0 件**（633 テストファイル）になった。
  すなわち **allowlist を空にして入れられる状態が、本 PR のマージで揃う。**
- **それでも本 PR には入れない。** 新しい CI ゲートの追加はワークフローと必須 check 名の表
  （`docs/ai-workflow.md`）に及び、**是正の diff とは別に読まれるべき変更**である。
  追随 issue **#921** に、検出規則・的中率・allowlist の形・着手条件を記載して起票済みである。

## 射程外（一覧には載せるが本 PR では直さない）

- 形 (c) の **`IServiceProvider.ExecuteAndWaitAsync`（既定 5 秒・約 30 テスト）**。
  `scripts/check-tracked-session-timeout.js`（#357 / IADR-0168）は素の `TrackActivity()` だけを禁じており、
  **この overload は素通りする**。#357 は 5 秒をスケジューリング遅延だけで超えた実測（6 秒）を持つ。
  **別 issue（#922）**（原因も是正も本件と違う）。
- gRPC の `GrpcAssumptionsClientIntegrationTests`（#885 本体・PR #896 で是正済み。残る経過時間の上界は
  変異検出のために必要で、IADR-0364 決定 5 が据え置きを決めている）。
- `backend/Tests/AiStockTrading.IntegrationTests/`（`Category=Integration`。既定 CI から除外され、
  `DisableTestParallelization = true` のため #885 の観測条件に現れない）。
- 上界だけが寛大な群（`MMApiMoomoo*ReconnectTests` / `FifoOpendStdinWriterTests`）は**低リスク**として記録に留める。

## 受け入れ基準

- [ ] 形 (a) を走査で列挙し、**PR #907 が直す 1 ファイルを除く 11 ファイル**を変換する
- [ ] 変換後、走査に当たるのが `HttpCostControlGateTests.cs`（#907）の 1 件だけになる
      （#907 のマージ取り込み後は **0 件**）
- [ ] 変換したテストがすべて緑
- [ ] 変換で**弱まっていない**ことを変異注入で示す（上限を無効化したら赤くなる）
- [ ] `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` / `dotnet format --verify-no-changes`
- [ ] 一覧を #885 へコメントとして投稿する

## テスト方針

テストケースは増やさない。既存ケースの**観測方法**を変え、打ち切りの観測を 1 本ずつ足す。

## 計画書との差異

なし（試験の観測方法の変更であり、製品コードは 1 行も変えていない）。

## 未決事項

- 検査器の投入は追随 issue **#921**（#907 はマージ済み。残る条件は本 PR のマージのみ）。
- 形 (c) の 5 秒の窓は別 issue **#922**。
