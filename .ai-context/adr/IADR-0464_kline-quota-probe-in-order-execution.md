---
title: IADR-0464 日足 K 線の取得枠と復権の扱いを確かめる読み取り専用の検証口は、OpenD の接続先と鍵を持つ order-execution に、発注の接続を作らない相場だけのクライアントで置く
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-15, UC-01, ADR-0048, ADR-0023, ADR-0002, IADR-0157, IADR-0300, IADR-0327, IADR-0016, IADR-0060, IADR-0451]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
---

# IADR-0464: 日足 K 線の取得枠の検証口の置き場所と形（#1117）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-30
- 決定者: Claude Code（実装）。統制・発注・判断の挙動は変えない（読み取り専用の道具を足すだけ）ため、裁定は要らない

## 起点・関連

- 関連する計画書 ID: FR-02（判断へ渡す出来高）・FR-15（日足 OHLC の履歴源）・UC-01（手順 3）
- 計画 ADR: ADR-0048 決定 3（K 線を判断へ流すのは、取得枠の確認と分割の確認が済んでから）・フォローアップ 1・2／ADR-0023 決定 5（確認 1: 取得枠の単位と回復周期）
- 起票: [#1117](https://github.com/endazon/ai-stock-trading/issues/1117)（背景は planning#702）
- 関連する実装仕様書: [`.ai-context/specs/20260930_1117_kline-quota-probe.md`](../specs/20260930_1117_kline-quota-probe.md)
- 前提:
  - [IADR-0300](IADR-0300_trade-expense-source-port-and-negative-recording.md)（2026-09-29 追記）: 注文費用照会の検証口 `--probe-order-fee`。起動引数の分岐・Host を組まない・読み取り専用ポートの生成関数だけを受け取る・出力の最終段で伏せる、の作法を本決定がそのまま踏襲する
  - [IADR-0157](IADR-0157_moomoo-history-kline-adapter.md): 履歴 K 線のアダプタは BacktestService にあり、取得枠の照会は実装しない。本決定はそれを変えない
  - [IADR-0327](IADR-0327_opend-connection-recreate-after-failed-attempt.md): SDK の接続オブジェクトへの薄いシーム（偽の OpenD を差す口）
  - [IADR-0016](IADR-0016_safe-broker-execution.md): SIMULATE 固定。[IADR-0060](IADR-0060_opend-production-cutover-gates.md): 鍵のマウント漏れは接続の前に落とす
  - [IADR-0451](IADR-0451_scheduled-decision-intraday-price-context.md): 出来高は「未提供」と明示する（本決定は変えない）

## 背景

ADR-0048（planning#702 の裁定）は、判断へ渡す出来高の出所を日足 K 線とし、流す前に 2 点の確認を条件にした。
取得枠（`remainQuota`）の単位と回復周期、そして株式分割をまたいでも 20 日平均比が歪まないこと（前復権が出来高を調整するか）である。
どちらも実機の OpenD が要り、AI のセッションからは打てない。利用者が `kubectl exec` の 1 行で打てる道具が要る。

着手前の実測（作業仕様書 §調査結果）:

- moomoo の相場（Qot）の接続を持つのは BacktestService だけである。ただし BacktestService の Pod には OpenD の接続先・RSA 鍵の env もマウントも無い（履歴源の provider は空＝no-op）。
- OpenD の接続先と `moomoo-rsa` のマウントが揃っているのは、moomoo-sim 階層の order-execution だけである。
- 2026-09-02 の読み取り専用 probe は、同じ RSA 鍵の暗号化接続で `QotRequestHistoryKL` と `QotRequestHistoryKLQuota` の両方に成功している。
- SDK（`moomoo-api` 10.8.6808）は `MMAPI_Qot.RequestHistoryKLQuota` と `QotRequestHistoryKLQuota`（`BGetDetail` → `UsedQuota` / `RemainQuota` / `DetailList`）を持つ（リフレクションで確認）。

## 決定

### 決定 1: order-execution のイメージに置き、相場だけのクライアントを検証口専用に持つ

- 検証口は `--probe-kline-quota` として **OrderExecutionService** に置く（`Program.cs` の先頭で分岐し、Host を組まない）。理由は、OpenD の接続先と鍵がこの Pod にだけ揃っており、利用者が再配備なしに 1 行で打てるからである。お手本（`--probe-order-fee`）と同じ場所・同じ作法になる。
- **BacktestService の Qot クライアントは参照できない**（サービス間の直接参照は禁止）。よって検証口専用の最小のクライアント `MMApiMoomooKLineProbeClient` を置く。要求の組み立て（Security・`KLType_Day`・`RehabType`・期間の書式・`MaxAckKLNum`・`NeedKLFieldsFlag`）は BacktestService の `MMApiMoomooHistoryKLineClient` に合わせる。
- `MMAPI.Init()` はプロセスで 1 回だけ呼ぶ必要があるため、`MMApiMoomooTradeClient` が持っていた処理を `MoomooApi.EnsureInitialized()` へ寄せ、両方から呼ぶ（挙動は変えない）。
- 採らなかった案: BacktestService に置き helm で鍵をマウントする（配備の変更が要り、利用者がすぐ打てない）／共有ライブラリへ Qot クライアントを移す（moomoo SDK を Shared へ持ち込む大きな構造変更）。

### 決定 2: 書き込み系へ届かないことを型で閉じる

- 検証口が受け取るのは、読み取り専用ポート `IKLineQuotaQuery`（`QueryQuotaAsync` / `RequestDailyKLinesAsync` の 2 メソッド）の生成関数だけである。
- 実装 `MMApiMoomooKLineProbeClient` は `MMSPI_Qot` と `MMSPI_Conn` だけを実装し、`MMSPI_Trd` を実装しない。発注の接続（`IMoomooTradeConnection`）も発注のポートも参照しない。接続のシーム `IMoomooQotProbeConnection` の面は、相場の 2 要求と接続の管理だけである。
- **検証口のプロセスは発注の接続を 1 本も作らず、口座も選ばない。** 口座番号は出力の経路に無い。
- 試験で固定する: ポートのメソッド名・検証口の引数型・実装の実装インターフェースとフィールドの型・シームのメソッド名。

### 決定 3: 手順・自制レート・1 回だけ

- 手順は、枠の照会（詳細つき）→ 銘柄ごとに前復権の日足と直後の枠の照会 → 分割の銘柄を無復権・前復権・後復権で 1 回ずつ取り、各取得の直後に枠の照会 → 枠の照会（詳細つき）。既定は往復 12 回。
- **各要求は 1 回だけ**撃つ。非成功（`retType≠0`）は結果として出し、次の段へ進む。接続失敗・タイムアウトなどの例外では、以後を撃たずに終わる。ページングはしない（続きの鍵は `hasMore=` として出すだけ）。
- 要求の間隔を **2.5 秒以上**あける（24 回/分。自制レート 30 回/分の内側）。全体は 3 分で打ち切る（打ち切りの時間は注入でき、試験は短い値で「待ちの途中でも打ち切られ `result=error`・終了コード 1」を固定する）。
- **接続は作り直さない。** 切断を検知したら、応答待ちの要求は返信待ちの打ち切りを待たずに即座に失敗させ、以後の要求は撃たずに失敗させる（同じ接続オブジェクトへ `InitConnect` を再び呼ばない）。常駐の BacktestService は切断後に接続を作り直す（IADR-0327）が、一発撃ちの検証口で切断をまたいで撃ち続けると、取得枠の前後の差が何を数えたのか読めなくなるため採らない。
- 既定の銘柄は AAPL・MSFT、分割の比較は NVDA の 2024-05-28〜2024-06-21（2024-06-10 に 10:1）。取得枠を消費するため少数に抑え、引数で変えられる（銘柄 5 つ・本数 100・分割の期間 120 日まで）。
- **既定の銘柄を監視銘柄から採らず、固定の AAPL・MSFT とする**（#1117 は「監視銘柄から少数」と求めた）。理由: 検証口は稼働設定（監視銘柄の構成・ConfigurationService）に依存せず、誰がいつ打っても同じ要求になる（再現できる）べきである。監視銘柄を読むには構成の取得経路を検証口へ足す必要があり、読み取り専用の面が広がる。監視銘柄で確かめたいときは `--symbols` で差し替えられる（5 銘柄まで）。

### 決定 4: 取得枠と復権は「読み」までを出し、結論は利用者の実測で決める

- 取得ごとの `usedQuota` の差を、その銘柄が既知（取る前から詳細一覧に在る、またはこの実行で既に取った）か未知かで分け、単位の読みを `per-security` / `per-request` / `inconclusive` で出す。**未知の銘柄の取得は単位によらず +1 になるため、区別は既知の銘柄の取得の差で行う。** 分割の比較は同じ銘柄を 3 回取るので、1 回の実行で既知の取得が 2 回以上ある。
- 回復周期は 1 回の実行では分からない。詳細一覧（銘柄と要求時刻）を before / final の両方で出し、日を置いて打ち直したときの変化から読む（手順書）。
- 復権は、日付ごとに無復権・前復権・後復権の終値と出来高を並べ、無復権と異なる日数と比の範囲を出す。「出来高も調整されるか」の判定の規則は手順書に置き、検証口は数えるまでにする。

### 決定 5: 出力の伏せと終了コードは注文費用照会の検証口にそろえる

- 構成で与えた接続先（host:port・host）と RSA 鍵のパスを、語の境界つきの完全一致で `<伏せ>` に置き換える（長い値から。4 文字未満は対象外）。例外文だけ、その後に 6 桁以上の数字の並びを末尾 2 桁以外伏せる。伏せる値の導出は `OrderFeeProbeComposition.SensitiveValues` を共有する。
- 伏せの実装（出力の最終段）は検証口ごとに持つ（機能の切片の間で互いを参照しないため）。口座番号の伏せは要らない（口座を選ばない）。
- 終了コード: 0 = すべて成功／1 = 非成功・接続失敗・タイムアウト／2 = 引数不正・構成不正（接続しない）。

## 結果

- 利用者は `kubectl exec deploy/order-execution-service -- sh -c 'exec dotnet "$SERVICE_DLL" --probe-kline-quota'` の 1 行で、ADR-0048 決定 3 の 2 つの確認に要る観測を得られる。
- 判断へ出来高を流す経路（ADR-0048 フォローアップ 3）は作っていない。出来高は「未提供」のままである（IADR-0451）。
- BacktestService の履歴源と IADR-0157 の決定（取得枠の照会を実装しない）は変えていない。

## 残余リスク（実機で確認が要る点）

- 取得枠の単位と回復周期の答えそのもの（検証口は観測を出すまで）。
- `DetailItem.RequestTime` の時刻帯。
- 後復権の要求が米国株で成功するか（非成功でも他の段は続ける）。
- Qot の要求の頻度制限の実値（2.5 秒間隔は安全側の目安）。
- 相場の要求の組み立てが BacktestService と 2 箇所になった。片方だけ変えると食い違う（今回は試験で送信の中身を固定した）。

## ［2026-09-30 追記 / #1125］回復周期の追試のため、K 線を取らず取得枠だけを読むモード（`--quota-only`）を足し、`requestTime` の時刻帯（UTC+8）を明記する

- 作業仕様書: [`.ai-context/specs/20260930_1125_kline-quota-only-probe.md`](../specs/20260930_1125_kline-quota-only-probe.md)
- **実測（利用者が 9/30 に 2 回実行。23:25 JST ごろと 23:43 JST ごろ）**:
  1. 同じ銘柄を取り直すと `delta.used=0`（枠は増えない）だが、詳細一覧のその銘柄の `requestTime` は取り直した時刻へ更新される（AAPL: `22:25:53` → `22:42:36`）。
     回復が「その銘柄の最後の取得から」数える仕組みなら、追試で K 線を取り直すたびに時計が戻り、決定 4 の「日を置いて打ち直したときの変化から読む」は既定の手順では成立しない。
  2. `requestTime` は JST ではなく **UTC+8**（moomoo のサーバ時刻）で返る（23:25 JST の実行で `22:25:53`）。残余リスクの「`DetailItem.RequestTime` の時刻帯」はこれで解消した。
- **決定 6（追加）**: `--probe-kline-quota --quota-only` は K 線を 1 本も取らず、枠の照会（詳細つき）を **1 回だけ**撃つ（枠を消費せず、どの銘柄の `requestTime` も更新しない）。
  出力は used / remain / total（used + remain）と詳細一覧。他のオプション（`--symbols` / `--count` / `--split-*`）との併用・重複・`=` の形は使い方の誤り（終了コード 2・接続しない）——
  併用は「K 線も取るのか」が曖昧であり、取り直しで回復の時計が戻る事故を黙って起こさないため。回復周期の追試はこのモードで数日おきに打つ（手順書）。
- **決定 7（追加）**: 詳細一覧の各行（両モード）の末尾に `requestTime.tz=UTC+8`・`requestTime.jst=`・`requestTime.utc=` を足す（文字列を UTC+8 の壁時計として読み換算する。読めなければ `(換算不可)`）。
  `requestTimeStamp`（int64）は単位が未確認のため換算しない。既定モードの要求の順序・回数・既存の欄は変えない（欄を行末へ足すだけ）。
- **決定 2（型で閉じる）は変えない。** quota-only は同じ読み取り専用ポートの `QueryQuotaAsync` だけを使う。「K 線を取らない」をポートの分割で型に閉じることは採らない
  （ポートを 3 つ目のメソッドや別の型へ広げると「2 メソッドに保つ」固定を崩す）。K 線を撃たないことは、偽の照会口の呼び出し記録と偽の OpenD の送信記録で固定した。
- 新しい IADR を起こさない理由: 決定 1〜5 の形（置き場所・読み取り専用・1 回だけ・伏せ・終了コード）の中でモードと出力の欄を足すだけで、新しい決定の軸が無いため。
- 残余: 回復周期そのもの（利用者の追試の結果による）。稼働中のサービスが日次で取り直す銘柄は詳細一覧に常に残るため、回復の観測に使えない。
