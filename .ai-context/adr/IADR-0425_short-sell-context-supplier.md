---
title: IADR-0425 空売り文脈を本番で組む供給元を入れる — 借株可否は発注執行が発注と同じ口座のヘッダで照会し、エクスポージャは保有の時価と当日の未約定の空売りの残数量 × 承認価格から出す。分からなければ文脈を組まず今と同じく拒否する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-05, UC-06, ADR-0016, ADR-0019, ADR-0026, IADR-0111, IADR-0131, IADR-0144, IADR-0158, IADR-0159, IADR-0163, IADR-0346, IADR-0354, IADR-0397, IADR-0408, IADR-0420]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定 2(a)・決定 3〔2026-08-06 改訂・同追記・2026-08-07 確定〕・決定 9・決定 10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md (PoC 項目 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0026_short-fee-rate-unit-poc.md (PoC 項目 9)
---

# IADR-0425: 空売り文脈の供給元と、未約定の空売りのエクスポージャ算入

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: endazon（#967 の裁定 2026-09-25「供給元を今すぐ実装する。失敗・照会不能は今と同じく拒否。偽の文脈は組まない」）/ claude（実装への写像）

## 起点・関連

- 関連する計画書 ID: **ADR-0016 決定 2(a)**（1 銘柄 equity の 10%）・**決定 3**（一次ゲートは `IsShortPermit`／照会できないなら空売りしない／
  `ShortFeeRate` の単位は未確定＝案 A のフェイルクローズ）・**決定 9**（空売り比率 50%）・決定 10（拒否理由は増やさない）、
  ADR-0019 PoC 項目 3（`TrdGetMarginRatio` は実弾口座のヘッダでのみ成功）、ADR-0026 PoC 項目 9（単位の確定手段）、FR-10 (1)(3)(6)・FR-05・UC-06
- 対象 Issue: #967（#832 項目 3 のトリアージから切り出し）
- 関連する実装仕様書: [20260925_967_short-sell-context-supplier](../specs/20260925_967_short-sell-context-supplier.md)
- 関連 IADR: [IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) 決定 2・4（文脈が無ければ拒否・判定できないものは通さない）、
  [IADR-0144](IADR-0144_moomoo-short-selling-poc-outcomes.md) 決定 3・5（実弾ヘッダでのみ成功・キャッシュと失敗時の即時リトライ禁止）、
  [IADR-0158](IADR-0158_short-sell-borrow-permit-primary-gate.md)（一次ゲート＝`ShortPermit`・`ShortFeeRate` は写像しない。決定 4「本 PR では供給元を実装しない」の後を本 IADR が受ける）、
  [IADR-0159](IADR-0159_buy-in-post-hoc-inference.md) 決定 5（偽の文脈を組まない）、
  [IADR-0163](IADR-0163_allow-list-and-required-dependency-scope.md) 決定 2（不在が統制の無効を意味する依存は必須）、
  [IADR-0346](IADR-0346_count-working-entry-orders-in-risk-limits.md) 決定 2（未約定の新規建てを残数量 × 承認価格で算入）、
  [IADR-0354](IADR-0354_capital-baseline-from-broker-account.md) 決定 6（起きていない事実を理由にしない）、
  [IADR-0111](IADR-0111_broker-tier-selection.md)（発注先 × 環境の 2 軸）、
  [IADR-0397](IADR-0397_composition-wiring-guard.md)（組み立てガード）、
  [IADR-0408](IADR-0408_report-positions-and-sizing-context-read-tolerance.md)・[IADR-0420](IADR-0420_cross-service-read-contract-convention-and-guard.md)（受け手の DTO は nullable・送り手の本物の型による契約テスト）

## コンテキストと課題

- 本番の審査（`OrderScreeningService`）は `RiskEvaluator.Evaluate` へ空売り文脈を渡しておらず（既定 null）、`ShortSellOrderContext` を組む本番コードは 0 件だった。
  判定コアは文脈 null で `BorrowUnavailable` を立てて**打ち切る**ため、1 銘柄 10% と空売り比率 50% は実装されているのに**一度も評価されない**。
  空売りの新規建ては全件拒否（安全側）で、統制の穴ではない（#967 の事実確認）。
- 🔴 供給元を入れる日に**エクスポージャを何から・何を含めて出すか**が未決だった。約定だけで数えると、指値が溜まっている間に上限を超えて承認し続ける
  （#829 と同型の穴。IADR-0346 は日次枠・段階資金・保有建玉数だけを塞いだ）。
- moomoo の取引接続（OpenD）は発注執行だけが持つ。リスク管理から OpenD へ直接繋ぐと接続が二重になる（IADR-0092 の単一接続）。
- `TrdGetMarginRatio` は SIMULATE 口座では失敗し、実弾口座のヘッダでのみ成功する（IADR-0144 決定 3）。上限は 30 秒 10 回で、失敗も枠を消費する（同 決定 5）。
  本系の取引接続はヘッダを SIMULATE に固定し、実弾は閂（`LiveTradingGate`・起動時拒否）で止めている。

## 検討した選択肢

### 論点 1: どのヘッダで照会するか

| 案 | 判定 |
| --- | --- |
| **A. 発注と同じ口座（SIMULATE）のヘッダで照会する**（採用） | **採用**。既存の接続・口座・閂を一切動かさない。SIMULATE では照会が失敗し「分からない」＝今と同じく拒否（裁定が明示した挙動） |
| B. 照会用に実弾口座のヘッダ（読み取り専用）を足す | **本件では採らない**。IADR-0144 決定 3 が予告した「照会用と発注用の環境を別に持つ」＝IADR-0111 の部分改定であり、`TrdEnv_Real` を本系のコードへ初めて入れる判断になる。裁定は「SIMULATE では成功しない見込み・そのとき拒否」で、実弾ヘッダの導入は裁定の射程外。**別 issue（#1000）で裁定を仰ぐ** |

### 論点 2: 未約定の空売りをエクスポージャへ算入するか

| 案 | 判定 |
| --- | --- |
| **A. 算入する（残数量 × 承認価格）**（採用） | **採用**。IADR-0346 決定 2 と同じ規則・同じ純関数（`PortfolioProjection.ProjectWorkingEntries`）。約定分は保有へ、残りだけが未約定へ入る（二重計上しない） |
| B. 約定だけで数える | **却下**。#829 と同型の穴（指値が溜まっている間に上限を超えて承認し続ける） |

### 論点 3: 保有建玉の評価

| 案 | 判定 |
| --- | --- |
| **A. 時価（現在値 × 数量 × 建玉の加重平均約定時レート）**（採用） | **採用**。SC-03 の空売り比率の表示（`ShortSellingStatusService`）と同じ定義。現在値が 1 件でも無ければ「分からない」 |
| B. 取得原価 | **却下**。損失の出ている空売りほどエクスポージャを小さく見積もる（上限が緩む側）。計画 ADR-0016 決定 2(a)・決定 9 の本文は評価の基準を明示していないため、これは実装の選択である |

### 論点 4: 審査の同期／非同期

| 案 | 判定 |
| --- | --- |
| **A. 審査を `ScreenAsync` にし、同期の入口を消す**（採用） | **採用**。供給元を通らない審査の経路を作らない。テストの呼び出し 67 箇所を機械的に置き換えた |
| B. 同期の `Screen` を残し、`HttpClient.Send`（同期）で照会する | **却下**。`DelegatingHandler` を `SendAsync` だけで書いたハンドラ（サービストークン）を同期送信は素通りし得る |
| C. 同期の `Screen` を残し、非同期の入口を足す | **却下**。「供給元を通らない入口」が残り、ハンドラを戻す変更が全緑で通る（過去 4 本の PR が「配線を外しても全緑」だった形） |

## 決定

### 決定 1: 借株可否は発注執行が照会し、`GET /order-execution/short-permit` で返す

- 照会ポート `IShortPermitSource`（Features）を `MMApiMoomooTradeClient` が実装する（`TrdGetMarginRatio`・`MarginRatioInfo.IsShortPermit`）。**moomoo 構成でだけ登録**し、内蔵 paper は未登録＝常に「分からない」。
- 口は `OwnerOrService`（発注執行に `AddAiStockTradingAuth` と共通ミドルウェアを初めて入れた）。応答 `ShortPermitView(Symbol, Market, Status, UnknownReason, ObservedAt)`、
  `Status` は Unknown(0) / Permitted(1) / NotPermitted(2)。**既定値 0 は Unknown**。**`ShortFeeRate` は運ばない**（単位未確定。IADR-0158 決定 3）。
- 応答に当該銘柄の行が無い・`IsShortPermit` の欄が無い → Unknown（不許可と取り違えない）。非成功の retType は例外 → Unknown。

### 決定 2: 照会のヘッダは発注と同じ口座（SIMULATE）。実弾ヘッダの照会経路は作らない

論点 1 の A。**現状（SIMULATE）では照会は常に失敗し、空売りは今と同じく `BorrowUnavailable` で拒否される。** 実弾ヘッダでの照会は別の裁定に委ねる（#1000）。

### 決定 3: 照会を節約する（IADR-0144 決定 5）

`ShortPermitQueryService`（singleton）が (銘柄, 市場) ごとに答えを **60 秒**、失敗・欄の欠落を **30 秒**キャッシュし（失敗時に即時リトライしない）、
実際の照会回数を失敗も含めて **30 秒あたり 9 回**（上限 10 回より 1 回少なく）で打ち切る（予算切れは照会せず Unknown・キャッシュしない）。米国株以外は照会しない（ADR-0016 決定 13）。
同じ (銘柄, 市場) の照会が走っている間の要求は相乗りする（照会は 1 回。照会は呼び出し側の打ち切りで止めず、結果はキャッシュする）。

### 決定 4: リスク管理の受け手は `HttpShortSellBorrowSource`。どの失敗も「分からない」

`OrderExecution:BaseUrl`（未設定・不正は `UnavailableShortSellBorrowSource`＝常に Unknown）・サービストークン・5 秒。非 2xx・例外・タイムアウト・本文の読み違い・
**項目の欠落や未定義値**（DTO は全項目 nullable。IADR-0408）・**要求と別の銘柄／市場の答え**は Unknown。観測 `ShortSellBorrowObservation` は許可／不許可／分からないの 3 状態。

### 決定 5: 文脈は `ShortSellContextSupplier` が組み、分からなければ組まない

`OrderScreeningService` の**必須依存**（IADR-0163 決定 2 と同じ理由——不在は「文脈なし＝拒否」で安全側だが、上限が一度も評価されない状態へ黙って戻る）。
**新規の売り建て（`Sell`×`Open`）の審査でだけ**呼ぶ（それ以外でブローカーの枠を使わない）。

- 借株可否が Unknown → **null**（判定コアが `BorrowUnavailable` で打ち切る＝今と同じ）。
- エクスポージャが分からない → **null**（0 で埋めない）。
- 組めたとき: `ShortPermit`＝観測／**`BorrowRateAnnual`＝常に null**（単位未確定。ADR-0016 決定 3 の 2026-08-07 確定＝案 A）／`DividendRecordDate`＝null（供給元が無い）／
  `MarginSnapshot`＝既存の維持率の供給（既定は供給なし）／`BuyInBanUntil`＝推定台帳の期限／判定日＝注文の市場の現地取引日。
- 🔴 **したがって借株が許可された銘柄でも `BorrowUnavailable` が立ち、空売りは今も通らない。** 変わるのは、判定コアが打ち切らずに 10% / 50%・維持率・株価下限・逆指値必須を評価し、
  その理由が監査に載ることである（偽の文脈で「評価したことにする」のではなく、観測した値の上で評価する）。

### 決定 6: エクスポージャ（基準通貨）は保有の時価と当日の未約定の新規建ての残数量 × 承認価格から出す

`ShortExposureProjection.Project`（純関数）。入力は統制の射影と同じ（台帳の約定 → `ProjectOpenPositions`／当日承認・未終端の新規建て → `ProjectWorkingEntries`）。
売り建ては空売り、買い建てはロングとして建玉総額へ入る。保有建玉のうち 1 件でも現在値が無ければ null、建玉も未約定も無ければ 0（無いことを台帳で確かめた値）。
現在値ソースは構成を問わず渡す（時価評価が無効なら手元の値が補充されず、保有建玉があれば null＝拒否）。

### 決定 7: 審査は非同期（`ScreenAsync`）で、同期の入口を持たない

論点 4 の A。ハンドラ `TradeDecisionMadeHandler` は `await ScreenAsync(message, cancellationToken)`。

### 決定 8: 本番の組み立てを通した結線テストを置く

`ShortSellContextWiringTests`（T-10-1027〜T-10-1029）が `Program.cs` の組み立てで、照会先ありは `HttpShortSellBorrowSource`・無しは `UnavailableShortSellBorrowSource` が解決されること、
本番の Wolverine ハンドラ経由で「許可」の空売りが 10% 超・50% 超で `ShortExposureExceeded` になり内側では立たないこと、「分からない」では上限を評価せず `BorrowUnavailable` で拒否することを固定する。
変異注入（審査が文脈を渡さない／照会先を常に分からないにする／未約定を数えない／現在値の無い建玉を 0 と数える／分からないを false で組む／受け手が欠落を許可と読む）はすべて赤くなる（テスト仕様書に実測を記録）。

## 理由

- **裁定の 2 点（今すぐ実装・失敗は今と同じく拒否）を同時に満たす形がこれだけである。** 供給元・算入・結線は入れ、分からないときの挙動は 1 ビットも変えない。
- **未約定を数えるのは IADR-0346 と同じ理由・同じ関数である。** 規則を 2 か所に書かない。
- **実弾ヘッダを入れないのは、閂を動かす判断を黙って混ぜないためである。** SIMULATE での失敗は仕様どおりで、上限が実地で評価されるかは別の裁定で決まる。

## 結果

- 良い影響:
  - 本番の審査が空売り文脈を組む経路を持ち、借株可否が答えられた日には 10% / 50% が観測した値の上で評価される（結線テストと変異注入で固定）。
  - 未約定の空売りが上限を食う（#829 と同型の穴を供給元と同時に塞いだ）。
  - 「分からない」と「借りられない」「0」が型の上で区別される（照会・受け手・文脈・エクスポージャのすべてで）。
- 悪い影響・トレードオフ（残余リスク）:
  - 🔴 **SIMULATE では照会が常に失敗するため、現状の本番では上限は今も評価されない**（全件 `BorrowUnavailable`）。実地で評価されるのは実弾ヘッダの照会か、SIMULATE で照会が成功する日である。**未検証**（実 OpenD へは照会していない）。
  - 🔴 **料率が単位未確定で null のため、借株が許可されても空売りは通らない。** 単位は ADR-0026 PoC 項目 9（#342）待ち。
  - 🔴 **権利確定日の「不明」を型が「無し」と区別しない**（`DividendRecordDate` null）。いまは料率の null が先に拒否するので実害は無いが、**料率の供給を始める前に、権利確定日の不明を拒否へ倒す手当てが要る**（T-10-1022 がこの前提を表明している）。
  - 保有建玉の評価に現在値が要る。時価評価が無効な構成（`values.yaml` の既定）では、建玉が 1 件でもあれば文脈は組まれない（安全側）。
  - 配備: `values.yaml` は order-execution に `auth: true` を足し、risk-management の `OrderExecution__BaseUrl` は**空（未結線）**のまま置いた（`values-local.yaml` も同じ空の値を写した。helm はリストを置換するため）。結線は配備の判断（SIMULATE では結線しても結果は変わらない）。
  - 照会の予算とキャッシュはプロセス内（発注執行の複製が複数になれば予算も複数になる。現状は単一）。同じ銘柄の同時の要求は走っている照会に相乗りする（T-10-1036）。借株可否の答えは最長 60 秒古い。
  - 新規の売り建ての審査は発注執行への照会（最大 5 秒）を待つ。
- フォローアップ: [#1000](https://github.com/endazon/ai-stock-trading/issues/1000)（実弾口座のヘッダ〔読み取り専用〕で借株可否を照会する照会用の環境を足すか。IADR-0111 の部分改定・実弾の閂との関係の裁定）。

## 関連

- Supersedes: なし（IADR-0158 決定 4「本 PR では供給元を実装しない」は範囲の宣言であり、本 IADR がその後を受ける。IADR-0131・IADR-0158・IADR-0159 の決定はいずれも有効）
- Superseded by: なし
