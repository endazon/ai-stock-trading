---
title: GFV 発生回数の自前計数と、決済済み資金の代替値（MaxCashBuy 等）を構造で禁じる
type: spec
status: approved
related_ids: [FR-19, FR-10, FR-11, UC-06, ADR-0025, ADR-0021, ADR-0019, IADR-0153, IADR-0148, IADR-0159, IADR-0166]
author: Claude Code
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0025_settled-cash-poc-and-gfv-counting.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: GFV 発生回数の自前計数と、決済済み資金の代替値を構造で禁じる（#425 / ADR-0025）

> 本仕様書は実装着手前に作成した。実装は本書に沿って進める。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-19**（取引ガード）・**FR-10**（リスク統制）・**FR-11**（監査ログ）
- ユースケース（UC）: UC-06（統制設定の変更・運用状態の把握）
- 関連 ADR: **[ADR-0025](../../planning/projects/ai-stock-trading/07_adr/ADR-0025_settled-cash-poc-and-gfv-counting.md) 決定1・決定2・決定3**（`Accepted`・2026-08-07・質問票 第 13 回 Q8-2）／
  [ADR-0021](../../planning/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md) 決定4-2・4-3・4-5／
  [ADR-0019](../../planning/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md) PoC 項目 8（本作業の対象外）
- 関連 IADR: **[IADR-0153](../adr/IADR-0153_broker-account-type-supply-and-fail-closed.md)**（前提として扱い覆さない。ADR-0025 が明記）／
  [IADR-0148](../adr/IADR-0148_control-violation-supply-and-unavailable-state.md)（「未供給」と「0 件」の区別）／
  [IADR-0159](../adr/IADR-0159_buy-in-post-hoc-inference.md)（事後推定・追記専用台帳・集計元の固定）／
  **[IADR-0166](../adr/IADR-0166_gfv-self-counting-and-settled-cash-source-ban.md)**（本作業で新規作成）
- 対象 Issue: [#425](https://github.com/endazon/ai-stock-trading/issues/425)（由来: #375・環流 [feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md](../../feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md)）

## 目的・背景

#375 で「決済済み資金」「GFV 発生回数」のいずれも moomoo API から取得できないことを実測し、
`docs/blocked-tasks.md` へ 2 件登録した。**2026-08-07 の利用者裁定（ADR-0025）でその 2 件が決着した。**

| 求める値 | 裁定 | 本 issue の扱い |
| --- | --- | --- |
| **決済済み資金** | ADR-0019 の **PoC 項目 8**（`TrdFlowSummary` の検証・期限 2026-08-31） | **範囲外**（実 OpenD が要る）。blocked-tasks へ新規登録する |
| **GFV 発生回数** | **自前で計数する**（決定2）。手入力は採らない | **本 issue で実装する** |

**現金口座は決定1・2 が成立するまで選べない**（決定3）。決定1（PoC 項目 8）が未了である以上、
本 issue の完了後も**現金口座の買付は `CashAccountSettlementHold` で止まったままである**。
`IADR-0153` の fail-closed は解除しない。

## 対象範囲

- **対象**
  1. **GFV 発生回数の自前計数**（ADR-0025 決定2）——未決済資金による買付を自ら記録し、その件数を GFV 発生回数として扱う
  2. 計数値を判定コアへ供給し、**未供給なら新規建てを拒否する**（`GoodFaithViolationLimitReached`・fail-closed の維持）
  3. 記録を**監査証跡（FR-11）の粒度**で残す（イベント発行 → 中央監査台帳）
  4. **`MaxTrdQtys.MaxCashBuy` / `Funds.AvlWithdrawalCash` / `Funds.MaxWithdrawal` を決済済み資金の代替に使えなくする構造的な担保**
  5. `docs/blocked-tasks.md` の追随（既存 2 件を「解消済み」として残置・PoC 項目 8 を新規登録）
- **対象外**（issue の「やらないこと」）
  - **PoC 項目 8 の実施**（実 OpenD が要る。期限 2026-08-31）
  - **決済済み資金の供給経路の実装**（PoC 項目 8 の結果に依存する）
  - **現金口座を選べるようにすること**。`LiveTradingGate.LiveTradingReleased = false` は**変更しない**
  - 停止基準（2 件）・`BuyInBanned` など既存の値の変更
  - **手入力による GFV 回数の受け入れ**（裁定で却下）
  - 2 回目の警告の Discord 通知結線（`WarnsForGoodFaithViolations` は純関数として残す。#375 の残件のまま）

## 設計

### 1. 何を数えるのか（**取り違えると統制の目的が崩れる**）

ADR-0025 §理由 が明記した限界を、実装のコメント・IADR・本仕様書のいずれにも残す。

> 自前で数えられるのは**自らのガードをすり抜けた買付**だけであり、**ブローカー側が独自に GFV と判定した
> 事象は捕捉できない**。したがって本計数は「ブローカーの GFV カウンタの写し」ではなく
> 「**自らのガードの失敗回数**」である。**両者が一致する保証はない。**

- **ガードが正しく働けば発生回数は 0 のままである。** 記録されるのはガードをすり抜けた場合だけである。
- ブローカーが先に 3 回目を計上して口座制限が掛かる事態は、本計数では防げない（ADR-0025 §結果）。

### 2. 計数の対象事象と検出点

計数の対象は **ADR-0021 決定4 の統制2 が拒否しようとする事象**（決済済み資金を超える買付）と同じである。
したがって**判定式を 2 か所に書かない**——`AccountTypePolicy.ExceedsSettledCash` を単一情報源とし、
発注前のガード（`RiskEvaluator`）と事後の計数（検出器）が同じ述語を呼ぶ。

```
ExceedsSettledCash(settledCashInBase, purchaseAmountInBase)
  = settledCashInBase が未供給（null）  ||  purchaseAmountInBase > settledCashInBase
```

**検出点は約定（`OrderExecuted`）である。** 発注審査の時点では、統制2 が真なら注文は拒否されるため
「すり抜け」は原理的に観測できない。すり抜けが露見するのは、**現金口座であるという観測**と
**買付が現に約定した事実**が突き合わさったときだけである（[IADR-0159](../adr/IADR-0159_buy-in-post-hoc-inference.md) と同じ事後推定の形）。

検出の条件（すべて満たしたときだけ 1 件記録する）:

| # | 条件 | 満たさないときの扱いと理由 |
| --- | --- | --- |
| 1 | 約定がある（`FilledQuantity > 0`） | 受付・失注・約定 0 の取消は「買付」ではない |
| 2 | 現在有効な口座観測が**現金口座**である | 信用口座・不明では記録しない。GFV は現金口座の制度であり、不明な口座の新規建ては `BrokerAccountTypeUnverified` が既に止めている |
| 3 | 承認台帳に相関があり、**新規建ての買い**（`Open` × `Buy`）である | 相関が無ければ金額も方向も不明であり、推測で違反を記録しない |
| 4 | `ExceedsSettledCash(観測の決済済み資金, 本約定の基準通貨建て金額)` が真 | 決済済み資金の範囲内で説明できる買付は違反ではない |

- **金額は本約定 1 件ぶん**（`FilledQuantity × AveragePrice × 承認 Intent の FxRateToBase`）である。
  発注前のガードが「**当日の新規建て累計 ＋ 本注文**」で判定する（IADR-0153 決定4）のと粒度が違う。
  現状は決済済み資金が常に未供給であり、述語は第 1 項で真になるため差は生じない。
  **PoC 項目 8 が成立して決済済み資金が供給された時点で、累計での比較へ揃える必要がある**（IADR-0166 §フォローアップ）。
- **計上単位は 1 注文 1 件**（主キー = `OrderId`）。部分約定の進行・メッセージ再送で二重計上しない。

### 3. 記録と供給

```
[OrderExecuted]
   └ OrderExecutedGoodFaithViolationHandler
        ├ IGoodFaithViolationStore.Append（EF・追記専用・永続）  ← 監査証跡の実体
        └ GoodFaithViolationRecorded を発行 → AuditService（中央監査台帳・FR-11）

[発注審査]
   PortfolioSnapshotBuilder
        └ IGoodFaithViolationStore.GetTally() → PortfolioSnapshot.GoodFaithViolations
              └ RiskEvaluator: 未供給（null）／停止基準到達 → GoodFaithViolationLimitReached
```

- **台帳は永続でなければならない。** プロセス内に持つと再起動で違反記録が消え、停止が解ける（fail-open）。
- **`GoodFaithViolationTally`（第一級の値）で供給する。** `null` = 未供給、`Observed(0)` = 数えた結果 0 件。
  両者を同じ `int` の 0 で表さない（[IADR-0148](../adr/IADR-0148_control-violation-supply-and-unavailable-state.md) 決定1 と同じ規律・#424 の表示規約と同じ区別）。
  判定は型のプロパティ（`BlocksNewEntry` / `Warns`）に持たせ、`tally?.Count >= 2` のような
  **持ち上げ比較で未供給が黙って「違反なし」へ倒れる**書き方の動機を消す。
- **`BrokerAccountState.GoodFaithViolationCount` は削除する。** ブローカーは本値を供給できないことが
  実測で確定しており（IADR-0153 決定4）、自前計数の値をこの欄へ入れると
  「**ブローカーの GFV カウンタの写し**」と読まれる——ADR-0025 が名指しで否定した取り違えである。
  死んだ欄を残すと次の実装者が判定へ結線し直す余地が残る（IADR-0148 決定2 と同じ規律）。
- **失効期間は設けない**（計数は累計）。ADR-0021・ADR-0025 は「違反記録の失効」の期間を定めていない。
  自動失効は fail-open であり、値を発明せずに安全側へ倒す。**運用上の解除手段が要る点は未決事項へ記す。**

### 4. `MaxCashBuy` を構造で止める（**コメントでは足りない**）

ADR-0025 が採ってはならない候補として 2 つを名指しした。

- `Funds.AvlWithdrawalCash` / `Funds.MaxWithdrawal` は**出金可能額**であり決済済み資金とは別概念である。
- **`MaxTrdQtys.MaxCashBuy` はもっとも危険である。** 現金買付余力は現金口座では**未決済の売却代金を含む**のが
  通例であり、**それこそが GFV を引き起こす当の資金である。これを分母に据えると GFV 回避ガードが GFV を許可する。**

担保は 2 層にする。

1. **機械的な遮断**: `scripts/check-banned-settled-cash-sources.js` が `backend/**/*.cs` の
   **コードとして書かれた**参照（`.MaxCashBuy` 等）を検出して CI を落とす。散文・コメント・XML ドキュメントの
   言及は検出しない（既存の IADR・仕様書・テストのコメントが説明のために名前を挙げているため）。
   先例は `check-banned-libraries.js`（剥がした事実をレビューの記憶に頼ると必ず戻る）。
2. **供給側の否定形テスト**: moomoo アダプタが `SettledCashInBase` を `null` のまま返すこと（既存 T-19-271）。

### 5. 影響を受ける既存の挙動（**net で緩めないことの確認**）

自前計数が供給されると、**現金口座の新規建てが `GoodFaithViolationLimitReached` で止まらなくなる**
（計数 0 件のため）。これは ADR-0025 決定2 が意図した状態そのものである
（ADR-0021 決定4-5 は同理由を「停止基準到達**／回数が供給されない**」と定義しており、供給されれば理由は立たない）。

**現金口座が使えるようになるわけではない。** 統制2（決済済み資金）が未供給のままであり、
現金口座の買付は `CashAccountSettlementHold` で止まり続ける。**この不変条件をテストで固定する**（T-19-296）。

## 受け入れ基準

- [ ] 未決済資金による買付（＝ガードをすり抜けた買付）が**自らの台帳へ 1 件記録**され、監査台帳（FR-11）にも残る
- [ ] 計数値が**供給されないとき**、現金口座の新規建てが `GoodFaithViolationLimitReached` で拒否される（fail-closed）
- [ ] 計数値の **0 件と未供給が区別される**（#424 の表示規約と同じ区別）
- [ ] **2 件目で警告・3 件目の手前で新規建て停止**（1 件では止まらない／2 件で止まる）
- [ ] `AccountTypePolicy.GoodFaithViolationStopThreshold` は **2 のまま**である
- [ ] **信用口座は本統制の影響を受けない**（否定形）
- [ ] `MaxCashBuy` / `AvlWithdrawalCash` / `MaxWithdrawal` が決済済み資金として**使われていない**ことを機械的に検査する
- [ ] `CashAccountSettlementHold` へ写像していない（解除条件が違う）
- [ ] **現金口座の買付は依然として止まる**（`IADR-0153` の fail-closed を解除していない）
- [ ] `LiveTradingGate.LiveTradingReleased` は `false` のままである

## テスト方針

- 境界値: 計数 {未供給, 0, 1, 2, 3} × {停止, 警告}。**2 が境界**。
- 否定形（主眼）:
  - 計数が未供給なら通してしまう（fail-open）ことがない
  - 0 件を未供給と同一視しない／未供給を 0 件と同一視しない
  - `CashAccountSettlementHold` へ写像しない（拒否理由の取り違え）
  - 信用口座では計数の供給を要求しない
  - 検出が信用口座・口座不明・売却・手仕舞いで発火しない
  - 同一注文の再送・部分約定の進行で二重計上しない
  - `MaxCashBuy` 等をコードから参照すると検査が落ちる（構造テスト）
- ミューテーションテスト（本リポジトリの規律）で「テストが効いていること」を実測し、結果を PR と
  テスト仕様書へ残す。

## 計画書との差異

- **無し（計画に忠実）。** ただし計画が定めていない点を実装判断で埋めた箇所が 3 つあり、
  いずれも [IADR-0166](../adr/IADR-0166_gfv-self-counting-and-settled-cash-source-ban.md) に根拠を残す。
  1. 検出点を約定（事後）に置いたこと
  2. 計上単位を 1 注文 1 件（`OrderId` 主キー）としたこと
  3. **違反記録の失効期間を設けなかったこと**（計画に規定が無く、自動失効は fail-open のため）

## 未決事項

- **違反記録の失効・解除の手段が計画に無い。** `GoodFaithViolationLimitReached` の解除条件は
  IADR-0153 決定5 で「違反記録の失効」としたが、失効の期間も手段も定義されていない。
  自前計数を累計で持つ本実装では、**1 件でも記録されると 2 件目で恒久的に新規建てが止まる**。
  ブローカーの GFV は通常 12 か月のローリング窓で失効するが、**それを実装が発明してはならない**。
  → 計画へ環流する（`feedback/20260807_adr0025-gfv-counting-open-points.md`）。
- **PoC 項目 8 の成立後、比較の粒度を「当日累計」へ揃える必要がある**（§2 の注記）。
- 2 回目の警告の通知経路（Discord）は未結線のまま（#375 からの継続）。

## 関連仕様

- 機能仕様書: [FR-19 取引ガード](../functional/FR-19_trading-guard.md)
- テスト仕様書: [FR-19 取引ガード](../tests/FR-19_trading-guards-tests.md)
- 実装 ADR: [IADR-0166](../adr/IADR-0166_gfv-self-counting-and-settled-cash-source-ban.md)・[IADR-0153](../adr/IADR-0153_broker-account-type-supply-and-fail-closed.md)
- 先行作業仕様書: [20260806_375_cash-account-support](20260806_375_cash-account-support.md)
