---
title: IADR-0166 GFV 発生回数は約定の事後検出で自前計数し、決済済み資金の代替値は構造で遮断する
type: impl-adr
status: Accepted
related_ids: [FR-19, FR-10, FR-11, UC-06, ADR-0025, ADR-0021, ADR-0019, IADR-0153, IADR-0148, IADR-0159, IADR-0134]
author: Claude Code (implementation session)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0025_settled-cash-poc-and-gfv-counting.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0166: GFV 発生回数は約定の事後検出で自前計数し、決済済み資金の代替値は構造で遮断する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-07
- 決定者: Claude Code（実装セッション。計画側の裁定は ADR-0025・`Accepted`・2026-08-07・質問票 第 13 回 Q8-2）

## 起点・関連

- 関連する計画書 ID: **FR-19**（取引ガード）／**FR-10**（リスク統制）／**FR-11**（監査ログ）／UC-06／
  **[ADR-0025](../../planning/projects/ai-stock-trading/07_adr/ADR-0025_settled-cash-poc-and-gfv-counting.md) 決定1・決定2・決定3**／
  [ADR-0021](../../planning/projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md) 決定4-2・4-3・4-5／
  [ADR-0019](../../planning/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md) PoC 項目 8
- 関連する実装仕様書: [作業仕様書 20260807_425](../specs/20260807_425_gfv-self-counting.md)／
  [機能仕様書 FR-19](../functional/FR-19_trading-guard.md)／[テスト仕様書 FR-19](../tests/FR-19_trading-guards-tests.md)
- 先行 IADR: **[IADR-0153](IADR-0153_broker-account-type-supply-and-fail-closed.md)**（**前提として扱い覆さない**。
  ADR-0025 が明記）／[IADR-0148](IADR-0148_control-violation-supply-and-unavailable-state.md)（「未供給」と「0 件」の区別）／
  [IADR-0159](IADR-0159_buy-in-post-hoc-inference.md)（事後推定・追記専用台帳・集計元の固定）／
  [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定2（拒否理由の序数不変）
- 起点 issue: [#425](https://github.com/endazon/ai-stock-trading/issues/425)（由来: [#375](https://github.com/endazon/ai-stock-trading/issues/375)・
  環流 [feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md](../../feedback/20260806_adr0021-rejection-reasons-and-settled-cash.md)）

## コンテキストと課題

#375 で本リポジトリが moomoo SDK 10.8.6808 をリフレクション全走査した実測（決済済み資金・GFV 発生回数の
いずれも存在しない）がそのまま計画へ採り入れられ、2026-08-07 に [ADR-0025](../../planning/projects/ai-stock-trading/07_adr/ADR-0025_settled-cash-poc-and-gfv-counting.md) で決着した。

| 求める値 | 裁定 | 本 IADR の範囲 |
| --- | --- | --- |
| **決済済み資金** | ADR-0019 の **PoC 項目 8**（`TrdFlowSummary` の検証・期限 2026-08-31） | **範囲外**（実 OpenD が要る） |
| **GFV 発生回数** | **自前で計数する**（決定2）。手入力は採らない | **本 IADR** |

計画が定めたのは「未決済資金による買付を自らのシステムで記録し、その件数を GFV 発生回数として扱う」ところまでで、
実装に委ねられた点が 4 つある。

1. **どの時点で検出するか**（発注審査か、約定か）
2. **どこへ、どの単位で記録するか**（計上単位・冪等の鍵・永続の要否）
3. **計数値をどの経路で判定コアへ供給するか**（ブローカー照会の型に載せるのか、別経路か）
4. **違反記録の失効**（ADR-0021 決定4-5 が解除条件を「違反記録の失効」としたが、期間も手段も未定義）

あわせて、ADR-0025 が名指しした**採ってはならない代替値**（`MaxTrdQtys.MaxCashBuy` /
`Funds.AvlWithdrawalCash` / `Funds.MaxWithdrawal`）を**構造で**遮断する形を決める必要がある。
IADR-0153 決定4 は同じ罠をコメントで塞いだが、**コメントは次の実装者の手を止めない**。

## 検討した選択肢

### 論点 A: 検出点

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | **発注審査（`RiskEvaluator`）で検出する** | 審査の時点で述語が真なら注文は `CashAccountSettlementHold` で**拒否される**。すり抜けは原理的に観測できず、**常に 0 件を返す計数**になる（数えているつもりで何も数えない） |
| **A-2（採用）** | **約定（`OrderExecuted`）で事後検出する** | すり抜けが露見するのは「現金口座であるという観測」と「買付が現に約定した事実」が突き合わさったときだけである。[IADR-0159](IADR-0159_buy-in-post-hoc-inference.md)（強制買戻しの事後推定）と同じ形であり、経路が増えない |
| A-3 | ブローカーの GFV カウンタを照会する | **存在しない**（実測。IADR-0153 決定4）。ADR-0025 決定2 はこれを断念した結果である |

### 論点 B: 記録先と計上単位

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | プロセス内（口座種別の観測と同じ形・IADR-0153 決定3） | **再起動 1 回で違反記録が消え、統制が解ける**（fail-open）。口座種別が非永続でよいのは「いま照会すれば得られる現在値」だからであり、違反件数は**再観測で復元できない履歴**である |
| **B-2（採用）** | **EF の追記専用台帳（`good_faith_violations`）・主キー `OrderId`** | 永続であり、**計上単位（1 注文 1 件）そのものを主キーにする**ことで部分約定の進行・メッセージ再送での二重計上を DB 側で止められる（IADR-0148 決定3 の `DecisionId` 主キーと同じ規律） |
| B-3 | 拒否理由 `CashAccountSettlementHold` の件数を数える | **向きが逆である。** 同理由はガードが**働いた**記録（買付を止めた回数）であり、本計数はガードが**すり抜けられた**記録である。代理にすると「ガードが働くほど GFV が増える」という反転した像になる（[IADR-0159](IADR-0159_buy-in-post-hoc-inference.md) 決定3 が `BuyInBanned` について同じ取り違えを禁じた） |

### 論点 C: 判定コアへの供給経路

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | `BrokerAccountState.GoodFaithViolationCount`（既存の欄）へ自前計数を入れる | **最も危険である。** 同じ欄に載せた瞬間、**「ブローカーが報告した GFV 件数」と読まれる**。ADR-0025 §理由 が名指しで否定した取り違えであり、これが起きると「監査ログが 0 件だからブローカー側も 0 件だ」という誤読が成立してしまう |
| **C-2（採用）** | **欄を削除し、`PortfolioSnapshot.GoodFaithViolations`（`GoodFaithViolationTally?`）で別経路から供給する** | 供給元が違うことが型の位置で表れる。死んだ欄を残すと次の実装者が判定へ結線し直す余地が残る（[IADR-0148](IADR-0148_control-violation-supply-and-unavailable-state.md) 決定2 の `ControlViolationCount` 列削除と同じ規律） |
| C-3 | `int?` のまま渡す | `count >= 2` という**持ち上げ比較が未供給を黙って「違反なし」へ倒す**（IADR-0148 決定1 が名指しした罠。#387 の fail-open が型を変えても再現する） |

### 論点 D: `MaxCashBuy` の遮断方法

| 案 | 内容 | 評価 |
| --- | --- | --- |
| D-1 | コメント・IADR に注意書きを書く（IADR-0153 決定4 の現状） | 一度は効いたが、**次の実装者が「決済済み資金の供給元」を探すときには読まれない**。moomoo SDK には近い名前の値が並んでおり、手はいちばん近い名前へ伸びる |
| **D-2（採用）** | **機械的検査**（`scripts/check-banned-settled-cash-sources.js`）＋**型の否定形テスト** | `check-banned-libraries.js` の先例（剥がした事実をレビューの記憶に頼ると必ず戻る）。**コメント中の言及は誤検出しない**——散文まで落とすと禁止の理由を書けなくなる |
| D-3 | 決済済み資金を専用の値オブジェクトにし、正しい導出からしか作れなくする | 筋は良いが、**導出（`TrdFlowSummary` の積み上げ）は PoC 項目 8 の結果に依存し本 issue の範囲外**である。作れない導出のための型だけ先に置くのは、計画外の抽象化になる |

## 決定

### 決定 1: 計数の対象事象は**発注前ガードと同一の述語**で定義し、検出は約定（事後）で行う（論点 A）

`AccountTypePolicy.ExceedsSettledCash(settledCashInBase, purchaseAmountInBase)` を**判定の単一情報源**とし、
次の 2 か所が同じ述語を呼ぶ。

1. **発注前のガード**（`RiskEvaluator`・統制2）——真なら `CashAccountSettlementHold` で拒否する
2. **事後の計数**（`GoodFaithViolationDetection.CountsAsViolation`）——ガードをすり抜けて約定した買付を 1 件記録する

ADR-0025 決定2 が「計数の対象は統制2 が拒否しようとする事象と同じである」と定めた以上、**式を 2 か所に
書いてはならない**。片方だけ直すと「拒否はするが数えない」「数えるが拒否しない」がすり抜ける
（両者が一致することを Theory テストで固定した）。

検出の条件は 4 つで、いずれも満たしたときだけ 1 件記録する。

| # | 条件 | 満たさないときの理由 |
| --- | --- | --- |
| 1 | 約定がある（`FilledQuantity > 0`） | 受付・失注・約定 0 の取消は「買付」ではない |
| 2 | 現在有効な口座観測が**現金口座**である | **不明を現金口座とみなさない**——みなすと信用口座の通常の回転売買が違反として積み上がり、2 件で恒久的に新規建てが止まる（統制ではなく事故）。不明な口座の新規建ては `BrokerAccountTypeUnverified` が既に止めている |
| 3 | 承認台帳に相関があり、**新規建ての買い**である | 金額も方向も分からない約定を推測で違反として記録しない。売却は GFV を起こさず、手仕舞いは止めない（ADR-0009） |
| 4 | 述語が真（決済済み資金で説明できない） | 資金の範囲内の買付は違反ではない |

**金額は本約定 1 件ぶん**（`FilledQuantity × AveragePrice × 承認 Intent の FxRateToBase`）である。
発注前のガードが「当日の新規建て累計 ＋ 本注文」で判定する（IADR-0153 決定4）のとは粒度が違うが、
**決済済み資金が常に未供給である現状では述語の第 1 項で真になるため差は生じない**。
PoC 項目 8 が成立した時点で累計での比較へ揃える（§フォローアップ）。

### 決定 2: 計数は第一級の値 `GoodFaithViolationTally` で供給し、`BrokerAccountState` の欄は**削除する**（論点 C）

- `PortfolioSnapshot.GoodFaithViolations`（既定 `null`）。**`null` は未供給であり、`Observed(0)` とは別物である。**
  判定は型のプロパティ（`BlocksNewEntry` / `Warns`）に持たせ、持ち上げ比較を書く動機を消す。
- **`BrokerAccountState.GoodFaithViolationCount` を削除した。** 自前計数をブローカー照会の欄へ入れると
  「**ブローカーの GFV カウンタの写し**」と読まれる。**自前で数えられるのは自らのガードをすり抜けた買付だけであり、
  ブローカー側が独自に GFV と判定した事象は捕捉できない。両者が一致する保証はない**（ADR-0025 §理由）。
- **`AccountTypePolicy.GoodFaithViolationStopThreshold = 2` は変更しない。** 未供給は止める（fail-closed）。
- **`CashAccountSettlementHold` へ写像しない。** 解除条件が違う（前者は「違反記録の失効」、後者は「T+1 の決済」。
  IADR-0153 決定5）。畳むと監査ログ（FR-11）で「なぜ止まっているのか」が実態と食い違う。

### 決定 3: 記録は EF の追記専用台帳（主キー `OrderId`）とし、監査台帳（FR-11）へ運ぶ（論点 B）

- `good_faith_violations`（リスク管理サービス専有 DB）。**永続でなければならない**——プロセス内に持つと
  再起動 1 回で「2 件で止める」統制が解ける。
- **計上単位は 1 注文 1 件**。主キーが `OrderId` であること自体が単位の担保であり、部分約定の進行・
  メッセージ再送で二重計上しない。
- **記録が先、イベント発行が後。** 逆順にすると「発行できたが記録が落ちた」違反が生まれ、件数が過小になる（緩い側）。
- `GoodFaithViolationRecorded` を発行し、`AuditService` が中央監査台帳へ記録する。ADR-0025 が手入力を
  採らなかった理由の 1 つが「**監査証跡に乗らない**」ことであり、ここがその要求への回答である。
- **監査の要約に「自前計数」「自らのガードの失敗」「ブローカーの GFV 判定とは一致しない」を必ず書く**
  （テストで固定した）。書かないと「監査ログが 0 件だからブローカー側も 0 件だ」と読まれる。

### 決定 4: 違反記録に**失効期間を設けない**（累計とする。計画未定義のため値を発明しない）

ADR-0021 決定4-5 は `GoodFaithViolationLimitReached` の解除条件を「違反記録の失効」としたが、
**失効の期間も手段も計画に無い**。ブローカーの GFV は通常 12 か月のローリング窓で失効するが、
**それを実装が発明してはならない**（発明した瞬間、実装だけが知っている統制の緩みが生まれる）。

- 自動失効は fail-open である。**累計のまま止める**方が安全側に倒れる。
- 帰結として、**1 件でも記録されれば 2 件目で恒久的に現金口座の新規建てが止まる**。
  ただし本計数は「ガードの失敗回数」であり、**1 件出た時点で人が原因を調べるべき事象**である。
- **計画へ環流する**（[feedback/20260807_adr0025-gfv-counting-open-points.md](../../feedback/20260807_adr0025-gfv-counting-open-points.md)）。

### 決定 5: 決済済み資金の代替値は**機械的検査 ＋ 型の否定形テスト**で遮断する（論点 D）

- `scripts/check-banned-settled-cash-sources.js` が `backend/**/*.cs` を走査し、
  `MaxCashBuy` / `AvlWithdrawalCash` / `MaxWithdrawal` の**コードとしての参照**を検出して CI を落とす
  （ジョブ `banned-settled-cash-sources`）。
- **コメント・XML ドキュメント中の言及は誤検出しない。** 既存の IADR・アダプタ・仕様書はまさにこれらの
  名前を挙げて禁止を説明しており、散文まで落とすと**検査が禁止の説明を殺す**。
- あわせて `BrokerAccountState` が**これらの欄を持たない**ことを否定形テストで固定した
  （[IADR-0159](IADR-0159_buy-in-post-hoc-inference.md) の `ReportView` が拒否理由由来の入力を**持たない**ことで
  取り違えを不可能にしたのと同じ形）。
- **PoC 項目 8 の検証時にも同じ禁止が掛かる**（ADR-0025 決定1 の明文）。検査は検証コードにも等しく効く。

## 理由

- **決定1 の述語を単一情報源にしたのは、計数とガードが「同じ事象」を指すことが ADR-0025 の前提だからである。**
  別々に書くと、どちらかが緩んだときに「拒否しないのに数えない」が成立し、**統制の失敗が統計にも現れなくなる**。
- **決定2 で欄を削除したのは、取り違えの防止がコメントでは達成できないからである。** `BrokerAccountState` の
  ある欄に数字が入っていれば、読む側は「ブローカーが言っている」と受け取る。**欄が無ければその誤読は成立しない。**
- **決定3 で `OrderId` を主キーにしたのは、計上単位を DB の制約として表現するためである。** 「1 注文 1 件」を
  アプリ側の条件分岐で守ると、部分約定の進行が加わったときに静かに壊れる。
- **決定4 で失効を設けなかったのは、計画に無い値を実装が決めると「実装だけが知っている統制」になるからである。**
  安全側（止まる側）へ倒したうえで計画へ返す。
- **決定5 でスクリプトを選んだのは、この罠が「探しに来た人」に対して働くからである。** レビューは変更差分を見るが、
  **危険なのは差分が「もっともらしく見える」こと**である（`MaxCashBuy` を分母にしたコードは一見正しい）。
  機械的検査は差分の見た目に依存しない。

## 結果

- 良い影響:
  - ADR-0025 決定2 が実装され、GFV 発生回数に**供給経路ができた**（監査証跡にも乗る）。
  - 「未供給」と「0 件」が型で区別され、**未供給は止まる**（fail-closed が維持された）。
  - `MaxCashBuy` を分母に据える経路が**機械的に塞がった**。PoC 項目 8 の検証時にも同じ検査が効く。
  - **自前計数の限界**（ブローカー判定と一致しない）が、型のコメント・イベントのコメント・監査ログの要約・
    テスト・本 IADR の 5 か所に残った。
- 悪い影響 / トレードオフ:
  - **本経路は通常運行では 1 件も発火しない見込みである。** 現金口座の買付は `CashAccountSettlementHold` で
    止まっており、そもそも約定が生まれない。発火するのは**ガードが壊れたとき**だけである
    （それが「ガードの失敗回数」という定義の意味でもある）。
  - **ブローカーが先に 3 回目を計上して口座制限が掛かる事態は、本計数では防げない**（ADR-0025 §結果）。
    運用では moomoo のアプリ側の GFV 表示との定期的な目視突合が要る。
  - **違反記録に失効が無い**（決定4）。1 件出れば人の介入が要る状態になる。
  - `BrokerAccountState` の欄削除は**破壊的変更**である。ただし当該欄に値が入る経路は一度も存在しなかったため、
    実質的な情報の損失は無い（運用前のため許容した）。
  - 検出の金額比較が**本約定 1 件ぶん**であり、発注前ガードの「当日累計」と粒度が違う（決定1 の注記）。
- フォローアップ:
  - **PoC 項目 8**（`TrdFlowSummary` が実口座で `SettlementDate` 付きの明細を返すか。期限 2026-08-31）。
    成立したら決済済み資金を供給し、**あわせて検出の比較を当日累計へ揃える**。`docs/blocked-tasks.md` に登録した。
  - **違反記録の失効の裁定**（決定4・環流済み）。
  - 2 回目の警告の通知経路（Discord）は未結線のまま（#375 からの継続）。判定（`WarnsForGoodFaithViolations`）は
    純関数として置いてある。

## 関連

- Supersedes: なし（[IADR-0153](IADR-0153_broker-account-type-supply-and-fail-closed.md) 決定4 が
  「GFV 発生回数は供給せず、供給が無ければ止める」とした部分に**供給経路を与える**補完である。
  同 IADR の fail-closed は覆していない——**決済済み資金は依然として未供給であり、現金口座の買付は止まったままである**）
- Superseded by: なし
- 計画への環流: [feedback/20260807_adr0025-gfv-counting-open-points.md](../../feedback/20260807_adr0025-gfv-counting-open-points.md)
