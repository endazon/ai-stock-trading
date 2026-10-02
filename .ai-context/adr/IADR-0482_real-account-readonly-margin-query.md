---
title: IADR-0482 借株可否と維持率の束の照会に限り、実弾口座（Real × Margin）のヘッダを使う読み取り専用の照会用の環境を足す。発注経路から型・DI・ソースの 3 層で切り離し、既定は無効、照会ごとに Real であることを監査へ残す（口座 ID は伏せる）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, FR-05, FR-20, UC-06, ADR-0016, ADR-0019, ADR-0026, ADR-0002, IADR-0111, IADR-0144, IADR-0425, IADR-0473, IADR-0476, IADR-0256, IADR-0056, IADR-0060, IADR-0327]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定 3〔2026-08-06 追記・照会は実弾口座を要する〕・決定 14)
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md (PoC 項目 3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・FR-20)
---

# IADR-0482: 実弾口座のヘッダによる読み取り専用の照会（借株可否・維持率の束）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-02
- 決定者: endazon（#1000 の裁定 2026-10-02「読み取り専用で足す」）/ claude（実装への写像）

## 起点・関連

- 関連する計画書 ID: **ADR-0016 決定 3 の 2026-08-06 追記**（`TrdGetMarginRatio` は SIMULATE 口座では失敗し実弾口座では成功する。
  「Stage 1 は SIMULATE で発注しながら実弾口座のヘッダで照会する、環境をまたぐ構成になる。統制の設計上この非対称を前提にすること」）、
  ADR-0016 決定 14（Stage 1 で検証できない統制の表。決定 3・決定 7 の行）、ADR-0019 PoC 項目 3、ADR-0026 PoC 項目 9（料率の単位）、
  FR-10（空売りの一次ゲート）・FR-20（段階ごとの動作モード SIMULATE / 実弾の強制）・FR-05、UC-06
- 対象 Issue: #1000（#967 の残余。裁定 2026-10-02）
- 関連する実装仕様書: [20261002_1000_real-account-readonly-margin-query](../specs/20261002_1000_real-account-readonly-margin-query.md)
- 関連 IADR: [IADR-0144](IADR-0144_moomoo-short-selling-poc-outcomes.md) 決定 3（照会は実弾口座のヘッダ。「照会用の環境と発注用の環境を別に持つ必要があり、実装時に IADR-0111 を部分改定する」「実弾ヘッダでの照会は読み取り専用に限る」）・決定 5（30 秒 10 回）、
  [IADR-0425](IADR-0425_short-sell-context-supplier.md) 決定 2（照会は発注と同じ SIMULATE 口座のヘッダ。実弾ヘッダは #1000 の裁定へ）、
  [IADR-0111](IADR-0111_broker-tier-selection.md)（発注先 × 環境の 2 軸と閂 0〜4）、[IADR-0056](IADR-0056_moomoo-simulate-poc-complete-real-gated.md) §3（実弾解禁の前提）、
  [IADR-0473](IADR-0473_quote-refresh-closed-market-and-account-log-demotion.md) / [IADR-0476](IADR-0476_redact-account-id-in-opend-retmsg.md)（口座 ID の伏せ方）、
  [IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)（ソース走査による依存の検査）、[IADR-0327](IADR-0327_opend-connection-recreate-after-failed-attempt.md)（接続オブジェクトの作り直し）

## コンテキストと課題

- #967（IADR-0425）で空売り文脈の供給元は入ったが、借株可否の照会は発注と同じ SIMULATE 口座のヘッダで送るため、実測どおりなら常に失敗し、
  空売りの新規建ては `BorrowUnavailable` で全件拒否のまま（1 銘柄 10% / 空売り比率 50% は実地で評価されない。安全側）。
- 本系のコードは取引ヘッダを `TrdEnv_Simulate` に固定し（閂 2）、実弾は閂（`LiveTradingGate`・起動時拒否）で止めている。
  実弾ヘッダの照会経路を足すことは **`TrdEnv_Real` を本系のコードへ初めて入れる**判断であり、#1000 で裁定を仰いだ。
- 裁定（2026-10-02）: 「読み取り専用で足す。借株可否と維持率の束の照会に限り Real × Margin のヘッダを使う照会用の環境を足す。発注系は一切作らない。
  発注経路から構造的に切り離す（別の型・別の結線。発注のクライアントから到達できない）。閂は変えない。既定は無効。監査に Real の照会であることを残し、口座 ID は伏せる。
  料率の単位（#342）が確定するまで空売りは通らない前提は変わらない」。
- 発注執行は単一プロジェクト＋VSA（IADR-0259）であり、**プロジェクト参照の向きでは切り離せない**。

## 検討した選択肢

### 論点 1: 照会用の環境をどこに置くか

| 案 | 判定 |
| --- | --- |
| **A. 発注執行の中に、照会専用の別の型・別の接続を置く**（採用） | **採用**。moomoo の取引接続（OpenD）は発注執行だけが持つ（IADR-0425 決定 1）。照会の口（`GET /order-execution/short-permit`）・予算・キャッシュ（`ShortPermitQueryService`）をそのまま使える |
| B. 既存の `MMApiMoomooTradeClient` に実弾ヘッダの照会メソッドを足す | **却下**。発注の面を持つ型の中に実弾のヘッダを作る経路ができ、「発注のクライアントから到達できない」を満たさない |
| C. 照会専用の別プロジェクト（csproj）を作り、発注執行から参照する | **却下**。単一プロジェクト＋VSA（IADR-0259）に逆行し、照会プロジェクトは SDK（`MMAPI_Trd` は発注メソッドを持つ）を参照するので、プロジェクトの境界だけでは発注 API への到達を止められない。型・DI・ソース走査の 3 層で同じ保証を得る（決定 2） |
| D. 別サービス（別 Pod）にする | **却下**。OpenD の接続・口・配備が増え、裁定の射程（照会用の環境を足す）を超える |

### 論点 2: 既定と有効化

| 案 | 判定 |
| --- | --- |
| **A. 既定は無効。`Broker:Moomoo:RealMarginQuery:Enabled=true` の明示でだけ有効**（採用） | **採用**（裁定）。無効なら IADR-0425 の挙動（SIMULATE のヘッダで照会し失敗＝拒否）から 1 ビットも変えない |
| B. moomoo 構成なら常に有効 | **却下**（裁定に反する） |

### 論点 3: 監査の形

| 案 | 判定 |
| --- | --- |
| **A. 照会を送るたびに監査イベント `RealAccountReadOnlyQueried` を発行する**（採用） | **採用**。本系が実弾のヘッダを OpenD へ送った回数・時刻・銘柄・結果を中央の監査台帳だけで数えられる。上限は照会の予算（30 秒 9 回）で抑えられる |
| B. ログだけに残す | **却下**。ログは保持期間・検索性が監査台帳より弱く、「実弾には触れていない」を記録で示せない |
| C. `ShortPermitView` に取引環境を足してリスク管理の監査へ運ぶ | **却下（本件では）**。受け手の契約・リスク管理の監査を変える範囲が広がる。照会を実際に送った側で残すほうが漏れない |

## 決定

### 決定 1: 照会のクライアントは `MMApiRealMarginQueryClient`。照会は `TrdGetMarginRatio` だけ、口座は Real × Margin × 米国株を 1 つだけ選ぶ

- 置き場は `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/RealReadOnly/`（名前空間 `…ExternalServices.RealReadOnly`）。
- 接続時に口座一覧（`TrdGetAccList`・`userID=0`）を読み、**`TrdEnv_Real` かつ `TrdAccType_Margin` かつ `TrdMarketAuthList` に `TrdMarket_US` を含む**口座を選ぶ。
  該当が 0 件・2 件以上なら選ばず例外（照会を送らない）。取り違えた口座で照会しない。
- 照会は `TrdGetMarginRatio`（借株可否 `IsShortPermit` と維持率の束を 1 つの応答で返すプロトコル）だけ。ヘッダは `TrdEnv_Real`・選んだ口座・`TrdMarket_US`。
  **実弾のヘッダを作るのは `BuildRealHeader` の 1 か所だけ**で、照会の C2S にしか渡さない。
- `IShortPermitSource` の契約（行・欄が無ければ null、照会の失敗は例外）はそのまま。予算・キャッシュ・相乗りは `ShortPermitQueryService`（IADR-0425 決定 3）が引き続き持つ。
- 維持率の束（`MarginRatioInfo` の他の欄）を読む呼び手は今は無い（読まない。足すときは同じ型の照会として足す）。

### 決定 2: 発注経路から構造的に切り離す（型・DI・ソースの 3 層）

1. **型**: `MMApiRealMarginQueryClient` が実装するのは `IShortPermitSource`・SDK のコールバック（`MMSPI_Trd` / `MMSPI_Conn`）・`IDisposable` だけ。
   `IMoomooTradeClient`・`IBrokerAdapter`・`IOrderFeeQuery` 等を実装しない。接続は**専用の狭いシーム `IMoomooMarginQueryConnection`**（面は口座一覧と `TrdGetMarginRatio` だけ）で持ち、
   発注の面を持つ `IMoomooTradeConnection` とは相互に継承・変換しない。SDK の `MMAPI_Trd` は本番実装の中に private で包み、外へ出さない。
2. **DI**: 有効時だけ `MMApiRealMarginQueryClient` を自身の型と `IShortPermitSource` として登録する。発注の型（`IMoomooTradeClient`・`IBrokerAdapter`・予約の照会・建玉の照会・口座の照会）は従来どおり
   SIMULATE の OpenD クライアントから組み、照会のクライアントを受け取らない。OpenD への TCP 接続も別に張る（発注に使う接続オブジェクトを共有しない）。
3. **ソース**（IADR-0256 と同じ作法・被検査コードを参照しない）: `RealReadOnly/` のソースに発注の型・発注の SDK メソッド（`PlaceOrder` / `ModifyOrder` / `PlaceComboOrder` / `UnlockTrade`）・
   発注要求の型（`TrdPlaceOrder.Request` 等）が識別子として現れない。`RealReadOnly/` の型を参照してよいのは合成起点（`Program.cs`）だけ。`TrdEnv_Real` を書いてよいのは `RealReadOnly/` だけ。
- 取引の解錠（`UnlockTrade`）を送らない。moomoo は実弾口座の発注に解錠を要するため、OpenD 側でも発注は通らない（二重の守り。**実機未検証**）。
- **実弾の閂（`LiveTradingGate`・閂 1〜4）は 1 行も変えない。** `LiveTradingGate.Ensure` は照会の構成を読む前に走り、live 階層は照会を有効にしても起動時に止まる。

### 決定 3: 既定は無効（`Broker:Moomoo:RealMarginQuery:Enabled`）

- 未設定・空・`false` は無効、`true`（大文字小文字・前後の空白は問わない）だけが有効、それ以外は起動時に停止する（既定へ黙って倒さない）。
- 無効なら照会ポートは従来どおり SIMULATE の OpenD クライアント（IADR-0425 決定 2）で、本系は実弾のヘッダを 1 度も作らない。
- moomoo 以外の構成（内蔵 paper）で `true` なら起動時に停止する（照会先の口座が無い構成で「有効のつもり」を作らない）。
- helm の既定（`values.yaml`）・`values-local.yaml` には env を足さない（描画は変わらない）。有効化は PoC 側で env を明示する。

### 決定 4: 監査（`RealAccountReadOnlyQueried`）

- 照会を送ったら（成功・不許可・欄の欠落・失敗のいずれでも）1 件発行する。欄: `TradingEnvironment`（常に `Real`）・`Operation`（`GetMarginRatio`）・
  `MaskedTail`（口座 ID の末尾 2 桁以外を伏せた形。全桁は運ばない）・`Symbol`・`Market`・`Outcome`（`permitted` / `not-permitted` / `field-missing` / `failed`）・`QueriedAt`。
- 監査サービスは要約に「実弾口座（Real）のヘッダで読み取り専用の照会」「発注はしない」を明記し、相関 `real-readonly-query` で束ねる。
- 接続・口座の選択の失敗（照会を送っていない）は発行しない（実弾のヘッダは出ていない）。ログには残る。
- 🔴 **監査に残せなければ答えを使わない**（記録の失敗は例外＝照会サービスは「分からない」＝空売りは拒否）。

### 決定 5: 口座 ID の伏せ方は既存の 1 つを共有する

- `TailOnly` / `RedactAccountId` / `RedactRetMsg` の本体を `MoomooAccountIdRedaction`（static）へ移し、`MMApiMoomooTradeClient` の同名メンバは委譲にした（伏せ方は 1 文字も変えない）。
  照会側から発注クライアントの型を参照させないための移動である。
- 照会のクライアントも口座一覧で見た全口座 ID を集合に持ち、`retMsg` を例外へ載せる前に伏せる（IADR-0476 と同じ）。ログの口座 ID も末尾 2 桁（IADR-0473 と同じ）。

### 決定 6: IADR-0111・IADR-0425 との関係

- IADR-0111 の「発注先 × 環境の 2 軸で環境が 1 つに定まる」は**発注の環境**について有効のまま。本 IADR は照会だけに別の環境（Real）を足す部分改定であり、
  `BrokerSelection`・`Broker:Environment`・閂は変えない（IADR-0144 決定 3 の予告どおり）。
- IADR-0425 決定 2（照会は SIMULATE のヘッダ）は**既定（無効）**として有効のまま。有効にしたときだけ本 IADR の照会に替わる。

## 理由

- 裁定の 5 点（読み取り専用・構造的な切り離し・閂を変えない・既定無効・監査に Real と伏せた口座）を、単一プロジェクトのまま同時に満たす形がこれである。
- 切り離しを 1 つの層だけで持つと、その層の盲点（例: 型の検査は `MMAPI_Trd` を包んだ private の中を見ない／ソース走査は `global using` を見ない）から崩れる。3 層で重ねる（IADR-0256 決定 1 と同じ考え方）。
- 監査を照会を送った側で出すのは、受け手（リスク管理）へ取引環境を運ぶより経路が短く、照会を送ったのに記録が無い状態を「答えを使わない」で塞げるからである。

## 結果

- 良い影響:
  - PoC で有効にすれば、借株可否が実弾口座のヘッダで答えられ、空売り文脈が組まれて 1 銘柄 10% / 空売り比率 50%・維持率・株価下限・逆指値必須が観測した値の上で評価される（IADR-0425 決定 5）。
  - 本系が実弾のヘッダを OpenD へ送った事実が、すべて監査台帳に残る（口座は伏せた形）。
  - 発注の経路・閂・SIMULATE の発注は変わらない（試験で固定）。
- 悪い影響・トレードオフ（残余リスク）:
  - 🔴 **料率の単位（#342）が確定するまで、借株が許可されても空売りは通らない**（`BorrowRateAnnual` は null のまま。IADR-0425 決定 5）。本件だけでは空売りは解禁されない。
  - 🔴 **実 OpenD での照会は未検証**（実弾口座の口座一覧の形・`TrdMarketAuthList`・応答・頻度制限・解錠なしで照会が通るかは PoC で確かめる）。
  - OpenD への取引接続が 2 本になる（発注用・照会用）。OpenD 側の同時接続数の上限は確かめていない。
  - 照会の予算（30 秒 9 回）はプロセス内で、発注用の接続の照会とは別に数えない（照会はどちらか一方だけが送る）。
  - 実弾口座の口座 ID がプロセスのメモリに載る（ログ・監査・例外には全桁を出さない）。`MMAPI_Trd` は SDK の型として発注メソッドを持つが、本系はそれを包んだ private の外へ出さない（ソース走査と型の検査で固定）。
  - 維持率の束の他の欄は読まない（呼び手が無い）。維持率の供給は従来どおり（既定は供給なし）。
- フォローアップ: PoC での有効化と照会の確認（手順は作業仕様書）。#342（料率の単位）。

### ［2026-10-02 追記 / #1000・独立監査］残余リスクと盲点の追記

- 🔴 **決定 4「監査に残せなければ答えを使わない」が捕まえるのは、プロセス内での発行（`WolverineRealReadOnlyQueryAudit` の `new MessageBus(runtime).PublishAsync`）の失敗だけである。**
  発注執行は Wolverine の永続化した送信箱（durable outbox）を持たないため、`PublishAsync` はメッセージをプロセス内の送信待ちへ渡した時点で成功を返す。
  その後の**ブローカ（RabbitMQ）への送信の失敗**（接続断・送信待ちのままのプロセス終了）と、**監査サービス側の台帳への書き込みの失敗**（再試行を使い切って `<queue>_error` へ退避）は、
  照会の答えが使われた**後**に起き、照会のクライアントには届かない。したがって「照会を送ったのに監査台帳に行が無い」は起こり得る（答えは既に使われている）。
  塞ぐには発注執行へ durable outbox（送信の永続化）を入れるか、監査サービスの受領を同期で確かめる必要があり、本件の範囲を超える（コードは変えない）。
  数え合わせの代替: 照会クライアントのログ（接続完了・照会の失敗）と監査台帳の件数の突き合わせは手作業になる。
- 🔴 **決定 2 の「取引の解錠（`UnlockTrade`）を送らない＝OpenD 側でも発注は通らない（二重の守り）」は OpenD の運用に依存する。**
  moomoo の取引の解錠は**接続ごとではなく OpenD（ログイン中のユーザー）ごと**に効くとされる（**実機未検証**）。運用者が OpenD の GUI や別のクライアントから実弾の取引を解錠すれば、
  同じ OpenD に繋いだ本系の接続からも実弾の発注が通り得る状態になり、この OpenD 側の守りは成り立たない。そのときの守りは本系の側（型・DI・ソース走査の 3 層と閂）だけになる。
  PoC では、照会に使う OpenD で実弾の取引を解錠しない運用とし、解錠の効く単位を実機で確かめる。
- **盲点（決定 2 の 3 層が見ないもの）に「リフレクション」を足す。** ソース走査（IADR-0256 の作法）は識別子を見るため、
  文字列で名前を渡す経路（`Type.GetType("…")`・`GetMethod("BuildRealHeader")`・`Enum.Parse` 等）を見ない。独立監査を受けて、照会側の外のソースについて
  ①`BuildRealHeader`・照会側の名前空間を文字列リテラルにも識別子（`nameof` を含む）にも書かない、②照会側の型名を文字列リテラルに書かない（合成起点は除く）、
  ③名前でメンバ・型へ到達するリフレクション API（`GetMethod`・`GetField`・`InvokeMember`・`CreateInstance`・`BindingFlags`・引数つきの `GetType(…)` 等）を使わない、
  ④文字列全体が `"TrdEnv_Real"` のリテラルを書かない、を検査に足した（T-10-2063）。ただし `dynamic`・式木・DI の型走査・名前を実行時に組み立てる文字列・別アセンブリ経由は見えず、**網羅ではない**。
  既存の盲点（型の検査は `MMAPI_Trd` を包んだ private の中を見ない／ソース走査は `global using` とソースジェネレータを見ない）と並べて、型・DI の検査と重ねて持つ。
- ソース走査の禁止識別子に、発注側のポート（`IOrderFeeQuery`・`IBrokerAccountSource`・`IReservationBrokerProbe` ほか。母集合は作業仕様書の追記）を足した。
- ログの口座 ID の伏せ（決定 5）を試験で固定した（T-10-2067。記録するロガーで、接続・照会の成功・欄の欠落・失敗・監査の失敗・切断の各経路の文言と構造化ログの引数に全桁が無いこと）。

## 関連

- Supersedes: なし（IADR-0111 の環境 1 軸を照会について部分改定し、IADR-0425 決定 2 を既定として残す。決定 6）
- Superseded by: なし
