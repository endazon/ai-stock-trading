---
title: 偽 OpenD を CI 内で駆動する moomoo アダプタ結合試験と、手数料・為替スプレッド実績登録の可否判定
type: spec
status: done
related_ids: [FR-05, FR-12, FR-17, FR-20, UC-01, UC-02, ADR-0002, ADR-0019, ADR-0021, ADR-0026, IADR-0016, IADR-0021, IADR-0111, IADR-0144, IADR-0211, IADR-0327, IADR-0334]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0026_short-fee-rate-unit-poc.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 偽 OpenD による moomoo アダプタ結合試験（#754）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-05**（moomoo 証券へ発注し、注文状態を追跡できる）。副次的に FR-12（ペーパー／実弾の
  発注先切替）・FR-17（全体前提条件の登録）・FR-20（段階ゲートの算入先＝`BrokerProvider`）。
- ユースケース（UC）: UC-01（発注）・UC-02（注文追跡）。
- 画面（SC）: なし。
- 関連 ADR: **ADR-0002**（ブローカ選択＝moomoo / OpenD 経由）・**ADR-0019**（moomoo PoC。成果物として
  「moomoo アダプタの結合テスト（SIMULATE 環境）を CI から実行可能な形で残す」を挙げる）・
  ADR-0021（口座種別）・**ADR-0026**（`ShortFeeRate` の単位 PoC。期限 2026-08-31・未着手）。
- 関連 IADR: IADR-0016 / IADR-0056（SIMULATE 限定・実弾を撃たない）・IADR-0111（provider × environment の
  2 軸選択と閂 0）・IADR-0211（接続不達は `BrokerUnavailableException`＝見送り。Rejected へ丸めない）・
  **IADR-0327**（`IMoomooTradeConnectionFactory`＝SDK 差し替え口。本作業が使う唯一の縫い目）・
  IADR-0021（`TradingAssumptions`＝手数料・為替スプレッドの設定点。既定は 0＝未登録）。
- 起点 issue: #754（親 #342 のうち AI で着手できる 2 点の切り出し）。

## 目的・背景

#342 の 2026-09-11 監査は、クローズ条件 6 件のうち 2 件を「AI で着手可能」と判定した。#754 はその 2 件だけを
切り出したものであり、本作業の対象はその**チェックリスト 2 項目に厳密に一致する**。

1. **CI で走る moomoo アダプタ結合試験** ——「既存の moomoo 系試験はすべて単体・写像であり、
   `backend/Tests/AiStockTrading.IntegrationTests/` に OpenD を駆動するものが無い」。偽 OpenD を立て、
   接続 → 口座照会 → SIMULATE 発注 → 約定照会を 1 本で通す。陰性対照（OpenD が拒否 →
   `BrokerUnavailableException`・**発注は 1 度も送られない**）を対にする。
2. **手数料・為替スプレッドの実績登録** —— PoC 観測値（#342 のコメント）を設定／台帳へ写す。

**実弾は撃たない。稼働クラスタには一切触れない。** 偽 OpenD は in-process のフェイクであり、TCP も
Docker も使わない。

## 走査した母集合

規則 1〜6・9・10（`.claude/rules/traceability.md` / `traceability.repo.md`）に従い、**着手前に**引いた。
出力は加工せず（`head` で切らず）生のまま判断した。

### 項目 1（既存の OpenD 駆動試験の不在）

| 軸 | 引き方 | 結果 |
| --- | --- | --- |
| 1 | `git ls-files 'backend/**' \| grep -iE "moomoo\|opend" \| grep -iE "test"` | 24 件。うち発注経路は `MMApiMoomooTradeClientMappingTests`（3 Fact・静的写像のみ）／`MMApiMoomooTradeClientReconnectTests`（4 Fact・接続の張り直し）／`MoomooBrokerAdapterTests`（23 Fact・`IMoomooTradeClient` を fake 化）／`MoomooBrokerOptionsTests` / `MoomooReservationBrokerProbeTests`。**アダプタと SDK 層を同時に通すものは 0 件** |
| 2 | `git ls-files 'backend/Tests/AiStockTrading.IntegrationTests/**'` | 10 件。OpenD を駆動するものは 0 件（Postgres / RabbitMQ / Keycloak のみ） |
| 3 | `grep -rln "IMoomooTradeConnectionFactory" --include=*.cs backend/` | 3 件（宣言 2・利用 1）。利用は `MMApiMoomooTradeClientReconnectTests` だけで、**そのフェイクは private nested クラス**であり他アセンブリから再利用できない |
| 4 | `grep -rln "BrokerFactory.Create" --include=*.cs backend/` | 2 件（`Program.cs` / `BrokerFactoryTests.cs`）。`BrokerFactoryTests` は生成物の型だけを見る |

**帰結**: 「選択（`BrokerSelection`）→ 生成（`BrokerFactory`）→ アダプタ（`MoomooBrokerAdapter`）→ SDK 層
（`MMApiMoomooTradeClient`＋protobuf）→ 接続（`IMoomooTradeConnection`）」を 1 本で通す試験は**実在しない**。
既存の陰性対照（`接続できない間は発注要求がブローカーへ届かない`）は `MMApiMoomooTradeClient` 単体に
掛かっており、**アダプタが `BrokerUnavailableException` を Rejected へ丸めないこと**とは別の命題である。

### 項目 2（手数料・為替スプレッドの登録先と実績値）

| 軸 | 引き方 | 結果 |
| --- | --- | --- |
| 1 | `grep -rniE "為替スプレッド\|手数料.*実額\|FxSpread\|fx_spread"`（拡張子で絞らない） | 設定点は `TradingAssumptions`（`JapanCommission` / `UnitedStatesCommission` の `CommissionSchedule(Rate,Minimum,Cap)`・`FxSpreadRatio`）に単一化されている |
| 2 | `grep -rln "FxSpreadRatio\|CommissionSchedule" backend/ deploy/ infra/` | 15 件。本体は `Shared.Kernel/Trading/TradingAssumptions.cs` / `TradingAssumptionsDefaults.cs` / `CostCalculator.cs`、供給は ConfigurationService（`PUT /assumptions`・版管理つき）、UI は SC-01 設定画面 |
| 3 | `appsettings*.json` / `deploy/helm/**/values*.yaml` / `infra/` / `docker-compose.yml` の全走査 | 手数料・スプレッドの構成キーは**0 件**（設定点は DB の `TradingAssumptions` 1 行のみ。`Fx__Provider` はレート源であってスプレッドではない） |
| 4 | 実績台帳: `TradeExpense` / `TradeExpenseCategory`（`Commission` 4 / `Fee` 5 / `FxCost` 6） | 記録側は実装済み。ただし本番の供給元は `UnsuppliedOrderExpenseSource`（no-op）で**行は 1 件も無い**（`docs/blocked-tasks.md` L740） |
| 5 | **観測値の側から引く**: #342 の全コメント（365 行）を `手数料\|スプレッド\|為替\|fee\|spread\|FX\|commission` で走査 | 手数料・為替スプレッドの**観測値は 1 つも無い**。ヒットするのは `ShortFeeRate`（借株料）と、監査コメント自身の「未着手」の記述だけ |
| 6 | PoC の凍結記録（`.ai-context/specs/20260805_342_moomoo-poc-plan.md` / `IADR-0144` / `20260902_397_342_moomoo-readonly-probes.md`） | 同上。観測されたのは `ShortFeeRate=1.5`・`ImShortRatio=50`・`MmShortRatio=30`・`ShortPoolRemain` のみ |

**除外したものと理由**

- `frontend/src/features/sc01-settings/**` のテスト／E2E フィクスチャ（`rate 0.00055` 等）: **画面の描画確認用の
  作り値であり実測ではない**。実績として引き写すと、出所の無い数値が設定へ入る。
- `BacktestCostModel.SlippageRatio`・計画 §2 の「取引諸費用（SEC Fee / TAF）」: #754 の 2 項目に含まれない
  （前者は構成キーを持たず、後者は `CostCalculator` に欄そのものが無い）。**本作業では触らない。**
- `ShortFeeRate=1.5`: 借株料であり手数料・為替スプレッドではない。加えて**単位が未確定**（ADR-0026 PoC 項目 9）
  であり、実装は 3 経路で明示的に接続を止めている。ここへ写すことは規約違反である。
- `.ai-context/specs/` 配下の過去の記録: 凍結記録であり書き換えない。

## 対象範囲

- **対象**:
  1. `backend/Tests/AiStockTrading.IntegrationTests/` に偽 OpenD（in-process）と、それを駆動する結合試験を追加する。
  2. 手数料・為替スプレッドの実績登録の**可否を判定し、結果を IADR-0334 と本仕様書へ残す**。
- **対象外**: 実 OpenD への接続・稼働クラスタの操作・実弾発注・`ShortFeeRate` の写像・スリッページの構成化・
  「取引諸費用」欄の新設・`docs/` 配下の仕様書追加（`docs/README.md` の必須範囲 FR は 10/12/15/19/20 であり
  FR-05 は含まれない）。

## 設計

### 偽 OpenD（`FakeOpenD`）

IADR-0327 が開けた `IMoomooTradeConnectionFactory` / `IMoomooTradeConnection` の縫い目に差す。
**SDK の protobuf 型はそのまま通す**（縫い目の契約どおり）ため、`TrdHeader` の `TrdEnv` / `AccID`、
`TrdPlaceOrder.C2S` の `TrdSide` / `OrderType` / `Code` / `Qty` / `Price` / `Remark`、`TrdGetOrderList` の
`FillQty` / `FillAvgPrice` まで**実際の protobuf 直列化を経て**検証できる。

- **口座一覧**: `Real`（Margin）を先頭に、`Simulate`（Margin・`accId=724808`）を返す。#342 の PoC が実測した
  3 口座の形を写す。**実弾口座を先に置く**ことで「先頭を拾っていないか」を試験が捕まえる。
- **発注**: `TrdPlaceOrder.C2S` を記録し `orderId` を採番して返す。
- **約定照会**: `TrdGetOrderList` に、直前に発注した注文を `Filled_All`（`OrderStatus=11`）＋
  `FillQty` / `FillAvgPrice` つきで返す。
- **拒否モード**: `InitConnect` が `true` を返すが接続完了通知を返さない（#732 が実測した OpenD 停止時の
  見え方そのもの）。

`MMAPI.Init()` を呼ぶ経路は `MMApiMoomooTradeClientReconnectTests` が既定 CI で通しており、Docker も
ネイティブの追加要件も無いことは実測済みである。

### 通す経路

```text
BrokerSelection.Parse("moomoo", "sim")   ← Broker__Provider / Broker__Environment と同じ語彙
  → LiveTradingGate.Ensure（閂 0）
  → BrokerFactory.Create
  → MoomooBrokerAdapter（Provider = MoomooSimulate）
  → MMApiMoomooTradeClient（protobuf・応答相関・接続管理）
  → FakeOpenD（IMoomooTradeConnection）
```

### `Category=Integration` を付けるか

**付けない。** 偽 OpenD は in-process であり Docker を要さない。同アセンブリの `E2EInfrastructureTests` に
既に無 trait の先例がある。既定 CI（`ci.yml` の `--filter "Category!=Integration"`）で毎 PR 走り、
`integration.yml` はフィルタ無しのため**両方で走る**（二重に走っても副作用は無い）。
trait を付けると「PR では走らない」＝退行防止の目的そのものを失う。

## 受け入れ基準

- [x] 接続 → 口座照会 → SIMULATE 発注 → 約定照会が 1 本の試験で通る（`BrokerOrder.Status` が
      `Accepted` → 照会で `Filled`・約定数量／平均約定価格がブローカ応答どおり）。
- [x] 発注が **SIMULATE 口座**（`TrdEnv_Simulate` / `accId=724808`）のヘッダで送られる。実弾口座は選ばれない。
- [x] アダプタが名乗る発注先は `BrokerProvider.MoomooSimulate`（Stage 1 の算入先。IADR-0149 決定 1）。
- [x] **陰性対照**: OpenD が拒否する構成では `BrokerUnavailableException` が**アダプタの外まで伝播**し
      （Rejected へ丸めない・IADR-0211）、偽 OpenD は `PlaceOrder` を **1 度も受け取らない**。
- [x] Docker 無しで走る（`dotnet test` の既定経路）。
- [x] 手数料・為替スプレッドの実績登録について、**登録先の有無と実績値の有無を分けて**結論を残す。

## テスト方針

`backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs`（新規）。

| # | テスト | 写像先 |
| --- | --- | --- |
| 1 | 接続から口座照会と SIMULATE 発注と約定照会までが偽 OpenD を通して成立する | 受け入れ基準 1・2・3 |
| 2 | 発注ヘッダは実弾口座ではなく SIMULATE 口座を指す | 受け入れ基準 2（陰性の側から） |
| 3 | 建玉照会も同じ経路で成立する（可用性 probe が使う口） | UC-02 |
| 4 | **陰性対照**: OpenD が応答しないと BrokerUnavailable で見送られ発注は 1 度も送られない | 受け入れ基準 4 |
| 5 | **陰性対照**: OpenD が応答しないとき Rejected へ丸めない | 受け入れ基準 4（IADR-0211） |

**変異試験（ミューテーション）で門が効くことを実測する**——受け入れ基準ごとに、対応する本番コードを
意図的に壊して赤くなることを確認し、証跡を PR へ残す。

## 計画書との差異

- 差異: **あり**。ADR-0019 が #342 の成果物として求めた「moomoo アダプタの結合テスト（SIMULATE 環境）を
  CI から実行可能な形で残す」を、**実 OpenD ではなく偽 OpenD で**満たす。実 OpenD を CI から駆動するには
  有人の対話デバイス認証（#730）が要り、CI では原理的に成立しない。この差は IADR-0334 に残す
  （計画の誤りではないため環流はしない）。
- 差異: **あり**。#754 のチェックリスト 2 は「PoC 観測値を設定／台帳へ写す」と書くが、**#342 に手数料・
  為替スプレッドの観測値は存在しない**（上表 軸 5・6）。同 issue の 2026-09-11 監査自身が当該行を
  「未着手。本 issue のいずれのコメントにも実施記録が無い」と記録している。**登録先は既に在り、
  実績値だけが無い。** 値を発明しない（IADR-0334 決定 2）。

## 未決事項

- 手数料・為替スプレッドの実額は**利用者が moomoo の口座・約定明細から確認して登録する**事項として残る
  （`PUT /assumptions` は OwnerOnly）。#754 のチェックリスト 2 は AI では完結しない。
- 実 OpenD に対する結合の実走（写像の live 検証）は引き続き未実施。偽 OpenD は protobuf の組み立てまでは
  固定するが、**OpenD が実際にその形を受理するか**は固定しない。

## 検証（2026-09-11 実測）

### 実装したもの

| ファイル | 内容 |
| --- | --- |
| `backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs`（新規） | 偽 OpenD（`FakeOpenD` / `FakeConnection`）＋ 5 テスト（正常系 3・陰性対照 2） |
| `.ai-context/adr/IADR-0334_fake-opend-adapter-integration-test-and-fee-actuals-absence.md`（新規） | 決定の記録 |
| `.ai-context/adr/README.md` | IADR-0334 の索引行 |

**本番コードは 1 行も変更していない。** 偽 OpenD は IADR-0327 が既に開けていた差し替え口へ差すだけである。

### 実行結果

```text
dotnet build backend/backend.slnx          → 成功・警告 0・エラー 0
dotnet format backend/backend.slnx --verify-no-changes → 出力なし（差分なし）
dotnet test backend/backend.slnx --filter "Category!=Integration"
    → 21 プロジェクト・失敗 0・合格 6,141・スキップ 4
      うち AiStockTrading.IntegrationTests.dll: 合格 10（既存 5 ＋ 本作業 5）
dotnet test（Category=Integration のみ）
    → 失敗 8（すべて Docker 未提供の環境要因。`Docker.DotNet` の NamedPipe 接続失敗。
      本作業の変更前から同じ。CI の integration.yml は Docker 前提であり影響しない）
```

新規 5 テストの単体実行は **約 4 秒・Docker 不要**。

### 変異試験（門が効くことの実測）

**本番コードを意図的に壊し、対応するテストが赤になることを 1 件ずつ確認した。** 全て確認後に元へ戻し、
`git status` で作業ツリーが新規 3 ファイルのみであることを確認している。

| # | 壊した箇所 | 変異 | 落ちたテストと実測メッセージ |
| --- | --- | --- | --- |
| 1 | `MMApiMoomooTradeClient.BuildHeader` | `TrdEnv_Simulate` → `TrdEnv_Real` | `発注ヘッダは実弾口座ではなくSIMULATE口座を指す` — `Expected sent.Header.TrdEnv[0] to be 0 …, but found 1` |
| 2 | `MMApiMoomooTradeClient.FetchSimulateAccountAsync` | `TrdEnv` の絞り込みを外し**先頭の口座**を採る | 同上 — `Expected sent.Header.AccID to be 724808UL, but found 284852705357372276UL` |
| 3 | `MoomooBrokerAdapter.PlaceCoreAsync` の catch フィルタ | `and not BrokerUnavailableException` を外す | 陰性対照 2 本 — `Assert.Throws() Failure: No exception was thrown` |
| 4 | `MMApiMoomooTradeClient.MapState` | `11 => FilledAll` → `FilledPart` | `偽OpenDに対して…一巡する` — `Expected queried!.Status to be OrderStatus.Filled …, but found OrderStatus.PartiallyFilled` |
| 5 | `MMApiMoomooTradeClient.QueryOrderAsync` | `FillQty` / `FillAvgPrice` → `Qty` / `Price` | 同上 — `Expected queried.AveragePrice to be 149.87M, but found 150M` |

**5 件とも殺せた**（門が形だけでないことの証跡）。

### 機械検査

| 検査器 | 結果 |
| --- | --- |
| `check-trace-blocks` | OK（44 件） |
| `check-doc-links` | OK（733 件） |
| `check-cross-repo-refs` | OK（2,200 件） |
| `check-plan-id-qualification` | OK（2,251 件） |
| `gen-knowledge-graph --check` | OK（ノード 1,599・エッジ 7,620） |
| `check-reading-budget` | OK（Claude 44,041 バイト＝予算の 86%。**本作業は必読規約を 1 バイトも増やしていない**） |
| `check-adr-index-sync` | skip（PR 番号が無いローカル実行のため検査範囲を決められない。CI で効く） |
| `check-banned-libraries` / `check-banned-settled-cash-sources` / `check-consumer-endpoint-names` | OK |
| `check-test-traceability` | 🔴 **[T1] で赤。ただし本作業とは無関係の既存事象である**（下記） |

#### `check-test-traceability` の [T1] について（既存・Windows 固有）

`origin/develop` を `git archive` で素の木へ展開し、`TEST_TRACE_ROOT` を向けて実行しても**同一の [T1] が出る**
（実測。本作業の差分は `backend/Services/` 配下に 1 ファイルも無い）。原因は検査器 L142 の
`fs.existsSync(path.join(services, e.name, 'tests'))` が **Windows の大文字小文字を区別しないファイルシステム**で
実在する `Tests/` に当たり「旧樹形あり」と数える一方、走査側（L157）は実パスの `Tests` で数えるため
`old=0` になることである。姉妹検査器 `check-consumer-endpoint-names` は同じ状況で
「サービスディレクトリ: 旧 0 件 / 新 12 件」と正しく数えている。**Linux の CI では発生しない。**
本 PR の射程外のため直さず、事象として記録する。
