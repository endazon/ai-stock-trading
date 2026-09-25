---
title: IADR-0347 S3（他のブローカー側注文種別）は能力ポートを分けて StopLimit / TrailingStop を構成で選び、拒否理由（retType / retMsg）を専用イベントで監査へ残す — 結果の扱いは S0 と同一
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, FR-12, UC-02, ADR-0003, ADR-0016, ADR-0040, IADR-0016, IADR-0060, IADR-0111, IADR-0210, IADR-0211, IADR-0342]
author: claude (Claude Code)
created: 2026-09-18
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1 の S3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 の 3 文〔口座種別の軸〕)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (§逆指値が成立しない場合の扱い)
---

# IADR-0347: S3（他のブローカー側注文種別）を SIMULATE で試し、拒否理由を監査へ残す

- 状態: Accepted
- 日付: 2026-09-18
- 決定者: claude（起票 #821。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: FR-10（損切りの実行機構）、FR-11（監査）、FR-12（ペーパートレード）、UC-02、
  **ADR-0040 決定 1 の S3**（planning#638 の裁定）、ADR-0016 決定 2(b)（空売りの統制。改めない）、ADR-0003（AI は統制を上書きできない）
- 対象 Issue: #821（前提は #819＝選択機構）。症状の一次情報は #809（SIMULATE は `OrderType_Stop` を受理しない＝
  `Paper trading does not support Stop order`）
- 関連する実装仕様書: [20260918_821_s3-alternative-order-types](../specs/20260918_821_s3-alternative-order-types.md)
- 関連 IADR: [IADR-0342](IADR-0342_simulate-stop-loss-method-selection.md)（選択機構。本 IADR はその
  決定 4-5「S1 / S3 / 未知は S0 と同じ扱い」のうち **S3 だけを置き換える**）、
  [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md)（S0 の保護レグ。**本 IADR は覆さない**）、
  [IADR-0211](IADR-0211_opend-unavailable-forgo-without-queueing.md)（接続不可は見送り）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（moomoo 構成の起動時 preflight の作法）

## コンテキストと課題

ADR-0040 決定 1 の S3 は「他のブローカー側注文種別（`OrderType_StopLimit`、または設定で `TrailingStop`）で
保護レグを発注する」である。**公式は模擬取引を「指値・成行のみ」としており拒否される見込みが高く、
issue #821 は「拒否理由（retType・retMsg）を監査ログへ記録すること自体が目的」と明記する。**
拒否時の扱いは S0 と同じ（建玉を持たない）、受理された場合も S0 と同じ保護レグとして扱う。

実装で決めるべきは (a) 代替種別の発注をどの契約で表すか、(b) 拒否理由をどこからどう取り出すか、
(c) 監査へどの形で残すか、(d) StopLimit の**指値価格**と TrailingStop の**トレール幅**をどう決めるか、
(e) どちらの種別を使うかの設定をどこに置くか、である。

**現状の障害**: 拒否理由は `MMApiMoomooTradeClient.EnsureSucceeded` が
`InvalidOperationException($"…（retType={retType}）: {retMsg}")` と**文字列へ畳んで**投げ、
`MoomooBrokerAdapter` がそれを握って終端 `Rejected` に倒すため、**retType / retMsg はログにしか残らない**。

## 検討した選択肢

1. **`IProtectiveOrderBroker.PlaceStopOrderAsync` に注文種別の引数を足す** — 既存ポートの署名が変わり、
   paper（`PaperBrokerAdapter`）も代替種別の引数を受ける（が S3 は paper へ届かない＝意味のない引数を持つ）。
   さらに戻り値が `BrokerOrder` のままでは拒否理由を運べず、結局 `BrokerOrder` を汚すことになる。**却下**。
2. **`BrokerOrder` に拒否理由（retType / retMsg）を足す** — 保護レグに限らずすべての発注経路の戻り値が
   ブローカー固有の診断値を持つことになり、paper・将来の別ブローカーが「常に null を返す欄」を負う。
   契約の意味も薄まる（`Rejected` の理由はブローカーごとに体系が違う）。**却下**。
3. **姉妹の能力ポート `IAlternativeProtectiveOrderBroker` を足し、戻り値に理由を載せる**（採用）。
   本リポの既存の作法（`IClientOrderIdBroker` / `IBrokerPositionSource` / `IBrokerAccountSource` など、
   **できることを持つ実装だけが実装する能力インターフェース**）と同型である。
4. **拒否理由を `ProtectiveStopCoverageLost` の欄として足す** — 保護喪失は「統制が働いた／破れた」記録であり、
   受理されたときには出ない。**受理された S3 の「何の種別で試したか」が残らない**うえ、S0 の保護喪失にも
   常に null の欄が付く。IADR-0342 が選択肢 4 を却下したのと同じ理由。**却下**（専用イベントを採る）。
5. **S3 の種別選択を risk-management の利用者設定に足す** — ADR-0040 決定 3 が求めるのは**手法（S0〜S3）の
   選択経路**であり、S3 は既にその経路で選べる。「S3 のとき SDK のどの注文種別で試すか」は探索パラメータであって
   統制値ではなく、利用者設定・履歴・承認イベントの 3 か所を増やすだけの対価に見合わない。**却下**。

## 決定

1. **能力ポートを分ける**: `Shared.Contracts.Ports.IAlternativeProtectiveOrderBroker`
   （`AlternativeProtectiveOrderType` プロパティ＋`PlaceAlternativeStopOrderAsync`）。実装するのは
   `MoomooBrokerAdapter` だけである（S3 は moomoo SIMULATE にしか届かない。IADR-0342 決定 4）。
   戻り値 `AlternativeProtectiveOrderPlacement`（`BrokerOrder` ＋ `OrderType` ＋ `RejectReasonCode`＝retType ＋
   `RejectReasonMessage`＝retMsg）が**理由を持ち帰る**。**種別はプロパティでも公開する**——発注が例外で落ちても
   「何の種別で試したか」を記録できる必要があるためである。
   **S3 の能力が無い発注先へ S3 が届いたら発注せず見送る**（`StopOrderUnsupported`。S0 へ黙って読み替えない）。
2. **解決の分岐**: `StopLossMethodDisposition` に `AlternativeBrokerOrderType` を足し、
   `StopLossMethodPolicy.Resolve` の最後を「S2 → 免除／**S3 → 代替注文種別**／S1・未知 → S0（未実装）」に分ける。
   **判定の順序（S0 → 発注先 → 空売り → 手法）は 1 行も変えない**——実弾では `Refused`、空売りでは S0 のままである
   （ADR-0016 決定 2(b) / ADR-0040 決定 1 末尾）。
3. **注文種別の写像と価格パラメータ**（`MoomooOrderKind` に `StopLimit` / `TrailingStop` を末尾追加）:
   - **StopLimit**（`OrderType_StopLimit`）: `AuxPrice` = 発火価格（損切りライン）、`Price` = **指値**。
     指値は発火価格から**不利側**（売りなら下・買いなら上）へ `StopLimitOffsetRatio`（既定 **1%**）ずらす。
     🔴 **発火価格と同値にしてはならない** —— 急落・急騰で板が飛べば約定せず、「保護レグを置いたのに保護されない」
     状態（S0 が避けている当のもの）を作る。ずらし幅は構成
     （`Broker:Moomoo:StopLimitOffsetRatio`。0〜10%。範囲外は起動時に停止）。
   - **TrailingStop**（`OrderType_TrailingStop`）: `TrailType_Amount` ＋ `TrailValue` =
     |エントリーの判断価格 − 発火価格|、`TrailSpread` = 0（発火後は成行であり価差を持たない）。
     トレール幅は発火価格だけからは決まらないため、**能力ポートがエントリーの判断価格（`OrderIntent.Price`）を受ける**。
4. **種別の選択は発注執行の構成**: `Broker:Moomoo:AlternativeStopOrderType`（`stoplimit` 既定 / `trailingstop`）。
   **未知の値は起動時に停止する**（IADR-0060 の作法。既定へ黙って倒すと「TrailingStop を選んだつもりで
   StopLimit が飛ぶ」を作る）。**手法（S0〜S3）そのものの選択は従来どおり利用者の設定**であり（ADR-0040 決定 3・
   IADR-0342 決定 2）、本項目はその下位の探索パラメータである（選択肢 5 の却下理由）。
5. **拒否理由の捕捉**: `EnsureSucceeded` が投げる例外を `MoomooTradeRequestException`
   （`InvalidOperationException` 派生・`Operation` / `RetType` / `RetMsg` を保持）へ変える。
   **メッセージ文字列は 1 バイトも変えない**——既存の捕捉・ログ・表明が壊れないようにするためである。
   アダプタは従来どおり終端 `Rejected` へ倒しつつ、理由を戻り値へ載せる。**送信前に棄却した場合も理由を返す**
   （「なぜ送らなかったか」が空欄だと、拒否と未送信を台帳上で区別できない）。
   接続確立の失敗（`BrokerUnavailableException`）は従来どおり**丸めずに伝播**する（IADR-0211）。
6. **監査**: 新イベント `AlternativeProtectiveStopAttempted`（EntryDecisionId, StopDecisionId, Symbol, Market,
   OrderType, Status, BrokerOrderId, RejectReasonCode, RejectReasonMessage, Method, Provider, OccurredAt）と
   `AlternativeProtectiveStopAttemptedAuditHandler`。相関はエントリーの DecisionId（`ProtectiveStopPlaced` と同じ）。
   **受理・拒否のどちらでも 1 件出す**——受理を実測したときに「何の種別が通ったか」を台帳から読めなければ、
   S3 の目的（実測の記録）が半分しか果たせない。要約に種別・`retType=…`・retMsg を書く。
7. **結果の扱いは S0 と同一**: 受理なら `ProtectiveStopPlaced` ＋ `ExecutionRecord` ＋ `protective_stop_orders`
   （＝`ProtectiveStopGuard` の巡回対象）、拒否なら `ProtectiveStopCoverageLost`（未約定→取消／約定済み→成行手仕舞い）。
   **分岐するのは「何で発注するか」だけである。** 試行の記録は結果イベントと**排他ではなく**、同じ処理で重ねて出る。
8. **通知は足さない**: 拒否は既存の `ProtectiveStopCoverageLost` が Critical で鳴り、受理は S0 と同じで通知対象外。
   通知の母集合はハンドラ型であって全イベントではない（`NotificationConsumerCoverageTests`）。

## 理由

- **能力ポートを分ける**ことで、S0 の契約（`IProtectiveOrderBroker`）・`BrokerOrder`・paper 実装を 1 行も変えずに
  S3 を足せる。「できる実装だけが実装する」は本リポが既に 5 つの能力インターフェースで採っている形であり、
  新しい規律を持ち込まない。
- **理由を戻り値に載せる**のは、#821 の目的が「拒否理由を監査へ残すこと」そのものだからである。
  ログは 7 年保持されない。契約イベントの payload は保持される（NFR-10）。
- **専用イベントにする**のは、保護喪失（統制の記録）と「何の種別で試したか」（実測の記録）が別の事実であり、
  受理されたときにも後者だけが必要になるためである。
- **指値をずらす**のは、StopLimit が指値価格を必須とする以上どこかで決めるしかなく、同値に置くのが
  最も危険な既定だからである。構成へ出して**値が見える**ようにした（マジックナンバーにしない）。
- **未知の構成値で停止する**のは、種別の取り違えが「保護レグの種類が黙って変わる」形で現れ、
  実測（S3 の目的）そのものを無効にするためである。

## 結果・残余リスク

- 良い影響: SIMULATE で S3 を選ぶと、**注文種別と拒否理由が監査台帳に残る**。ADR-0040 が S3 に期待した
  「模擬取引で受理される種別があるか」の実測が、事後に台帳から読める形で得られる。S0・S1・S2 の挙動は不変。
- 🔴 **`ProtectiveStopGuard` の再発注は S0（`OrderType_Stop`）で行う。** 受理された S3 のレグが後から失効した場合、
  ガードは手法を知らないため S0 で再発注し、SIMULATE では拒否されて成行手仕舞いへ倒れる。
  ガードに手法を持たせるには巡回対象の記録（`protective_stop_orders`）へ手法を足す必要があり、本 PR の射程外とした
  （**S3 が受理されることを実測してから**判断する。現時点では受理されない見込みが高く、先に作ると使われない分岐が残る）。
- **StopLimit の指値幅（既定 1%）は実測に基づく値ではない。** SIMULATE で受理された場合に、
  この幅で実際に約定するかは別途の観測が要る。
- **TrailingStop は損切りラインを「幅」に変換する**ため、建玉後に有利側へ動くとトリガー価格も動く
  （S0 の固定した損切りラインとは意味が異なる）。**受理された場合の挙動差は計画へ環流する候補**であり、
  受理を実測したら planning へ issue を起票する（現時点では拒否される見込みのため起票しない）。
- 実弾（`TrdEnv=real`）で S3 が有効になる経路は増えていない（IADR-0342 決定 2・決定 4 の 2 方向の拒否がそのまま効く）。

## ［2026-09-18 追記 / #844］代替レグの価格は市場の刻みへ丸めてから送る

稼働環境（moomoo SIMULATE）で S3 を選び、保護レグが**拒否された**。ただし理由は本 IADR が想定した
「模擬取引が代替種別に非対応」ではなく、**こちらが送った価格の刻み**だった。

```
retType=-1 The precision of Price in Place Order does not meet the specification.
```

指値を `発火価格 ± 発火価格 × StopLimitOffsetRatio` で作り、**丸めずに**送っていた（`332.35 × 0.99 = 329.0265`）。
建玉は建てずに取り消された（fail-closed は設計どおり動作）。

🔴 **当初この追記は「エントリーの指値は上流で刻みに収まるため代替レグだけが踏む」と書いたが、これは誤りである**
（#845 の監査が実測で否定した）。**上流に丸めは無い。** 損切りラインは LLM が返す `StopLossDistancePerShare` から
作られ、桁の制約はプロンプトにもパーサにも無い。**S0 の発火価格とエントリーの指値も同じ拒否を踏み得る**
——実弾でも使う経路であり、**#846 で扱う**。

- 指値・発火価格・トレール幅を**市場の小数桁へ丸めてから送る**（`MoomooPriceRounding`）。発火価格も丸める
  ——指値だけ直しても、発火価格が刻みを外れていれば同じ拒否になる。
- **丸めの向きは保護が緩む側へ倒さない**。指値は約定しやすい側（売りは切り下げ）、発火価格は早く発火する側、
  トレール幅は狭い側。
- 丸めで指値が発火価格へ寄り切ったら**1 刻みだけ離す**（同値だと「保護レグを置いたのに約定しない」）。
- **刻みに満たないトレール幅は 0 のまま**にする。1 刻みを足すと「幅 0 は送らず理由を残す」発注前検証を無効化し、
  決定が求めていない保護距離を捏造することになる。

🔴 **本 IADR の見込みを 1 つ訂正する**: 「模擬取引は代替種別も拒否する見込み」は**まだ実測できていない**。
拒否理由が刻みの話で止まっていたためである。丸めを入れた後に改めて実測し、受理されたら上の残余リスク
（ガードの再発注が S0 のまま・指値幅・TrailingStop の意味差・planning への環流）を順に片付ける。

桁は**銘柄の基準価格（発火価格）で一度だけ**決め、指値にも同じ桁を使う（値ごとに判定すると 1 ドル近傍で
桁が混ざり、ブローカーの判定が銘柄価格で決まるなら同じ拒否が再発する）。

残る制約: 東証の呼値は価格帯で刻みが変わる（1 円・5 円・10 円…）。本追記は**小数桁だけ**を揃えるため、
高価格帯の日本株では刻みの倍数にならないことがある。米国株の「1 ドル未満は 4 桁」も慣行に基づく前提で、
ブローカーの仕様書と突き合わせた実測ではない。

## ［2026-09-25 追記 / #842］S3 は実測しない裁定の下で、監査の残論点 3・5 を決着させる

#809 にオーナーの裁定（2026-09-24）が出た: **S3 は測らず、S1（ソフトウェア逆指値）で進める。S3 の実測は将来の課題として
残す**（再開するときは #809 を参照して新しく起票する）。#842 の論点 3・5 は「S3 の受理を実測してから扱いを決める」と
保留されていたため、その前提ごと次のとおり決着させる（作業仕様書 `20260925_842_s3-audit-followups-residual`）。

1. **論点 3（受容した制約）: 発火したが約定しない StopLimit を `ProtectiveStopGuard` は検知できない。**
   ガードは Pending を残量ありなら「保護あり」とみなす（滞留時間の判定は無い）。StopLimit は発火後に指値幅を
   飛び越えた板では**未約定の Pending のまま残り、保護あり（`StillActive`）と読まれ続ける**。上の残余リスク
   （指値幅 1% は実測値ではない）とは別の欠落であり、ここへ明記する。
   - **検知は作らない。** 発火の判定・滞留の閾値・検知後の処置（取消して成行手仕舞いか、通知だけか）は S3 が受理される
     前提でしか意味を持たず、裁定の下では使われない分岐になる（上の「ガードの再発注が S0」と同じ理由）。
   - 🔴 **S3 を再開する前提条件**: 受理を実測した時点で本論点は**現実の欠陥に昇格する**。S3 を SIMULATE の常用手法に
     する前に、(a) 本論点の検知、(b) ガードの再発注を手法どおりにする（上の残余リスク）、(c) 指値幅の実測、の 3 つを扱う。
2. **論点 5（是正）: helm の調整値で数値の 0 を空と取り違えない。** `moomoo.stopLimitOffsetRatio`・
   `moomoo.opend.replyTimeoutSeconds`・`moomoo.alternativeStopOrderType` は `{{- with }}` で描画していたため、
   数値の 0（や `false`）が空と同じに扱われ、**アダプタ既定へ黙って戻っていた**（`--set …=0` で env 0 件を実測）。
   「空（`""`）・未設定（`null`）＝既定」「それ以外は値として注入し、検証はアプリの起動時に任せる」へ改めた。
   これは S3 の受理可否と無関係な chart の欠陥であり、裁定を待たずに直せる。
   - 帰結: `replyTimeoutSeconds=0`・`alternativeStopOrderType=0` は**起動時に停止する**（範囲外・未知の値。記述どおりになった）。
     `stopLimitOffsetRatio=0` は決定 3 の範囲（0〜10%）内として**受理される**——指値は #844 の丸めで発火価格から
     最低 1 刻み離れるため、発火価格と同値にはならない。
   - 0 を起動時に拒むか（下限を開区間にするか）は**変えない**。S3 の保護の形の設計判断であり、S3 を採らない裁定の下で
     動かす理由が無い。再開時に (c) 指値幅の実測と併せて扱う。
