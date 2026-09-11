---
title: IADR-0334 偽 OpenD による moomoo アダプタ結合試験を既定 CI へ置き、手数料・為替スプレッドは観測値が無いため登録しない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-12, FR-17, FR-20, UC-01, UC-02, ADR-0002, ADR-0019, ADR-0021, ADR-0026, IADR-0016, IADR-0021, IADR-0056, IADR-0111, IADR-0144, IADR-0149, IADR-0211, IADR-0226, IADR-0300, IADR-0327]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0026_short-fee-rate-unit-poc.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0334: 偽 OpenD による moomoo アダプタ結合試験を既定 CI へ置き、手数料・為替スプレッドは観測値が無いため登録しない

- 状態: Accepted
- 日付: 2026-09-11
- 決定者: endazon（実装は Claude Code）

## 起点・関連

- 関連する計画書 ID: FR-05 / FR-12 / FR-17 / FR-20 / UC-01 / UC-02 / ADR-0002 / **ADR-0019**（PoC の成果物として
  「moomoo アダプタの結合テスト（SIMULATE 環境）を CI から実行可能な形で残す」を要求）/ ADR-0021 /
  **ADR-0026**（`ShortFeeRate` の単位 PoC・期限 2026-08-31・未着手）。
- 関連する実装 ADR: IADR-0016 / IADR-0056（SIMULATE 限定）・IADR-0111（provider × environment と閂 0）・
  IADR-0149 決定 1（`BrokerProvider` が Stage 1 の算入先を決める）・IADR-0211（接続不達は見送り）・
  **IADR-0327**（`IMoomooTradeConnectionFactory`＝差し替え口）・IADR-0021（`TradingAssumptions`）・
  IADR-0144（moomoo PoC の結果）・IADR-0226 / IADR-0300（経費台帳と未供給の否定形）。
- 関連する実装仕様書: `.ai-context/specs/20260911_754_moomoo-adapter-fake-opend-integration.md`。
- 起点 issue: #754（親 #342）。

## コンテキストと課題

#342 の 2026-09-11 監査は、クローズ条件 6 件のうち **2 件を「AI で着手可能」**と判定し、#754 として切り出した。

1. **moomoo アダプタの CI 結合試験** —— 既存の moomoo 系テストは「アダプタを `IMoomooTradeClient` で
   fake 化するもの」と「SDK 層の静的写像／接続の張り直しだけを見るもの」に割れており、**その間を通す試験が
   1 本も無い**。`backend/Tests/AiStockTrading.IntegrationTests/` の 10 ファイルにも OpenD を駆動するものは無い。
2. **手数料・為替スプレッドの実績登録** —— #754 は「PoC 観測値（#342 のコメント）を設定／台帳へ写す」と書く。

課題は 2 つある。

- **CI で実 OpenD は駆動できない。** OpenD のログインは画像 CAPTCHA / SMS を伴う有人の対話デバイス認証を
  要し（#730 で稼働クラスタですら FIFO 経路が届かず `OPEND_STDIN_MODE=tty` へ退避した）、配布可能な
  コンテナイメージも無い。ADR-0019 の「CI から実行可能な形で残す」を額面どおり実 OpenD で満たす道は無い。
- **#342 に手数料・為替スプレッドの観測値が存在しない。** #754 の本文は「PoC 観測値」と書くが、
  同 issue の 365 行のコメント全文を走査しても手数料・為替スプレッドの実測は 1 件も無く、
  **監査コメント自身が当該行を「🔴 未着手。（本 issue のいずれのコメントにも実施記録が無い）」と記録している**。
  ヒットするのは `ShortFeeRate`（借株料。別区分・かつ単位未確定）だけである。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A** | Testcontainers で実 OpenD を立てる | ✗ 対話デバイス認証が要り CI で成立しない。配布可能なイメージも無い |
| **B** | 偽 OpenD を `IMoomooTradeConnection` の縫い目（IADR-0327）に差す | ✅ **採用。** protobuf の組み立て・応答相関・接続管理まで本物を通る |
| C | `IMoomooTradeClient` を fake 化する | ✗ 既存 `MoomooBrokerAdapterTests`（23 Fact）と同値。**protobuf を 1 バイトも組まない** |
| D | 偽 OpenD を TCP でリッスンさせ、実 SDK に本当に接続させる | ✗ OpenD のワイヤプロトコル（暗号化・ハンドシェイク）を偽装することになり、**試験対象より偽物のほうが複雑**になる |
| **E** | `Category=Integration` を付ける | ✗ 既定 CI の `--filter "Category!=Integration"` で外れ、**毎 PR の退行防止という目的そのものを失う** |
| **F** | SC-01 のテストフィクスチャにある「それらしい値」（`us.rate 0.00495` 等）を実績として登録する | ✗ **出所の無い数値が統制へ入る。** 採算ガード・最小期待利益・費用率がその値で動き出す |
| G | 「PoC 観測台帳」を新設して観測値を貯める | ✗ 入れる観測値が 1 件も無い。**消費者ゼロの器**を作ることになる |

## 決定

### 決定 1: 結合試験は **in-process の偽 OpenD** に対して行い、`Category=Integration` を付けない

`IMoomooTradeConnectionFactory` / `IMoomooTradeConnection`（IADR-0327 が開けた差し替え口）へ偽 OpenD を差し、
**「選択（`BrokerSelection.Parse("moomoo","sim")`）→ 閂 0（`LiveTradingGate`）→ 生成（`BrokerFactory`）→
アダプタ（`MoomooBrokerAdapter`）→ SDK 層（`MMApiMoomooTradeClient`＋protobuf）→ 接続」**を 1 本で通す。
TCP も Docker も使わない。

**trait を付けない**のは、付けると既定 CI（`ci.yml`）のフィルタで外れて PR で走らなくなるためである。
同アセンブリの `E2EInfrastructureTests` に無 trait の先例がある。`integration.yml` はフィルタを掛けないので
日次側でも走る（二重に走っても副作用は無い）。

**偽 OpenD は `IMoomooTradeClient`（SDK 非依存のポート）へは差さない。** そこへ差すと C 案と同値になり、
本 ADR が埋めようとしている隙間がそのまま残る。

### 決定 2: 手数料・為替スプレッドの実績は **登録しない**（登録先は在る。**観測値が無い**）

- **登録先は既に在り、追加実装は要らない** —— `TradingAssumptions`（`JapanCommission` /
  `UnitedStatesCommission` の `CommissionSchedule(Rate, Minimum, Cap)`・`FxSpreadRatio`）が単一の設定点であり、
  ConfigurationService の `PUT /assumptions`（OwnerOnly・版管理・理由必須・変更履歴・`AssumptionsChanged` 発行）
  から SC-01 設定画面の入力欄まで結線済みである（IADR-0021）。現在値は **0＝未登録**であり、これは
  計画 05_trading-assumptions §2/§3 の「数値は固定せず設定値として保持・口座開設後に登録」に忠実な状態である。
- **観測値が無い** —— #342 のコメント全文にも、PoC の凍結記録（`20260805_342_moomoo-poc-plan.md` /
  IADR-0144 / `20260902_397_342_moomoo-readonly-probes.md`）にも、手数料・為替スプレッドの実測は 1 件も無い。
- したがって **写すものが無い。値を発明しない。** `PUT /assumptions` は OwnerOnly であり、実額は利用者が
  moomoo の口座・約定明細から確認して登録する事項である。**#754 のチェックリスト 2 は AI では完結しない。**

### 決定 3: `ShortFeeRate=1.5` を手数料・為替スプレッドとして写さない

PoC が実測した唯一の費用らしい数値は `ShortFeeRate=1.5`（借株料）である。これは
**別の経費区分**（`TradeExpenseCategory.BorrowFee`。手数料は `Commission`・為替は `FxCost`）であり、
かつ **単位が未確定**（ADR-0026 PoC 項目 9・期限を超過）である。実装は既に 3 経路で明示的に接続を止めている
（`ShortSellOrderContext` / `BacktestCostModelTests` / `BorrowFeeAccrualService`）。ここへ写すのは
**その停止を迂回すること**であり、採らない。

### 決定 4: 偽 OpenD は口座一覧の**先頭に実弾（Real・Margin）口座を置く**

#342 の PoC が実測した 3 口座のうち US の 2 つ（Real/Margin `284852705357372276`、
Simulate/Margin `724808`）を、**実弾を先頭にして**返す。「先頭を拾う」実装になっていれば試験が落ちる。
陰性の側から固定しない限り、SIMULATE 固定は「たまたま 1 件しか無いから当たっている」だけになり得る。

### 決定 5: `packetID` は偽 OpenD 側で**完全に埋めて**返す

`TrdPlaceOrder.C2S` / `TrdModifyOrder.C2S` は `Build()`（required 充足を要求）で組まれる。
偽 OpenD の `NextPacketId()` が `BuildPartial()` で欠けたものを返すと `Build()` が
`UninitializedMessageException` を投げ、**アダプタがそれを Rejected へ丸めて試験が偽の緑になる**
（実際に起案時の初版で発生し、`Status` を `Accepted` で固定していたため赤で捕まえられた）。

## 理由

- **B 案が「間」を埋める唯一の案である。** C 案は既存テストと同値、A・D 案は CI で成立しない。B 案は
  protobuf の組み立て（`TrdHeader` の `TrdEnv` / `AccID`、`TrdPlaceOrder.C2S` の各フィールド）と
  応答相関（`nSerialNo`）と接続管理（`EnsureConnectedAsync` の fail-safe）を**本物のまま**通す。
- **E 案（trait を付ける）は目的を裏返す。** #342 が求めたのは退行防止であり、退行は PR で入る。
- **F 案は最も危険である。** 手数料・為替スプレッドは採算ガード（`CostCalculator.MinimumViableProfit`）の
  分母に入る。出所の無い値を入れると、**ガードが「効いている」形をとりながら根拠を失う**。
  0（未登録）のままなら費用が過小に出るという既知の性質（IADR-0021 のトレードオフ）が保たれ、
  `IADR-0076`（往復費用 ≤ 0 は採算不能として見送る）が受け止める。**「未登録」と「登録済みで小さい」は
  失敗モードが違う。**

## 結果

- 良い影響:
  - moomoo 発注経路の**退行が毎 PR で捕まる**ようになった。ADR-0019 が成果物として求めた
    「CI から実行可能な結合テスト」が（実 OpenD ではなく偽 OpenD という形で）残った。
  - **SIMULATE 固定が経路の端から端まで固定された** —— 実弾口座を先頭に置く偽 OpenD に対して
    `TrdEnv_Simulate` / `accId=724808` のヘッダで発注することを実測で押さえる。
  - 陰性対照（不達で `BrokerUnavailableException` が伝播し、**発注は 1 度も送られない**）が
    アダプタ層で固定された。従来の同種テストは SDK 層単体にしか掛かっていなかった。
- 悪い影響・トレードオフ:
  - **偽 OpenD は「OpenD がその protobuf を受理するか」を固定しない。** 組み立てまでが射程であり、
    写像の live 検証は引き続き未実施である（IADR-0327 の残余リスクと同じ性質）。
  - 偽 OpenD は `IMoomooTradeConnection` の面に追随する必要がある（SDK メソッドを足したら偽側も足す）。
  - 手数料・為替スプレッドは **0（未登録）のまま**であり、概算費用は過小に出続ける。
- フォローアップ:
  - **手数料・為替スプレッドの実額登録は #342 に残る**（利用者の作業。moomoo の約定明細から確認して
    `PUT /assumptions` で登録する）。計画の誤りではないため planning への環流はしない。
  - `MMApiMoomooHistoryKLineClient` 側の同型の結合試験は本 ADR の射程外（#743 が扱う接続固着と同じ経路）。
  - 経費台帳（`TradeExpense`）への実費供給は `UnsuppliedOrderExpenseSource` のままである
    （`docs/blocked-tasks.md` L740。ADR-0026 PoC 項目 9 と発注執行の手数料供給待ち）。

## 関連

- Supersedes: なし
- Superseded by: なし
