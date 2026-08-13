---
title: 取引ガード（FR-19・再実装）テスト仕様書
type: test-spec
status: approved
related_ids: [FR-19, FR-10, FR-11, FR-20, UC-06, ADR-0007, ADR-0009, ADR-0016, ADR-0021, ADR-0025, IADR-0131, IADR-0132, IADR-0153, IADR-0165, IADR-0182]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-08
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0025_settled-cash-poc-and-gfv-counting.md
related_specs:
  - ../functional/FR-19_trading-guard.md
  - ../specs/20260804_332_trading-guards.md
  - ../adr/IADR-0132_product-type-tri-state-and-guard-scope.md
  - ../adr/IADR-0153_broker-account-type-supply-and-fail-closed.md
  - ../specs/20260806_375_cash-account-support.md
  - ../specs/20260807_425_gfv-self-counting.md
  - ../adr/IADR-0165_gfv-self-counting-and-settled-cash-source-ban.md
  - ./README.md
  - ./FR-19_manipulation-detection-tests.md
  - ./FR-10_risk-controls-tests.md
---

# テスト仕様書: 取引ガード（FR-19・再実装 #332）

> 全面再実装（[#344](https://github.com/endazon/ai-stock-trading/issues/344)）の
> [#332](https://github.com/endazon/ai-stock-trading/issues/332) が扱う取引ガードのテスト仕様である。
> **相場操縦パターン検知の写像表は [FR-19_manipulation-detection-tests](./FR-19_manipulation-detection-tests.md) を
> 引き続き正とし**、本書は再実装で確定した規則（商品種別の 3 値化・ガードの適用範囲・
> 差金決済ガードの日本株現物限定・禁止銘柄の照合規則）を扱う。
>
> **2026-08-06 追補（[#375](https://github.com/endazon/ai-stock-trading/issues/375) / ADR-0021 / [IADR-0153](../adr/IADR-0153_broker-account-type-supply-and-fail-closed.md)）**:
> **米国口座の現金口座対応**（口座種別の照会・差金決済ガードの条件付き適用・GFV 回避ガード・GFV 回数の停止・
> 信用系の設定不能化）の写像表を §5〜§7 として追加した。**#332 の日本株現物限定は巻き戻していない**ことを
> 両方向で固定する（T-19-241 / T-19-251）。
>
> **2026-08-07 追補（[#425](https://github.com/endazon/ai-stock-trading/issues/425) / ADR-0025 決定2 / [IADR-0165](../adr/IADR-0165_gfv-self-counting-and-settled-cash-source-ban.md)）**:
> **GFV 発生回数の自前計数**の写像表を §8 として追加した。**数えているのは「自らのガードをすり抜けた買付」であり、
> ブローカーが GFV と判定した件数ではない**（両者が一致する保証はない・ADR-0025 §理由）。
> **`IADR-0153` の fail-closed は覆していない**ことを T-19-296 が固定する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-19**（取引ガード）／ FR-10（空売り統制との接続）・FR-20（段階別の商品種別＝ #333）は境界
- ユースケース（UC）: UC-06（ガード設定の変更・強制）
- 関連 ADR: **ADR-0016**（決定1＝ 3 値化・決定8＝段階解禁・決定13＝空売りは米国株のみ）・
  **ADR-0007**（ソフト設定・禁止銘柄）・ADR-0009（手仕舞い不停止）
- 受け入れ基準の所在: `02_requirements/01_requirements.md` FR-19 本文・受け入れ基準（103 行）／
  `05_trading-assumptions.md` §5（商品種別・差金決済防止・禁止銘柄・発注パターン・米国口座の種別）

## テスト対象・範囲

| 対象 | テストプロジェクト | ファイル |
| --- | --- | --- |
| 商品種別 3 値・実効値の解決・ガードの適用範囲 | `RiskManagementService.Domain.Tests` | `TradingGuardProductTypeTests.cs` |
| 差金決済ガードの適用範囲（日本株現物） | `RiskManagementService.Domain.Tests` | `TradingGuardProductTypeTests.cs` / `RiskEvaluatorTests.cs` |
| **口座種別 × 市場 × 商品種別**・GFV 回避・fail-closed（#375） | `RiskManagementService.Domain.Tests` | **`CashAccountControlsTests.cs`** |
| **現金口座での信用系の設定不能化**（#375） | `RiskManagementService.Application.Tests` / `…Api.Tests` | **`CashAccountSettingsGuardTests.cs`** / **`CashAccountGuardEndpointTests.cs`** |
| **口座種別の観測の保持・鮮度・供給経路**（#375） | `RiskManagementService.Application.Tests` / `…Infrastructure.Tests` | **`BrokerAccountObservationStoreTests.cs`** / **`BrokerAccountObservedConsumerTests.cs`** |
| **口座種別の照会と観測の発行**（#375） | `OrderExecutionService.Infrastructure.Tests` | `MoomooBrokerAdapterTests.cs` / `BrokerAvailabilityProbeServiceTests.cs` |
| **新拒否理由 3 種の序数・クラス分類**（#375） | `AiStockTrading.Shared.Contracts.Tests` | `RejectionReasonOrdinalStabilityTests.cs` / `RejectionReasonClassificationTests.cs` |
| 約定到達後に差金決済ガードが拘束すること（#270 回帰） | `RiskManagementService.Infrastructure.Tests` | `MoomooFillControlRegressionTests.cs` |
| 禁止銘柄の登録内容と照合規則 | `RiskManagementService.Domain.Tests` | `TradingGuardProductTypeTests.cs` |
| 相場操縦パターン禁止（既定・クラス C） | `RiskManagementService.Domain.Tests` / `…Application.Tests` | `TradingGuardProductTypeTests.cs` / `Manipulation/*` |
| 空売り統制との接続（有効化しても統制は解除されない） | `RiskManagementService.Domain.Tests` | `TradingGuardProductTypeTests.cs` / `ShortSellingControlsTests.cs` |
| 既定値の計画適合（`ProductType.Values` ほか） | `AiStockTrading.PlanConformance.Tests` | `PlanRiskDefaults.cs` / `ActualDefaults.cs` |
| 画面（SC-02）の商品種別 3 値・危険な緩和の確認 | `frontend`（vitest） | `risk/contracts.test.ts` / `sc02-risk-settings/RiskSettingsPage.guard.test.tsx` |

対象外（担当 issue）: 段階別の商品種別強制（#333）・発注先の 2 軸分離（#334）・
相場操縦しきい値の較正（#251）・空売り文脈の供給元（#342）・信用買いの建玉表現（未起票）。

## 3 点セット（テスト戦略 §2）

### 1. 境界値／組み合わせ表

| ID | 観点 | 入力 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-201 | 商品種別 × 有効/無効 × 市場（**12 通り**） | 3 値 × {有効, 無効} × {日本, 米国} | 有効なら `ProductTypeDisabled` を含まない／無効なら必ず含む | `商品種別3値と有効無効と市場の組み合わせで商品種別ガードが決まる` |
| T-19-202 | **既定＝現物のみ有効** | `TradingDefaults.CreateGuardSettings()` | `{ Cash }`（`MarginLong` / `ShortSell` を含まない） | `既定の有効な商品種別は現物のみである` |
| T-19-203 | 3 値の**独立性**（部分集合 8 通り） | 空集合〜全集合 | 種別 T が拒否されないのは T ∈ 有効集合のときに限る | `商品種別の有効化は互いに独立である` |
| T-19-204 | 差金決済の適用範囲（市場 × 種別の全組み合わせ）**※信用口座（既定）における現行挙動** | 同日取引済み × 2 市場 × 3 種別 | `SameDayReentry` は**日本株 × 現物**のときだけ | `差金決済ガードは日本株現物の新規建てにのみ作動する` |
| T-19-205 | 計画登録の禁止銘柄 3 件 | 6457 / 6902 / 6502（日本株） | 拒否され、理由・登録日（2026-07-07）を持つ | `計画が登録した禁止銘柄は発注前に拒否される` |
| T-19-206 | 既定値の計画適合 | `ProductType.Values` | `Cash, MarginLong, ShortSell` | `PlanConformanceTests`（既知逸脱レジストリ） |

### 2. プロパティベース（入力によらず成り立つ不変条件）

| ID | 不変条件 | 反証の意味 | テスト |
| --- | --- | --- | --- |
| T-19-211 | **無効な商品種別は、どの市場・どの金額・どの数量でも通らない**（2 種別 × 2 市場 × 4 価格 × 3 数量） | 金額を小さくすれば無効な種別が通る＝統制が金額に依存して漏れる | `無効な商品種別はどの市場でもどの金額でも通らない` |
| T-19-212 | **新規売り建ての実効商品種別は申告値によらず空売り**（申告 3 通り） | 申告を変えれば空売り統制を外せる | `新規売り建ての実効商品種別は申告値によらず空売りである` |
| T-19-213 | 新規売り建て**以外**の実効商品種別は申告どおり（過剰な読み替えをしない） | 手仕舞いや買いの種別が勝手に書き換わる | `新規売り建て以外の実効商品種別は申告どおりである` |
| T-19-214 | 商品種別の有効化は互いに独立（T-19-203 と同一の性質を部分集合全体で） | 1 つ有効化すると他も通る（束ね制御） | `商品種別の有効化は互いに独立である` |

### 3. 否定形（統制を迂回できないこと）

**否定形は「拒否されること」ではなく「迂回経路が塞がれていること」を見る。**

| ID | 塞ぐ迂回 | 手口 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-221 | **差金決済ガードの誤適用**（本 issue の必須要請）**※信用口座での挙動。現金口座は T-19-241 が逆を固定する** | 米国株で同日再エントリー | `SameDayReentry` を含まず**承認**される | `差金決済ガードは米国株では作動しない` |
| T-19-222 | 誤適用の是正で**取りこぼさない**（正の対照） | 日本株現物で同日再エントリー | 拒否され `SameDayReentry` | `差金決済ガードは日本株現物では作動する` |
| T-19-223 | 米国株の回転が**無統制**になっていない | 日次発注枠を使い切った状態で同日再エントリー | `DailyOrderAmountExceeded` で拒否 | `米国株の回転数は日次発注金額上限で管理される` |
| T-19-224 | **申告値の詐称** | 新規売り建てを `Cash` と申告 | `ProductTypeDisabled` ＋ `ShortSellDisabled` | `空売りを現物と申告してもガードを迂回できない` |
| T-19-225 | **別表記**（禁止銘柄） | `aapl` / `AAPL␣` / `␣Aapl` | いずれも `BannedSymbol` | `禁止銘柄は表記差で迂回できない` |
| T-19-226 | **商品種別の付け替え**（禁止銘柄） | 6457 を現物 / 信用買い / 空売りで発注 | いずれも `BannedSymbol` | `禁止銘柄は商品種別を変えても迂回できない` |
| T-19-227 | **統制違反の計上汚染** | 商品種別ガードの拒否 | クラス B（計上しない）／禁止銘柄はクラス C（計上する） | `商品種別ガードの拒否は統制違反に計上されない` |
| T-19-228 | **手仕舞いの封鎖**（逆向きの事故） | 無効な種別（空売り・信用買い）の Close | **承認**される（ADR-0009） | `無効な商品種別でも手仕舞いは止めない` |
| T-19-229 | **設定の緩和で統制ごと解除** | 商品種別で空売りを有効化 | `ShortSellDisabled` は消えるが `BorrowUnavailable`（フェイルクローズ）で止まる | `空売りを有効化しても専用統制は解除されない` |
| T-19-230 | **二重の情報源** | 空売り統制値だけを緩める | 有効・無効は変わらない（`Guard.EnabledProductTypes` が単一情報源） | `空売りの有効無効は取引ガードの商品種別だけで決まる` |
| T-19-231 | 画面からの**危険な緩和**の素通し | SC-02 で信用買い／空売りを有効化 | 確認チェックなしでは保存不可・警告に該当種別が出る | `treats enabling margin-long / short-selling as a dangerous change…`（vitest） |

## 計画確定値との適合検査（IADR-0127）

`ProductType.Values` の既知逸脱（担当 #332）を解消し、レジストリから削除した。
**赤→緑の実測**（IADR-0127 の機械的証明）:

| 段階 | 結果 |
| --- | --- |
| 実装を 3 値へ一致させ、登録行を残したまま実行 | **Failed: 2, Passed: 4**（検査3「登録済み逸脱は実際に逸脱している」・検査4「登録済み逸脱の現行値は実装の実際値と一致する」が `ProductType.Values` を名指し） |
| 登録行を削除して実行 | **Failed: 0, Passed: 6** |

なお `Guard.EnabledProductTypes`（既定＝ `Cash`）・`Guard.BannedSymbols`（6457/6502/6902）・
`Guard.PreventSameDayReentry`（True）・`Guard.ProhibitManipulativeOrderPatterns`（True）は
**逸脱していなかった**ため、レジストリに登録が無く、`PlanConformanceTests` が常時突き合わせている。

## 5. 現金口座対応（#375・ADR-0021・IADR-0153）— 口座種別 × 市場 × 商品種別

すべて `RiskManagementService.Domain.Tests/CashAccountControlsTests.cs`（明記のあるものを除く）。

| ID | 観点 | 入力 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-240 | **組み合わせ表**（口座種別 × 市場 × 商品種別・10 通り） | {信用, 現金} × {日本, 米国} × {現物, 信用買い, 空売り} | `現物 && (日本市場 ‖ 現金口座)` のときだけ適用 | `差金決済ガードの適用は口座種別と市場と商品種別の組で決まる` |
| T-19-241 | **現金口座では米国株に適用される**（両方向のうち正） | 現金口座 × AAPL 当日取引済み | 拒否され `SameDayReentry` | `現金口座では米国株の同日再エントリーを拒否する` |
| T-19-242 | **信用口座では米国株に適用されない**（#332 の退行防止） | 信用口座 × AAPL 当日取引済み | `SameDayReentry` を含まない | `信用口座では米国株の同日再エントリーを拒否しない` |
| T-19-243 | 日本株現物は口座種別に依存しない | 信用口座 × 7203 当日取引済み | 拒否され `SameDayReentry` | `信用口座でも日本株現物の同日再エントリーは拒否する` |
| T-19-244 | 口座種別**不明**でも日本の規制は効き続ける | 観測 null × {日本, 米国} × 現物 | 日本のみ適用 | `口座種別が不明でも日本株現物への差金決済ガードは効き続ける` |
| T-19-245 | 口座が対応する商品種別 | {信用, 現金} × 3 種別 | 現金口座は**現物のみ** | `口座が対応する商品種別を口座種別ごとに固定する` |
| T-19-246 | 既定の口座種別（ADR-0021 決定1） | `TradingDefaults.CreateGuardSettings()` | `AccountType.Margin` | `設定の既定の口座種別は信用口座である` |
| T-19-247 | 口座種別の**序数不変**（永続化と結合） | `AccountType` | Margin=0 / Cash=1 | `RiskSettingsSerializationAccountTypeTests.口座種別の序数は不変である` |
| T-19-248 | 拒否理由 3 種の**序数不変** | `RejectionReason` | 25 / 26 / 27（末尾追加） | `RejectionReasonOrdinalStabilityTests.拒否理由の序数は不変である` |

## 6. 現金口座対応 — GFV 回避ガードと GFV 回数（境界値）

| ID | 観点 | 入力 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-250 | **決済済み資金の境界値**（5 通り） | 決済済み $1,000・当日累計 {0, 900} × 注文額 | 「累計＋本注文 > 資金」でのみ `CashAccountSettlementHold`。**ちょうどは通す** | `決済済み資金を超える買付を境界値で拒否する` |
| T-19-251 | **累計で超過する経路**（#27 と同じ穴） | 累計 900 ＋ 注文 101（各々は上限内） | 拒否 | 同上（`alreadyOrdered: 900, notional: 101`） |
| T-19-252 | 信用口座では本ガードが働かない | 信用口座・決済済み資金 null | `CashAccountSettlementHold` を含まず承認 | `信用口座では決済済み資金が未供給でも買付を止めない` |
| T-19-253 | **GFV 回数のしきい値**（5 通り） | {null, 0, 1, 2, 3} | 停止＝{null, 2, 3}／警告＝{2, 3} | `GFV発生回数の停止と警告のしきい値を境界値で固定する` |
| T-19-254 | 停止基準到達で新規建てが止まる | 現金口座 × 回数 {null, 2, 3} | 拒否され `GoodFaithViolationLimitReached` | `GFV発生回数が停止基準に達していれば現金口座の新規建てを拒否する` |
| T-19-255 | 1 回までは止めない（正の対照） | 現金口座 × 回数 1 | 承認 | `GFV発生回数が1回までなら現金口座の新規建てを止めない` |
| T-19-256 | 信用口座では回数の供給を要求しない | 信用口座 × 回数 null | 承認 | `信用口座ではGFV回数が未供給でも新規建てを止めない` |
| T-19-257 | **観測の鮮度**（境界値 29 / 30 / 31 分） | 記録から経過時間 | 30 分ちょうどは有効・31 分で失効 | `BrokerAccountObservationStoreTests.観測は有効期間を過ぎると失効する` |
| T-19-258 | 有効期間が probe のクランプ上限と同値 | `MaxAge` | 30 分 | `BrokerAccountObservationStoreTests.有効期間は定期probeのクランプ上限と同値である` |

## 7. 現金口座対応 — 否定形（**本 issue の主眼**）

**否定形は「拒否されること」ではなく「迂回経路が塞がれていること」と「止めてはならないものを止めていないこと」を見る。**

| ID | 塞ぐ迂回／守る不変条件 | 手口・状況 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-260 | **口座種別が分からないまま発注する** | 観測 null × moomoo 発注先の新規建て | 拒否され `BrokerAccountTypeUnverified` | `口座種別を照会できていなければ新規建てを拒否する` |
| T-19-261 | **設定値で統制を偽る**（決定3） | 観測 {現金, 信用} × 設定 {信用, 現金}（食い違い 2 通り） | 拒否 | `照会結果と設定値が食い違えば新規建てを拒否する` |
| T-19-262 | 上記が「常に拒否」ではない（トートロジー防止） | 観測＝設定＝信用口座 | 承認・`BrokerAccountTypeUnverified` を含まない | `照会結果と設定値が一致していれば口座種別を理由に拒否しない` |
| T-19-263 | **決済済み資金が分からないのに通す** | 現金口座 × 資金 null × Buy | 拒否され `CashAccountSettlementHold` | `決済済み資金が未供給なら現金口座の買付を拒否する` |
| T-19-264 | **GFV 回数が分からないのに通す** | 現金口座 × 回数 null | 拒否され `GoodFaithViolationLimitReached` | `GFV発生回数が停止基準に達していれば…(violationCount: null)` |
| T-19-265 | **現金口座で信用系を発注する** | 現金口座 × {信用買い, 空売り} の新規建て（設定は 3 種すべて有効） | 拒否され `ProductTypeDisabled` | `現金口座では信用買いと空売りの新規建てを拒否する` |
| T-19-266 | **現金口座で信用系を設定する** | 現金口座を観測中に信用系を有効化 | `ArgumentException`（HTTP 400）。**設定も履歴も変わらない** | `CashAccountSettingsGuardTests.現金口座では信用買いと空売りを有効化できない` / `拒否された設定変更は現行値も変更履歴も変えない` |
| T-19-267 | **API 直叩きで設定側の統制を迂回する** | `PUT /risk-controls/settings/guard` を直接叩く | 400。有効集合は変わらない | `CashAccountGuardEndpointTests.現金口座を観測している状態で信用系を有効化する要求は400で拒否される` |
| T-19-268 | **全置換 PUT の送り漏らしで口座種別が消える** | 現金口座を設定後、口座種別を含まない本文で別項目を更新 | 現行の口座種別が保たれる | `CashAccountGuardEndpointTests.口座種別を送らないガード更新は現行の口座種別を保つ` |
| T-19-269 | **「不明なら信用口座」をアダプタが返す** | SDK の `TrdAccType` = {Unknown, TFSA, RRSP, SRRSP, Derivatives, 未知値} | すべて `null`（不明） | `MoomooBrokerAdapterTests.SDKの口座種別を未知は不明へ倒して写像する` |
| T-19-270 | **不明を観測として発行してしまう** | 照会が `null` を返す巡回 | `BrokerAccountObserved` を**発行しない**（稼働観測は発行される） | `BrokerAvailabilityProbeServiceTests.口座種別が不明なら口座観測を発行しない` |
| T-19-271 | **決済済み資金を代替値で埋める** | 現金口座を照会 | `SettledCashInBase` / `GoodFaithViolationCount` はいずれも `null` | `MoomooBrokerAdapterTests.決済済み資金とGFV回数は供給しない` |
| T-19-272 | **古い観測で発注する** | 記録から 2 時間経過 | スナップショットに載らない（＝新規建てが止まる） | `BrokerAccountObservationStoreTests.失効した観測はスナップショットに載らない` |
| T-19-273 | **逆行する観測で古い種別へ戻す** | 現金口座の観測後に、より古い時刻の信用口座観測 | 現金口座のまま | `BrokerAccountObservationStoreTests.逆行する観測は無視する` / `BrokerAccountObservedConsumerTests.順序が入れ替わって届いても新しい観測が保たれる` |
| **T-19-274** | **手仕舞いを止めてしまう**（FR-10 の不変条件・ADR-0009） | 観測 null × Close × {現物, 信用買い, 空売り買戻し} | **承認**される | `口座種別が不明でも手仕舞いは止めない` |
| T-19-275 | 口座種別を切り替えた後に信用建玉が閉じられない | 現金口座 × 信用買いの Close | **承認**される | `現金口座でも信用建玉の手仕舞いは止めない` |
| T-19-276 | 決済済み資金・GFV 回数の未供給で手仕舞いが止まる | 現金口座 × 資金 null / 回数 null × Close | **承認**される | `決済済み資金が未供給でも手仕舞いの買戻しは止めない` / `GFV発生回数が未供給でも手仕舞いは止めない` |
| T-19-277 | 現金口座で**売却**まで止めてしまう | 現金口座 × 資金 null × Sell の新規建て | `CashAccountSettlementHold` を含まない | `決済済み資金が未供給でも現金口座の売却は本ガードで止めない` |
| T-19-278 | 内蔵 paper が口座種別で止まる | 内蔵 paper × 観測 null × 新規建て | **承認**される | `内蔵paperの新規建ては口座種別を要求しない` |
| T-19-279 | 内蔵 paper の免除が迂回路になる | `RequiresVerifiedAccount` × {MoomooSimulate, MoomooReal} | いずれも `true` | `外部へ発注する発注先は口座種別の確認を要求する` |
| T-19-280 | 現金口座で空売り統制群を評価してしまう | 現金口座 × 空売り新規建て | `ProductTypeDisabled` のみ。`ShortSellDisabled` / `BorrowUnavailable` / `StopOrderRequired` を含まない | `現金口座では空売り統制群を評価しない` |
| T-19-281 | 口座種別**不明**を口実に空売り統制を省く（緩む側） | 観測 null × 空売り新規建て | `BrokerAccountTypeUnverified` **と** `BorrowUnavailable` の両方 | `口座種別が不明なら空売り統制群を評価する` |
| **T-19-282** | **3 種をクラス C（統制違反）へ混ぜる** | 3 種＋`SameDayReentry` だけの拒否 | 統制違反として計上されない・いずれもクラス C でない | `RejectionReasonClassificationTests.現金口座の拒否理由だけの拒否はいくつ重なっても統制違反にならない` |
| T-19-283 | 3 種を互いの別名にする | 3 種のコード | 互いに異なるコードとして存在する | `RejectionReasonClassificationTests.現金口座の3種の拒否理由は互いに別のコードである` |
| T-19-284 | 観測が無いのに既定値が入る | イベント 0 件 | ストアは `null` を返す | `BrokerAccountObservedConsumerTests.観測が届かなければ口座種別は未確定のままである` |
| T-19-285 | 旧行（口座種別なし）の読み方 | `configuredAccountType` キーの無い設定 JSON | 信用口座として読む（統制は照会結果で切り替わるため緩まない） | `RiskSettingsSerializationAccountTypeTests.口座種別を持たない旧行は信用口座として読まれる` |

## 8. GFV 発生回数の自前計数（#425・ADR-0025 決定2・IADR-0165）

> **★ 何を数えているのかを取り違えないこと。** 自前で数えられるのは**自らのガードをすり抜けた買付**だけであり、
> **ブローカー側が独自に GFV と判定した事象は捕捉できない**。本計数は「ブローカーの GFV カウンタの写し」ではなく
> 「**自らのガードの失敗回数**」であり、**両者が一致する保証はない**（ADR-0025 §理由）。

### 8.1 しきい値・「0 件」と「未供給」の区別（境界値）

すべて `RiskManagementService.Domain.Tests/GoodFaithViolationCountingTests.cs`。

| ID | 観点 | 入力 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-290 | **停止基準は 2 件のまま**（計画確定値・#425 は変えない） | `GoodFaithViolationStopThreshold` | 2 | `GFVの停止基準は2件のままである` |
| T-19-291 | **停止の境界**（1 件では止まらない・2 件で止まる） | 計数 {0, 1, 2, 3} | 停止＝{2, 3} | `自前計数は2件目から新規建てを止める` |
| T-19-292 | **警告の境界**（停止と同じ回数で立つ） | 計数 {0, 1, 2, 3} | 警告＝{2, 3} | `自前計数は2件目から警告する` |
| T-19-293 | **「未供給」と「0 件」の区別**（#424 の表示規約と同じ） | {null, Observed(0)} | 未供給は止め・0 件は止めない | `未供給は止め_0件は止めない` / `数えた結果の0件は未供給と別物である` |
| T-19-294 | 未供給は**警告しない**（供給の不在は停止側で表す） | null | 警告しない | `未供給は警告しない` |
| T-19-295 | 負の件数は計数結果として成立しない | -1 | `ArgumentOutOfRangeException` | `負の件数は受け付けない` |

### 8.2 fail-closed と「現金口座はなお使えない」ことの維持（**本 issue の主眼**）

| ID | 塞ぐ迂回／守る不変条件 | 状況 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-296 | **`IADR-0153` の fail-closed を解除してしまう** | 現金口座 × 計数 0 件（供給あり）× 決済済み資金 **null** | 拒否され `CashAccountSettlementHold`。**現金口座はなお使えない**（ADR-0025 決定3） | `自前計数を供給しても決済済み資金が無い限り現金口座の買付は止まる` |
| T-19-297 | **計数が分からないのに通す**（fail-open） | 現金口座 × 計数 **未供給** | 拒否され `GoodFaithViolationLimitReached` | `計数が未供給なら現金口座の新規建てを拒否する` |
| T-19-298 | **未供給を `CashAccountSettlementHold` へ写像する**（解除条件の取り違え） | 同上（決済済み資金は供給あり） | `GoodFaithViolationLimitReached` を含み `CashAccountSettlementHold` を**含まない** | `計数の未供給を決済保留へ写像しない` |
| T-19-299 | 上記が「常に拒否」ではない（トートロジー防止） | 現金口座 × 計数 0 件 × 決済済み資金あり | 承認 | `計数が0件なら本統制では新規建てを止めない` |
| T-19-300 | 手仕舞いを止めてしまう（ADR-0009 の不変条件） | 現金口座 × 計数 未供給 × Close | **承認**される | `計数が未供給でも手仕舞いは止めない` |
| T-19-301 | **信用口座が本統制の影響を受ける**（否定形） | 信用口座 × 計数 {未供給, 3} | いずれも承認 | `信用口座では計数が未供給でも新規建てを止めない` / `信用口座では計数が停止基準に達していても新規建てを止めない` |

### 8.3 計数の対象事象（＝ガードが拒否しようとする事象と同一であること）

| ID | 観点 | 入力 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-302 | **判定式が単一情報源である**（2 か所に書かない） | 決済済み資金 {null, 1000} × 金額 {999, 1000, 1001} | 「未供給 ‖ 超過」でのみ真。**ちょうどは真でない** | `決済済み資金を超える買付の判定は単一の述語である` |
| T-19-303 | **拒否する事象と数える事象が一致する** | 同じ入力を `RiskEvaluator` と検出器へ与える | 常に同じ判定 | `ガードが拒否する事象と計数が数える事象は一致する` |
| T-19-304 | 検出の条件表（口座種別 × 方向 × 建玉効果 × 資金） | 6 通り | 現金 × Buy × Open × 説明不能 のときだけ true | `未決済資金による買付だけをGFV発生として数える` |
| T-19-305 | **口座種別が不明なのに数える**（否定形） | 観測 null × Buy × Open | 数えない（信用口座の通常の売買が違反として積み上がるのを防ぐ） | `口座種別を確認できていなければGFV発生として数えない` |

### 8.4 供給経路・計上単位・監査（FR-11）

`RiskManagementService.Application.Tests/GoodFaithViolationCountingServiceTests.cs` および
`RiskManagementService.Infrastructure.Tests/GoodFaithViolationCountingConsumerTests.cs`。

| ID | 観点 | 状況 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-306 | 記録される場合 | 現金口座 × 決済済み資金 未供給 × 買付の約定 | 台帳へ 1 件・イベントを返す・**決済済み資金は `null` のまま記録** | `未決済資金による買付の約定をGFV発生として記録する` |
| T-19-307 | 金額は基準通貨で積む | 日本株（`FxRateToBase` 0.0064） | `数量 × 単価 × レート` | `買付金額は承認Intentの換算レートで基準通貨へ揃える` |
| T-19-308 | **記録されない場合**（否定形 6 通り） | 信用口座／観測なし／資金の範囲内／売却／手仕舞い／約定 0 | いずれも記録しない | `信用口座の買付は記録しない` ほか 5 件 |
| T-19-309 | **承認台帳に相関が無い約定**（推測で記録しない） | 承認なし | 記録しない | `承認台帳に相関が無ければ記録しない` |
| T-19-310 | **計上単位は 1 注文 1 件**（再送・部分約定） | 同一 `OrderId` を 2 度／累積数量の進行 | 1 件のまま | `同一注文の再配送は二重計上しない` / `部分約定の進行は1件として数える` |
| T-19-311 | 別注文は別件（正の対照） | `OrderId` 2 種 | 2 件 | `別注文は別件として数える` |
| T-19-312 | 2 件で停止基準に達する | 記録を 0 → 1 → 2 件 | 2 件目で `BlocksNewEntry` | `記録が2件に達したら新規建ての停止基準に達する` |
| T-19-313 | **台帳が未結線なら未供給**（fail-closed。0 件で埋めない） | ストア注入なし | `GoodFaithViolations` が `null` | `台帳が結線されていなければ未供給のまま渡す` |
| T-19-314 | 台帳が結線されていれば **0 行でも「0 件」** | ストア注入・行なし | `Observed(0)` | `台帳が結線されていれば0件でも供給する` |
| T-19-315 | **監査（FR-11）へ運ばれる** | 現金口座の買付が約定 | `GoodFaithViolationRecorded` を発行・取引日は**米国東部時間** | `未決済資金による買付は記録され監査イベントが発行される` |
| T-19-316 | 監査を無関係な事象で汚さない（否定形） | 信用口座／観測なし | 発行しない | `信用口座の約定では記録も発行もしない` / `口座種別を確認できていなければ記録も発行もしない` |
| T-19-317 | **監査の要約が限界を明記する** | `GoodFaithViolationRecorded` | 「自前計数」「ガードの失敗」「ブローカーの GFV 判定とは一致しない」「未供給」を含む | `AuditEntryFactoryTests.GoodFaithViolationRecorded_は自前計数でありブローカー判定と一致しないことを明記する` |

### 8.5 決済済み資金の代替値の遮断（**構造で止める**）

| ID | 塞ぐ迂回 | 手口 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-318 | **`MaxCashBuy` を決済済み資金の供給元にする** | `.cs` に `qtys.MaxCashBuy` を書く | 検査が落ちる | `scripts.repo.test.js: check-banned-settled-cash-sources: MaxCashBuy を分母に据える形を検出する` |
| T-19-319 | `AvlWithdrawalCash` / `MaxWithdrawal` を使う | `.cs` に `funds.AvlWithdrawalCash` / `f.MaxWithdrawal` | 検査が落ちる | 同ファイル 2 件 |
| T-19-320 | **検査が効きすぎて禁止の理由を書けなくなる**（否定形） | コメント・XML ドキュメントでの言及 | 検出しない | `行コメント中の言及は検出しない` / `XML ドキュメント中の言及は検出しない` / `ブロックコメント中の言及は検出しない` |
| T-19-321 | 前方一致の別名を巻き込む（否定形） | 撤退ドメインの `MaxWithdrawalRatio` | 検出しない | `前方一致の別名（撤退ドメイン）は巻き込まない` |
| T-19-322 | 実ツリーの回帰 | リポジトリ全体 | 参照 0 件 | `実ツリー: 決済済み資金の代替値のコード参照が無い（#425 の回帰）` |
| T-19-323 | **ブローカー照会の型に欄を生やす**（型レベル） | `BrokerAccountState` のプロパティ | `GoodFaithViolationCount` / `MaxCashBuy` / `AvlWithdrawalCash` / `MaxWithdrawal` を持たない | `ブローカー照会の型はGFV発生回数の欄を持たない` / `ブローカー照会の型は決済済み資金の代替値の欄を持たない` |

### 変異検査（実測・2026-08-07・#425）

「実装したが効いていない」を排除するため、実装を意図的に壊して**テストが赤くなること**を実測した。
壊した各ファイルは事前に退避し、実行後に `cp` で復元して全テスト緑を確認している。

| # | 壊した箇所 | 変異内容 | 落ちたテスト |
| --- | --- | --- | --- |
| **a** | `MoomooBrokerAdapter` ＋ `BrokerAccountState` | **`MaxCashBuy` / `AvlWithdrawalCash` / `MaxWithdrawal` を決済済み資金として使えるようにする**（最重要・否定形）。アダプタで `funds.MaxCashBuy ?? funds.AvlWithdrawalCash ?? funds.MaxWithdrawal` を分母に据え、`BrokerAccountState` に `MaxCashBuy` / `GoodFaithViolationCount` の欄を復活させた | **構造テストが赤**: `check-banned-settled-cash-sources.js` が **7 件**検出して exit 1（T-19-318 / T-19-319）／`scripts.test.js` の「実ツリー」テストが赤（T-19-322）／xUnit 2 件（T-19-323: `ブローカー照会の型はGFV発生回数の欄を持たない` / `…決済済み資金の代替値の欄を持たない`） |
| **b** | `AccountTypePolicy.BlocksForGoodFaithViolations` | `tally is null \|\|` を `tally is not null &&` へ（**未供給を通す＝fail-open へ倒す**） | **6 件**（T-19-293 `未供給は止め_0件は止めない` / T-19-297 `計数が未供給なら現金口座の新規建てを拒否する` / T-19-298 `計数の未供給を決済保留へ写像しない` / T-19-313 `台帳が結線されていなければ未供給のまま渡す` / 既存 T-19-253・T-19-254 の `violationCount: null` ケース） |
| **c** | `GoodFaithViolationTally.BlocksNewEntry` | しきい値 `>= 2` → `>= 3`（**境界を 1 件ずらす**） | **5 件**（T-19-291 `自前計数は2件目から新規建てを止める(count: 2)` / T-19-312 `記録が2件に達したら新規建ての停止基準に達する` / `記録した件数がスナップショットへ反映される` / 既存 T-19-253・T-19-254 の `violationCount: 2` ケース） |
| **d** | `PortfolioSnapshotBuilder` | 台帳が未結線のとき `Observed(0)` を渡す（**未供給を 0 件と同一視**） | **1 件**（T-19-313 `台帳が結線されていなければ未供給のまま渡す`） |
| **d2** | `PortfolioSnapshotBuilder` | 逆向き: 数えた結果 0 件を `null` へ倒す（**0 件を未供給と同一視**） | **1 件**（T-19-314 `台帳が結線されていれば0件でも供給する`） |

- 壊した 5 ファイルは事前にスクラッチパッドへ退避し、実行後に `cp` で復元して **md5 のバイト一致**を確認した。
- **d / d2 を両方向で試したのは、片方向だけでは「0 と未供給の区別」が守られている証明にならないため**である
  （#424 の表示規約と同じ区別）。

### 変異検査（実測・2026-08-06）

「実装したが効いていない」を排除するため、ガードを意図的に壊して**テストが赤くなること**を実測した。
壊した各ファイルは事前に退避し、実行後に `cp` で復元して**バイト一致（md5）と全テスト緑**を確認している。

| # | 壊した箇所 | 変異内容 | 落ちたテスト |
| --- | --- | --- | --- |
| a | `AccountTypePolicy.AppliesSameDayReentry` | `accountType == AccountType.Cash` → `!=`（適用を逆転） | **7 件**（うち 2 件は #332 由来の既存テスト `差金決済ガードは米国株では作動しない` / `…は日本株現物の新規建てにのみ作動する`） |
| b | `RiskEvaluator`（fail-closed） | 観測が無ければ「信用口座だった」とみなし `accountVerified = true` | **4 件**（T-19-260 / T-19-261 × 2 / T-19-281） |
| b2 | `RiskEvaluator` | 下流の `accountType` だけ `?? AccountType.Margin` | **0 件。等価変異である**（`Supports(Margin, *)` も `AppliesShortSellControls(Margin)` も `null` の場合と同値であり、観測有無の安全性は b が検証する `BrokerAccountTypeUnverified` が担っている） |
| b3 | `RiskEvaluator`（fail-closed） | 観測が無いときだけ素通し（食い違い検知は残す） | **2 件**（T-19-260 / T-19-281） |
| b4 | `InMemoryBrokerAccountObservationStore` | 失効判定を削除（古い観測を無期限に信じる） | **2 件**（T-19-257 / T-19-272） |
| c1 | `AccountTypePolicy.BlocksForGoodFaithViolations` | `violationCount is null ||` を削除 | **2 件**（T-19-253 / T-19-254） |
| c2 | `RiskEvaluator`（GFV 回避ガード） | 未供給なら通す **＋** 当日累計を無視する（＝未決済資金を買付余力へ算入する向き） | **2 件**（T-19-263 / T-19-251） |
| d | `AccountTypePolicy.Supports` | 現金口座でも信用系を成立させる | **10 件**（Domain 5 / Application 3 / Api 2。3 層すべてで検出） |

## テストデータ

- equity: `TradingDefaults.InitialCapital`（＝ $3,000。#364 / IADR-0152 決定3 で基準通貨が USD になり、参照レートによる 1 点換算は廃止した）
- 銘柄: `AAPL`（米国株）／ `7203`（日本株・禁止リスト外）／ `6457`・`6902`・`6502`（計画登録の禁止銘柄）
- 空売りの成立条件は米国株・株価 $5.00 以上・逆指値つき（ADR-0016 決定7・決定2(b)）

## 未カバー・実施予定

| 項目 | 理由 / 担当 |
| --- | --- |
| 段階別の商品種別強制（Stage 2＝現物のみ） | #333（本ガードの有効集合と AND で効かせる） |
| 相場操縦検知のしきい値較正 | #251（既存の検知アルゴリズムのテストは `Manipulation/*` が担う） |
| 信用買いの建玉・金利・必要証拠金 | 未起票（実弾解禁は Stage 3） |
| ~~禁止銘柄・市場ガードの手仕舞い適用の是非~~ | **✅ 2026-08-04 裁定済み（ADR-0007 追補）＝選択肢 A（全注文適用）。実装は現状のままで一致**。適用範囲は既存テストで固定済みであり、追加のテストは不要（#380） |
| **現金口座の GFV 回避ガードが実データで作動すること**（#375） | **決済済み資金の情報源が moomoo API に存在しない**（IADR-0153 決定4）。判定は境界値テストで固定済みだが、**実運用では常に「未供給」側で止まる**。`TrdFlowSummary` からの導出は未検証（計画へ環流済み） |
| **GFV 発生回数の追跡と 2 回目の警告通知**（#375） | 回数の情報源が無く、Discord への通知も未結線。判定（純関数）のみテスト済み |
| **現金口座を選択した状態の E2E**（#375） | 上記 2 件が解決するまで到達できない。SC-02 に口座種別を選ぶ UI も無い（本 issue の範囲外） |

## 関連仕様

- 機能仕様書: [FR-19 取引ガード](../functional/FR-19_trading-guard.md)・[FR-10 リスク統制](../functional/FR-10_risk-controls.md)
- 作業仕様書: [20260804_332_trading-guards](../specs/20260804_332_trading-guards.md)・[20260806_375_cash-account-support](../specs/20260806_375_cash-account-support.md)
- 実装 ADR: [IADR-0153](../adr/IADR-0153_broker-account-type-supply-and-fail-closed.md)（#375・口座種別の供給と fail-closed）・
  [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)・
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)
- テスト仕様書: [FR-19 相場操縦パターン検知](./FR-19_manipulation-detection-tests.md)・
  [FR-10 リスク統制（再実装）](./FR-10_risk-controls-tests.md)・[FR-10 リスクガードコア](./FR-10_risk-guard-core-tests.md)

## 未決事項

- 差金決済ガードの適用範囲を計画本文（FR-19）が「日本株の現物取引」と明記している一方、
  06_daytrading-review §2.2 には「日本の差金決済禁止は moomoo の米国株現物にも適用される」という
  2026-07 時点の調査記述が残る。**口座種別の裁定（2026-07-31・project-planning#81）と FR-19 の
  2026-08-01 改訂が新しく、実装はそちらに従った**。§2.2 の記述の更新要否は計画側の判断に委ねる。
  → 2026-08-04 に計画へ環流した（[feedback/20260804_fr19-guard-scope.md](../../feedback/20260804_fr19-guard-scope.md)
  論点 3）。あわせて論点 1（商品種別ガードの Open 限定の明示化）・論点 2（禁止銘柄ガードの
  Close 適用の裁定）も同文書で環流している。
  → **✅ 3 論点とも 2026-08-04 に裁定済み**（ADR-0007 追補。論点 1＝Open のみ／論点 2＝全注文／論点 3＝§2.2 更新済み）。
  **いずれも実装の変更は不要**であった（#380）。
  → **2026-08-06 に §2.2 の記述も環流の対象へ加えた**（[feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md](../../feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md)
  論点 3）。ADR-0021 により「米国株では GFV が発生しない」は**信用口座に条件づけられた命題**になったため、
  §2.2 の「米国株現物にも適用される」という記述は**現金口座については正しい**。3 文書の無条件の記述を
  条件付きへ改めることを提案している。


### GFV 違反による停止の解除（#464・ADR-0028 決定2/決定3・[IADR-0182](../adr/IADR-0182_gfv-violation-clearing.md)）

**解除は「消す」ことではない。** 決定1 が「違反記録は失効させない」と定めており、
解けるのは**停止**であって記録ではない。

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-19-40 | GFV 違反 2 件（停止中） | 解除する | 計数が 0 になり**停止が解ける**。解除した `OrderId` の一覧と残件数が返る | FR-19 | 自動 |
| T-19-41 | 同上 | 解除後に台帳を照会する | **違反記録 2 件はそのまま残る**（決定1。`GetRecordedBetween` は解除の有無に関わらず全件を返す） | FR-19, FR-11 | 自動（否定形・決定1 の核心） |
| T-19-42 | GFV 違反 1 件 | 同じ記録を 2 度解除する | 2 度目は**受理されない**（解除対象なし）。件数は狂わず記録も残る | FR-19 | 自動（否定形・冪等） |
| T-19-43 | GFV 違反 1 件 | 理由を空（空白のみ・未指定）で解除する | **受理せず解除行を 1 件も書かない**（決定2「原因の是正が済んでいることの確認を伴う」） | FR-19, FR-11 | 自動（否定形） |
| T-19-44 | 停止していない | 解除する | **422**（成功に見せない）。「解除した」と記録すると**何も起きていない操作が監査上の事実になる** | FR-19, FR-11 | 自動（否定形） |
| T-19-45 | 発生回数が**未供給**（ストア未結線） | 解除する | **拒否は解けない**（ADR-0028 明記。解除は件数にしか作用せず `null` を非 `null` にする手段が無い） | FR-19 | 自動（否定形・最重要） |
| T-19-46 | 多層認証: DM／許可外の利用者／設定が空 | `/gfv clear` を送る | **すべて拒否し Risk を呼ばない**（閂が「呼んだ後で無視する」形なら統制の解除がサーバ側で起きてしまう） | FR-19, FR-14 | 自動（否定形） |
| T-19-47 | 確認フレーズが不一致／未入力／**未設定** | 同上 | **すべて拒否し Risk を呼ばない**。未設定を「フレーズ不要」と解釈しない（安全既定） | FR-19, FR-14 | 自動（否定形） |
| T-19-48 | `/killswitch`・`/pause`・`/stage promote 2`・`/gfv`（副コマンド欠落）・typo | GFV 解除ハンドラへ渡す | **すべて実行しない**（種別を絞らないと別種のコマンドが解除経路へ落ちる） | FR-19, FR-14 | 自動（否定形） |
| T-19-49 | サービスロール（trading-service）／未認証 | 解除エンドポイントを叩く | **403 / 401**（生成AI・自動処理が統制を解けない。IADR-0051 最小権限） | FR-19, NFR | 自動（否定形） |
| T-19-50 | 解除を実行 | 監査エントリを見る | **誰が・いつ・どの記録に対して**＋**残件数**が残る。要約に「**違反記録そのものは失効しません**」が含まれる（解けたのは停止であって記録ではない） | FR-11 | 自動 |
| T-19-51 | 解除の理由が空（空白のみ・未入力） | `/gfv clear` の確認モーダルを送信する | **拒否し Risk を呼ばない**。**定型文で埋めない**——決定3 により Discord が唯一の窓口であるため、定型文にすると**全解除の理由が同一文字列になり「なぜ解除したか」が監査から復元できない** | FR-19, FR-11 | 自動（否定形） |
| T-19-52 | 利用者が理由を入力 | 同上 | 入力した理由が**そのまま**監査へ渡る（操作者も併記される） | FR-11 | 自動 |
| T-19-53 | 許可外の利用者 | `/gfv` を実行する | **確認ボタンすら出さない**（kill switch / pause / stage と同水準。ハンドラ側の閂だけに頼ると破壊的操作の窓口が許可外へ露出する） | FR-19, FR-14 | 手動（Gateway 経路） |
| T-19-54 | BFF を起動する | 登録済みルートを `EndpointDataSource` から列挙し想定集合と突合する | **完全一致**。**ホワイトリスト方式では「増えても赤くならない」**ため網羅突合にする。ADR-0028 決定3（画面からは解除できない）の回帰ガードであり、`good-faith-violation` を含むルートが無いことも個別に固定する | FR-19, SC-02, SC-03 | 自動（否定形・構造） |
| T-19-55 | 違反 2 件・owner | 解除エンドポイントを叩く | **200**。解除した `OrderId` の一覧と残件数を返し、**`GoodFaithViolationsCleared` がバス発行される**。**発行側を検査しないと `AuditEntryFactory` が緑でも Risk が一度も発行していない状態を検知できない** | FR-11 | 自動（供給経路） |
| T-19-56 | 解除対象なし | 同上 | **発行しない**（受理されない操作を監査上の事実にしない） | FR-11 | 自動（否定形） |
