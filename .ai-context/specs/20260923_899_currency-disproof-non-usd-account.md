---
title: 通貨の「反証」で非 USD 単一市場口座の基準資金を止める（cashInfoList。確証ではないので新たな fail-closed を生まない）
type: spec
status: accepted
related_ids: [FR-10, FR-19, FR-04, UC-06, ADR-0041, ADR-0016, ADR-0021, IADR-0153, IADR-0327, IADR-0334, IADR-0354, IADR-0373]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定 2)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 注記)
---

# 仕様書: 通貨の「反証」で非 USD 単一市場口座の基準資金を止める（#899）

## 起点

- #899。#897 / PR #898 の監査が挙げた**受け入れた残余リスク**への対処。
- PR #898 は「応答が `currency` を載せないとき、要求した通貨（USD）を前提として基準資金を採る」とした
  （IADR-0354 決定 1 の ［2026-09-19 追記 / #897］）。**この近似は撤回しない** —— 実機の OpenD は欄を
  載せないため、撤回すれば稼働環境で新規建てが一日中出なくなる（#897 の実害そのもの）。
- 残るリスクは **米国株の取扱権限を持つ非 USD 単一市場口座**（例: moomoo JP 口座）である。欄を載せずに
  `TotalAssets` を JPY で返し、現行はそれを USD として採る＝**全比率上限が約 150 倍緩む**。

## 🔴 本作業の第一の制約（これを壊したら是正ではなく再発である）

**実機の OpenD は `Funds.currency` を送らない。** その応答で基準資金が供給され続けることが、
本変更の**最優先の不変条件**である。#897 は「実機だけで常時 fail-closed」という形で起きた事故であり、
本件で新しい門を足すときに同じ形を作ってはならない。

したがって採るのは **「確証」ではなく「反証」**である。

| | 形 | 実機（欄が無い）への影響 |
| --- | --- | --- |
| 確証 | 「USD だと**確かめられたときだけ**採る」 | 🔴 **常に採らない**（#874 が作った状態＝#897 の実害） |
| **反証**（本件） | 「**非 USD だと示す積極的な証拠があるときだけ**採らない」 | **影響なし**（証拠が無ければ従来どおり採る） |

## 🔴 母集合（走査したファイルと除外理由）

**記憶で挙げず、誤りの側・関連の側の文字列で全追跡ファイルを走査した**
（`.claude/rules/traceability.repo.md` 規則 9・10）。

走査に使った語（`.git` を除く全ファイル）: `cashInfoList` / `CashInfo` / `反証` / `単一市場口座` /
`TrdMarketAuthList` / `#899` / `Position.currency`。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs` | **直す**（反証の門・取扱市場の記録） |
| `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/MoomooBrokerAdapter.cs` | **直す**（`GetAccountStateAsync` の XML コメントが通貨の扱いを説明している） |
| `backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs` | **直す**（偽 OpenD に `cashInfoList` を持たせ、T-10-658〜662 を足す） |
| `.ai-context/adr/IADR-0373_*.md` | **新設**（本件の実装判断） |
| `.ai-context/adr/README.md` | **直す**（IADR-0373 の索引行） |
| `docs/tests/FR-10_risk-controls-tests.md` | **直す**（T-10-658〜662・残余リスクの記述の追随） |
| `docs/operations/capital-baseline-seed-runbook.md` | **直さない**。`#899` を「実装側の対処が要る」と参照しているだけで、Runbook の手順は変わらない（人手投入の門は本件の射程外） |
| `.ai-context/adr/IADR-0354_capital-baseline-from-broker-account.md` | **直す**（§結果 の残余リスク 1 項に後継 IADR を併記する。決定そのものは変えない） |
| `.ai-context/specs/20260919_897_currency-absent-accepts-requested.md` / `20260902_397_342_moomoo-readonly-probes.md` | **直さない**。point-in-time の凍結記録（`traceability.repo.md` 除外表） |
| `docs/blocked-tasks.md` / `docs/tests/FR-19_trading-guards-tests.md` / その他の走査ヒット | **直さない**。語の一致が別文脈（`CashInfo` は無関係、`単一市場口座` の引用なし） |
| `CHANGELOG.md` | **直さない**（生成物） |

## 🔴 実測（本作業で自分で採った）

### 実測 1 — `Funds.cashInfoList` は SDK に実在し、行は通貨を持つ

`moomoo-api` 10.8.6808（`MMAPI4Net.dll`）のメタデータを走査した。`TrdCommon.Funds` は欄 15 に
`cashInfoList`（repeated `AccCashInfo`）を持ち、アクセサは `CashInfoListList` / `CashInfoListCount`。
`AccCashInfo` は `Currency` / `HasCurrency` / `Cash` / `AvailableBalance` / `NetCashPower` を持つ
（#897 の走査結果と一致する）。

### 実測 2 — 🔴 実機がこの欄を載せる保証は無い（だから「反証」でなければならない）

#397 の実機 probe（`.ai-context/specs/20260902_397_342_moomoo-readonly-probes.md`）は
`FUNDS accId=724808 totalAssets=968788.459 cash=968788.459 power=1937576.918` までしか記録しておらず、
**`cashInfoList` が載っていたかどうかは分からない**。`currency` と同じ optional であり、
**「載っているはず」を前提にした設計は #897 の再生産**である。
→ **欄が空／無いときは何もしない**（従来どおり採る）。これが本件の**主眼**である（issue の受け入れ基準 2）。

### 実測 3 — 反証が効く範囲は狭い（効かない範囲を先に確定させた）

| 口座の姿 | `cashInfoList` の見え方（想定） | 反証は効くか |
| --- | --- | --- |
| JPY 建て口座・JPY 現金のみ | JPY の行だけ | ✅ **止まる** |
| JPY 建て口座・米国株のために USD 現金も持つ | JPY と USD の行 | ❌ **止まらない**（USD の行がある） |
| 欄そのものが無い／空 | — | ❌ **止まらない**（設計どおり。新たな fail-closed を作らない） |
| USD 建ての US 単一市場口座（本系） | USD の行、または欄なし | ✅ 影響なし（通す） |

**「止まらない」ケースが残ることは害ではなく射程である。** 反証は「偶発的な歯止め」を
「限定的だが意図された歯止め」へ変えるものであって、非 USD 口座を網羅的に排除する手段ではない。

## 射程

| 含む | 含まない（理由） |
| --- | --- |
| `cashInfoList` による反証（**通貨の欄が無い＝近似で採る経路にだけ効かせる**） | 応答が `currency` を**明示**した経路。明示は documented な一次情報であり、内訳で上書きしない |
| 反証が効かないケースの明記（IADR-0373） | 通貨換算の実装（別通貨を換算して採る）。計画が定めていない |
| `TrdMarketAuthListList` を**記録する**（口座選択時に Information で残す） | `TrdMarketAuthListList` による**門**。取扱市場は通貨の証拠ではなく、これで止めると **US 権限のある口座を通貨と無関係に落とす新しい fail-closed**になる |
| 偽 OpenD に `cashInfoList` を持たせる（既定は**空**＝実機で確認できていない姿） | 偽 OpenD の他の応答の既定 |
| `docs/tests/FR-10_risk-controls-tests.md`（T-10-658〜662） | 残高 0 の検知（#889）・見送り理由の観測（#891） |

## 決めたこと

### 決定 A: 反証は `cashInfoList` の**通貨を名乗る行**だけで組む

- **止めるのは次の 3 条件がすべて成り立つときだけ**である。
  1. 応答が `currency` を**明示していない**（＝近似で採ろうとしている経路）。
  2. `cashInfoList` に**通貨を名乗る行が 1 つ以上ある**（`HasCurrency` かつ `Currency != Currency_Unknown`）。
  3. **そのどの行も USD でない**。
- 上のいずれかが崩れたら**従来どおり採る**。特に「欄が無い」「行はあるが通貨を名乗らない」
  「`Currency_Unknown` しかない」は**証拠にならない**ため何もしない。
- ログは **Warning**（止めるのは異常事態であり、実機の正常な見え方ではない）。

### 決定 B: 応答が `currency` を**明示**した経路には反証を適用しない

- 明示は moomoo が documented に「この照会で用いた通貨」と定めた一次情報である。
  universal 口座へ USD を要求すれば `currency=USD`・`TotalAssets` は換算後の USD で返り、
  **内訳（`cashInfoList`）は JPY だけかもしれない**。ここで内訳を優先すると、
  **正しく USD と名乗っている応答を落とす新しい fail-closed** になる。
- したがって反証は**近似の範囲を狭める道具**であり、明示を上書きする道具ではない。

### 決定 C: `Position.currency`（#30）による反証は**採らない**

- ①**建玉の通貨は口座の基準通貨ではない。** JP 建て口座が米国株を持てば建玉は USD であり、
  「USD の建玉がある」は「口座が USD 建て」を意味しない。逆向き（非 USD の建玉しかない）も、
  US 単一市場口座が日本株を持てない以上、**本系で観測され得ない**。
- ②反証のために**建玉照会を基準資金の経路へ引き込む**ことになる。建玉照会は失敗し得る
  （`GetPositionsAsync` の契約は「部分列挙なら例外」）。失敗を「反証できなかった」として通せば
  費用だけ増え、止めれば**新しい fail-closed**（本件の第一の制約に反する）。
- したがって採らない。**採らない理由を IADR-0373 に残す**（issue の受け入れ基準 3）。

### 決定 D: `TrdMarketAuthListList` は**記録するが門にしない**

- 口座選択（`FetchSimulateAccountAsync`）で取扱市場を Information で残す。
  **API から口座の基準通貨は読めず、取扱市場が唯一の手掛かり**であるため、
  実機の口座が何を返すかを**後から証跡で確かめられる**ようにする価値がある。
- 🔴 **門にはしない。** 取扱市場は通貨の証拠ではない。「JP を含むなら止める」は
  universal 口座（JP と US の両方を扱い、`currency` を明示する）を通貨と無関係に落とす。
  「US を含まないなら止める」は既存の `BuildHeader(TrdMarket_US)` が実質的に担っている。

## 受け入れ基準 → テスト

| # | 受け入れ基準（issue） | テスト |
| --- | --- | --- |
| 1 | JPY 建ての内訳しか持たない応答に対して基準資金を**採らない** | **T-10-658** |
| 2 | 🔴 `cashInfoList` が空／無い応答では**従来どおり採る**（新たな fail-closed を作らない） | **T-10-659**（否定形・本件の主眼）／既存 T-10-620（偽 OpenD の既定） |
| 3 | 反証が効かない範囲が実装 ADR に明記されている | IADR-0373 §結果（実測 3 の表・決定 C） |
| — | USD の行が 1 つでもあれば採る（JPY と混在） | **T-10-660** |
| — | 通貨を名乗らない行・`Unknown` の行は証拠にならない | **T-10-661** |
| — | 応答が USD を**明示**していれば内訳が JPY でも採る（決定 B） | **T-10-662** |

## 🔴 変異注入（示すこと）

| 変異 | 期待 |
| --- | --- |
| ① 反証の門を外す（`cashInfoList` を一切見ない） | **T-10-658 が赤**（他は緑のまま＝新しい門が既存の経路を塞いでいない） |
| ② 反証を「確証」へ反転する（USD の行が**無ければ**採らない） | **T-10-659・T-10-661・T-10-620 が赤**（＝ #897 と同型の事故の形） |
| ③ 決定 B を外す（明示経路にも反証を効かせる） | **T-10-662 が赤** |

## 検証

`dotnet build backend/backend.slnx` / `dotnet test`（OrderExecutionService・`AiStockTrading.IntegrationTests`）/
`dotnet format --verify-no-changes` / `node scripts/check-*.js`。
**`AiStockTrading.IntegrationTests` は Docker 不在の環境で既知の件数が落ちる**（本作業と無関係。
偽 OpenD のテストは Docker を要さないため走る）。
