---
title: IADR-0488 送信結果を確認できない発注を SIMULATE で意図的に作る故障注入は、アダプタへ渡す OpenD クライアントのデコレータで新規建てに 1 プロセス 1 回だけ当て、既定は無効・SIMULATE 以外の構成では起動を止める
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-05, NFR-09, ADR-0045, IADR-0057, IADR-0074, IADR-0092, IADR-0117, IADR-0362, IADR-0444, IADR-0111, IADR-0316, IADR-0482]
author: claude (Claude Code)
created: 2026-10-03
updated: 2026-10-03
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements (FR-10 リスク統制・FR-05 発注執行)
  - planning:projects/ai-stock-trading/07_adr (ADR-0045 決定1・決定2)
---

# IADR-0488: 送信結果を確認できない発注を SIMULATE で意図的に作る故障注入（#856）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-03
- 決定者: Claude Code（[#856](https://github.com/endazon/ai-stock-trading/issues/856) の利用者裁定 2026-10-03「`NotPlaced` の実機検証に使う事例は、SIMULATE で意図的に作る」を実装へ落とした）

## 起点・関連

- 起票: [#856](https://github.com/endazon/ai-stock-trading/issues/856)（解放の門を開けるための実機の記録。裁定 2026-10-03）
- 関連する計画書 ID: FR-10（二重発注をしない）・FR-05（発注執行）・NFR-09（取引環境ごとの門）・ADR-0045 決定1（門を開ける基準 (a)(b)）・決定2（門は取引環境ごと）
- 関連する実装 ADR: IADR-0057（予約）・IADR-0074（自動リコンサイル）・IADR-0092（実照会プローブ）・
  IADR-0117 改定6・7（届いたか不明は予約を据え置く）・[IADR-0362](IADR-0362_reservation-reconciliation-enabled-with-release-gate.md)（配備での有効化と解放の門）・
  IADR-0444（取引環境ごとの門）・IADR-0111（実弾の閂）・IADR-0316（ログの無害化）・IADR-0482（実弾口座の読み取り専用の照会）
- 作業仕様書: [`.ai-context/specs/20261003_856_indeterminate-dispatch-fault-injection.md`](../specs/20261003_856_indeterminate-dispatch-fault-injection.md)
- 基点コミット: `origin/develop` `3b2ef477`

## コンテキストと課題

- 突合は配備で 2026-09-25 から実効している。解放の門（`Reconciliation__ReleaseOnNotPlaced__Simulate`）は閉じている。
- 門を開けるには、送信結果を確認できなかった発注（`BrokerDispatchIndeterminateException`）について、突合が「発注済み」と「未発注」を正しく判定した実機の記録が要る（ADR-0045 決定1）。自然には起きていない（2026-09-25 の実測で滞留 0 件）。
- 裁定は、SIMULATE 限定・既定無効・実弾の構成では起動を止める故障注入のスイッチで、2 通り（送信後に不明＝AfterSend／送信前に不明＝BeforeSend）を作ることを求めた。PoC が開場中に 1 回ずつ流して観測する。
- 決めること: どこに挟むか、何に当てるか、1 回をどう数えるか、どの構成で拒否するか、どう観測させるか。

## 決定

### 決定 1 — 構成は `FaultInjection:IndeterminateDispatch:{Mode, Symbols, ExpiresAtUtc}`。既定は無効

- `Mode` = `None`（既定・未設定・空）/ `AfterSend` / `BeforeSend`。`None` 以外では `Symbols`（カンマ区切り。`*` 単独で全銘柄）と `ExpiresAtUtc`（ISO-8601）が必須。
- 未知の `Mode`・空の銘柄・`*` と銘柄の混在・読めない期限・**起動時刻から 24 時間より先の期限**は起動時に止める。
- **期限を過ぎた構成は起動を止めない**（注入しないだけ。構成の Warning に「期限切れのため注入しません」と出す）。切り忘れた構成で発注執行が再起動のたびに落ちると、発注と保護逆指値ガードがまとめて止まるため（IADR-0444 決定4 と同じ判断）。期限は「外し忘れ」を時間で無害にするためにある。

### 決定 2 — SIMULATE 限定。`Mode` が `None` 以外なら次の構成で起動を止める

| 条件 | 理由 |
| --- | --- |
| 発注先が moomoo でない（内蔵 paper） | 包む先が無い。「有効のつもり」を作らない（`RealMarginQuery` と同じ流儀） |
| 発注先が moomoo SIMULATE でない（live 階層。閂 0 が先に止める） | 裁定「SIMULATE 限定」 |
| `LiveTradingGate.LiveTradingReleased` が真 | 実弾を解禁した版で故障注入を残さない |
| `Broker:Moomoo:TrdEnv` が `simulate` 以外 | 同上（`MoomooBrokerOptions` と同じ語彙） |
| `Broker:Moomoo:RealMarginQuery:Enabled=true` | 実弾口座へ接続する構成と同じプロセスで故障注入を許さない |

最後の行は、裁定の「実弾の構成では起動を止める」を広く読んだ判断である。実弾口座の照会は読み取り専用で発注経路から到達できない（IADR-0482）が、「SIMULATE 限定」を構成の上で一目で確かめられる形を優先した。PoC の構成では無効なので影響しない。

### 決定 3 — 当てるのは新規建て（`PositionEffect.Open` かつ指値・remark あり）だけ

- SDK 非依存の発注要求 `MoomooOrderRequest` の末尾に既定 null の `PositionEffect` を足し、アダプタが発注意図の値を載せる。**OpenD へは送らない**（`MMApiMoomooTradeClient` は読まない）。
- 保護レグ（S0 の逆指値・S3 の代替注文種別）・成行の手仕舞い（ガード・S1・承認の成行）・指値の手仕舞いはすべて `Close` であり、構造上対象外になる（母集合は作業仕様書）。
- 理由: (1) PoC が観測したいのは承認の配送である。(2) BeforeSend を保護レグ・手仕舞いへ当てると、解放の門が閉じたまま建玉が無保護・未決済で据え置かれ、人が解くまで残る。(3) 新規建てなら BeforeSend でも建玉は生じない。remark が無い発注は突合できないので対象外。

### 決定 4 — 挟む場所はアダプタへ渡す OpenD クライアント（`IMoomooTradeClient`）のデコレータ。注入は既存のアダプタの catch を通す

- デコレータは対象の発注で、AfterSend なら内側で実際に送信してから、BeforeSend なら送信せずに、`IndeterminateDispatchFaultInjectedException` を投げる。
- アダプタ（`MoomooBrokerAdapter`）は確認できた失敗（retType -1）以外の例外を `BrokerDispatchIndeterminateException` に包む。**発注執行・予約・突合のコードは 1 行も変えない**——PoC は本番の経路そのものを観測する。
- AfterSend で内側の送信が先に失敗したら、注入せずに実際の例外をそのまま伝播させる（分類はアダプタの既存の経路）。

### 決定 5 — 1 プロセスにつき 1 回。1 回分は送る前に取る

- 対象の発注が来たら `Interlocked` で 1 回分を取り、取れた 1 本だけに注入する。2 本目以降・同時の発注は素通し。
- 送る前に取る（前の端だけ）。後の端（成功した送信で数える）では BeforeSend が数えられず毎回注入になる。両端（送信が失敗したら戻す）では、実は届いた送信の後に 2 本目を注入し得る（作業仕様書の窓の表）。
- 再起動すると 1 回分が戻る。Runbook は注入を見た直後に構成から外す手順にした（その再起動が突合の巡回の初回も兼ねる）。

### 決定 6 — 配線はアダプタへ渡すクライアントだけ。ログは構成時に 1 行・発火時に 1 行

- Program.cs は構成の読み取りと拒否を合成起点の先頭（`LiveTradingGate.Ensure` の直後）で 1 回行い、有効なときだけ `IBrokerAdapter` の生成でクライアントを包む。DI の `IMoomooTradeClient` は包まない（借株可否の照会はそのインスタンスを `IShortPermitSource` へ型変換して使う。突合のプローブも素のクライアントで照会する）。
- 構成時の Warning（形・銘柄・期限・期限切れか）と、発火時の Warning（形・DecisionId・銘柄・数量・AfterSend なら注文 ID）。外部由来の文字列（銘柄・remark・注文 ID）は `LogSanitizer.Sanitize` を通す（IADR-0316）。口座 ID は載せない。例外の本文にも「故障注入」と形を書く（発注執行の Error ログに出るので、自然発生と取り違えない）。
- helm の values.yaml にはコメントの案内だけを置き、描画は変えない。helm.yml の突合の描画検査に「`FaultInjection__` が 3 描画のどれにも出ないこと」を足す（既定の配備で有効にならないことの機械的な担保）。

## 検討した選択肢

| 案 | 内容 | 判定 |
| --- | --- | --- |
| A（採用） | アダプタへ渡す `IMoomooTradeClient` のデコレータ。注入はアダプタの既存の catch を通す | ◎ アダプタの分類・発注執行・予約・突合が本番どおりに走る。挟む口は 1 つ |
| B | `IBrokerAdapter` のデコレータで `BrokerDispatchIndeterminateException` を直接投げる | ✕ アダプタの分類を通らない。能力の型（`IClientOrderIdBroker`・`IProtectiveOrderBroker` ほか 8 つ）をすべて委譲する必要があり、`is` の判定を 1 つ落とすと経路が黙って変わる |
| C | DI の `IMoomooTradeClient` 登録そのものを包む | ✕ 借株可否の照会が型変換で壊れる。突合のプローブまで包まれる |
| D | 注文要求を壊して OpenD に拒否させる | ✕ 確認できた失敗（retType -1）は `Rejected` で、届いたか不明にならない |
| E | 銘柄の制限なし・回数の制限なし | ✕ PoC が 1 件ずつ観測できない。切り忘れで新規建てが全部止まる |
| F | 期限なし（回数だけ） | △ 再起動のたびに 1 回分が戻るため、切り忘れが続く。期限で無害にする |

## 統制と現在の実現手段

- 既定で無効: 構成キーを置かない（values.yaml はコメントだけ）。helm.yml の描画検査が `FaultInjection__` の描画を赤にする。T-10-2230。
- SIMULATE 限定: 起動時の拒否（決定 2）。T-10-2236（単体と本番の組み立て）。
- 新規建てだけ・1 回だけ・期限内だけ: デコレータの判定。T-10-2234・T-10-2235。
- 予約・突合のコードを変えないこと: T-10-2231〜T-10-2233（本物のアダプタ・発注執行・プローブ・突合を通す）。
- 観測: 構成と発火の Warning。T-10-2238。

## 自己変異（実測 2026-10-03。15 件すべて赤）

対象の試験（`IndeterminateDispatchFaultInjection*`・`MoomooBrokerAdapterTests`）を変異ごとに走らせた（1 件ずつ当てて戻す）。表は作業仕様書と試験仕様書に同じものを置く。

| # | 変異 | 赤になった試験（件数） |
| --- | --- | --- |
| M1 | 新規建ての判定を外す（手仕舞い・保護レグにも当たる） | T-10-2235（3） |
| M2 | 1 回分を取らない（毎回注入） | T-10-2231・T-10-2232・T-10-2234・T-10-2238・T-10-2239（9） |
| M3 | AfterSend が送信しない | T-10-2231・T-10-2233・T-10-2234・T-10-2238・T-10-2239（8） |
| M4 | BeforeSend も送信してから投げる | T-10-2231・T-10-2232・T-10-2233・T-10-2234・T-10-2239（7） |
| M5 | 期限を見ない | T-10-2234・T-10-2237（2） |
| M6 | 許可した銘柄を見ない | T-10-2234（1） |
| M7 | 実弾口座の照会との同居を許す | T-10-2236（2） |
| M8 | 内蔵 paper と live 階層を許す | T-10-2236（3） |
| M9 | Program.cs がアダプタのクライアントを包まない | T-10-2239（2） |
| M10 | アダプタが発注意図の効果を載せない | T-10-2231・T-10-2233・T-10-2235・T-10-2239・既存の写像の試験（10） |
| M11 | 期限の上限（起動から 24 時間）を外す | T-10-2237（1） |
| M12 | 発火の Warning を出さない | T-10-2238（1） |
| M13 | ログの銘柄を無害化しない | T-10-2238（1） |
| M14 | 送信が失敗したら 1 回分を戻す（両端の形） | T-10-2234（1） |
| M15 | TrdEnv を見ない | T-10-2236（1） |

## 結果

- PoC は `Mode` と銘柄と期限を構成に足して発注執行を再起動するだけで、AfterSend と BeforeSend を 1 件ずつ作れる。手順は発注経路の Runbook。
- 解放の門・helm.yml の門のアサーション・#851 の文面は本件では変えない。PoC の結果を見て別の PR で開ける（1 件でも食い違えば閉じたまま。裁定）。

## 残余

- 新規建ての承認がいつ来るかは取引判断次第である。PoC は許可する銘柄を監視対象（ウォッチリスト）の銘柄にするか `*` にして、最初の新規建てを待つ。
- AfterSend の新規建ては、突合が「発注済み」と確定するまで（最悪 3 時間）約定追跡にも保護レグにも載らない。確定した時点で承認時の手法で保護レグを張る（IADR-0428）。SIMULATE なので実損は無いが、PoC は数量の小さい承認で流す。
- BeforeSend の新規建ては、門が閉じているあいだ予約が Reserved のまま残る。S0 / S3 の承認の文脈（AwaitingEntry）・S1 の行も残る。PoC の観測の後、Runbook の「滞留した予約を人が解決する」の手順で解く（証券会社に注文が無いことを確かめてから予約行を消す）。
- 1 回の数えはプロセスの中だけ。複数台・再起動をまたぐと台ごと・起動ごとに 1 回ずつ戻る（配備は 1 台）。期限で上限を掛けてある。
