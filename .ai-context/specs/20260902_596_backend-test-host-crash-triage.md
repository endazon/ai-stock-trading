---
title: backend-test の「テスト失敗 0 件なのに exit 1」を実ログで切り分け、失敗の可読性を CI へ組み込む
type: work
status: review
related_ids: [NFR, ADR-0001, IADR-0208, IADR-0257]
author: claude (Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/02_non-functional.md
---

# 作業仕様書: backend-test の exit 1 の切り分け（#596）

> Issue [#596](https://github.com/endazon/ai-stock-trading/issues/596)（`backend-test` のシャードが
> 「テスト失敗 0 件なのに exit 1」で落ちる・テストホストの異常終了と推定）を対象とする。
> 設計判断は [IADR-0277](../adr/IADR-0277_backend-test-failure-legibility.md)。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（CI の非機能。NFR）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: ADR-0001（リポ構成・スタック）
- 関連 IADR: [IADR-0208](../adr/IADR-0208_ci-pr-latency-reduction.md)（CI レイテンシと必須チェック。
  シャーディング＝決定 10、変更領域の自前判定＝決定 12）、
  [IADR-0257](../adr/IADR-0257_ci-test-sharding-lpt-by-scan.md)（LPT 分配・シャード数 4）

## 目的・背景

### issue #596 の主張

`backend-test` のシャードが **テストを 1 件も失敗させていないのに exit 1** で落ちる現象が 3 回
観測された、というもの。根拠として挙がっていたのは次の 4 点である。

1. ログに `Failed` のテストが 1 行も無く `Test Run Successful` だけが並ぶ
2. `Build FAILED` なのに `0 Warning(s) / 0 Error(s)`
3. カバレッジ添付の件数＝担当プロジェクト数（＝全アセンブリが走り切っている）
4. ジョブ末尾で orphan の `dotnet` が 3 件 ＋ `VBCSCompiler` が 1 件強制終了されている

そこから「テストの失敗ではなく**テストホストプロセスの異常終了**」と推定し、
仮説として (1) メモリ枯渇 (2) `WebApplicationFactory` 系の後始末 (3) シャード構成の変化との相関
が挙げられていた。

### 🔴 実測の結論: 前提が誤っている

**3 回とも、実際にテストが 1 件失敗している。** 失敗したテスト名・アサーションメッセージ・
スタックトレースはすべてログに出ていた。テストホストは異常終了していない。

再実行（attempt 2）のログを添えて 3 本すべての attempt 1 を全文で取得し、突き合わせた結果を
下表に示す。

## 対象範囲

- 対象: `.github/workflows/ci.yml` の `backend-test` ジョブの**失敗時の可読性**（計装）、
  ならびに #596 の 3 仮説の実測による棄却の記録
- 対象外:
  - テストの skip / 無効化 / quarantine（🔴 禁止）
  - ジョブ名（`backend-test` / `build-and-test`）の変更（必須チェック名。
    `scripts/check-workflow-job-refs.js` が検査する）
  - `-m:1`（プロジェクト間並列の停止）の投入。恒久策として遅く、かつ**原因が並列度ではない**
    ことが判明したため入れない
  - 失敗したテスト 2 種の修正。いずれも既に develop で解消済み（後述）

## 実測 1: 3 回の失敗の共通点

取得元は `gh api repos/endazon/ai-stock-trading/actions/jobs/<id>/logs`（**attempt 1**。
`gh run list` は再実行後の conclusion しか返さないため、`actions/runs/<id>/attempts/1/jobs` から
job id を引く必要がある）。

| # | PR | run / job（attempt 1） | シャード | 失敗したテスト | アサーション |
| --- | --- | --- | --- | --- | --- |
| 1 | #587 | 33198144355 / 98940484935 | `backend-test (2)` | `MarketMonitorService.Api.Tests.Contracts.MonitorContractFixtureTests.監視設定応答がフロントの契約フィクスチャと一致する` | 契約フィクスチャの乖離。`4 行目で相違: 期待(フィクスチャ)="monitoredSymbols": [ / 実応答="monitoredSymbols": []` |
| 2 | #591 | 33232430367 / 99047480228 | `backend-test (2)` | `TradeDecisionService.Api.Tests.LlmPurposeWiringTests.費用計上イベントも層別の用途で発行される` | `Expected ... to be equal to {"trade-decision-screening", "trade-decision"}, but {"trade-decision", "trade-decision-screening"} differs at index 0.` |
| 3 | #594 | 33237115480 / 99059907940 | `backend-test (1)` | `TradeDecisionService.Tests.LlmPurposeWiringTests.費用計上イベントも層別の用途で発行される` | 同上（VSA 移送で名前空間が `.Api.Tests` → `.Tests` へ変わっただけで同一テスト） |

**共通点**:

- 3 回とも `Failed: 1` が出ている（`Total tests` の内訳に確かに現れる）
- 3 回とも失敗は**アサーション失敗**であり、ホストのクラッシュではない
  （`The active test run was aborted` / `Test host process crashed` / `OutOfMemory` /
  終了コード 134・137・139 は**全文検索して 1 件も無い**）
- 2 / 3 は**同一のテスト**（#597 で原因特定済み・PR #598 で修正済み）

### なぜ「失敗 0 件」に見えたのか（機序）

`dotnet test <solution>` はプロジェクトを**並列**に走らせ、各 VSTest のコンソールロガーが
**同じ標準出力へ同期せずに書く**。その結果:

- **各プロジェクトのサマリ塊が他プロジェクトの逐次出力に割り込まれ、塊自体が分断される。**
  #594 の実ログ（行番号は取得した生ログのもの）:

  ```
  1427:  Total tests: 537
  1428: Test Run Failed.        ← 割り込みで塊の内側へ入り込んでいる
  1429:      Passed: 536
  1430:      Failed: 1
  ...
  1540: Test Run Successful.    ← ログ末尾に最も近いサマリは別プロジェクトのもの
  1541: Total tests: 229
  1542:      Passed: 229
  ```

  🔴 **issue #596 が引用した `Test Run Successful. / Total tests: 229 / Passed: 229` は、
  失敗したプロジェクトとは別のプロジェクトのサマリである。** 末尾から読むと必ずこれに当たる。

- 失敗テストの `Failed <name>` 行・`Error Message:` も、前後を他プロジェクトの `Passed` 行に
  挟まれて 1 行だけ現れる（#591 では 1269 行目、#594 では 1237 行目）。1,600〜1,700 行の
  ログを目視で追うと**見落とす**。

### 仮説の棄却（実測）

| # | 仮説 | 判定 | 根拠 |
| --- | --- | --- | --- |
| 1 | 並列実行時のメモリ枯渇（OOM でホストが落ちる） | **棄却** | 失敗はアサーション失敗であり、そのテストは**結果を報告して終わっている**。OOM / exit 137 / `aborted` / `crashed` の語がログに 1 件も無い |
| 2 | `WebApplicationFactory` 系の後始末（orphan `dotnet` 3 件が示唆的） | **棄却** | 🔴 **同じ run の緑のジョブ（33237115480 attempt 2 / job 99060230849 = `backend-test (1)` success）にも、`dotnet` 3 件 ＋ `VBCSCompiler` 1 件のまったく同じ orphan 掃除が出る。** MSBuild のノード再利用プロセスと Roslyn コンパイラサーバが残るのは**毎 run の定数**であり、失敗の信号ではない |
| 3 | シャード構成（LPT 分配）の変化との相関 | **趣旨は正しい／ただし別の意味で** | ホストを落としたのではなく、**同時に走る顔ぶれが変わったことで既存の順序依存テストが露出した**（#597 が実測込みで特定済み）。#587 は契約フィクスチャの乖離で、そもそも並列とは無関係 |

`Build FAILED` に対し `0 Warning(s) / 0 Error(s)` になるのも異常ではない。VSTest の
MSBuild ターゲットは**テストの失敗を戻り値で伝える**のであって MSBuild のエラーイベントとして
上げないため、MSBuild のサマリが 0 件を数えるのは仕様どおりである。

## 実測 2: 失敗したテスト 2 種の現況

| 失敗 | 現況 |
| --- | --- |
| `LlmPurposeWiringTests.費用計上イベントも層別の用途で発行される`（#591 / #594） | **修正済み**。[#597](https://github.com/endazon/ai-stock-trading/issues/597) が「Wolverine の送信記録の並びに順序を要求しているのが defect」と特定し、PR #598（`00458d7c`）が当該 1 行を `Should().Equal(...)` → `Should().BeEquivalentTo(...)` へ変えている。順序の検出力は `handler.Purposes.Should().Equal(...)` 側に残る |
| `MonitorContractFixtureTests.監視設定応答がフロントの契約フィクスチャと一致する`（#587） | フィクスチャ乖離であり、当該 PR の作業中に解消されている（同 PR の attempt 2 は緑） |

→ **テスト側に本 PR で直すべき欠陥は残っていない。** #596 が疑ったテストホストの異常終了は
そもそも起きていない。

## 設計

残る実在の欠陥は**可読性**である。名前の付いた失敗が 1,600 行の並列ログに埋もれ、
**「失敗 0 件なのに落ちた」という誤診で issue が 1 本立った**。同じ誤診は分配が変わるたびに再発する。

### 決定（詳細は IADR-0277）

1. **TRX ロガーを足し、失敗時のみ「実際に落ちたテスト」を機械で名指しする。**
   `--logger trx` を付け、失敗時に `scripts/summarize-test-failures.js` が結果ディレクトリの
   全 `*.trx` を読んで、失敗テストの完全修飾名とメッセージを**ジョブログの末尾**と
   `$GITHUB_STEP_SUMMARY`（PR の Checks 画面から 1 クリック）へ出す。
   並列の割り込みを受けない構造化データを読むので、**塊の分断とは無関係に必ず正しい**。

2. 🔴 **「TRX を 1 つも読めなかった」を成功にしない（fail-loud）。**
   0 件検査で「失敗テストなし」と出すのは、#596 の誤診そのものを機械で再生産する形である。

3. 🔴 **「テストは全部通ったのにジョブが落ちた」を、要約器が明示的に名指しする。**
   TRX を読めて失敗 0 件だったときだけ「テスト失敗 0 件 ＝ ホスト異常終了・ビルド失敗・
   後続ステップの失敗を疑え」と、次に見るべき成果物（blame の Sequence.xml / dump）を挙げる。
   **これが #596 で人手 1 日かかった切り分けを 1 行にするもの**である。

4. **メモリと並列度を実行の前後で記録する**（`free -m` / `nproc`）。仮説 1 は棄却済みだが、
   出力は 2 行で費用がなく、再発時に**推定ではなく実測で**棄却できる。

5. **`--blame-crash --blame-hang --blame-hang-timeout 10m` を付ける。**
   クラッシュもハングも起きなければ成果物を書かないため定常の費用はほぼゼロで、
   **本物のホスト異常終了（＝ TRX に失敗 0 件のまま exit≠0）が起きたときだけ**
   Sequence.xml / dump が残る。#596 が求めた保険をここで掛ける。

6. **`--diag` の VSTest 診断ログ**を付け、**失敗時のみ** artifact として上げる。
   費用は CI 実測で確認する（本仕様書「実測 4」へ追記）。

7. 🔴 **`-m:1` は入れない。** 恒久策として遅く、原因が並列度でないことが判明したため。

8. 🔴 **ジョブ名は変えない。** `backend-test` / `build-and-test` は必須チェック名である。

### 変更しないもの

- `--verbosity normal` は据え置く（逐次の `Passed` 行を消すと、要約器が壊れたときに
  参照できる生の記録まで失う）
- `Verify report count` の不変条件（1 テストプロジェクト = 1 カバレッジレポート）
- カバレッジ artifact のパス（`cov/**/coverage.cobertura.xml`）。TRX と blame の成果物は
  同じ `cov/` に落ちるが、glob が拾わないので混ざらない

## 受け入れ基準

- [ ] #596 の 3 観測すべてについて、attempt 1 の実ログから**失敗したテスト名**を特定して表にした
- [ ] 3 仮説それぞれを実測で判定した（とくに orphan プロセスが**緑のジョブにも出る**ことを実ログで示した）
- [ ] `backend-test` が失敗したとき、**落ちたテストの完全修飾名**がジョブログ末尾と実行サマリに出る
- [ ] TRX を 1 つも読めなかった場合は要約器が**落ちる**（0 件検査で緑にしない）
- [ ] TRX は読めたが失敗 0 件の場合、「ホスト異常終了を疑え」と次に見る成果物を名指しする
- [ ] `free -m` / `nproc` が実行の前後で記録される
- [ ] クラッシュ／ハング時に blame の成果物が、失敗時のみ artifact として残る
- [ ] ジョブ名 `backend-test` / `build-and-test` を変更していない
- [ ] テストの skip / 無効化 / quarantine を行っていない
- [ ] `-m:1` を入れていない
- [ ] `check-workflow-job-refs.js` / `scripts.test.js` / `check-trace-blocks.js` /
      `check-adr-index-sync.js` が通る
- [ ] CI を 3 回以上回し、各 run のシャードごとの結果と `free -m` の実測値を本書へ追記した
- [x] 要約器を**実 TRX**（緑・赤の両方）で実走確認し、TRX 名の衝突時に取りこぼさないことも実測した

## テスト方針

- `scripts/summarize-test-failures.js --self-test` が、要約器そのものの振る舞いを固定する。
  **否定形を含める**:
  - TRX が 0 件 → **失敗する**（「検査したつもりで何も見ていない」を作らない）
  - TRX はあるが失敗 0 件 → 「ホスト異常終了を疑え」の分岐へ入り、成果物名を挙げる
  - 失敗 1 件以上 → 完全修飾名とメッセージを出す（複数 TRX・複数失敗をまたいで集約する）
  - 壊れた XML の TRX は**読み飛ばさず報告する**（黙って件数から抜けると 0 件検査へ退行する）
- 自己試験は `scripts/scripts.repo.test.js` から呼ぶ（CI の `scripts-tests` ジョブが拾う。
  `static-checks` へ新ステップを足さない＝ IADR-0208 のレイテンシ予算を増やさない）

## 実測 3: ローカルでの実 TRX 検証（要約器の実地確認）

自己試験（合成フィクスチャ）だけでは「VSTest が実際に吐く形を読めるか」を保証しないため、
`dotnet test` を実走させて確かめた（プローブ用の一時テスト・一時 solution は確認後に削除した）。

| # | 実測したこと | 結果 |
| --- | --- | --- |
| 1 | 実 TRX（緑・27 件合格）を読む | 件数を正しく表にし、**失敗 0 件の分岐**（ホスト異常終了を疑う案内）へ入った。終了コード 0 |
| 2 | 実 TRX（赤・故意に順序違いのアサーションを 1 件足した）を読む | 完全修飾名 `AiStockTrading.Shared.Kernel.Tests.TmpProbeTests.一時的な失敗プローブ`・メッセージ・スタックトレースを出し、`::error::` 注釈も出た。終了コード 1。**#591 / #594 と同じ形のアサーション**で確認している |
| 3 | 3 プロジェクトの solution を 1 回の `dotnet test` で走らせる | TRX が **3 件**別々に出た（プロジェクトごとに 1 件） |
| 4 | 🔴 **TRX のファイル名が衝突したらどうなるか**（既定の名前は `<user>_<machine>_<yyyy-MM-dd_HH_mm_ss>_<tfm>.trx` で、**同一秒に終わった 2 プロジェクトは同名になり得る**） | 2 プロセスを同時に同じ結果ディレクトリへ走らせて実測 —— VSTest は `..._net10.0.trx` と **`..._net10.0[1].trx`** を作り、**上書きしない**。取りこぼしは起きない |

> 上表の #4 は「要約器が読む母集合が黙って減る」経路の確認である。上書きされていたら
> **失敗したプロジェクトの TRX が消えて「失敗 0 件」と報告し得た** —— 本件が直そうとしている
> 誤診そのものを新しい経路で作るところだった。

## 実測 4: CI 実走の記録

<!-- PR 作成後に追記する -->

## 計画書との差異

- 差異: なし

## 未決事項

- なし
