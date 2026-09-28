---
title: SIMULATE 口座で注文費用照会（Trd_GetOrderFee）が値を返すかを確かめる、読み取り専用の 1 回実行の検証口（#1086 段 1）
type: spec
status: accepted
related_ids: [FR-11, FR-16, UC-07, ADR-0016, ADR-0027, IADR-0300, IADR-0226, IADR-0327, IADR-0016]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
---

# 仕様書: 注文費用照会の検証口（#1086 段 1）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-11（取引記録は経費区分を持ち建玉単位で紐づく）／FR-16（損益・費用はコードで集計する）
- ユースケース（UC）: UC-07（監査ログの参照）
- 画面（SC）: なし
- 関連 ADR: ADR-0016 決定15（経費区分 7 種・「集計は後から作れても記録は遡って復元できない」）／ADR-0027（取れなかったぶんを 0 として積まない）
- 関連する実装ADR: IADR-0300（経費明細の取得ポート・段 2 は実費の供給）／IADR-0226（区分・保存先）／IADR-0327（接続オブジェクトのシーム）／IADR-0016（SIMULATE 固定）

## 目的・背景

稼働 PoC（2026-09-29 01:59 JST、develop `73a79e33`）で初めての約定（NVDA 買い 1049 株・moomoo SIMULATE）があり、
経費の 7 区分がすべて未計上になった（IADR-0300 の段 1 どおり）。段 2（実費の供給）へ進む前に
issue #1086 が挙げた確認点のうち **「SIMULATE 口座で費用照会が値を返すか」** を、実装を積まずに**実測で**確かめる。

段 2 の設計（区分の写像・通貨・重複排除）は応答の形を見るまで決められない（IADR-0300 決定9）。
よって本 PR は**照会を 1 回撃って応答をそのまま見せるだけ**の検証口を置く。`TradeExpenseRecorded` の発行・
`IOrderExpenseSource` の実装差し替えは**行わない**（別 PR）。

## 調査結果（着手前の実測）

| 問い | 結果 |
| --- | --- |
| SDK（`moomoo-api` 10.8.6808）に Trd_GetOrderFee の protobuf 定義は同梱されているか | **在る**。`Moomoo.OpenApi.Pb.TrdGetOrderFee`（`C2S` = `Header` ＋ `OrderIdExList`（string の反復）、`S2C` = `OrderFeeList`）、`TrdCommon.OrderFee`（`OrderIDEx` / `FeeAmount` / `FeeList`）、`TrdCommon.OrderFeeItem`（`Title` / `Value`）。`ProtoID.TrdGetOrderFee` = 2225。`MMAPI_Trd.GetOrderFee(Request)` が在る（リフレクションで確認） |
| 既存アダプタの定義 | `MMApiMoomooTradeClient.OnReply_GetOrderFee` は**空実装**（応答を捨てる）。`IMoomooTradeConnection` に `GetOrderFee` は**無い** |
| 照会の鍵 | 🔴 **`OrderIDEx`（文字列）であって `OrderID`（uint64）ではない。** 本システムが持つブローカー注文 ID は `OrderID` の 10 進表記なので、照会の前に注文一覧から `OrderIDEx` を引く必要がある（`TrdCommon.Order.OrderIDEx`） |
| 既存の管理用 CLI・ワンショット実行 | **無い**（各サービスの `Program.cs` は Host を立てるだけ。Wolverine の `JasperFxCommandLine` は codegen 用） |
| 稼働クラスタ向けの明示オプトイン規約（`--live` / `LIVE=1`） | **無い**（`--live` は `scripts/helm-release-drift.js` の「比べるファイル」の意味で、オプトインではない）。本検証口は**読み取り専用**であり、`kubectl exec` を打つこと自体が明示操作になるため、新たなオプトインは設けない |

## 対象範囲

- 対象（すべて `backend/Services/OrderExecutionService/`）:
  - `Features/OrderExecution/ProbeOrderFee/IOrderFeeQuery.cs`（新規）— 読み取り専用ポート（メソッド 1 つ）と SDK 非依存の結果型
  - `Features/OrderExecution/ProbeOrderFee/OrderFeeProbeCommand.cs`（新規）— 引数の解釈・1 回実行・出力・終了コード
  - `Infrastructure/ExternalServices/OrderFeeProbeComposition.cs`（新規）— 構成から照会口を組む（moomoo・SIMULATE 以外は拒否）
  - `Infrastructure/ExternalServices/IMoomooTradeConnection.cs` — `GetOrderFee` を 1 本足す
  - `Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs` — `IOrderFeeQuery` の実装・`OnReply_GetOrderFee` を `Complete` へ結ぶ
  - `Program.cs` — `--probe-order-fee` のときだけ Host を立てずに実行して終了する分岐
  - テスト: `Tests/Features/OrderExecution/ProbeOrderFee/`（新規）と、`IMoomooTradeConnection` の偽物 4 箇所へ `GetOrderFee` を 1 行ずつ
- 手順書: `docs/operations/order-fee-probe-runbook.md`（新規。trace ブロック）
- 対象外: `IOrderExpenseSource` の実装差し替え・`TradeExpenseRecorded` の発行・報告書の費用表示・helm / values（並行 #1085）・InformationCollected / TradeDecision（並行 #1081）・KB 検索（並行 #1083）

## 設計

1. **形**: order-execution のイメージに同梱する起動引数 `--probe-order-fee <注文ID>`。`Program.cs` の先頭で判定し、
   **Host を組まない**（Wolverine・DB・常駐ジョブを起動しない）。構成は `WebApplication.CreateBuilder()` の
   `Configuration`（サービス本体と同じ appsettings＋環境変数。**引数は構成へ渡さない**）から読む。
2. **接続は既存を再利用する。** `MMApiMoomooTradeClient`（OpenD 接続・RSA 暗号・SIMULATE 口座の選択・
   応答相関・返信待ちタイムアウト）をそのまま使い、新しい接続実装を作らない。ログは `NullLogger`
   （既存ログは口座 ID を平文で出すため、検証口の出力へ混ぜない）。
3. 🔴 **書き込み系を呼べない構造**: 検証口（`OrderFeeProbeCommand`）が受け取るのは
   **メソッドが 1 つしか無いポート `IOrderFeeQuery`** だけであり、`IMoomooTradeClient`（発注・取消を持つ）も
   `IBrokerAdapter` も受け取らない。試験で「ポートのメソッドが 1 つ」「検証口の引数型に発注系の型が無い」
   「偽の OpenD で PlaceOrder / ModifyOrder が 0 回」を固定する。
4. 🔴 **Trd_GetOrderFee は 1 回だけ**: 再試行・ループを持たない。失敗（retType ≠ 0・タイムアウト・切断）は
   そのまま出力して終わる。**失敗を例外にせず retType / retMsg をデータとして返す**（検証の目的は応答を見ること）。
5. **注文 ID の解決**: 引数が 10 進数なら `OrderID` とみなし、対応市場（US → JP）ごとに当日の注文一覧 →
   履歴（過去 30 日）の順で探して `OrderIDEx` を得る（読み取りのみ・最大 4 回）。数字以外を含むなら
   `OrderIDEx` とみなしてそのまま使う（ヘッダ市場は US）。見つからない・`OrderIDEx` が空なら**照会を撃たずに**
   その旨を出して失敗で終わる（`OrderIDEx` が空であること自体が「SIMULATE では照会できない」証拠になる）。
6. **出力**（標準出力・1 行 1 事実・`key=value`）: 成否（`result=`）・`retType` / `retMsg`・返ってきた費用
   （注文ごとの `feeAmount`、項目ごとの `title` と `value`）・照会を送ったか（`getOrderFee.sent=`）。
   🔴 **秘密を出さない**: 口座 ID は末尾 2 桁以外を伏せ（`****08`）、retMsg・例外文に口座 ID が現れたら同じく伏せる。
   鍵・トークン・接続文字列は出力の経路に載らない（構成の値を 1 つも表示しない）。
   ［2026-09-29 追記 / AI レビュー指摘］伏せは**出力の最終段**で行う。注文一覧の照会の失敗は `EnsureSucceeded` が生の retMsg を
   例外文へ載せるため、応答の retMsg だけでは例外経路で全桁が漏れる。照会口が任意で実装する `IProbeOutputRedactor` を通し、
   例外文を含むすべての行を書く前に伏せる（`IOrderFeeQuery` は 1 メソッドのまま）。
   ［2026-09-29 追記 / 別文脈監査］口座が確定する前（口座一覧の照会の失敗）は伏せる値が分からないため、例外文の 6 桁以上の
   数字の並びを末尾 2 桁以外伏せる。接続先（host:port・host）と RSA 鍵のパスも構成から伏せる値として検証口へ渡し、
   構成不正（照会口の生成前）の文も含めて `<伏せ>` に置き換える。往復は最大 7 回（接続・口座一覧・注文一覧 4・費用照会）で、
   打ち切り 120 秒は 15 秒 × 7 = 105 秒を覆う。
7. **終了コード**: 0 = 照会成功で費用項目 1 件以上 ／ 3 = 照会成功だが費用が空 ／ 1 = 照会失敗（retType ≠ 0・
   注文が見つからない・`OrderIDEx` が空・接続失敗・タイムアウト）／ 2 = 引数不正・構成不正（moomoo 以外・実弾階層）。
8. **構成の拒否**: `Broker:Provider` が moomoo でない、または実弾階層なら照会口を組まずに終了コード 2
   （`LiveTradingGate` と SIMULATE 固定のヘッダはそのまま効く）。
9. **引数の検証**: `--probe-order-fee` の直後にちょうど 1 つの値（［2026-09-29 追記 / AI レビュー 🟢］`--probe-order-fee=<値>` の 1 引数形も同じく受け付ける。起動判定が `=` 形を拾うため解釈も揃える）。値は英数字・`_`・`-` の 1〜64 文字で `-` から
   始まらない。余分な引数・重複は拒否。拒否時は照会口を**組まない**（接続しない）。

## 母集合（規則 9・10: 誤りの側で走査する）

- `IMoomooTradeConnection` の実装（`GetOrderFee` を足すと壊れる側）: `grep -rn ": IMoomooTradeConnection\|IMoomooTradeConnection$"` → 本番 1（`MMApiTradeConnection`）＋ 偽物 4（`MoomooAdapterFakeOpenDIntegrationTests.FakeConnection` / `SimulateLikeConnection` / `FakeTradeConnection` / `MarginRatioConnection`）。すべて追随する。
- `OnReply_GetOrderFee` の参照: 本体 1・`UnsuppliedOrderExpenseSource` / `IOrderExpenseSource` のコメント 2。コメントは「空実装」と書くが、本 PR でも**経費の供給は空のまま**（応答を相関へ結ぶだけで、供給ポートは差し替えない）なので記述は偽にならない。**除外**（凍結記録ではないが、書き換える必要が無い）。
- `Program.cs` の早期 return 分岐: 既存に無い（新設）。

## 受け入れ基準

- [ ] Trd_GetOrderFee を **1 回だけ**呼ぶ（成功・失敗・例外のいずれでも 1 回。再試行しない）
- [ ] **書き込み系 API（PlaceOrder / ModifyOrder）を呼ばない**（構造と偽 OpenD の両方で固定）
- [ ] **秘密を出さない**（口座 ID の全桁・RSA 鍵の内容が出力に現れない）
- [ ] 失敗時は retType / retMsg を出し、終了コード 1 で終わる
- [ ] 引数不正は終了コード 2 で、照会口を組まない（接続しない）
- [ ] 注文が見つからない・`OrderIDEx` が空なら照会を撃たずに終了コード 1
- [ ] 起点 ID コメント（FR-11 / FR-16 / ADR-0016）
- [ ] 手順書（kubectl exec の 1 回実行・OpenD Pod に触れない・頻度制限）

## テスト方針

- `OrderFeeProbeCommandTests`: 偽の `IOrderFeeQuery` で 1 回・終了コード・引数不正・構成不正・出力。
- `OrderFeeProbeEndToEndTests`: `OrderFeeProbeCommand` ＋ 実物の `MMApiMoomooTradeClient` ＋ 偽 OpenD
  （`IMoomooTradeConnection`）で、送信回数・書き込み系 0 回・ヘッダ（SIMULATE・`OrderIDEx`）・口座 ID の伏せを固定する。
- `OrderFeeProbeCompositionTests`: paper・実弾階層で組まないこと。
- 変異 4 件以上で赤を確認（`scratchpad/impl-1086-*.sh`）。

## 計画書との差異

無し（計画書に費用照会の手段の定めは無い。本 PR は段 2 の前提を実測で確かめる道具である）。

## 残余

- OpenD の Trd_GetOrderFee の頻度制限の実値はリポ内に一次情報が無い。手順書では「連続して打たない（30 秒以上あける）」に留める。
- SDK のスレッドが前景で残ると `dotnet` が戻らない可能性があるため、検証口は `Environment.Exit` で終える。

## 採番

新しい IADR は起こさず、**IADR-0300 へ日付つき追記**する（段 2 の前提確認の道具であり、IADR-0300 の決定を変えない。並行 PR の IADR-0453 との衝突も避けられる）。

## 未決事項

- 段 2 の設計（区分への写像・通貨・重複排除）は本検証口の実測結果を見て別 PR で決める。
