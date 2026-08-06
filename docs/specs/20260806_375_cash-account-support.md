---
title: 米国口座の現金口座対応 — 口座種別の照会・差金決済ガードの条件付き適用・GFV 回避ガード・信用系の設定不能化
type: spec
status: approved
related_ids: [FR-19, FR-10, FR-11, FR-20, UC-06, ADR-0021, ADR-0007, ADR-0016, IADR-0132, IADR-0134, IADR-0139, IADR-0148, IADR-0153]
author: Claude Code
created: 2026-08-06
updated: 2026-08-06
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 米国口座の現金口座対応（#375 / ADR-0021 決定4）

> 本仕様書は実装着手前に作成した。実装は本書に沿って進める。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-19**（取引ガード）・**FR-10**（リスク統制）・FR-11（監査ログ）・FR-20（段階ゲート）
- ユースケース（UC）: UC-06（統制設定の変更）
- 関連 ADR: **ADR-0021**（本作業の直接の起点。決定1〜5）・ADR-0007（2026-08-04 追補: 商品種別ガードは新規建てのみ）・ADR-0016（決定10 の拒否理由分類。**現金口座では全決定が適用対象外**＝決定5）
- 関連 IADR: [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)（差金決済ガードの限定・#332）・[IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定2（拒否理由の序数不変）・[IADR-0139](../adr/IADR-0139_stage-product-type-enforcement.md)（段階別商品種別強制）・[IADR-0148](../adr/IADR-0148_control-violation-supply-and-unavailable-state.md)（「未供給」と「0 件」の区別）・**IADR-0153**（本作業で新規作成）
- 対象 Issue: [#375](https://github.com/endazon/ai-stock-trading/issues/375)

## 着手可能性の確認（issue #375 が求めた「第 2 回 Q24 の裁定」）

issue #375 は「**着手前に第 2 回 Q24 の裁定を確認すること**」を条件に挙げていた。本セッションで次を実測し、**解消済みと判断した**。

| 確認事項 | 実測結果 |
| --- | --- |
| 第 2 回 Q24 の裁定 | **裁定済み**。[ADR-0021](../../planning/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md) 23 行目「詳細は**第 2 回 Q24 で裁定済み**」。決定3（照会を正とし設定値との食い違いで止める）・決定4（現金口座で追加する 5 統制）・決定5（ADR-0016 は信用口座前提）が、[blocked-tasks](../blocked-tasks.md) B-4 の「裁定待ち」3 点にそのまま答えている |
| ADR-0021 が `Proposed` であること | 計画リポの `.claude/rules/adr.md` 40〜41 行が「**`Proposed` は決定の効力とは別の軸であり、利用者裁定によって内容が確定した決定は ADR が `Proposed` であっても計画側の現行値である**」と定める。`Proposed` に留まる理由は `/sync-impl` の GitHub API 401 のみ（ADR-0021 25 行）。同じ扱いの ADR-0016 に対しては #333 / PR #384 で既に実装した先例がある |
| 口座種別の判定手段 | **実測済み**。[作業仕様書 20260805_342](20260805_342_moomoo-poc-plan.md) 79 行: `TrdGetAccList` / `TrdAcc.AccType` / `TrdAccType` = `Cash` / `Margin`。本セッションで SDK を再走査し **`TrdAccType_Unknown=0 / Cash=1 / Margin=2`** を確認した |
| 実際の口座構成 | 同 218〜226 行。実弾側に**現金口座（accId=284852702813153760・`AccType=Cash`）と信用口座（accId=284852705357372276）の両方が実在する**。SIMULATE は信用口座 1 つ（accId=724808） |
| 依存 issue #374 | **クローズ済み**。submodule `d980a01` に ADR-0021 が入っている |

## 目的・背景

従来の前提（[project-planning#81](https://github.com/endazon/project-planning/issues/81)・2026-07-31）は「米国は信用口座」で固定されており、そこから「Good Faith Violation（GFV）は発生しない → 差金決済ガードは米国株に適用しない」（#332 / IADR-0132 決定5 で是正済み）が導かれていた。

2026-08-04 の利用者裁定（ADR-0021）により**信用口座と現金口座の双方に対応する**方針になった。**現金口座ではこの前提が成り立たない。**

**GFV の影響が運用上もっとも重い。** GFV は違反しても即座には分からず、**3 回目で口座が 90 日間制限される**。自動売買は回転数が多く人間より速く 3 回に到達し得る（ADR-0021 46 行）。

## 対象範囲

- **対象**（ADR-0021 決定4 の 5 統制）
  1. 口座種別の型・照会結果の供給経路・**設定値との食い違いおよび不明時の fail-closed**（決定2・決定3）
  2. 差金決済ガードの**条件付き適用**（現金口座では米国株にも適用。決定4-1）
  3. **GFV 回避ガード**（未決済資金による買付を発注前に拒否。決定4-2）
  4. **GFV 発生回数の追跡と警告**（2 回目で警告・3 回目の手前で新規建て停止。決定4-3）
  5. **信用買い・空売りの設定不能化**（決定4-4）と ADR-0016 の適用対象外化（決定5）
  6. 拒否理由の新設とクラス分類（決定4-5）
- **対象外**
  - 実弾（`TrdEnv_Real`）での発注・照会。`LiveTradingGate.LiveTradingReleased = false` は**変更しない**
  - 現金口座を実際に選択して運用する経路（moomoo クライアントは SIMULATE の信用口座に固定されたままである。#331 の範囲）
  - GFV 発生の**検知**そのもの（moomoo API に GFV カウンタは存在しない。§実測結果を参照）。本作業は「供給されたら統制が効く」ことと「供給が無ければ止まる」ことを確定する
  - 2 回目の警告の**通知経路**（Discord）。判定は純関数として置くが通知の結線は行わない

## moomoo API の実測結果（決済済み資金・GFV カウンタの取得可否）

ADR-0021 116 行は「決済済み資金の残高追跡が要り、**ブローカーからの取得可否に依存する**（取得できない場合の扱いは実装側で設計する）」、120 行は「**決済済み資金の残高を moomoo API から取得できるかを確認する**」を実装側へ委ねている。本セッションで SDK（`moomoo-api` 10.8.6808 / `MMAPI4Net.dll`）をリフレクションで走査した。

| 求める値 | SDK 上の該当 | 判定 |
| --- | --- | --- |
| 口座種別 | `TrdCommon.TrdAcc.AccType` / `TrdAccType_Unknown=0 / Cash=1 / Margin=2 / TFSA / RRSP / SRRSP / Derivatives` | ✅ **取得できる**。列挙に `Unknown` があることは、**不明が起こり得る**ことを SDK 自身が認めている |
| **決済済み資金（settled cash）** | `TrdCommon.Funds` の全 40 フィールドを走査（`Cash` / `AvlWithdrawalCash` / `AvailableFunds` / `NetCashPower` / `MaxWithdrawal` / `FrozenCash` / `PendingAsset` / `BeginningDTBP` / `RemainingDTBP` ほか） | ❌ **専用フィールドは存在しない**。アセンブリ全体で `Settl` を含む取引系プロパティは `TrdFlowSummary.FlowSummaryInfo.SettlementDate` のみ |
| GFV 発生回数 | `GoodFaith` / `Violation` / `Gfv` を含む型・プロパティを全走査 | ❌ **存在しない**。`Funds.IsPdt` / `PdtSeq` / `DtStatus`（PDT 系）はあるが GFV ではない |
| 代替の導出経路 | **`TrdFlowSummary`**（資金フロー明細）: `ClearingDate` / **`SettlementDate`** / `CashFlowAmount` / `CashFlowDirection` / `CashFlowID` | 🟡 **候補としては存在する**。`SettlementDate` を持つ入出金明細を積み上げれば決済済み資金を導ける見込みだが、**実口座で値が返るかは未検証**（OpenD 非稼働。PoC 項目に無い） |

**2026-08-06 の再実測（別セッションによる独立検証）**: 上表を SDK のリフレクション走査でやり直し、**すべて一致した**。
`TrdCommon.Funds` の実プロパティ数は 42（`Has*` を除き indexer 2 個を含む）。`GoodFaith` / `good_faith` / `Violation` /
`Gfv` / `unsettled` / `sellable` / `freecash` はアセンブリ全体で **0 件**。`TrdAccType` の実測値は
`Unknown=0 / Cash=1 / Margin=2 / TFSA=3 / RRSP=4 / SRRSP=5 / Derivatives=6`。

あわせて、**採ってはならない紛らわしい候補を 2 つ特定した**（IADR-0153 論点 C に記録）。

- `Funds.AvlWithdrawalCash` / `MaxWithdrawal` は**出金可能額**であり決済済み資金とは別概念である。
- **`MaxTrdQtys.MaxCashBuy`（現金買付余力）がもっとも危険である。** ブローカーの現金買付余力は現金口座では
  **未決済の売却代金を含む**のが通例であり、それこそが GFV を引き起こす当の資金である。分母に据えると
  **GFV 回避ガードが GFV を許可する**。

**結論**: 決済済み資金は **moomoo API から直接は取得できない**。ADR-0021 120 行が予告した「自前の推定」の側に落ちる。したがって本実装は次の設計を採る（詳細は IADR-0153）。

- 決済済み資金は**外部から供給される観測値**として型に載せ、**供給が無ければ現金口座の買付を止める**（fail-closed）
- 「残高が分からないから通す」には**絶対に倒さない**
- 導出経路（`TrdFlowSummary`）の実装は本作業の対象外とし、計画（ADR-0019 の PoC 項目）へ環流する

## 設計

### 1. 口座種別の型と供給経路（決定2・決定3）

```
[OrderExecutionService]                       [RiskManagementService]
MoomooBrokerAdapter                            BrokerAccountObservedHandler
  : IBrokerAccountSource                          ↓
  └ TrdGetAccList → TrdAcc.AccType            IBrokerAccountObservationStore（最新 1 件）
        ↓                                         ↓
BrokerAvailabilityProbeService（定期 probe）   PortfolioSnapshotBuilder
        ↓ 成功時のみ                              ↓
  BrokerAccountObserved（イベント） ──────→   PortfolioSnapshot.Account
                                                  ↓
                                              RiskEvaluator（決定的判定）
```

**なぜこの形か**（#385 / #386 / #387 で確立した「観測（observation）が別サービスから届き、型に載る」形に揃える）:

- 口座種別は**注文ごとの外部入力ではなく運用状態**である。よって `ShortSellOrderContext`（注文ごと・銘柄ごと）ではなく `PortfolioSnapshot`（`KillSwitchEngaged` / `TradingPaused` と同じ場所）に載せる。組み立ては `PortfolioSnapshotBuilder` が担う
- ブローカーへの接続を持つのは発注執行サービスだけである。既に**定期 probe**（`BrokerAvailabilityProbeService`・IADR-0150）が 5 分周期で OpenD へ照会しており、`TrdGetAccList` は接続確立時に既に呼ばれている。**新しい常駐を増やさずに同じ巡回へ相乗りする**
- **沈黙が安全側に倒れる**（`BrokerAvailabilityObserved` と同じ作法）。到達不能・照会失敗・種別不明はいずれも「発行しない」であり、受け手は観測が無いまま古い観測が失効する。`BrokerAccountObserved` は**発行できたときだけ**発行する

**fail-closed の設計**:

| 状態 | 挙動 |
| --- | --- |
| 観測が無い（未供給・照会失敗・`TrdAccType_Unknown`・SIMULATE/REAL いずれも） | **新規建て（Open）を拒否**（`BrokerAccountTypeUnverified`） |
| 観測はあるが設定値と食い違う | **新規建てを拒否**（同上。決定3） |
| 観測がある・設定値と一致 | 観測された種別で統制を切り替える |
| 手仕舞い（Close）・損切り | **止めない**（ADR-0009 の不変条件・ADR-0007 2026-08-04 追補） |
| 発注先が内蔵 `paper`（`BrokerProvider.InternalPaper`） | 口座種別を要求しない（**外部へ一度も発注せず、ブローカー口座が存在しない**ため。IADR-0153 決定2） |

**「不明なら信用口座とみなす」は採らない。** 現金口座なのに GFV 回避ガードが無効のまま回ることが、決定3 が防ごうとしている当の事故である（ADR-0021 79 行）。

### 2. 差金決済ガードの条件付き適用（決定4-1）

現行（#332 / IADR-0132 決定5）: `isEntry && PreventSameDayReentry && Market == Japan && 実効種別 == Cash`。

改める点は**市場の条件だけ**である。

```
適用する = 実効種別 == Cash
        && ( Market == Japan          // 日本の差金決済規制（金商法 161 条の 2）。口座種別に依存しない
           || 口座種別 == Cash )       // ADR-0021 決定4-1: 現金口座では米国株にも適用する
```

**#332 の是正は巻き戻さない。** 信用口座（既定）では現行どおり `Market.Japan && ProductType.Cash` のみに適用される。両方向をテストで固定する。

`RiskEvaluator.cs` の「米国株は信用口座で運用するため GFV が発生しない」という**無条件の**コメントは、ADR-0021 を参照する条件付きの記述へ改める。

### 3. GFV 回避ガード（決定4-2・最重要）

現金口座の**新規建ての買付**に対して、決済済み資金を超える買付を拒否する。

```
現金口座 && isEntry && Side == Buy のとき:
  決済済み資金が未供給            → CashAccountSettlementHold（fail-closed）
  当日発注累計 + 本注文額 > 決済済み資金 → CashAccountSettlementHold
```

- 累計で判定する理由は #27 と同じである。1 件ずつの比較では、上限内の注文を複数回通して累計で超過できる
- 当日発注累計（`DailyOrderedAmount`）は**新規建てのみ**を積む（IADR-0130 決定4）。現金口座では新規建て＝現物買付であり、この定義がそのまま「当日の現金の払い出し」になる
- ブローカーが約定時点で現金を引き落としていれば二重計上になり得るが、**過剰拘束（発注が止まる）側**であり安全である
- 拒否理由 `CashAccountSettlementHold` は**クラス A**（ADR-0021 決定4-5 が明示）。`BannedSymbol` の**クラス C には混ぜない**——混ぜると段階昇格ゲートの「統制違反 0 件」が壊れる

### 4. GFV 発生回数の追跡と警告（決定4-3）

```
警告する   = 回数 >= 2      // 「2 回目で警告」
停止する   = 回数 is null || 回数 >= 2   // 「3 回目の手前で新規建てを停止」
```

**警告と停止が同じ回数で立つ**のは意図どおりである。3 回目で 90 日の口座制限という**不可逆**な結果に対し、「2 回に達した時点で警告し、かつ 3 回目を出させない」ことが決定4-3 の要求だからである。回数が**未供給（null）なら停止する**（2 に達していないことを確認できないため）。

### 5. 信用買い・空売りの設定不能化（決定4-4・決定5）

**口座が対応する商品種別**を単一情報源として置く。

| 口座種別 | 対応する商品種別 |
| --- | --- |
| 信用口座（Margin） | 現物 / 信用買い / 空売り |
| 現金口座（Cash） | **現物のみ**（信用買い・空売りは口座の能力として不可能） |

- **設定側**（決定4-4「選べなくする」）: `RiskSettingsService.UpdateGuard` が、観測された口座種別で対応できない商品種別を有効化しようとする要求を **`ArgumentException`（HTTP 400）で拒否し、設定を一切変更せず履歴も残さない**（IADR-0141 / IADR-0151 決定2 と同じ規律）
- **発注側**（多層防御）: 実効的に有効な商品種別を「利用者設定 ∩ 口座が対応する種別」で解決し、既存の `ProductTypeDisabled` で拒否する
- **適用は新規建てのみ**。既存建玉の手仕舞いは止めない（ADR-0007 2026-08-04 追補・project-planning#179）。**口座種別を切り替えた瞬間に、その口座で建てた信用建玉が閉じられなくなる**ことを防ぐ
- **決定5**: 現金口座では ADR-0016 の空売り統制群（拒否理由 9 種）を**評価しない**。空売りが「無効に設定されている」（`ShortSellDisabled`）のとは別の状態であり、口座の能力の問題である

### 6. 拒否理由（決定4-5）

**新設は常に末尾**（序数不変・IADR-0134 決定2）。

| 拒否理由 | クラス | 根拠 |
| --- | --- | --- |
| `CashAccountSettlementHold` | **A** | ADR-0021 決定4-5 が明示 |
| `BrokerAccountTypeUnverified` | **B** | 「取引を止めている状態そのものの記録」。KillSwitch / Paused / 段階制約と同じ区分 |
| `GoodFaithViolationLimitReached` | **A** | 統制が設計どおり作動した記録 |

`SameDayReentry` の米国株への適用拡大も**クラス A のまま**である（分類は理由コードごとであり、適用範囲の拡大では変わらない）。

> **計画との差異**: ADR-0021 決定4-5 が新設を求めたのは `CashAccountSettlementHold` の **1 種のみ**である。残る 2 種は、決定3（照会失敗・食い違いで発注を止める）と決定4-3（3 回目の手前で停止）が**統制としては要求しているが拒否理由コードを与えていない**ために追加した。ADR-0016 決定10 の規律（**原因も解除条件も異なるものを畳むと監査ログが実態と食い違う**）に従う。`CashAccountSettlementHold` は T+1 の決済で解け、`GoodFaithViolationLimitReached` は違反記録の失効で解け、`BrokerAccountTypeUnverified` は照会の成功で解ける。先例は `StopOrderRequired`（#329 で実装が先行し 2026-08-04 に計画が同名で追認）である。**計画へ環流済み**（[feedback/20260806](../../feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md)）。

## 受け入れ基準

- [x] 口座種別 × 市場 × 商品種別の組み合わせで判定が確定する。**信用口座では #332 の現行挙動が保たれる**（退行防止）
- [x] 現金口座では差金決済ガードが米国株に**適用される**／信用口座では**適用されない**（両方向）
- [x] 現金口座で、決済済み資金を超える買付が `CashAccountSettlementHold` で拒否される（境界値を含む）
- [x] 決済済み資金が**供給されないとき**、現金口座の買付が止まる
- [x] 口座種別の**照会に失敗した（観測が無い）とき**、新規建てが止まる。**手仕舞いは止まらない**
- [x] 観測と設定値が**食い違うとき**、新規建てが止まる
- [x] 現金口座で信用買い・空売りを**設定する経路が塞がれている**（設定は変更されず履歴も残らない）
- [x] 現金口座で信用買い・空売りを**発注する経路が塞がれている**。ただし**手仕舞いは通る**
- [x] GFV 発生回数 2 回で新規建てが止まる。未供給でも止まる
- [x] `CashAccountSettlementHold` / `GoodFaithViolationLimitReached` がクラス C（統制違反）に**計上されない**
- [x] `LiveTradingGate.LiveTradingReleased` は `false` のまま
- [x] `dotnet build` 警告 0 / `dotnet test` 緑 / `dotnet format --verify-no-changes` 差分なし

## テスト方針

- 新規 `CashAccountControlsTests`（Domain）に**口座種別 × 市場 × 商品種別の組み合わせ表**を `[Theory]` で置く
- 否定形を最重要として明示的に書く（fail-closed・設定経路の遮断・手仕舞いが止まらないこと）
- 拒否理由の分類は `RejectionReasonClassificationTests` に追加し、**クラス C に混ざらない**ことを固定する
- 供給経路（handler → store → snapshot、probe → イベント）は各層の単体テストで固定する
- **変異検査**: (a) 口座種別の分岐、(b) fail-closed の分岐、(c) GFV の残高判定 を反転／削除してテストが赤くなることを確認する

## 実装中に決めた追加事項（本仕様書の初版に無かったもの）

初版（着手前）に書いていなかった判断を、実装中に 5 件加えた。いずれも IADR-0153 に決定として記録している。

| # | 事項 | 判断 | 理由 |
| --- | --- | --- | --- |
| 1 | **観測の永続化** | **しない**（プロセス内・最新 1 件・有効期間 30 分） | #385/#386/#387 が EF 永続化するのは**再観測で復元できない集計**であり、口座種別は「いま照会すれば得られる現在値」で性質が違う。永続化は「ブローカーが応答しなくなった後も古い種別で回り続ける」危険を生み、得るものは高々 1 probe 間隔ぶんの猶予しかない（IADR-0153 決定3・論点 B） |
| 2 | **観測の鮮度** | **30 分**（`BrokerAvailabilityProbeOptions` のクランプ上限と同値） | ADR-0021 は照会の頻度・キャッシュを定めていない。健全な probe（既定 5 分）は必ず窓の内に更新でき、probe が止まれば失効して新規建てが止まる。**古い観測を無期限に信じる形にはしない** |
| 3 | **ガード更新 PUT の口座種別** | `AccountType?` として受け、**省略は「変更しない」** | 全置換 PUT であり、SC-02 は口座種別を編集も送信もしない。非 nullable enum だと**禁止銘柄を 1 件足しただけで口座種別が既定（信用口座）へ黙って戻る**。`BrokerProviderUpdateRequest.Provider` と同じ規律（IADR-0153 決定8） |
| 4 | **監査台帳への記録** | `BrokerAccountObserved` を `AuditService` が記録する | リポジトリの網羅ガード（`AuditConsumerCoverageTests`）が要求した。要約に決済済み資金・GFV 回数の「未供給」を明示し、事後に「なぜ止まっていたか」を要約だけで辿れるようにした |
| 5 | **フロントの契約型** | `TradingGuardSettings.configuredAccountType` を追加（表示・編集はしない） | 応答 JSON にキーが増えるため、契約フィクスチャ（IADR-0146）を再生成しフロント型も追随させないと `npm run typecheck` が赤になる。**口座種別を選ぶ UI は本 issue の範囲外**（ADR-0021 は画面を定めていない） |

## 変異検査の実測（2026-08-06）

ガードが実際に効いていることを、壊してテストが赤くなることで確認した。詳細は
[テスト仕様書 FR-19 §7](../tests/FR-19_trading-guards-tests.md#変異検査実測2026-08-06)。
**8 通の変異のうち 7 通が検出され、1 通（b2）は等価変異であった**（観測 `null` と `Margin` が下流で同値になる
経路であり、観測有無の安全性は `BrokerAccountTypeUnverified` が担う）。壊した 3 ファイルは退避してから
`cp` で復元し、**md5 のバイト一致と全テスト緑**を確認している。

## 計画書との差異

- 差異: **あり**
  1. **拒否理由を 3 種新設した**（計画が明示したのは 1 種）。理由は §6 のとおり。`feedback/` へ環流済み（[20260806_adr0021-rejection-reasons-and-settled-cash.md](../../feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md)）
  2. **決済済み資金は moomoo API から取得できない**ことを実測した。ADR-0021 120 行のフォローアップ（ADR-0019 の PoC 項目へ追加）に対する回答であり、`feedback/` へ環流済み（[20260806_adr0021-rejection-reasons-and-settled-cash.md](../../feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md)）
  3. **内蔵 `paper` は口座種別を要求しない**とした。ADR-0021 はブローカー口座の存在を前提としており、外部へ発注しない擬似約定は想定していない。IADR-0153 決定2 として記録する
  4. FR-19 本文・`05_trading-assumptions` §5 の 2 行が「米国株は信用口座で運用するため GFV は発生しない」と**無条件のまま**である（注記で条件付き化しているが本文は未改訂）。ADR-0021 119 行が自ら挙げたフォローアップであり、`feedback/` へ環流済み（[20260806_adr0021-rejection-reasons-and-settled-cash.md](../../feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md)）。あわせて FR-19 163 行・§5 222 行の「**第 2 回 Q24 で裁定待ち**」は ADR-0021 23 行（裁定済み）と食い違っており、これも環流済み

## 未決事項

- 決済済み資金の**供給経路**（`TrdFlowSummary` からの導出）は未実装である。現金口座を実際に選ぶ運用に入る前に解決が要る。現時点では現金口座の買付は**常に止まる**（安全側）
- GFV 発生の**検知**（回数の増やし方）は情報源が無い。同上
- 2 回目の警告の**通知経路**は結線していない
- いずれも [`docs/blocked-tasks.md`](../blocked-tasks.md)「実装済みだが実際には発動しない機能」へ**追記済み**（4 行）
- 口座種別を選ぶ UI（SC-02）は無い。ADR-0021 は画面を定めておらず、上記が解決するまで現金口座を選ぶ意味が無いため本 issue の範囲外とした
