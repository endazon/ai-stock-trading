---
title: 送信結果を確認できない発注を SIMULATE で意図的に作る故障注入のスイッチ（#856）
type: spec
status: accepted
related_ids: [FR-10, FR-05, NFR-09, ADR-0045, IADR-0488, IADR-0057, IADR-0074, IADR-0092, IADR-0117, IADR-0362, IADR-0444, IADR-0111, IADR-0316, IADR-0482]
author: claude (Claude Code)
created: 2026-10-03
updated: 2026-10-03
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements (FR-10 リスク統制・FR-05 発注執行)
  - planning:projects/ai-stock-trading/07_adr (ADR-0045 決定1・決定2 解放の基準は取引環境ごとの実機の記録)
---

# 仕様書: 送信結果を確認できない発注を SIMULATE で意図的に作る故障注入のスイッチ（#856）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制。二重発注をしない）・FR-05（発注執行）
- 非機能: NFR-09（取引環境ごとの解放の門）
- 関連 ADR: ADR-0045 決定1（解放の門を開ける基準＝その取引環境の実機の記録で (a) 発注済みを発注済みと判定した記録・(b) 未発注の判定に誤判定が 0 件）・決定2（門は取引環境ごと）
- 関連 IADR: IADR-0057（予約）・IADR-0074（自動リコンサイル）・IADR-0092（実照会プローブ）・IADR-0117 改定6・7（届いたか不明は予約を据え置く）・IADR-0362（配備での有効化と解放の門）・IADR-0444（取引環境ごとの門）・IADR-0111（実弾の閂）・IADR-0316（ログの無害化）・IADR-0482（実弾口座の読み取り専用の照会）
- 起票: [#856](https://github.com/endazon/ai-stock-trading/issues/856)。利用者裁定 2026-10-03「`NotPlaced` の実機検証に使う事例は、SIMULATE で意図的に作る」
- 基点コミット: `origin/develop` `3b2ef477`

## 目的・背景

- 配備では突合（`Reconciliation__Enabled` / `UseBrokerProbe`）が 2026-09-25 から実効している。解放の門（`ReleaseOnNotPlaced__Simulate`）は閉じている。
- 門を開ける判断には、送信結果を確認できなかった発注（`BrokerDispatchIndeterminateException`）の実例が要る。自然には起きていない（2026-09-25 の実測で滞留 0 件）。
- 裁定: SIMULATE 限定・既定無効・実弾の構成では起動を止める故障注入のスイッチを足す。作るのは 2 通り。
  1. 送信した後に結果を不明にする（AfterSend）→ 突合は「発注済み」（`ProbeTerminalized`）になるはず（肯定形）。
  2. 送信する前に結果を不明にする（BeforeSend）→ 突合は「未発注」（`HeldNotPlaced`）になるはず（否定形。本丸）。
- PoC が開場中に 1 回ずつ流し、ログと証券会社の注文一覧で観測する。門を開ける変更は本件の外（両方が期待どおりになった後の別 PR）。

## 対象範囲

- 対象:
  1. 故障注入の構成（`FaultInjection:IndeterminateDispatch:*`）の読み取りと起動時の検証。
  2. 発注アダプタが使う OpenD クライアント（`IMoomooTradeClient`）の発注 1 本を包むデコレータ。注入の結果は既存のアダプタの catch を通って `BrokerDispatchIndeterminateException` になる（予約・突合のコードは 1 行も変えない）。
  3. デコレータが新規建てを見分けるため、SDK 非依存の発注要求 `MoomooOrderRequest` の末尾に既定 null の `PositionEffect` を足し、アダプタが発注意図の値を載せる（OpenD への送信内容は変えない）。
  4. Program.cs の配線（アダプタへ渡すクライアントだけを包む。DI の `IMoomooTradeClient` は包まない）。
  5. helm: values.yaml にコメントの案内だけを置く（描画は変えない）。helm.yml の描画検査に「`FaultInjection__` が描画されないこと」を足す。
  6. Runbook（`docs/operations/broker-execution-paths-runbook.md`）に PoC の手順。運用仕様書の該当行から参照を張る。
  7. 試験 T-10-2230〜T-10-2239（`docs/tests/FR-10_risk-controls-tests.md`）・自己変異。
- 対象外:
  - 解放の門を開けること（`Reconciliation__ReleaseOnNotPlaced__Simulate` を `true` にする）・helm.yml の門のアサーションの変更・#851 の文面の追随（射程 4）。PoC の結果を待つ。
  - 突合の巡回間隔・閾値を短くする仕組み（下限 1 時間は構造の砦であり、PoC のためには変えない。Runbook で既存の値の範囲での短縮を案内する）。
  - 決済・保護レグ（逆指値・代替注文種別・成行の手仕舞い）への注入（下の設計 3）。
  - 実弾の記録（ADR-0045 決定2。実弾では作らない）。

## 母集合（規則 9。`origin/develop` `3b2ef477`）

引き方:
- `git grep -n "new BrokerDispatchIndeterminateException" -- backend ':!*/Tests/*'` → 送出点は `MoomooBrokerAdapter.PlaceWithRejectionDetailAsync` の 1 か所だけ。
- `git grep -n "\.PlaceOrderAsync(" -- 'backend/Services/OrderExecutionService/*.cs' ':!*Tests*'` → `IMoomooTradeClient.PlaceOrderAsync` の呼び出しはアダプタの同じ 1 か所だけ。
- `git grep -n "PlaceStopOrderAsync\|PlaceMarketOrderAsync\|PlaceAlternativeStopOrderAsync" -- 'backend/Services/OrderExecutionService/*.cs' ':!*/Tests/*'` → アダプタの発注の入口の呼び出し元。
- `git grep -n "BuildCloseIntent\|PositionEffect.Close" -- backend/Services/OrderExecutionService ':!*/Tests/*'` → 保護レグ・手仕舞いの発注意図の効果。

| アダプタの入口 | 呼び出し元 | 送る要求（種別・効果） | 注入 |
| --- | --- | --- | --- |
| `PlaceOrderAsync(intent, decisionId)` | `OrderExecutionAppService`（承認の配送。新規建て） | Limit・Open・remark あり | **対象**（許可した銘柄・1 プロセス 1 回・期限内） |
| 同上 | `OrderExecutionAppService`（承認の配送。指値の手仕舞い） | Limit・Close | 対象外（効果が Close） |
| `PlaceMarketOrderAsync` | `OrderExecutionAppService`（成行の手仕舞い・保護の成行）・`ProtectiveStopGuard`・`SoftwareStopExecutor` | Market・Close（`BuildCloseIntent` / `new OrderIntent(..., PositionEffect.Close, ...)`） | 対象外 |
| `PlaceStopOrderAsync` | `OrderExecutionAppService`・`ProtectiveStopGuard`（S0） | Stop・Close | 対象外 |
| `PlaceAlternativeStopOrderAsync` | `OrderExecutionAppService`（S3） | StopLimit / TrailingStop・Close | 対象外 |
| `PlaceOrderAsync(intent)`（remark なし） | `OrderExecutionAppService`（`IClientOrderIdBroker` でない発注先） | remark なし | 対象外（moomoo のアダプタは常に remark を付ける。remark が無いと突合できない） |

`IMoomooTradeClient` の他の口（照会・取消・建玉・口座・remark 突合）は素通しにする。突合のプローブ（`MoomooReservationBrokerProbe`）は DI の素のクライアントを使い、デコレータを通らない。

## 設計

### 1. 構成（既定は無効）

| キー（env） | 値 | 既定 |
| --- | --- | --- |
| `FaultInjection:IndeterminateDispatch:Mode`（`FaultInjection__IndeterminateDispatch__Mode`） | `None` / `AfterSend` / `BeforeSend`（大小文字・前後空白は問わない） | 未設定・空 ＝ `None` |
| `FaultInjection:IndeterminateDispatch:Symbols`（`__Symbols`） | 許可する銘柄のカンマ区切り（発注意図の `Symbol` と大小文字を問わず一致）。`*` 単独で全銘柄 | `Mode` が `None` 以外なら必須 |
| `FaultInjection:IndeterminateDispatch:ExpiresAtUtc`（`__ExpiresAtUtc`） | ISO-8601 の時刻（例 `2026-10-05T20:00:00Z`）。この時刻以降は注入しない | `Mode` が `None` 以外なら必須 |

- 未知の `Mode`・空の銘柄・`*` と銘柄の混在・読めない時刻・**起動時刻から 24 時間より先の期限**は起動時に止める（「有効のつもりで無効」「無効のつもりで有効」「切り忘れ」を作らない）。
- **期限を過ぎた構成は起動を止めない**（注入しないだけ。Warning を 1 行出す）。切り忘れた構成で発注執行が再起動のたびに落ちると、発注と保護逆指値ガードがまとめて止まるため（IADR-0444 決定4 と同じ判断）。
- **1 プロセスにつき 1 回だけ**注入する（`Interlocked` で 1 回目を取った呼び出しだけ）。2 本目以降は素通し。再起動すると再び 1 回分が戻るので、Runbook では注入を見た直後に `Mode` を外す（その再起動が突合の巡回の初回も兼ねる）。

### 2. 起動時の拒否（`Mode` が `None` 以外のとき）

| 条件 | 扱い | 理由 |
| --- | --- | --- |
| 発注先が moomoo でない（内蔵 paper） | 起動時に止める | デコレータを挟む先が無い＝「有効のつもり」を作らない（`RealMarginQuery` と同じ流儀） |
| 発注先が moomoo SIMULATE でない（live 階層） | 起動時に止める（閂 0 が先に止める） | 裁定「SIMULATE 限定」 |
| `LiveTradingGate.LiveTradingReleased` が真 | 起動時に止める | 実弾が解禁された版では故障注入を許さない（解禁の版で外し忘れを作らない） |
| `Broker:Moomoo:TrdEnv` が `simulate` 以外 | 起動時に止める（`MoomooBrokerOptions` と同じ語彙） | 同上 |
| 実弾口座の読み取り専用の照会（`Broker:Moomoo:RealMarginQuery:Enabled=true`）が有効 | 起動時に止める | 実弾口座へ接続する構成と同じプロセスで故障注入を許さない（「実弾の構成では起動を止める」を広く読む。PoC の構成では無効） |

**実弾口座の照会が有効かは合成起点（Program.cs）が読み、真偽値で検証へ渡す。** 故障注入の側は照会側の型・名前空間・それを指す文字列を持たない（照会側の型を参照してよいのは合成起点だけ。IADR-0482 決定2。初版は照会側の構成の型を直接参照し、Architecture.Tests の照会側の隔離の検査 2 件で CI が赤になった）。

### 3. 注入の対象（新規建てだけ）

- 対象は `PositionEffect == Open` かつ `Kind == Limit` かつ remark（DecisionId）がある要求。保護レグ（S0 / S3）・成行の手仕舞い・指値の手仕舞いはすべて `Close` なので構造上対象外。
- 理由: (1) PoC が観測したいのは承認の配送（突合の主対象）である。(2) BeforeSend を手仕舞い・保護レグへ当てると、解放の門が閉じたまま建玉が無保護・未決済で据え置かれ、人が解くまで残る。(3) 新規建てなら BeforeSend でも建玉は生じない（観測の副作用が最小）。

### 4. 2 つの形

- **AfterSend**: 内側のクライアントで実際に送信し（OpenD が注文 ID を返す）、その後に `IndeterminateDispatchFaultInjectedException` を投げる。アダプタは確認できた失敗（retType -1）以外の例外を「届いたか不明」として `BrokerDispatchIndeterminateException` に包む（既存の経路）。内側が例外を投げたら、そのまま伝播させる（1 回分は使い切る。Warning を出す）。
- **BeforeSend**: 内側を呼ばずに同じ例外を投げる。証券会社には注文が無い。
- 発注執行の側は既存の経路のまま: 予約は Reserved のまま、結果は保存しない、Error ログ「発注の結果を確認できませんでした…」。

### 5. ログ

- 有効化（デコレータの生成）時に Warning を 1 行: 形・許可した銘柄・期限（期限切れなら「注入しません」）。
- 注入が発火したとき Warning を 1 行: 形・DecisionId（remark）・銘柄・（AfterSend なら）注文 ID。銘柄・remark・注文 ID は `LogSanitizer.Sanitize` を通す（IADR-0316。外部由来の文字列）。秘密は載せない（口座 ID も載せない）。
- 例外の本文にも「故障注入」と形を書く（アダプタと発注執行の Error ログの例外に出るので、自然発生と取り違えない）。

### 6. 配線（Program.cs）

- 構成の読み取りと拒否は合成起点の先頭（`LiveTradingGate.Ensure` の直後）で 1 回行う。
- **アダプタへ渡すクライアントだけを包む。** DI の `IMoomooTradeClient` は素のまま（借株可否の照会 `IShortPermitSource` は DI のインスタンスを型変換で取り出すので、包むと壊れる。突合のプローブも素のクライアントを使う）。

### 7. 窓（規則 11）

窓 = 注入から突合が判定するまで（滞留の閾値 2 時間＋巡回 1 時間）。この間に変わるのは「証券会社に注文が在るか」と「予約が Reserved か」である。
- 増える側のプローブ P1（AfterSend）: 窓の前の端で注文が 1 本増える。期待: 予約は Reserved のまま・突合は発注済み。
- 減る側のプローブ P2（BeforeSend）: 窓の前の端で注文は増えない。期待: 予約は Reserved のまま・突合は未発注（門が閉なので据え置き）・証券会社への送信 0 回。
- 形の比較（注入の 1 回をどちらの端で数えるか）:

| 形 | P1 | P2 | 再起動を挟む場合 |
| --- | --- | --- | --- |
| 前の端だけ（送る前に 1 回分を取る。**採用**） | ○（送信 1・例外） | ○（送信 0・例外） | 再起動で 1 回分が戻る → Runbook で注入直後に `Mode` を外す |
| 後の端だけ（送った後・成功したときだけ数える） | ○ | ✕（BeforeSend は送らないので数えられず、毎回注入＝新規建てが全部止まる） | 同左 |
| 両端（送る前に取り、送信が失敗したら戻す） | ○ | ○ | △ 戻すと、送信が例外になった（実は届いているかもしれない）後に 2 本目を注入し得る＝1 回の保証が崩れる |

前の端だけを採る。P1・P2 は T-10-2231・T-10-2232（単体）と T-10-2233（突合まで通す）で固定する。

## 受け入れ基準 → 試験

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-10-2230 | 既定（未設定）は無効で、発注は 1 本も変わらない。Program.cs ではアダプタのクライアントが包まれない | `IndeterminateDispatchFaultInjectionTests`・`IndeterminateDispatchFaultInjectionCompositionTests` |
| T-10-2231 | AfterSend: 送信した後に注入の例外を投げ、アダプタ越しに `BrokerDispatchIndeterminateException`、発注執行では予約が Reserved のまま・結果を保存しない | 同上 |
| T-10-2232 | BeforeSend: 送信せずに注入の例外を投げ、アダプタ越しに `BrokerDispatchIndeterminateException`、予約は Reserved のまま | 同上 |
| T-10-2233 | 本物のアダプタ・本物のプローブ・本物の突合を通すと、AfterSend は発注済み（終端化・本物の注文 ID）、BeforeSend は未発注で据え置き（門が閉） | `IndeterminateDispatchFaultInjectionTests` |
| T-10-2234 | 範囲の制限: 許可外の銘柄・2 本目・期限切れ・remark なしは素通し。`*` は全銘柄 | 同上 |
| T-10-2235 | 保護レグ・手仕舞い（Close の Limit / Market / Stop / StopLimit / TrailingStop）には注入しない（1 回分も消費しない） | 同上 |
| T-10-2236 | 起動時の拒否: 内蔵 paper・live 階層・実弾解禁・TrdEnv が simulate 以外・実弾口座の照会が有効 | 同上（単体）・`IndeterminateDispatchFaultInjectionCompositionTests`（paper・live・実弾口座の照会） |
| T-10-2237 | 不正な値で起動を止める: 未知の Mode・銘柄なし・`*` の混在・期限なし・読めない期限・24 時間より先の期限。期限切れは止めない | `IndeterminateDispatchFaultInjectionTests` |
| T-10-2238 | 注入の Warning は発火 1 回につき 1 行で、形・DecisionId・銘柄を載せ、外部由来の文字列は無害化される | 同上 |
| T-10-2239 | Program.cs で有効にすると、承認の配送の口（アダプタ）にだけ注入が効き、DI の `IMoomooTradeClient` と借株可否の照会は素のまま | `IndeterminateDispatchFaultInjectionCompositionTests` |

試験 ID は帯 T-10-2230〜T-10-2239 を確保した（develop の最大は T-10-2219。並行レーンとの衝突を避けて 10 空けた）。採番行は `docs/tests/FR-10_risk-controls-tests.md` に置く。

## 既存の記述・試験への影響（規則 10）

- `MoomooBrokerAdapterTests.発注を_SIMULATE_リクエストへ写像し結果を_BrokerOrder_へ変換する` は要求の record 等価で比べている。アダプタが `PositionEffect` を載せるので、期待値に `PositionEffect.Open` を足す（送信内容の写像は変わらない）。
- `MMApiMoomooTradeClient` は `PositionEffect` を読まない（OpenD への要求は 1 バイトも変わらない）。
- 文面: 運用仕様書の「`SIMULATE` に限り、記録を集めるために送信後に結果を確認できない発注を意図的に作ってよい」に、手段（本スイッチと Runbook）への参照を足す。`BrokerDispatchIndeterminateException` の契約コメント・IADR-0117 改定6・IADR-0362 の「門が閉じている」記述は**本件では変わらない**（門は開けない）。
- 自分の記述で新たに誤りになるもの: 無し（「自然には起きていない」は issue のコメントの記述で、本件の後も自然発生の数は変わらない）。

## 自己変異（実測 2026-10-03。15 件すべて赤）

対象の試験（`IndeterminateDispatchFaultInjection*`・`MoomooBrokerAdapterTests`、118 件）を変異ごとに走らせた（1 件ずつ当てて戻す）。
初回の実測で「内蔵 paper を許す」変異が生き残った（`IsMoomoo` の判定が、後続の「発注先が moomoo SIMULATE か」の判定と重複していた）。
重複していた判定を 1 つにまとめ、変異を「内蔵 paper と live 階層を許す」に改めて赤を確かめた（M8）。

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

## 計画書との差異

- 差異: なし（裁定の範囲内の実装。門の開閉は計画どおり利用者の判断に残る）。

## 未決事項

- 無し。実弾口座の照会が有効な構成での拒否は裁定の「実弾の構成では起動を止める」を広く読んだ判断であり、IADR-0488 に記録した（PoC の構成では無効なので影響しない）。
