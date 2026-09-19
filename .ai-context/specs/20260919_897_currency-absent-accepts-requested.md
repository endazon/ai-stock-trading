---
title: 口座照会の応答が通貨を明示しないときは要求した通貨（USD）を前提として基準資金を採る
type: spec
status: accepted
related_ids: [FR-10, FR-19, FR-04, UC-06, ADR-0041, ADR-0016, ADR-0021, ADR-0025, IADR-0153, IADR-0327, IADR-0334, IADR-0354]
author: claude (Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定 2)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 注記)
---

# 仕様書: 応答が通貨を明示しないときは要求した通貨を前提として基準資金を採る（#897）

## 起点

- #897。**稼働環境で実測された実害**（2026-09-19 13:24 JST・develop `b0fc1653` 配備直後）。
- #874（IADR-0354 決定 1 の ［2026-09-19 追記］）が入れた通貨ガードが、**実機の OpenD に対して常に fail-closed**
  になり、基準資金が一度も供給されない。新規建てが一切出ない。

```
[13:24:02 WRN] 口座照会の応答通貨を USD と確認できません hasCurrency=False currency=null。基準資金は未供給として扱います。

GET /risk-controls/sizing-context → { "capital": null, "stageCapitalRemaining": null, ... }
```

- 🔴 **期限は 2026-09-21（月）22:30 JST の寄り付き。** 直らなければ新規建てが一日中出ない。

## 🔴 実測（本作業で自分で採った）

### 実測 1 — `Funds.currency` は protobuf の optional であり、ほかに通貨を確証できる欄は応答に無い

SDK（`moomoo-api` 10.8.6808 / `MMAPI4Net.dll`）を**リフレクションで走査**し、
`TrdCommon.Funds` の protobuf ディスクリプタ（`Descriptor.Fields` の `IsRequired` / `IsOptional` / `IsRepeated`）を
実際に読んだ。出力の要点:

| フィールド | 番号 | ラベル |
| --- | --- | --- |
| `power` / `totalAssets` / `cash` / `marketVal` / `frozenCash` / `debtCash` / `avlWithdrawalCash` | 1〜7 | **required** |
| `currency` | 8 | **optional** |
| `cashInfoList`（`AccCashInfo`） | 15 | repeated |
| `marketInfoList`（`AccMarketInfo`） | 33 | repeated |
| 残り 29 欄（`availableFunds` 〜 `remainingLimit`） | — | optional |

**#874 の監査が書いた「required は 7 つ」は正しい。** 実機の OpenD が欄 8 を載せないのは protobuf として合法である。

### 実測 2 — 応答側に「通貨を確証できる別のフィールド」は無い（issue の案を潰した）

issue は `AssetCategory` などで確証できないかを検討させている。走査の結果:

- **`AssetCategory` は要求側（`TrdGetFunds.C2S`）にしか無い。** 応答（`TrdGetFunds.S2C` / `Funds`）には存在しない。
  したがって**応答の通貨を確証する材料にならない**。
- `Funds.cashInfoList`（`AccCashInfo`）は `currency` / `cash` / `availableBalance` / `netCashPower` を持ち、
  **通貨ごとの現金の内訳**である。`totalAssets`（欄 2）は**内訳ではなく換算後の集計値**であり、
  内訳に USD の行があることは `totalAssets` が USD 建てであることを意味しない。
  しかも `currency` と同じく optional の repeated であり、**実機が載せる保証が無い**
  （載せてこなければ同じ fail-closed の罠を作り直すだけである）。
- `Funds.marketInfoList`（`AccMarketInfo`）は `trdMarket` / `assets` の 2 欄だけで、通貨を持たない。

**結論: 応答から通貨を確証する手段は `Funds.currency` 以外に無い。** 実機がそれを載せない以上、
「確証できたときだけ採る」は「永久に採らない」と同義である。

### 実測 3 — 偽 OpenD の既定が実機と違っていた（今回の事故の再発条件）

`FakeOpenD.FundsCurrency` の**既定値が `Currency_USD`** であった
（`backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs`）。
#874 は 3 通り（USD / JPY / 欠落）を Theory で固定していたが、**既定が実機と違う**ため、
通貨に触れない他の全ケース（発注・約定照会・建玉照会・T-10-515）は
「**実機では起きない、通貨を送る OpenD**」に対して緑になっていた。

🔴 **偽物が本物より行儀が良いと、守りが常時発動していても気づけない。**

## 射程

| 含む | 含まない（理由） |
| --- | --- |
| `MMApiMoomooTradeClient.GetAccountEquityInBaseAsync` の通貨ガードの是正 | 読み出し側（`ICapitalBaselineStore`）の門。**変えない** |
| 応答が USD 以外を**明示**したときに採らない守りの**維持** | 通貨換算の実装（別通貨を換算して採る）。計画が定めていない |
| `TotalAssets <= 0` の門（T-10-515）の**維持** | 残高 0 の検知（#889） |
| 偽 OpenD の既定を実機と同じ（通貨を送らない）へ変える | 偽 OpenD の他の応答（発注・約定・建玉）の既定 |
| IADR-0354 決定 1 への日付つき追記・索引行の追随 | 新 IADR の起草（採番済みの `IADR-0365` は**使わない**。本件は決定 1 の**是正**であり、新しい決定ではない） |
| `docs/tests/FR-10_risk-controls-tests.md`（T-10-514 の写像・変異注入表・新規 T-10-620/621） | 他の FR の試験仕様書 |
| `docs/operations/capital-baseline-seed-runbook.md` のログ文言の追随 | Runbook の手順そのもの |

## 🔴 母集合（走査したファイルと除外理由）

**記憶で挙げず、誤りの側の文字列で全追跡ファイルを走査した**（`.claude/rules/traceability.repo.md` 規則 9・10）。

走査に使った語（`.git` を除く全ファイル）: `HasCurrency` / `FundsCurrency` / `Currency_USD` /
`応答通貨` / `USD と名乗` / `通貨を名乗` / `通貨の欠落` / `通貨が未設定` / `T-10-514` / `T-10-515`。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs` | **直す**（門の本体・コメント・ログ） |
| `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/MoomooBrokerAdapter.cs` | **直す**（`GetAccountStateAsync` の XML コメントが「通貨の欠落も null」と書いている） |
| `backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs` | **直す**（偽 OpenD の既定・T-10-514 の期待値・新規 T-10-620/621） |
| `.ai-context/adr/IADR-0354_capital-baseline-from-broker-account.md` | **直す**（決定 1 へ日付つき追記・フォローアップ） |
| `.ai-context/adr/README.md` | **直す**（IADR-0354 の索引行に同じ追記を反映） |
| `docs/tests/FR-10_risk-controls-tests.md` | **直す**（T-10-514 の期待・変異注入表・§の注記・新規行） |
| `docs/operations/capital-baseline-seed-runbook.md` | **直す**（恒久の直し方が引いている警告ログの文言） |
| `.ai-context/specs/20260919_869_capital-baseline-from-broker-account.md` | **直さない**。point-in-time の凍結記録であり、当時の判断を後から書き換えない（`traceability.repo.md` 除外表） |
| `backend/Services/RiskManagementService/Tests/Features/RiskManagement/CapitalBaselineTests.cs` | **直さない**。T-10-515 の**読み出し側**の門であり本件の射程外。維持を確認するだけ |
| `CHANGELOG.md` | **直さない**（生成物） |

除外して**いない**のに手を入れないものは上の 3 行だけである。

## 決めたこと

### 決定 A: 応答が通貨を**明示しない**ときは、**要求した通貨**を前提として採る（近似）

- 要求は `TrdGetFunds.C2S.SetCurrency(Currency_USD)` で **USD を明示している**。
  moomoo の API 契約では応答はその通貨で返る。**応答が黙っていることは「別の通貨で返した」を意味しない。**
- 要求と門が同じ値を見るよう、`RequestedCurrency` という 1 つの定数から両方を組む
  （2 か所に書くと、片方を変えたときに黙って割れる）。
- 🔴 **近似である。** 「要求に従った」という証拠は応答に無く、**moomoo の契約を信じている**。
  この近似は IADR-0354 決定 1 の追記とログに残す。

### 決定 B: 応答が通貨を**明示しており USD でない**ときは、従来どおり**採らない**（撤去しない）

- 実機で起こり得る（口座の通貨設定が変わる・別市場の口座を足す）。
  JPY 建ての数値を USD の統制上限の分母に据えると桁が 2 つずれる。
- **門の撤去は選ばない。** 変異注入①（門を外して常に採る）で、この分岐が赤になることを示す。

### 決定 C: ログは **Warning ではなく Information**

- 通貨の欠落は**実機の正常な見え方**であり、**毎巡回（既定 5 分）必ず出る**。
  Warning のままだと警告が常時鳴り、**本物の警告が埋もれる**（#874 が作ってしまった状態がまさにそれである）。
- **黙らせもしない。** 近似で採ったことは毎回記録に残す（Information）。
  状態を持って初回だけ Information・以後 Debug、という案は採らない——
  「いま採っている値が近似かどうか」をログの 1 行で答えられなくなるためであり、
  5 分に 1 行（288 行/日）は運用上の負担にならない。
- USD 以外を**明示**した場合は **Warning のまま**（こちらは異常である）。

### 決定 D: 偽 OpenD の既定を**実機と同じ（通貨を送らない）**へ変え、3 通りすべてを固定する

- `FakeOpenD.FundsCurrency` の既定を `null`（＝欄を設定しない）にする。
  **既定が実機と違うままだと、同じ事故がまた起きる。**
- 3 通り（USD 明示 / 別通貨明示 / 欠落）は T-10-514 の Theory で引き続き固定する（期待値だけ替わる）。
- **既定が「欠落」であること自体**を T-10-620 で固定する（既定が黙って戻されたら赤くなる）。

### 決定 E: `TotalAssets <= 0` の門（T-10-515）は**維持**

- 通貨の門より**後**に置いたまま変えない。変異注入②で赤になることを示す。

## 🔴 代替案とその害（採らなかった理由）

| 案 | 害 | 判定 |
| --- | --- | --- |
| **人手で `account_equity_days` へ毎日 1 行投入する**（#874 の Runbook） | 毎営業日の手作業。忘れれば止まる。**PoC の運用として現実的でない**（Runbook は事故時の応急であって定常運用ではない） | 不採用 |
| **門を撤去する**（通貨を一切見ない） | 応答が JPY を**明示**したときも採ってしまう。桁が 2 つずれた分母で統制が効かなくなる | 不採用（決定 B） |
| **別のフィールドで通貨を確証する**（`AssetCategory` / `cashInfoList`） | 実測 2 のとおり **`AssetCategory` は応答に存在しない**。`cashInfoList` は内訳であって `totalAssets` の建値を語らず、しかも optional で実機が載せる保証が無い＝**同じ fail-closed の罠を作り直す** | 不採用（**見つかれば第一候補だったが、無かった**） |
| **桁の健全性検査**（`LedgerEquity` と 100 倍以上乖離したら採らない） | ①`MMApiMoomooTradeClient` は OpenD 専用のインフラ層で、台帳（Risk 側の `LedgerEquity`）を**知らないし知るべきでない**——渡すには依存の向きを反転させる必要がある。②`LedgerEquity` は IADR-0354 決定 3 が **DD 専用の別量**と定めた値であり、基準資金の検証に流用すると決定 3 が消した結線を裏口から戻すことになる。③初回起動・入出金直後・システム外売買の取り込み直後は**正当に大きく乖離する**ため、閾値は本物の値を落とし得る（＝ fail-closed の罠の再生産）。**脆い** | 不採用 |

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 実機の OpenD（`currency` を送らない）に対して基準資金が供給される | **T-10-514**（`unset` → 3,000）／**T-10-620**（偽 OpenD の**既定**で供給される） |
| 2 | 応答が USD 以外を明示したときは採らない | **T-10-514**（`jpy` → null。既存を維持） |
| 3 | 近似であることが記録されている | IADR-0354 決定 1 の ［2026-09-19 追記 / #897］／**T-10-621**（Information で「要求した通貨を前提」と残る・Warning ではない） |
| 4 | `TotalAssets <= 0` は維持 | **T-10-515**（既存を維持。偽 OpenD の既定が通貨なしに替わっても緑） |
| 5 | 偽 OpenD の既定が実機と同じ | **T-10-620** |
| 6 | OpenD 更新時の再確認の追随 | IADR-0354 §フォローアップ |

## 🔴 変異注入（示すこと）

| 変異 | 期待 |
| --- | --- |
| ① 新しい門を外す（通貨を**一切見ず**常に採る） | **USD 以外を明示したケース（T-10-514 の `jpy`）が赤** |
| ② `TotalAssets <= 0` の門を外す | **T-10-515 が赤** |

## 検証

`dotnet build backend/backend.slnx` / `dotnet test`（OrderExecutionService・RiskManagementService・
`AiStockTrading.IntegrationTests`）/ `dotnet format --verify-no-changes` / `node scripts/check-*.js`。
**`AiStockTrading.IntegrationTests` は Docker 不在の環境で 11 件落ちる**（本作業と無関係の既知の落ち方。
偽 OpenD のテストは Docker を要さないため走る）。
