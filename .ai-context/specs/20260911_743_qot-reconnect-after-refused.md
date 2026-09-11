---
title: BacktestService の履歴 K 線経路も OpenD 接続失敗後に接続オブジェクトを作り直す
type: spec
status: draft
related_ids: [FR-15, ADR-0002, ADR-0023, IADR-0064, IADR-0157, IADR-0327]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_backtest-data-source.md
---

# 仕様書: 履歴 K 線経路の OpenD 接続オブジェクト再生成（#743）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-15**（バックテスト基盤。過去データの取得経路そのもの）。
- ユースケース（UC）: 直接対応する UC は無い（バックテストの内部経路であり、利用者操作の起点を持たない）。
- 画面（SC）: なし。
- 関連 ADR: **ADR-0002**（ブローカ選択＝moomoo / OpenD 経由）／**ADR-0023 決定5**（米国株日足 OHLC の履歴源に
  moomoo OpenAPI の履歴 K 線を採用）。
- 関連 IADR: **IADR-0327**（発注経路で確定した「接続試行が失敗するたびに接続オブジェクトを作り直す」決定。
  本作業はその決定を相場（Qot）経路へ**同型に適用するだけ**であり、新しい決定を作らない）／
  **IADR-0157**（履歴 K 線アダプタ。SDK 依存を本クラス 1 つへ閉じる）。
- issue: [#743](https://github.com/endazon/ai-stock-trading/issues/743)（[#732](https://github.com/endazon/ai-stock-trading/issues/732) /
  [PR #739](https://github.com/endazon/ai-stock-trading/pull/739) の同型欠陥として起票）。

## 目的・背景

#732 で実測したとおり、OpenD が停止している間に一度 `Connection refused` を受けた moomoo SDK の接続
オブジェクトは、以後 `InitConnect` を呼んでも TCP を張り直さない（`true` を返すだけで `SYN_SENT` すら
現れない）。発注経路（`MMApiMoomooTradeClient` / `MMAPI_Trd`）はこれを IADR-0327 で直したが、
**`MMApiMoomooHistoryKLineClient` は `MMAPI_Qot _qot = new()` を 1 度だけ生成する同じ構造のまま**であり、
OpenD 停止中に BacktestService が起動すると、OpenD が復旧しても履歴 K 線の取得経路が死んだままになる。

**OpenD は有人認証（SMS 検証コード）を要するため「OpenD が後に立ち上がる」のが常態**であり、
その並びでは本経路も必ず同じ固着に入る。

## 対象範囲

- 対象
  - `MMApiMoomooHistoryKLineClient` が使う SDK 接続オブジェクトへ、IADR-0327 と**同型の薄いシーム**
    （`IMoomooQotConnection` / `IMoomooQotConnectionFactory`）を挟む。
  - 接続試行が失敗した／切断された後は、次の `InitConnect` の前に接続オブジェクトを作り直す。
  - 作り直しを区別できる `Warning` ログを 1 行足す。
  - 実 OpenD 不使用の単体テスト（偽 OpenD）で、陽性・陰性の対照つきに固定する。
- 対象外
  - **新しい実装 ADR は起こさない。** 決定は IADR-0327 が既に持っており、本作業はその適用先を広げるだけである
    （IADR-0327 のフォローアップ欄が「別 issue で追う」と名指ししていた作業そのもの）。同 IADR へ
    日付つき追記ブロックで消化を記録する。
  - **fail-safe の変更**。OpenD が不達の間は従来どおり例外を送出し、`MoomooHistoricalBarSource` が
    銘柄ごとの欠測として記録する（IADR-0157 決定1）。例外の型・分類を変えない。
  - **Program.cs の DI 登録**。ファクトリはコンストラクタの**任意引数**（既定＝本番実装）とし、登録は触らない。
  - `RequestHistoryKLQuota`（取得枠の照会）。IADR-0157 決定1 のとおり実装しない。
  - **実 OpenD での再現・検証**（稼働クラスタへ触らない）。

## 設計

### 1. SDK の接続オブジェクトに薄いシームを挟む（`IMoomooQotConnection` / `IMoomooQotConnectionFactory`）

`Moomoo.OpenApi.MMAPI_Qot` は継承で差し替えられない（IADR-0327 が `MMAPI_Trd` で実測したのと同じく
メソッドが virtual でない）ため、**本クラスが実際に使う 8 メンバだけ**を写したインターフェースを挟む。

```
SetClientInfo / SetConnCallback / SetQotCallback / SetRsaPrivateKey
InitConnect / RequestHistoryKL / Close / Dispose
```

🔴 **SDK 非依存のポートではない**——protobuf 型（`QotRequestHistoryKL.Request`）はそのまま通す。
SDK 非依存の境界は既存の `IMoomooHistoryKLineClient` が持っており、**二重に作らない**
（IADR-0327 決定1 と同じ理由）。**使っていない SDK メンバを「将来のために」足さない。**

本番実装 `MMApiQotConnectionFactory` は `new MMAPI_Qot()` を 1 つ包むだけで、状態も判断も持たない。

### 2. 接続試行が失敗したら、次の `InitConnect` の前に接続オブジェクトを作り直す

```
_connection      : 現在の接続オブジェクト（作り直しで差し替わる。volatile）
_connectionStale : 直前の接続試行が失敗した／切断された＝作り直しが要る
```

`EnsureConnectedAsync`（`_connectGate` の内側）:

1. `_connectionStale` なら `RecreateConnection()`（**先に新しい方を差し替えてから**旧 `Close()` → `Dispose()`。
   `SetClientInfo` / `SetConnCallback` / `SetQotCallback` / RSA 鍵を配線し直す）。
2. `_connectTcs` を張り直して `InitConnect`。
3. `finally` で **`_connected` が立っていなければ `_connectionStale = true`**、`_connectTcs = null`。
   `InitConnect` が `false` を返した経路（例外を直接投げる）もキャンセルも、これで一様に拾う。

`OnDisconnect` でも `_connectionStale` を立てる（一度つながった後に落ちた場合も同じ固着に入り得る）。

RSA 秘密鍵は**コンストラクタで 1 度だけ読んで内容を保持**し、作り直しのたびに再適用する
（作り直しのたびにファイルを読み直すと、鍵の差し替え中に失敗する経路が増える）。**ログへ出さない。**

送信側（`RequestUsDailyKLinesAsync`）は IADR-0327 と同じく **1 操作の中でローカルへ受けてから使う**
（採番と送信が別インスタンスへ跨がらない）。

### 3. 作り直しを区別できるログを 1 行足す

```
OpenD（相場）接続オブジェクトを作り直しました（直前の接続試行が失敗／切断されたため）。opend:11111 通算作り直し=1
```

`Warning`。**ホスト・ポート・通算回数のみ**（秘匿情報＝RSA 鍵・銘柄は出さない）。
「作り直しても繋がらない（＝OpenD が本当に落ちている）」と「作り直しに入っていない（＝別の欠陥）」を
ログだけで切り分けられるようにする。

### 4. fail-safe は変えない

OpenD が不達の間は**従来どおり同じ例外**（`InvalidOperationException` / `TimeoutException`）を送出し、
`MoomooHistoricalBarSource` が銘柄ごとの欠測として記録して他銘柄を続行する（IADR-0157 決定1）。
作り直しは**接続確立の前段**でのみ起き、**取得そのものの再試行は 1 度も増やさない**
（作り直しの回数は呼び出し回数を超えない＝1 呼び出しにつき高々 1 回）。

### 採らなかった案

| 案 | 却下理由 |
| --- | --- |
| 新しい IADR を起こす | 決定は IADR-0327 が持っており、本作業は**適用先を広げるだけ**である。同じ決定を 2 つの番号で持つと、片方だけが更新される |
| 発注経路とシームを共有する（`Shared` へ出す） | サービス境界を跨ぐ横断参照になる。`MoomooBarDataPreflight` が発注経路の `MoomooPreflight` と実体を共有していないのと同じ理由（BarDataOptions.cs のコメントが明記） |
| コールバックの `MMAPI_Conn client` 引数で世代を突き合わせる | IADR-0327 選択肢 C と同じ。**SDK が何を渡すかを実 OpenD 無しに確認できない**ため採らない |
| `IMoomooHistoryKLineClient` 自体を DI で作り直す | 接続だけ作り直せば足りる（IADR-0327 選択肢 E と同じ） |

## 走査した母集合（`.claude/rules/traceability.md` §是正・追随の母集合の取り方 規則 1〜8）

追跡下の全ファイルを `git grep -l` で軸ごとに走査した（拡張子で絞らず・行フィルタを継がず・生の出力で判断）。

| 軸 | 検索語（誤りの側／変更で意味が変わる側から引く） | ヒット |
| --- | --- | --- |
| 1 | `MMAPI_Qot` | 4 ファイル（コード 1・記録 3） |
| 2 | `MMSPI_Qot` | 6 ファイル（コード 1・記録 5） |
| 3 | `MMApiMoomooHistoryKLineClient` | 13 ファイル |
| 4 | `InitConnect` | 14 ファイル |
| 5 | `EnsureConnectedAsync` | 10 ファイル |
| 6 | `_connectTcs` | 4 ファイル |
| 7 | `同型の欠陥`（**本変更で新たに誤りになる自分の記述**を引く軸。#732 の記録が本経路を「射程外・別 issue」と名指ししている） | 6 ファイル |

**規則 8（自己参照）**: 本仕様書自身が軸 1〜7 の検索語を含む。上表の件数は**本ファイルを追加する前**の
`git grep -l` の実測であり、コミット後は各軸に本ファイルが 1 行加わる
（例: 軸 1 は 4 → 5、軸 3 は 13 → 14）。値はコミットで固定する。

| 箇所 | 扱い |
| --- | --- |
| `backend/Services/BacktestService/Infrastructure/ExternalServices/MMApiMoomooHistoryKLineClient.cs` | **変更**（本件の実装点） |
| `backend/Services/BacktestService/Infrastructure/ExternalServices/IMoomooQotConnection.cs` | **新規**（シーム＋本番実装＋ファクトリ） |
| `backend/Services/BacktestService/Tests/Infrastructure/ExternalServices/MMApiMoomooHistoryKLineClientReconnectTests.cs` | **新規**（再接続の陽性・陰性） |
| `.ai-context/adr/IADR-0327_*` §フォローアップ ＋ `.ai-context/adr/README.md` の IADR-0327 行 | **追記**（`［2026-09-11 追記 / #743］`）。「`MMApiMoomooHistoryKLineClient` に同型の欠陥が残る／別 issue で追う」が本作業で消化されたため、**残したまま消化を併記する**（本文プロズは書き換えない） |
| `docs/tests/FR-15_backtest-tests.md` | **追記**（T-15-95 の行 ＋ trace ブロックの `specs` / `issues`）。テスト仕様書は FR-15 の必須仕様書であり、追加したテストを表へ載せないと対応表が実態と食い違う |
| `backend/Services/BacktestService/Program.cs` | **据え置き**: コンストラクタの追加引数は任意（既定＝本番実装）であり、DI 登録は 1 行も変わらない |
| `backend/Services/BacktestService/Infrastructure/ExternalServices/BarDataOptions.cs` | **据え置き**: 構成項目は増やさない（作り直しに設定は要らない） |
| `backend/Services/BacktestService/Infrastructure/ExternalServices/MoomooHistoricalBarSource.cs` | **据え置き**: 欠測の記録・レート自制は不変（例外の型も分類も変えない） |
| `backend/Services/BacktestService/Tests/.../MMApiMoomooHistoryKLineClientMappingTests.cs` | **据え置き**: 静的な写像のテストであり接続に触れない（緑のままであることを検証する） |
| `docs/functional/FR-15_backtest.md`（「SDK 依存は `MMApiMoomooHistoryKLineClient` に閉じる」） | **据え置き**: シームも同じ SDK 依存の内側（同一サービスの Infrastructure）にあり、記述は真のまま |
| `docs/blocked-tasks.md`（同上の言及） | **据え置き**: A-3 の未確認 2 点は本変更で解消しない |
| `.ai-context/adr/IADR-0157_*` / `IADR-0211_*` / `IADR-0153_*` | **据え置き**: 決定（SDK 隔離／見送り／fail-closed）は本変更で変わらない |
| `backend/Services/OrderExecutionService/**`（`MMAPI_Trd` 側一式） | **据え置き**: #732 で完了済み。本 PR は触らない（ファイル領域を交差させない） |
| `.ai-context/specs/20260806_382_*` / `20260805_342_*` / `20260829_w11s4d_*` / `20260902_397_342_*` / `20260910_732_*` / `20260715_13_*` / `20260828_331_*` | **除外**: 凍結記録（point-in-time）。当時の記述をいま直すと史実と食い違う |
| `backend/Shared/AiStockTrading.Shared.Contracts/Ports/BrokerUnavailableException.cs` ほか軸 4 の残り | **据え置き**: 発注経路の分類であり、相場経路は本例外を使わない |
| `deploy/opend/k8s/rsa-secret.example.yaml` | **据え置き**: 鍵の配置手順。接続の張り直しとは無関係 |

## 受け入れ基準

- [ ] 接続試行が失敗した後、次の `RequestUsDailyKLinesAsync` が**新しい接続オブジェクト**を生成し、
      偽 OpenD の接続完了通知で**接続に成功して K 線を返す**（＝Pod を入れ直さずに回復する）
- [ ] 陰性対照: 接続が失敗し続ける間は毎回同じ例外で落ち、**無限ループにならず**、
      作り直しは**呼び出し回数を超えない**（1 呼び出しにつき高々 1 回）
- [ ] 陰性対照: 接続できない間は**履歴 K 線の要求が偽 OpenD へ 1 件も届かない**
- [ ] 作り直しを区別できるログ（`Warning` 1 行）が出る。ホスト・ポート・回数のみで秘匿情報を含まない
- [ ] 既存テストが緑のまま（`dotnet test backend/backend.slnx`）。DI 登録は変更しない
- [ ] 変異検査: 作り直し（`RecreateConnection` の呼び出し）を外すと陽性テストが赤くなる

## テスト方針

xUnit v3 ＋ AwesomeAssertions。`IMoomooQotConnectionFactory` にフェイクを差す（**実 OpenD 不使用**）。
`ReplyTimeoutSeconds` は最小値（1 秒）ではテストが遅いため、`TimeSpan` を差せるテスト専用の
オーバーロードは作らず、**フェイクが即座にコールバックを返す**ことで待ちを避ける。
不達側は待ちが要るため `ReplyTimeoutSeconds = 1` を使う（3 回で高々 3 秒）。

| 種別 | 内容 |
| --- | --- |
| 陽性 | 1 回目はコールバックを返さない（＝タイムアウト）→ 例外。2 回目は**別インスタンス**が生成され、接続完了通知 ＋ K 線応答で**成功**する。旧インスタンスは `Close` / `Dispose` される |
| 陰性 | コールバックを一切返さないフェイクで 3 回連続。毎回例外で、作り直しは**呼び出し回数分**（各インスタンスの `InitConnect` は 1 回だけ）、ハングしない |
| 陰性 | 接続できない間は `RequestHistoryKL` がフェイクへ 1 件も届かない |
| ログ | 1 回目は「作り直しました」が出ず、2 回目に `Warning` が 1 行出る（ホスト・ポート・`通算作り直し=1`） |

## 計画書との差異

- 差異: なし。FR-15 / ADR-0023 決定5 の範囲内の欠陥修正であり、計画の変更を要さない。

## 未決事項・残余リスク

- 🔴 **実 OpenD での再現・検証は本作業では行っていない**（稼働クラスタへ触らない制約）。
  SDK が「作り直せば TCP を張る」ことは #732 の実測（プロセス入れ直し＝新しい接続オブジェクトで即回復）
  からの推論であり、IADR-0327 と同じ残余リスクを引き継ぐ。
- 古い接続オブジェクトからのコールバックが新しい試行の `_connectTcs` を完了させ得る
  （IADR-0327 選択肢 C を採らないため）。その場合も続く `RequestHistoryKL` が新しい（未接続の）
  オブジェクトで失敗して例外へ倒れる＝**収束し fail-safe を破らない**が、1 回分の無駄な試行になる。
- 進行中の送信と作り直しの競合。IADR-0327 と同じ 3 点（`volatile` 化・1 操作 1 インスタンス・
  「先に差し替えてから解放」）で窓を狭めるが、完全な相互排他は採らない。残る窓で起きるのは
  `TimeoutException` が `ObjectDisposedException` に替わることだけで、**いずれも銘柄ごとの欠測**として
  `MoomooHistoricalBarSource` が同じに扱う（発注経路と違い、取り違えによる実弾のリスクが無い）。
- ADR-0023 決定5 の未確認 2 点（取得枠の単位と回復周期／前復権と費用モデルの整合）は本変更で解消しない
  （`docs/blocked-tasks.md` A-3 のまま）。
