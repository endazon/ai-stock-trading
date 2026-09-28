---
title: 運用 Runbook — 注文費用照会の検証口（SIMULATE 口座で費用照会が値を返すかを 1 回だけ確かめる）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
---
<!-- trace:
ids: [FR-11, FR-16, UC-07]
adrs: [ADR-0016, ADR-0027]
iadrs: [IADR-0300, IADR-0016]
specs: [20260929_1086_order-fee-probe]
issues: [#1086, #633]
-->
<!-- 起点 ID・関連 ADR/IADR・仕様書名・修飾付き issue 参照は本文へ書かず、上の trace ブロックへ入れる（scripts/check-trace-blocks.js が検査する） -->

# 運用 Runbook: 注文費用照会の検証口

> 運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。
> 発注経路（`moomoo-sim`）の見分け方は [発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md) を参照。

order-execution のイメージには、moomoo の**注文費用照会（Trd_GetOrderFee）を 1 回だけ撃って応答を見せる**
検証口が同梱されている。経費明細の実績記録へ進む前に、**SIMULATE 口座で費用照会が値を返すか**を実測するための道具であり、
**経費の記録（監査台帳への書き込み）は行わない**。

- **読み取り専用**である。検証口が使えるのは費用照会と注文一覧の照会だけで、発注・取消へ届く経路を持たない。
- **Trd_GetOrderFee は 1 回だけ**撃つ。失敗しても撃ち直さない。
- **取引環境は SIMULATE 固定**である（実弾のヘッダを組む経路が無い）。構成が moomoo の SIMULATE 階層でなければ接続せずに終わる。

## この手順を実行する条件（いつ走らせるか）

- moomoo SIMULATE で約定が起き、経費の 7 区分が「未計上」と記録されたとき（約定時の警告「経費明細を照会できません」）。
- 経費明細の実績記録（費用照会の応答を区分へ写す実装）の設計に入る前に、応答の形（項目名・値・通貨の有無）を確かめたいとき。

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | 名前空間 `ai-stock-trading` の Pod への `kubectl exec` |
| 必要なツール | `kubectl` |
| 対象 | **order-execution の Pod**（`deploy/order-execution-service`）。**OpenD の Pod には触れない**（exec・再起動・ログインのいずれもしない） |
| 構成 | 階層が `moomoo-sim` であること（[発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md) の introspection で確かめる） |
| 所要時間の目安 | 1 分 |

## 手順

1. **照会する注文 ID を決める。** ブローカー注文 ID（moomoo の OrderID。10 進の数字）を使う。
   order-execution のログの発注成功行から拾える:

   ```bash
   kubectl -n ai-stock-trading logs deploy/order-execution-service | grep "moomoo SIMULATE 発注成功"
   ```

   `orderId=` の値が注文 ID である。moomoo の OrderIDEx（英数字）が分かっていれば、それを直接渡してもよい。

2. **検証口を 1 回だけ実行する。** 稼働中の order-execution の Pod の中で、同じイメージの `dotnet` を別プロセスとして起動する
   （稼働中のサービスは止めない。構成・RSA 鍵は Pod の環境変数とマウントをそのまま使う）:

   ```bash
   kubectl -n ai-stock-trading exec deploy/order-execution-service -c order-execution-service -- \
     sh -c 'exec dotnet "$SERVICE_DLL" --probe-order-fee <注文ID>'
   echo "exit=$?"
   ```

   引数は `--probe-order-fee <注文ID>`（または `--probe-order-fee=<注文ID>`）だけにする（他の引数を足すと使い方の誤りとして接続せずに終わる）。

3. **出力を記録する**（「記録」の節）。**続けて打たない。** 撃ち直す必要があるときも 30 秒以上あける。

## 出力の読み方

1 行 1 事実の `key=value` で出る。**口座番号は、応答・エラー文を含むすべての行で末尾 2 桁以外を伏せて出す**（ただし口座が確定する前＝接続・口座一覧の照会で失敗したときは、伏せる値がまだ分からないため伏せられない）。主な行:

| 行 | 意味 |
| --- | --- |
| `account=SIMULATE(****NN)` | 照会に使った口座。**口座番号は末尾 2 桁以外を伏せて出す** |
| `order.orderIdEx=` / `order.market=` / `order.status=` | 注文一覧から引いた照会の鍵（OrderIDEx）・市場・注文状態の数値（11 が全約定） |
| `getOrderFee.sent=` | 費用照会を送ったか（`yes` / `no` / 例外で判別できないときは `unknown`） |
| `retType=` / `retMsg=` | OpenD の応答。`retType=0` が成功 |
| `fee[i].feeAmount=` / `fee[i].item[j].title=` / `fee[i].item[j].value=` | 返ってきた費用の合計と項目（項目名と値は応答のまま。区分へは写さない） |
| `result=` | 結末（下表） |

| `result=` | 終了コード | 読み方 |
| --- | --- | --- |
| `fees-returned` | 0 | **SIMULATE 口座で費用照会が値を返した** |
| `no-fees` | 3 | 照会は成功したが費用が空だった（SIMULATE では値を返さない可能性） |
| `failed` | 1 | OpenD が非成功を返した（retType / retMsg が理由）。retType が -100 / -200 / -400 / -500 は「返事を読めなかった」側 |
| `order-not-found` | 1 | 注文一覧（当日・過去 30 日の履歴・US / JP）に指定の注文が無い。**費用照会は送っていない** |
| `order-id-ex-missing` | 1 | 注文は在ったが OrderIDEx が空。照会の鍵が無いため**費用照会は送っていない** |
| `error` | 1 | 接続失敗・返信待ちのタイムアウト等（`error[n].type` / `error[n].message`） |
| `usage-error` / `config-error` | 2 | 引数の誤り／構成が moomoo の SIMULATE 階層でない等。**接続していない** |

## 確認（この手順が成功したと言える条件）

- `result=` の行と終了コードが得られ、`fees-returned` / `no-fees` / `failed` のいずれかで**費用照会の答えが出た**こと。
- 稼働中の order-execution のサービス（ヘルス・約定追跡）に影響が出ていないこと（Pod の再起動が起きていない）。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| `config-error`（moomoo の SIMULATE 階層でだけ動く） | 対象の Pod が paper 階層 | 階層を確かめる。paper では照会先が無い |
| `config-error`（RSA 秘密鍵のファイルが無い） | 鍵のマウント漏れ | サービス本体も同じ理由で起動できないはずである。配備を確かめる（本手順では直さない） |
| `error`（OpenD への接続を確立できない） | OpenD が停止中・再ログイン待ち | **OpenD の Pod には触れない。** サービス本体のログで OpenD の状態を確かめ、復旧後に 1 回だけ打ち直す |
| `error`（TimeoutException） | OpenD が応答しない・頻度制限に掛かった可能性 | 30 秒以上あけて 1 回だけ打ち直す。続けて失敗するなら打つのをやめて記録する |
| `order-not-found` | 注文 ID の取り違え・30 日より古い注文 | ログの `orderId=` を確かめる。古い注文なら OrderIDEx を直接渡す |
| `order-id-ex-missing` | SIMULATE の注文が OrderIDEx を持たない | **それ自体が実測結果である**（SIMULATE では費用照会の鍵が得られない）。記録する |
| コマンドが戻らない | SDK の接続が閉じない | 1〜2 分待っても戻らなければ Ctrl-C で抜ける（検証口は照会の上限 120 秒で打ち切る） |

## 頻度制限への注意

- OpenD の取引系 API には口座ごと・一定時間あたりの回数制限がある。稼働中のサービス（約定追跡・建玉照会）も
  同じ口座で照会しているため、**検証口を続けて打たない**（打ち直すときは 30 秒以上あける）。
- 検証口 1 回で送るのは、注文一覧の照会が最大 4 回（数字の注文 ID を OrderIDEx へ引くとき）と費用照会 1 回である。
  OrderIDEx を直接渡せば注文一覧の照会は 0 回になる。

## 記録

- 出力（標準出力の全行）と終了コードを、経費明細の実績記録の作業の issue へ貼る。
  出力は口座番号を伏せており、鍵・トークンを含まない（貼る前に念のため目視で確かめる）。

## 限界（この手順で担保できないこと）

- **実口座（TrdEnv_Real）での応答は確かめられない**（検証口は SIMULATE 固定）。SIMULATE で値が返らなくても、実口座で返らないとは言えない。
- 費用項目を経費区分へ写す規則はこの手順では決めない（応答の項目名を見てから別途決める）。
- OpenD の頻度制限の実値は確かめていない（上の「30 秒以上あける」は安全側の目安である）。
