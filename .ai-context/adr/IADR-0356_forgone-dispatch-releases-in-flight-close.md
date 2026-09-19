---
title: IADR-0356 見送り（発注していない）は取引台帳でも終端として記録し「処理中の決済」から外す — ただし解放の引き金は「確実に未発注」と実測できた理由だけの allowlist で、注文状態は捏造しない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-10, FR-11, UC-06, ADR-0002, ADR-0003, ADR-0013, ADR-0024, IADR-0018, IADR-0057, IADR-0067, IADR-0113, IADR-0117, IADR-0129, IADR-0210, IADR-0211, IADR-0342, IADR-0346, IADR-0347]
author: claude (Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注執行「拒否」の定義 / FR-10 リスク統制)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-06 利用者による建玉の手仕舞い)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md (OpenD 常駐・SPOF・INDEX 決定 33「再起動中は発注不可」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md
---

# IADR-0356: 見送りは取引台帳でも終端として記録し、在庫解放の引き金は「確実に未発注」の allowlist に限る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-19
- 決定者: claude（起票 #852。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: **FR-05**（発注執行。「拒否」＝証券会社が受理しなかった状態。planning#60 裁定）、
  **FR-10**（リスク統制。手仕舞いと損切りは止めない）、FR-11（監査）、UC-06、
  ADR-0002 / ADR-0024（OpenD 常駐・SPOF。再起動中は発注不可）
- 対象 Issue: #852（#848 の 2 巡目監査で非ブロッキングとして指摘され、別 issue に切られたもの）
- 関連する実装仕様書:
  [20260919_852_forgone-close-approvals-release-inventory](../specs/20260919_852_forgone-close-approvals-release-inventory.md)
- 関連 IADR: [IADR-0117](IADR-0117_owner-position-close-path.md)（決定 3 と 2026-09-19 追記＝在庫解放と
  fail-safe の向き。**本 IADR はその延長であり、向きを変えない**）、
  [IADR-0211](IADR-0211_opend-unavailable-forgo-without-queueing.md)（見送りの定義・
  「発注されていないものは注文状態を持たない」）、
  [IADR-0018](IADR-0018_portfolio-ledger-projection.md)（取引台帳）、
  [IADR-0067](IADR-0067_order-lifecycle-telemetry.md)（注文アクティビティ射影。**別の表・別の規約**）、
  [IADR-0129](IADR-0129_wolverine-messaging-topology.md)（決定 10。同一イベント型は 1 本のチェーン）、
  [IADR-0346](IADR-0346_count-working-entry-orders-in-risk-limits.md)（決定 5。見送りを射影の終端にした先例）

## コンテキストと課題

`IPortfolioLedgerStore.GetInFlightCloseQuantity`（IADR-0117 決定 3）は、承認から既定 30 分の窓のあいだ
「承認数量 − 約定累計」を**処理中の決済**として建玉から差し引く。#848 はここから
「**終端になったと確認できた**承認」（取消・失効・拒否）を除く改定を入れた。

**見送り（`OrderDispatchForgone`）は終端ではないので、その改定の対象外だった。** 見送りは
「ブローカーへ発注しなかった」という事実であり、注文が存在しない以上、注文状態（`OrderStatus`）を持たない
（IADR-0211）。その結果:

- 発注執行が見送った決済承認は、取引台帳では**処理中のまま**残る。
- 手仕舞いの再要求は窓（30 分）が満了するまで在庫超過（`ExceedsAvailable` → 422）で拒否され続ける。
- 見送りが出るのは **OpenD の再起動中**（ADR-0002 の SPOF・ADR-0024）である。つまり
  **手仕舞いが必要なときにまとまって発生し得る**。#848 の実害（下落局面で手仕舞えない）と同じ形である。

実測した母集合（`grep -rln "OrderDispatchForgone" backend --include=*.cs | grep -v /Tests/`）:
リスク管理側の購読は `OrderDispatchForgoneActivityHandler`（注文アクティビティ射影。IADR-0346 決定 5）の
**1 本だけ**で、`IPortfolioLedgerStore` を触るハンドラが無い。`order_activity` は終端になるが、
**`approved_orders` は一切動かない**（IADR-0117 改定 5 のとおり「台帳が数える承認の終端は台帳が持つ」）。

## 検討した選択肢

1. **理由を見ずに、見送りをすべて処理中から外す。** 実装は最小だが、`OrderDispatchForgoneReason` に
   将来「送ったかもしれない」理由が足されたとき、**足した人が気付かないうちに在庫が解放される**。
   これは #848 の 2 巡目監査 B3（「安全側」と書かれた既定が、下流の意味変更で fail-open へ反転した）と
   同型の穴である。**却下**。
2. **`MarkTerminal(decisionId, OrderStatus.Cancelled, …)` を流用する。** 列も口も足さずに済むが、
   **見送りに注文状態を与えることになる**。IADR-0211 は「発注されていないものは注文状態を持たない」を
   決定の中核に置き、`OrderStatus` へ `Unplaced` を足す案を明示的に却下している。`Cancelled` / `Rejected` を
   捏造すると FR-05 の「拒否」の別集計が接続障害で汚染される —— IADR-0211 が正面から塞いだ穴そのもの。**却下**。
3. **`OrderStatus` へ「見送り」を足す。** IADR-0211 決定の検討 1 で既に却下済み（FR-05 の状態集合は
   fixed であり、enum 序数は HTTP 経路の互換制約も持つ。IADR-0134）。**却下**。
4. **台帳に専用の口 `MarkForgone` を足し、呼ぶかどうかを「確実に未発注」の allowlist で門にする**（採用）。

## 決定

**選択肢 4 を採る。** 具体的には次の 3 点を決める。

### 決定 1: 在庫解放の引き金は「確実に未発注」と**実測できた**理由だけ（allowlist・既定は解放しない）

リスク管理の `Domain.OrderDispatchForgoneLifecycle.ConfirmsNoOrderPlaced(reason)` を新設する。

```csharp
reason switch
{
    BrokerUnavailable          => true,
    StopLossPriceMissing       => true,
    StopOrderUnsupported       => true,
    StopLossMethodNotPermitted => true,
    _                          => false,   // 🔴 既定は「解放しない」
}
```

🔴 **既定（`_`）が `false` であることが本決定の中核である。** 列挙に値が足されても、その値は
**何もしなければ在庫を解放しない**。理由を見ずに一律で外す実装（選択肢 1）は、列挙が増えた瞬間に
「送ったかもしれない見送り」で押さえが解け、証券会社側で生きているかもしれない手仕舞いと合わせて
同じ株数に 2 本の決済が並ぶ＝**二重決済でショート化**する。

**列挙の根拠は実測である。** `OrderExecutionAppService.ExecuteAsync` を読み、4 値すべてが
`reservations.TryReserve`（発注着手の権威。IADR-0057 の相 2）より**前**・ブローカーへの送信より**前**に
`return Forgone(...)` することを確かめた:

| 理由 | 実測した位置 | 判定 |
| --- | --- | --- |
| `BrokerUnavailable` | `catch (BrokerUnavailableException)`。接続確立の失敗（IADR-0211 決定 1 が「注文がブローカーへ届き得ない段階」に限定）。発注執行自身も**予約を解放**している＝二重発注の窓が無いという判断 | 確実に未発注 |
| `StopLossPriceMissing` | Open の発注前判定（`intent.StopLossPrice` が無い。IADR-0210 決定 1 の fail-closed） | 確実に未発注 |
| `StopOrderUnsupported` | Open の発注前判定（`broker is not IProtectiveOrderBroker` ／ S3 の能力なし。IADR-0347） | 確実に未発注 |
| `StopLossMethodNotPermitted` | Open の発注前判定（`disposition == Refused`。IADR-0342 決定 4。予約の取得より前） | 確実に未発注 |
| `BrokerPositionAbsent` | 決済の発注前判定（#873 / IADR-0355 決定 2。`ExecuteAsync` **L123** で `return`） | 確実に未発注 |
| `BrokerPositionsIndeterminate` | 決済の発注前判定（#873 / IADR-0355 決定 3。`ExecuteAsync` **L112** で `return`） | 確実に未発注 |

［2026-09-19 追記 / [#873](https://github.com/endazon/ai-stock-trading/issues/873) が develop へ入った］
**下 2 行は実際にマージして追加した**ものである（本 PR が「後からマージする側」になったので、
下の注記の手順をそのまま実行した。実測は PR 本文）。**根拠は `origin/develop` の実コードで確かめた** ——
建玉の突き合わせは**読み取りの** `brokerPositions.GetPositionsAsync` だけで、2 分岐は `ExecuteAsync` の
**L112 / L123** で `return` する。`reservations.TryReserve` は **L178**＝**後**である
（#873 側のコメントも「予約はまだ取っていない」と書いている）。
🔴 **建玉の照会は注文の送信ではない。** どちらも「確実に未発注」である。

🔴 **issue #852 の本文は 3 つを例示しているが、4 つ目（`StopLossMethodNotPermitted`。#819 で後から足された）も
同じく発注前確定であることを確かめて列挙に入れた。** 「入れない側」も主張であり、実測せずに落とせば
根拠の無い列挙になる（#848 の B5 の教訓——**「変えない側」も主張である。書く前に値域を実測する**）。
なお 4 つ目は `PositionEffect.Open` でしか出ないため、Close だけを数える `GetInFlightCloseQuantity` の
挙動は**入れても 1 バイトも変わらない**。入れるのは台帳の記録を事実に合わせるためである。

🔴 **「送信は済んだが結果が確認できない」は見送りではない。** それは
`BrokerDispatchIndeterminateException` として伝播し、見送りイベントを一切作らない
（IADR-0117 改定 6 / IADR-0211 の 2026-09-19 追記）。したがって本 allowlist に「不明」は入り得ない ——
**見送りの契約そのものが「確実に未発注」を要求している**ことが、この列挙の安全性の土台である。

> 🔴 **［注記 / #873］上の「不明」は *発注（dispatch）* の不明である。「不明」という語が付く見送り理由を
> 一律に既定 `false` へ落とさないこと。**
> [#873](https://github.com/endazon/ai-stock-trading/issues/873) は `OrderDispatchForgoneReason` へ
> `BrokerPositionAbsent` / `BrokerPositionsIndeterminate` を足す（4 → 6）。後者の「不明」は
> ***建玉照会* の不明**——「ブローカーに建玉がいくつあるか確認できなかった」であって、
> 「注文を送ったかどうか分からない」ではない。**発注には一切着手していない**（#873 側の監査と本 PR 側の監査が
> いずれも「送信前に `return` する＝確実に未発注」と確認している）ので、**2 値とも allowlist へ `true` で足す**。
> 字面の「不明」に引かれて既定 `false` に落とすと、**建玉が確認できない局面で見送られた手仕舞いが
> 30 分ロックされる＝#852 の実害がそのまま再発する。**
> **判定の基準は「理由の名前」ではなく「ブローカーへ送信したか」**である。
>
> 🔴 **［2026-09-19 追記 / #820］「不明」の 3 つ目の文脈——`UnattributedPosition`。**
> [#820](https://github.com/endazon/ai-stock-trading/issues/820) は S1（ソフトウェア逆指値）の武装の前提条件として
> `UnattributedPosition` を足す（6 → 7。**#873 が序数 4・5 を先に取ったため #820 側は自分の値を 4 → 6 へ繰り下げた**）。
> この理由は「同一銘柄・同方向に**帰属不明の建玉**がある」ときだけでなく、
> **建玉照会の能力が無い／照会が `null`** のときにも出る（fail-closed で「ある」側へ倒す）。
> つまり「不明」の語が付く 3 つ目の文脈だが、これも***建玉照会*の不明**であって***発注*の不明**ではない。
> **`true`（確実に未発注）で allowlist へ入れる**——実測: `OrderExecutionAppService.ExecuteAsync` で
> この分岐は **L194** で `return` し、`reservations.TryReserve` は **L223**、ブローカーへの送信は **L233/234** である。
> 判定の中で叩く `GetPositionsAsync` は**読み取りの建玉照会だけ**で、注文は 1 バイトも送らない。
> - **ただし本理由は `PositionEffect.Open` の分岐の内側でしか起き得ない**ため、
>   **決済の在庫解放が実際に動くことは今のところ無い**（在庫解放は Close の押さえを外す経路）。
>   それでも `true` へ分類するのは、**既定 `false` が「送ったかもしれない」という*誤った事実*を述べる**からであり、
>   本列挙が「確実に未発注か」の**単一情報源**である以上、事実の側に合わせる。
> - **この 3 例目が示すこと**: 「不明」を含む理由は**繰り返し現れる**。
>   毎回「名前で判断しない・送信したかで判断する・行番号を実測する」をなぞること。
>
> 🔴 **［2026-09-19 追記 / #873 は develop へ入り、本 PR がこの手順を実行済み］**
> 下の手順は**実際になぞって緑を確認した**（実測は PR 本文）。#873 のマージで赤くなったのは
> **要素数テスト 1 本だけ**であり、番兵は列挙由来なので自動で追随した（literal のままなら
> 「`BrokerPositionAbsent` を解放するな」という事実と逆の赤が 2 件出ていた）。以下は次に理由を足す人向けに残す。
>
> **後からマージする側の手順**——【必須】① 要素数テスト（`見送り理由の要素数を固定する`）を **6** にする
> ② 2 値とも allowlist へ `true` で足す。【赤にはならないが揃える】③ 肯定形の Theory 2 本へ 2 値を足す（被覆）。
> 【触らない】否定形の番兵は `UndefinedForgoneReasons` が**列挙から導く**ので自動で追随する。
>
> 🔴 **番兵を literal で書かないこと。** 初版は `[InlineData(4)]` と書いており、#873 後の序数 4 は
> `BrokerPositionAbsent` **そのもの**になっていた —— 否定形テストが
> 「`BrokerPositionAbsent` を解放するな」という**事実と逆のメッセージ**で赤くなり、手順どおり作業した人を
> 「② が誤りだった」＝**#852 の実害の再導入**へ誘導する。**本注記が無効化したと主張した罠を、
> 本注記自身が別の形で再設置していた**（#873 の段取りを実際になぞった監査が実測）。
> 番兵は「現に定義されている最大序数の 1 つ先」を実行時に計算する形へ改め、
> **要素数テストだけが唯一のトリップワイヤとして残る**ようにした。

述語を**リスク管理側に置く**のは `OrderStatusLifecycle` と同じ規律による ——
サービス間で共有する契約は列挙そのものであり、**その解釈は各サービスが自分で持つ**。
また、見送りは `OrderStatus` を持たないので `OrderStatusLifecycle` の述語では判定できない。
同ファイルが宣言する「**足すなら述語を足す**」をそのまま適用し、別の列挙には別の純関数を持たせる。

### 決定 2: 台帳の口は `MarkForgone` を新設し、`TerminalStatus` は `null` のままにする

`IPortfolioLedgerStore.MarkForgone(Guid decisionId, DateTimeOffset forgoneAt)` を足す。

- **`TerminalAt` に見送り時刻を立てる。** 判定に使うのは本列だけであり（IADR-0117 改定 1）、
  「未約定残が二度と約定しない」という本列の意味は見送りでも**そのまま成立する**
  ——注文が存在しないので、未約定残は永久に約定しない。
- 🔴 **`TerminalStatus` は `null` のままにする（本 issue が決めよと指示した点）。** 見送りは注文状態を
  持たない（IADR-0211）。`Cancelled` / `Rejected` を入れると FR-05 の「拒否」の別集計が接続障害で汚染される。
  診断用の列に**嘘を書かない**方が、あとで DB を読む人を誤らせない。
  結果として **`TerminalAt is not null && TerminalStatus is null` が「見送り」の表現**になる
  （`approved_orders` の 2 列だけで、終端か見送りかが読み分けられる）。
- **`OrderStatus` にも `TerminalStatus` にも新しい値を足さない。** IADR-0211 決定の検討 1 と
  IADR-0117 改定 6 の「共有契約 `OrderStatus` へ値は足さない」を踏襲する。必要なのは
  「見送りという状態を運ぶこと」ではなく「**注文状態を主張しないこと**」である。
- **Migration は不要。** `TerminalAt`（`timestamptz?`）と `TerminalStatus`（`int?`）は #848 の
  `20260918151239_AddApprovedOrderTerminalState` で既に追加済みで、どちらも nullable である
  （`dotnet ef migrations has-pending-model-changes` で「変更なし」を実走確認した）。
- **意味論は `MarkTerminal` と揃える**（いずれも fail-safe の向き）: 相関する承認が無ければ**何もしない**
  （後着の承認は終端未確認＝処理中として数える）／既に終端（見送りを含む）が記録されていれば**何もしない**
  （単調・冪等。再送・順序前後で時刻が動かない）。
- **実装は 2 つとも揃える** —— `EfPortfolioLedgerStore`（本番）と `InMemoryPortfolioLedgerStore`（テスト用）。
  同名のテストを両側に置き、乖離を検知する（#848 と同じ作法）。

### 決定 3: 届け方は既存のイベント・既存のキュー（新しいキューを 1 本も増やさない）

`OrderDispatchForgoneLedgerHandler`（新設）が `OrderDispatchForgone` を受け、
`ConfirmsNoOrderPlaced(reason)` が `true` のときだけ `MarkForgone` を呼ぶ。

- リスク管理は `OrderDispatchForgone` を**既に購読している**。Wolverine は同一サービス内の同一イベント型の
  ハンドラを 1 本のチェーンにまとめる（IADR-0129 決定 10）ので、**新しいキューは増えない**。
  再試行では射影のハンドラと両方が再実行されるが、双方の書き込みは冪等である
  （`MarkForgone` は単調、`RecordForgone` はストア側で冪等）。
- **新しいイベントも同期照会も作らない**（IADR-0117 改定 4 と同じ理由。同じ事実を 2 契約で運ぶと
  片方だけ届く事故の面が増える）。
- **分類されていない理由では沈黙しない。** 解放しなかったときは Warning でログする ——
  新しい理由が足されたのに分類されていないことは、外からはここでしか見えない。
- **`order_activity` 側（IADR-0346 決定 5）は一切変えない。** 母集合も規約も違う表である
  （IADR-0117 改定 1 の注記: 同じ `TerminalAt` という列名で、こちらは単調・あちらは無条件上書き）。

## 理由

- **fail-safe の向きは IADR-0117 が決めたまま変えない。** 本 IADR が足すのは「**確実に未発注**と
  分かっている分だけ在庫を返す」経路であり、不明・未分類は従来どおり処理中として押さえる。
  #848 が 4 巡の監査で繰り返し学んだのは「**意味を変えたら、その意味に依存している既定を全部引き直す**」で
  あり、本 IADR は**新しい引き金を足す側**にあたるので、引き金の門を allowlist（既定は閉）で持つ。
- **見送りに注文状態を与えない**のは、FR-05 の状態集合が「ブローカーに存在する注文」のライフサイクルだから
  である（IADR-0211）。存在しない注文に状態を与えると、注文数・拒否数の集計が実態とずれる。

## 結果・残余リスク

- 見送られた手仕舞いの数量が、30 分の窓を待たずに在庫へ戻る（#852 の受け入れ基準 1）。
- 将来「送ったかもしれない見送り」が列挙へ足されても、自動では在庫を解放しない（受け入れ基準 2）。
  否定形テスト（未定義の列挙値で既定の向きを固定）と、**列挙の要素数を固定するテスト**で守る
  ——値が増えたらテストが落ち、分類し直しを強制する。
- **残余リスク 1**: 見送りが承認より**先に**台帳へ届いた場合は記録できず、承認は窓が満了するまで処理中の
  ままになる（`MarkTerminal` と同じ既知の性質。安全側へ倒れる）。実運用では `OrderApproved` は
  リスク管理自身が発行し、見送りは発注執行がそれを消費した後に出るため、この順序は起きにくい。
- **残余リスク 2**: 窓（30 分）は残す。終端も見送りも**届かない**事象（イベント欠落）は依然あり得るため、
  恒久ロックを防ぐ最後の受け皿として要る（IADR-0117 の判断を変えない）。
- 🔴 **残余リスク 3（本 IADR が新たに作った露出。追随は
  [#876](https://github.com/endazon/ai-stock-trading/issues/876)）**:
  **見送りで在庫を解放した後に同じ `OrderApproved` が重複配送されると、そのとき実発注された決済が
  「処理中の決済」に数えられない。** 🔴 **本項はリポジトリ内のテストでは固定していない**
  （PR #872 のフェーズ末監査が監査側の使い捨てプローブで実測したものであり、
  **その名前を出典として引かない**——本リポジトリに存在せず後から検証できないからである）。
  下の再現順序は**本リポジトリのコードを読んで確かめた事実**だけで書いてある（各段に出典を併記した）:
  1. `OrderApproved`（Close 100）→ `BrokerUnavailableException` → `reservations.Release` が
     **予約行を削除**（`EfOrderReservationStore.Release` は `Remove(row)`）→ 見送りを発行。
  2. 本決定の `MarkForgone` で `TerminalAt` が立ち、`GetInFlightCloseQuantity` = 0（在庫が戻る）。
  3. **同じ `OrderApproved` が重複配送される**（at-least-once・ack 喪失）。`ExecuteAsync` 相 1 の
     `store.FindByDecisionId` は **null**（見送りは `ExecutionRecord` を残さない。IADR-0211 決定 3(b)）、
     `TryReserve` も**成功する**（予約行は削除済み）。OpenD が復帰していれば**本物の決済注文が出る**。
  4. 台帳の `TerminalAt` を戻す経路は無い —— `AppendApproval` は冪等、`MarkTerminal` / `MarkForgone` は
     どちらも `TerminalAt is not null` で早期 return（単調）、`OrderExecutedLedgerHandler` も戻さない。
     **生きている決済が押さえられず、2 本目の手仕舞いが通り得る**（#848 の 2 巡目監査 B3 と同じ帰結）。
  - 🔴 **本決定の前にはこの露出は無かった。** 予約の解放と再予約の成立は従来どおりだったが、
    **台帳が 30 分の窓のあいだ承認数量を押さえ続けていた**ため、重複配送で出た注文は押さえの中に収まっていた。
    本決定でその押さえを外したことで露出した ——「**意味を変えたら、その意味に依存している既定を全部引き直す**」
    （IADR-0117 の 2026-09-19 追記の教訓）が、今度は**本決定の側**に当たったものである。
  - **ブロッキングとしなかった理由**: 前提条件（同一 `OrderApproved` の重複配送）が要り、定常経路では起きない。
    実発注に至るにはさらに見送り直後の OpenD 復帰が要る。一方 #852 が直した実害
    （手仕舞いが必要なときに 30 分手仕舞えない）は発生頻度・深刻度とも上であり、是正を止める理由にならない。
  - **是正の方向は #876 で裁定する**（`MarkForgone` を戻す経路を持つ／再配送を見送り済みの `DecisionId` で弾く／
    予約を解放せず別状態で残す／露出を受容する、のいずれか）。**どれも台帳の単調性・予約の 3 相・
    見送りの定義のどれかを触る**ため、本 PR では決めない。
- 🔴 **残余リスク 5（TOCTOU。`MarkTerminal` と同型で、#881 の並行トークンが入れば解消する）**:
  `EfPortfolioLedgerStore.MarkForgone` は **「`Find` → `TerminalAt is not null` を検査 → 代入 → `SaveChanges`」**
  であり、`ApprovedOrderRow` に並行トークンが無い現状では**検査と書き込みのあいだが TOCTOU である**。
  見送り（`OrderDispatchForgone`）と終端（`OrderCancelled` / `OrderExecuted`）は Wolverine の**別キュー**
  ＝並行に走る（IADR-0129 決定 1）ため、同じ承認を同時に触り得る。
  - **実測（PR #872 のフェーズ末監査。`MarkForgone` と `MarkTerminal` を Barrier で同時起動・
    EF InMemory・別 `DbContext`・200 試行）**:
    `forgoneWon=91 / terminalWon=76 / TORN=33`（`fabricatedStatusOnForgone=33`・`erasedRealStatus=0`・`noWrite=0`）。
    🔴 **33/200 が直列実行では出得ない状態**——`TerminalAt` は見送りの時刻なのに `TerminalStatus = Cancelled`。
    **決定 2 が守ると宣言した不変条件（`TerminalAt is not null && TerminalStatus is null` が見送りの表現）
    そのものの破れ**である。
  - **在庫の押さえは壊れない**（どちらが勝っても `TerminalAt` は立ち、`GetInFlightCloseQuantity` は同じ値に
    落ち着く冪等な書き込みである）。壊れるのは**見送りと終端を読み分ける診断**だけであり、
    統制（二重決済の防止）には波及しない。これがブロッキングとされなかった理由である。
  - **解消**: [#881](https://github.com/endazon/ai-stock-trading/issues/881)（IADR-0357）が
    `TerminalAt` を `IsConcurrencyToken()` にすると、負けた側の UPDATE は 0 行になり
    `DbUpdateConcurrencyException` へ落ちる＝**引き裂けなくなる**。
    🔴 **本 PR は先回りして `MarkForgone` にもその catch を入れてある**（下の「マージ順」を参照）。
    **トークンが入るまでは引き裂き得る**ことを、ここに残す。
  - `InMemoryPortfolioLedgerStore.MarkForgone` は `ConcurrentDictionary.TryUpdate` の CAS ループなので
    この破れは持たない（EF 実装だけの性質である）。

- 🔴 **マージ順への非依存（#881 との結線）**: `MarkForgone` は `DbUpdateConcurrencyException` を
  **捕まえて黙って何もしない**（追跡状態を `Detached` にする。`MarkTerminal` と同一の作法）。
  負けた＝「先に誰かが終端または見送りを記録した」であり、**単調性（最初の終端が真）と同義**だからである。
  - **トークンがまだ無い現状では決して投げないので無害**であり、**先に入れておけばマージ順に依存しない**。
  - 🔴 **「片方だけ」では壊れる**: #881 が先に入ると `TerminalAt` は並行トークンになるが、#881 は本 PR より
    前に分岐しており `MarkForgone` を 1 件も含まない（`grep -c MarkForgone` = 0）。catch が無いまま競合すると
    `OrderDispatchForgoneLedgerHandler` を貫通して Wolverine の再試行 → error キューへ至り、
    **見送りが記録されない＝在庫が解放されない＝#852 の実害が間欠的に再発する**
    （監査の実測: #881 のトークンだけを本ブランチに再現して 200 試行 → `MarkForgoneTHREW=43`）。
  - 🔴 **rebase 時の義務**: #881 が `RiskManagementDbContext` に書いた
    「**本列を書く唯一の操作（`EfPortfolioLedgerStore.MarkTerminal`）**」というコメントは、
    本 PR がマージされた時点で**偽になる**（`MarkForgone` も本列を書く）。
    **`MarkTerminal` / `MarkForgone` の 2 つへ是正する。**
    これは「**意味を変えたら、その意味に依存している既定・記述を全部引き直す**」（IADR-0117 の 2026-09-19 追記）
    そのものであり、本 IADR 自身が残余リスク 3 で引用している作法である。

- **残余リスク 4（軽微・対応しない）**: `MarkForgone` のあとに本物の終端（`MarkTerminal(Cancelled)` 等）が
  後着しても、単調性のため **`TerminalStatus` は永久に `null` のまま**である。判定に使うのは `TerminalAt`
  だけなので統制上の害は無く、診断としても「見送った承認に後から取消が届いた」は追える情報が乏しい
  （そもそも注文が存在しない）。**単調性の方を優先する**（後着で状態だけ書けるようにすると、
  「最初の終端が真」という #848 以来の不変条件に例外を作ることになる）。
- 監査・通知の集計は変えていない。見送りは従来どおり `OrderRejected`（事前拒否）・
  `OrderExecuted(Status=Rejected)`（証券会社拒否）と**別集計**である（IADR-0211 決定 5）。

## フォローアップ

- 🔴 **[#876](https://github.com/endazon/ai-stock-trading/issues/876)**（残余リスク 3 の裁定。
  見送りで解放した予約が再配送で実発注されると、生きている決済が処理中に数えられない）。
- 見送りの多発（OpenD 長期停止）の集約通知は IADR-0211 の残余リスクのまま（本 IADR で変えない）。
- #847（成行での手仕舞い・取消の口）は射程外。
