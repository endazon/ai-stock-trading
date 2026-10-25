---
title: 経路B（values-local）の Stage 0 の記録の欄へ、オーナー承認（2026-10-13）の値を入れる（#1219）
type: spec
status: accepted
related_ids: [FR-15, FR-04, ADR-0033, ADR-0054, ADR-0064, IADR-0318, IADR-0498, IADR-0524]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md（決定 3・決定 5）
  - planning:projects/ai-stock-trading/07_adr/ADR-0064_llm-assignment-to-5-5-family-and-stage0-rerun.md（決定 3）
---

# 経路B（values-local）の Stage 0 の記録の欄へ、オーナー承認（2026-10-13）の値を入れる（#1219）

## 起点

- #1219 のコメント 2026-10-12T20:23Z（PoC の実測と見積り）と 2026-10-12T22:41Z（**オーナー承認 2026-10-13**: 12 銘柄・多数決 1 回・承認額 1,700 円・窓 2026-07-01〜2026-10-09）。
- **［2026-10-13 追記・オーナー裁定］窓を 2026-09-15〜2026-10-09 へ縮めた。** 承認（2026-10-12T22:41Z）の前提は「窓は 2026-07-01 から」だった。〜2026-09-14 は当時の監視銘柄と確定日報が無く、as-of 入力を再構成できないため、オーナーが裁定で起点を 09-15 へ移した。銘柄・多数決 1 回・トークン量は変えない。
- 欄は #1309（`41cac9ea`。作業仕様書 `20261011_1219_stage0-recording-local-values`）で置いた。本作業は値を入れるだけである。

## 入れる値（承認どおり）

| サービス | 欄 | 値 |
| --- | --- | --- |
| trade-decision | `Stage0Recording__Enabled` | **`false`**（承認値は入れるが、配備しただけでは記録は走らない。有効化は PoC が手順に沿って一度だけ行う。下の「現状」） |
| trade-decision | `Stage0Recording__From` / `To` | `2026-09-15` / `2026-10-09`（平日 19 日。裁定で 07-01 起点から縮めた） |
| trade-decision | `Stage0Recording__Symbols__0..11` | TSLA・META・GOOGL・AMZN・AAPL・SMCI・PLTR・NVDA・COIN・MSTR・MARA・MSFT（すべて `UnitedStates`） |
| trade-decision | `VoteCount` / `ApprovedVoteCount` | `1` / `1` |
| trade-decision | 本判断 入力 / 出力 | `3478` / `425` |
| trade-decision | 一次 入力 / 出力 | `2269` / `582` |
| trade-decision | `OutputPath` | `/tmp/stage0/records.json`（#1219 の手順の既定） |
| trade-decision・report | `ApprovedEstimateJpy` | **`437.28`**（下の「承認額の写し方」） |
| backtest | `Backtest__Stage0__Symbols__0..11` | 記録と同じ 12 銘柄（下の「backtest 側の要否」） |

## 承認額の写し方（🔴 1,700 をそのまま入れると承認ゲートを通らない）

- 承認ゲートは**一致**で見る: `decimal.Round(approvedJpy, 2) != decimal.Round(estimate.TotalJpy, 2)` なら `NotApproved`（`Stage0DecisionRecorder.cs` の `ValidateApproval`）。「承認額 ≥ 見積り」ではない。
- 見積り（`Stage0RecordingBudget.Estimate` と同じ式・values-local の単価）。窓 2026-09-15〜2026-10-09: 平日 19 日 × 12 銘柄 = 228 判断時点。
  本判断 228 ×（3.478 × 0.327 ＋ 0.425 × 1.637）＝ 417.931068 円、一次 228 ×（2.269 × 0.0164 ＋ 0.582 × 0.0819）＝ 19.3520472 円、**合計 437.2831152 円**。呼び出し 456 回。
  （参考: 承認時の窓 2026-07-01〜 では平日 73 日・876 判断時点・1,680.0877584 円・1,752 回。PoC の「約 1,681 円」は切り上げ。）
- したがって `1700` を書くと記録器は `NotApproved` で LLM を呼ばずに戻る（試験で固定）。#1219 の手順 3 も「ログの `{TotalJpy}` をそのまま」と書いている。
- 🔴 coordinator からは「`ApprovedEstimateJpy` は 1700 のまま（上限として承認済み）。ただしゲートが見積りとの近さを要求する作りなら報告せよ」と指示を受けた。ゲートは一致を要求するため、1700 では記録が走らない。**値は見積りの写し `437.28` とし、その旨を報告する**（1700 へ戻すのは 2 行の変更で済むが、その場合 #1308 の後も `NotApproved` で記録は 0 回になる）。
- **見積り 437.28 円 ≤ オーナーの承認額 1,700 円**であり、超過の上限（1 判断ぶん ＝ 一次 1 ＋ 本判断 1 ≒ 1.92 円）を足しても 439.2 円で 1,700 円に収まる。よって `437.28` はオーナーの承認の範囲内である。月報 §7 の分母（report）も同値にする。

## backtest 側（`Backtest:Stage0`）の要否

| 欄 | 要否 | 根拠 |
| --- | --- | --- |
| `Strategy=recorded-replay` | 既に入っている（変更なし） | `values-local.yaml` backtest 節 |
| `Symbols` | **要る（12 銘柄へ揃える）** | 再生は記録の銘柄集合と評価ユニバースの**集合一致**を要求する（`Stage0ReplayEvaluation.SameSymbols`）。AAPL 1 銘柄のままだと `RecordingMismatch`。既存試験 `経路Bの記録の対象銘柄と_backtest_の評価銘柄は同じ集合である` も赤になる |
| `EvaluationEnd` | **要る（`2026-10-09`）** | 窓の終端は既定で「今日」、再生は `記録の To ≥ 窓の終端` を要求する（`Stage0ReplayEvaluation.Validate`）。空のままでは 2026-10-10 以降のどの評価も `RecordingMismatch`。#1324（IADR-0528 決定9）で終端を構成できるようになった |
| `LookbackDays` | **要る（`365` → `24`）** | 窓は `[終端 − LookbackDays, 終端]`。365 のままだと起点 2025-10-09 が記録の From より前で `RecordingMismatch`、カットオフ以前の足で `DataCutoff` も未充足。2026-10-09 − 24 日 ＝ 2026-09-15 ＝ 記録の From |
| `IntervalSeconds` | 入れない（86400） | 記録を差し込んだ後に早めたいときだけ PoC が一時的に縮める（#1219 の手順 5） |
| `Recording__Path` | 入れない（空） | 記録を取り出して backtest の Pod へ差し込むときに入れる（#1219 の手順 5） |

記録の窓は承認で決まり、渡す日に依存しないため、`EvaluationEnd` と `LookbackDays` は今入れる。

（経緯: 最初の版では「評価の窓の終端は常に今日で、`To=2026-10-09` の記録は再生できない」を発見として残していた。#1324 で終端が構成できるようになり、上の 2 欄で解消した。）

## 現状（#1308 は実装済み。develop `6977ff8e` をマージした）

- as-of 入力の基底の供給は実装済みである（#1308 / IADR-0528。values-local の trade-decision の `OrderExecution__BaseUrl`・`Stage0AsOf__*`）。日足は `Backtest__BarData__Provider=order-execution`（#1326）。
- 🔴 #1308 がそろった今は、`Enabled=true` の版を配備するとその起動で記録が LLM を呼び（承認額の範囲）、Pod が再起動するたびに上書きする。**そのため `Enabled=false` で入れる**（coordinator 指示 2026-10-13。AI レビューの 🟡 を受けて）。
- 有効化は PoC が次の順序で行う（values-local の注記に書いた）。
  1. 少額の通し試験: AAPL 1 銘柄 × 2026-09-15〜09-19 に縮めた組で、見積りを承認値に写して回す。
     - 2026-09-15 は火曜、09-19 は土曜なので、平日は 4 日・8 回・**7.6716336 円**（7.67）である。
     - 指示は「9.59 円の組」だったが、9.59 円は平日 5 日（例 09-15〜09-21）の額である。窓を正として 7.67 円と書き、報告で確認を仰ぐ。
  2. 結果の確認（記録の件数・実効モデル・費用計上 `purpose=stage0-recording`）。
  3. この承認値の組で一度だけ `true`。
  4. 取り出したら `false` へ戻し、`ApprovedEstimateJpy`（trade-decision）を空へ戻す。

## SEC EDGAR の CIK が AAPL の 1 件だけであることの影響（coordinator の確認依頼 2026-10-13）

結論: **AAPL 以外の 11 銘柄の (b) は「再構成不可」にならない。CIK は足さない。** 根拠はコードの次の箇所である（`6977ff8e` 時点）。

| 箇所 | 挙動 |
| --- | --- |
| `Stage0AsOfComposition.cs:93-99` | SEC の源は「UserAgent があり、CIK が 1 件以上ある」ときに 1 つだけ組む。銘柄ごとには組まない。AAPL の CIK 1 件で組まれる。 |
| `AsOfReferenceFeeds.cs:126-143`（`SecEdgarAsOfFilingFeed.FetchAsync`） | 構成した全 CIK の提出一覧を引き、`Ticker` が判断の銘柄と一致する文書だけを返す（`:139`）。CIK の無い銘柄（例 TSLA）には**空の一覧**を返し、`null` は返さない。`null` になるのは取得の失敗だけである（`:133-136`）。 |
| `AsOfReferenceAssembler.cs:84-87, 101-104` | `FailedSources` に入るのは、必須の源が未構成のとき（`:86`）と、取得が `null` のとき（`:103`）だけである。空の一覧は失敗に数えない。 |
| `AsOfReferenceAssembler.cs:15`・`BaseAsOfDecisionInputProvider.cs:69` | `IsComplete`（`FailedSources` が空）なら、(b) を再構成不可と申告しない。 |
| `Stage0AsOfExternalSourcesTests.cs:199` | 既存試験が「構成した CIK の銘柄ではない MSFT は空」を固定している。 |

- したがって 11 銘柄の判断は「SEC の開示が無かった」として記録され、Stage 0 の判定母集団に残る。
- 本番の KB も、収集の CIK は AAPL の 1 件だけである（`values-local.yaml` の `Collection__Source__SecEdgar__Ciks__0`）。11 銘柄の開示は本番の判断にも渡っていない。記録へだけ 11 銘柄の CIK を足すと、記録の入力が本番より厚くなり、本番と同じ入力で再現するという前提から外れる。そのため足さない（values-local の注記「本番の KB に無い開示は記録にも渡さない」と同じ判断）。
- 付記: AAPL の CIK の取得が失敗すると、その判断時点では銘柄によらず `null` が返り（`:133-136`）、12 銘柄すべての (b) が「取れなかった源」になる。失敗は覚えず、次の判断時点で撃ち直す。
- CIK を足さないので、SEC の対応表（company_tickers.json）は取得していない。

## 母集合（規則 6・9）

- `Stage0Recording__` を values 2 本で引いた: values-local は trade-decision と report（`ApprovedEstimateJpy`）。values.yaml（本番）は触らない。
- `Backtest__Stage0__Symbols` を全追跡ファイルで引いた（`git grep`）: values-local と `.github/workflows/helm.yml:768`（キーの在否だけを見る。ラベルに「AAPL」とある）。→ ラベルを銘柄名なしへ直す。
- values-local のコメント「経路B は AAPL 1 銘柄」「欄の既定」を引き直し、今回の値に合わせる（規則 10）。
- 除外: `docs/blocked-tasks.md` B-7 は記録がまだ走っていないため更新しない（#1309 と同じ扱い）。`docs/tests/FR-10_risk-controls-tests.md` の AAPL は記録器の単体試験の題材で、values と無関係。

## 受け入れ基準と試験（`Stage0RecordingLocalProfileTests`）

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | values-local の欄が承認値で束縛される（12 銘柄・期間・トークン量・承認値・出力先）。🔴 `Enabled` は `false` | `経路Bの記録の欄は承認値で埋まっている` |
| 2 | 承認値の組の見積りは 437.2831152 円・456 回（平日 19 日）で、オーナーの承認額 1,700 円を超過上限込みで超えない | `承認値の組の見積りはオーナーの承認額_1700_円に収まる` |
| 3 | 🔴 承認値の組で `Enabled` だけを試験側で `true` にし、基底の供給（外部の口はスタブ）の下で走らせると承認ゲートを通る（`Completed`）。見積り 437.2831152 円が承認値 437.28 と一致する | `経路Bの承認値の組で有効にすると承認ゲートを通る` |
| 3b | 通し試験の組（AAPL × 2026-09-15〜09-19）の見積りは 8 回・7.6716336 円 | `通し試験の組の見積りは_7_67_円である` |
| 4 | 🔴 否定形: 承認額に 1,700 をそのまま入れると `NotApproved` で LLM を呼ばない | `承認額に_1700_をそのまま入れると承認ゲートを通らない` |
| 5 | 記録の対象銘柄と backtest の評価銘柄が同じ集合（既存） | `経路Bの記録の対象銘柄と_backtest_の評価銘柄は同じ集合である` |
| 6 | 本番 values.yaml は不変（既存） | `本番の取引判断の記録の欄は従来のままで_記録は無効である` |
| 7 | backtest の評価の窓 [2026-09-15, 2026-10-09] が記録の窓と一致する | `経路Bの_backtest_の評価の窓は記録の窓と一致する` |
| 8 | #1326 の配線の試験は残る | `経路Bの配線の欄で基底の供給が選ばれ_欄を埋めて承認すれば記録が作られる` |
| 9 | values-local の描画が通り、helm.yml の検査が緑 | `helm template -f values-local.yaml` |

## 範囲外

- 記録の実行・取り出し・backtest への受け渡し（PoC が行う）。
- `Stage0AsOf__SecEdgar__Ciks` は収集と同じ AAPL の 1 件のまま（#1326 の範囲）。

## ［2026-10-13 追記］#1308 の develop 反映（`6977ff8e` まで）に合わせた仕上げ

- develop をマージした（rebase しない）。衝突 3 件は #1326 の配線を保って解いた。
  - `values-local.yaml`: #1326 の `Backtest__Stage0__EvaluationEnd` の欄と、本 PR の 12 銘柄の両方を残した。
  - `helm.yml`: #1326 の検査（日足の源は `order-execution`・moomoo 直結なし）を採り、ラベルの「（AAPL）」だけを外した。
  - 試験: #1326 の `経路Bの配線の欄で基底の供給が選ばれ_欄を埋めて承認すれば記録が作られる` はそのまま残した。
- **基底の供給は実装済みになった。** values-local の注記を次の内容へ直した。
  - 基底の供給は実装済みである。
  - `Enabled=true` の版を配備すると、その起動で記録が LLM を呼び、記録を上書きする。
  - 最初は少額の通し試験から始める。
  - その後の指示で `Enabled` を `false` にし、有効化の順序を注記した（上の「現状」）。
- 試験「承認値のまま走らせると…記録は 0 件」は前提が崩れたので、`経路Bの承認値の組のまま走らせると承認ゲートを通る` へ置き換え、その後 `経路Bの承認値の組で有効にすると承認ゲートを通る`（`Enabled` を試験側で `true`）へ改めた。
  - 基底の供給の外部の口は試験用のスタブにした。
  - 見るのは `Completed` になることと、見積り 437.2831152 円（437.28）が承認値と一致することだけである。
- **評価の窓の終端（#1324 / IADR-0528 決定9）を固定した。**
  - 設定値: `Backtest__Stage0__EvaluationEnd=2026-10-09`、`LookbackDays=24`。窓 [2026-09-15, 2026-10-09] は記録の窓と一致する。
  - 終端だけ固定して `LookbackDays=365` のままにすると、窓の起点 2025-10-09 が記録の From より前になる。その場合は `RecordingMismatch` になり、カットオフ以前の足で `DataCutoff` も未充足になる。そのため 2 欄をそろえて入れた。
  - 記録の窓は承認で決まっており、渡す日には依存しない。そのため、渡すときまで待つ理由が無くなった。
  - `Recording__Path` は記録を取り出してから入れる（空のままにした）。
  - 試験 `経路Bの_backtest_の評価の窓は記録の窓と一致する` で固定した（旧試験「コードの既定と同値」は置き換えた）。
- 前段の「発見（To と評価窓）」は #1324 で解消できる形になり、本追記の設定で解消した。
