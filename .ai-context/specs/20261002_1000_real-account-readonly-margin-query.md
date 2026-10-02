---
title: 借株可否と維持率の束の照会に限り、実弾口座（Real × Margin）のヘッダで読み取り専用の照会をする環境を足す（#1000）
type: spec
status: accepted
related_ids: [FR-10, FR-11, FR-05, FR-20, UC-06, ADR-0016, ADR-0019, ADR-0026, IADR-0482, IADR-0425, IADR-0144, IADR-0111, IADR-0473, IADR-0476, IADR-0256]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定 3 の 2026-08-06 追記・決定 14)
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md (PoC 項目 3)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・FR-20)
---

# 実弾口座のヘッダでの読み取り専用の照会（#1000）

## 背景と裁定

- #967（IADR-0425）で空売り文脈の供給元は入ったが、借株可否の照会（`TrdGetMarginRatio`）は発注と同じ SIMULATE 口座のヘッダで送るため、
  SIMULATE では失敗し（IADR-0144 決定 3 の実測）、空売りの新規建ては `BorrowUnavailable` で全件拒否のまま。
- 計画 ADR-0016 決定 3 の 2026-08-06 追記は「Stage 1 は SIMULATE で発注しながら実弾口座のヘッダで照会する、環境をまたぐ構成になる。この非対称を前提にすること」と定める。
- **裁定（2026-10-02・オーナー）**: 読み取り専用で足す。借株可否と維持率の束の照会に限り Real × Margin のヘッダを使う照会用の環境を足す。発注系は一切作らない。
  発注経路から構造的に切り離す（別の型・別の結線。発注のクライアントから到達できない）。閂（`LiveTradingGate`・起動時拒否）は変えない。既定は無効。
  監査に Real の照会であることを残し、口座 ID は伏せる。料率の単位（#342）が確定するまで空売りは通らない前提は変わらない。

## 計画との整合（着手前の確認）

| 計画 | 内容 | 本件との関係 |
| --- | --- | --- |
| ADR-0016 決定 3（2026-08-06 追記） | 照会は実弾口座のヘッダでのみ成功。環境をまたぐ構成を前提にする | **本件はこの構成を実装する**（照会だけ実弾・発注は SIMULATE） |
| ADR-0016 決定 3（照会できないなら空売りしない） | フェイルクローズ | 口座を選べない・照会の失敗・監査に残せない、はすべて「分からない」＝拒否 |
| ADR-0016 決定 3（2026-08-07 確定・料率は単位未確定の間は供給しない） | 借株が許可されても `BorrowUnavailable` | 変えない（料率は読まない） |
| ADR-0016 決定 14 | Stage 1 で検証できない統制・実弾解禁前の確認 | 本件は確認の手段（実弾ヘッダの照会）を用意するだけ。verdict の形式は変えない |
| FR-20（段階ごとの動作モード SIMULATE / 実弾の強制） | Stage 1 は SIMULATE | **発注の動作モードは SIMULATE のまま**。閂・`BrokerSelection` は 1 行も変えない |
| ADR-0002 / ADR-0019 | moomoo・PoC 項目 3（照会可否） | PoC での確認手順を下に置く |

安全統制の中核の FR（FR-10）に当たるため、機能仕様書（`docs/functional/FR-10_risk-controls.md`）とテスト仕様書（`docs/tests/FR-10_risk-controls-tests.md`）を更新する（`docs/README.md` の裁定）。

## 範囲

1. 照会クライアント `MMApiRealMarginQueryClient`（発注執行の `Infrastructure/ExternalServices/RealReadOnly/`）・狭い接続のシーム `IMoomooMarginQueryConnection`・構成 `RealMarginQueryOptions`・監査の口 `IRealReadOnlyQueryAudit`。
2. 監査イベント `RealAccountReadOnlyQueried`（Shared.Contracts）と監査サービスの写像・ハンドラ。イベント契約の基準の更新。
3. 合成起点（`Program.cs`）の結線（既定無効・有効時だけ照会ポートを差し替え・moomoo 以外で有効なら起動停止）。
4. 口座 ID の伏せ方の本体を `MoomooAccountIdRedaction` へ移す（挙動不変・発注クライアントは委譲）。
5. 試験 T-10-2060〜T-10-2066 と変異注入、アーキテクチャ試験（ソース走査）。
   ［2026-10-02 追記 / #1000・独立監査］T-10-2067（照会クライアントのログに口座 ID の全桁を出さない）を足した（既存の最大 T-10-2066 の次。`git grep -ohE "T-10-2067"` で未使用を確認）。
6. IADR-0482、機能仕様書・テスト仕様書・`blocked-tasks.md`・helm の注記の追随。

範囲外: 稼働での有効化と照会の確認（PoC 側）。維持率の束の他の欄の供給（呼び手が無い）。料率の単位（#342）。リスク管理の照会先の結線（配備の判断）。

## 設計（IADR-0482 の要約）

- 口座: `TrdGetAccList` から `TrdEnv_Real` × `TrdAccType_Margin` × `TrdMarket_US` を 1 つだけ。0 件・複数は照会しない。
- ヘッダ: `TrdEnv_Real`・選んだ口座・`TrdMarket_US`。作るのは `BuildRealHeader` の 1 か所、照会の C2S にだけ渡す。
- 切り離し: 型（実装インターフェース・メンバの型・シームの面）・DI（照会ポートとしてだけ登録。発注の型は SIMULATE のクライアントから組む）・ソース走査（照会側に発注の識別子が無い・照会側を参照するのは `Program.cs` だけ・`TrdEnv_Real` は照会側だけ）。
- 既定: `Broker:Moomoo:RealMarginQuery:Enabled` 未設定・空・false は無効。true だけ有効。他は起動停止。paper で true は起動停止。
- 監査: 照会を送るたびに 1 件（Real・`GetMarginRatio`・`****NN`・銘柄・市場・結果・時刻）。記録の失敗は例外＝答えを使わない。

## 母集合（規則 9・10。origin/develop 10a32a8e）

### 規則 9: 誤りの側の文字列で全文書を走査した

引き方: `git grep -n -E "#1000|実弾ヘッダ|実弾口座のヘッダ|照会用の環境|実弾ヘッダの照会経路は作らない|裁定待ち（#1000）" -- . ':!.ai-context/specs' ':!CHANGELOG.md'`。

| 位置 | 記述 | 扱い |
| --- | --- | --- |
| `IShortPermitSource.cs` の契約の注記 | 「実弾ヘッダでの照会経路は作らない（IADR-0425 決定2）」 | **是正**（既定は SIMULATE・有効時は実弾の照会クライアントに替わる、を追記） |
| `MMApiMoomooTradeClient.GetShortPermitAsync` の注記 | 「実弾ヘッダの照会経路は作らない」 | **是正**（本型には作らない・別の型が持つ、を追記。本型については今も正しい） |
| `docs/functional/FR-10_risk-controls.md` 規則 3 の節 | 「実弾ヘッダでの照会を足すかは裁定待ち（#1000）」 | **是正**（既定無効の設定として入った。新しい小節） |
| `docs/tests/FR-10_risk-controls-tests.md` の未検証の表・空売り文脈の節の残余 | 「#1000（実弾ヘッダでの照会の裁定）」「裁定待ち（#1000）」 | **是正**（新しい節 T-10-2060〜2066 を足し、行を追随） |
| `docs/blocked-tasks.md`（最終更新・空売りの一次ゲートの行・強制買戻しの事後推定の行） | 「#1000 の裁定待ち」 | **是正**（設定は入った・既定無効・PoC での有効化と確認が残る） |
| `deploy/helm/.../values.yaml`・`values-local.yaml` のリスク管理 `OrderExecution__BaseUrl` の注記 | 「SIMULATE では結線しても結果は変わらない（#1000）」 | **是正**（有効化したときだけ変わる、を追記。描画は不変） |
| `.ai-context/adr/IADR-0425`（論点 1・決定 2・フォローアップ） | 実弾ヘッダは #1000 へ | **凍結記録のため本文は書き換えない**。索引（README）の IADR-0425 行へ日付つき追記を足す |
| | | ［2026-10-02 追記 / #1000・独立監査］上の扱いは実際の差分と食い違っていた。差分は索引の行に加えて **IADR-0425 本体のフォローアップの箇条へ日付つき追記（`［2026-10-02 追記 / #1000］`）を 1 行足し、frontmatter の `updated:` を進めている**。既存の本文プロズは書き換えておらず、日付つき追記は凍結記録へ足してよい形であるため差分はそのまま残し、本表の記述をこの追記で正す |
| `.ai-context/adr/IADR-0144` 決定 3（「実装時に IADR-0111 を部分改定する」） | 予告 | 凍結記録。IADR-0482 決定 6 が受ける |
| `MMApiMoomooTradeClientShortPermitTests`・`OrderFeeProbeEndToEndTests` の「実弾ヘッダの照会経路は作らない」 | 発注クライアントについての表明 | **対象外**（発注クライアントについては今も正しい。変えない） |
| `RiskManagementService` の `ShortSellOrderContext`・`ShortSellingStatusService`/`View`、frontend の SC-03 の試験 | 維持率は実弾口座のヘッダを要する | **対象外**（維持率の供給は `Funds` の欄であり本件は供給しない。記述は今も正しい） |
| `docs/blocked-tasks.md` 維持率割れの自動縮小の行（#420） | 実弾ヘッダでの読み取りが要る | **対象外**（維持率は本件で供給しない。記述は今も正しい） |
| `IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests` | 発注口座の選び方 | 対象外（発注の経路。変えていない） |
| `.ai-context/specs/` の過去の作業仕様書 | point-in-time の記録 | 対象外（凍結） |

### 規則 10: この変更で新たに誤りになる自分の記述

- `MMApiMoomooTradeClient` の `TailOnly`・`RedactAccountId`・`RedactRetMsg` の注記は「本体はここ」と読めた → 委譲になったので注記を置き換えた。
  `MoomooRetMsgRedactionTests`・`OrderFeeProbeEndToEndTests` は委譲メンバを呼ぶため変更不要（挙動不変の確認として残す）。
- `LiveTradingGate.cs` の閂の一覧（閂 2「TrdHeader を TrdEnv_Simulate に固定 … MMApiMoomooTradeClient.BuildHeader」）は**発注のヘッダ**について今も正しい。
  閂は変えない裁定のため同ファイルは触らない（実弾のヘッダは照会側の `BuildRealHeader` にだけ現れることをアーキテクチャ試験が固定する）。
- IADR-0111 の「環境は 1 つ」は発注について有効、照会について IADR-0482 が部分改定（IADR-0482 決定 6 に明記）。
- 導出値: 試験 ID は既存の最大 T-10-2039 の後、並行作業との衝突を避けて T-10-2060〜T-10-2066 を使う（`git grep -ohE "T-10-20[3-9][0-9]"` で未使用を確認）。

### 規則 11: 窓（時間差）を扱う是正か

扱わない。照会のキャッシュ（成功 60 秒・失敗 30 秒）・予算（30 秒 9 回）・相乗りは `ShortPermitQueryService` のまま変えず、本件は照会の送り先（ヘッダの取引環境と口座）だけを差し替える。
したがって「増える側／減る側」のプローブと 3 通りの形の実測表は要らない（窓の端の扱いを 1 つも変えていない）。

## 受け入れ基準と試験

| 受け入れ基準 | 試験 |
| --- | --- |
| 既定で無効（未設定・空・false は無効、true だけ有効、他は起動停止。既定では実弾のヘッダを作らない） | T-10-2062（`RealMarginQueryOptionsTests`・`RealMarginQueryCompositionTests` の既定） |
| 照会用の環境から発注系の型・API に構造的に到達できない | T-10-2063（`RealReadOnlyQueryIsolationTests`〔ソース走査・Architecture.Tests〕・`RealReadOnlyTypeIsolationTests`〔型〕・`RealMarginQueryCompositionTests`〔DI〕） |
| 実弾 × 信用 × 米国株のヘッダで照会し答えを返す | T-10-2060 |
| 監査に Real の照会であることが残り、口座 ID が伏せられる | T-10-2060・T-10-2061・T-10-2066 |
| 監査に残せなければ答えを使わない | T-10-2064 |
| LiveTradingGate の挙動が変わらない（live は照会を有効にしても起動時に止まる・paper で有効は止まる・既存の閂の試験） | T-10-2065・既存の `LiveTradingGateTests` |
| 変異を入れると赤になる | 下の変異注入の表（テスト仕様書にも記録） |
| ［2026-10-02 追記 / #1000・独立監査］照会クライアントのログ（接続・照会の成功・欄の欠落・失敗・監査の失敗・切断）に口座 ID の全桁が出ない（文言と構造化ログの引数の両方） | T-10-2067（`MMApiRealMarginQueryClientTests`。記録するロガー） |
| ［2026-10-02 追記 / #1000・独立監査］照会側の外（発注クライアント・アダプタ・Features・Hosted・合成起点）から照会側の内部へ、文字列リテラル・`nameof`・リフレクションでも到達しない。発注側のポート（`IOrderFeeQuery`・`IBrokerAccountSource`・`IReservationBrokerProbe` ほか）を照会側が識別子として使わない | T-10-2063（`RealReadOnlyQueryIsolationTests` に 1 件追加・禁止識別子の母集合を引き直し） |

## PoC で有効にする手順（稼働での確認は PoC 側）

1. 発注執行（order-execution）の env に `Broker__Moomoo__RealMarginQuery__Enabled=true` を足す（`services.order-execution.extraEnv`。helm の既定には置かない）。`broker.tier=moomoo-sim` のままにする。
2. リスク管理の `OrderExecution__BaseUrl` を発注執行へ結線する（既定は空。`ServiceAuth__*`〔trading-service〕と発注執行の `auth: true` が要る）。
3. OpenD のログイン中のユーザーに、実弾の信用口座（米国株の取扱あり）が 1 つだけあることを確かめる（0 件・複数なら照会しない＝ログに出る）。
4. 起動ログ「実弾口座の読み取り専用の照会の接続完了 accId=****NN」を確かめ、`GET /order-execution/short-permit?symbol=AAPL&market=…` で `Status` が Permitted / NotPermitted になることを確かめる。
5. 監査台帳に `RealAccountReadOnlyQueried`（要約「実弾口座（Real）のヘッダで読み取り専用の照会 … 発注はしない」）が照会ごとに 1 件あり、口座が `****NN` だけであることを確かめる。
6. 発注が SIMULATE のままであること（発注の監査・`/introspection` の階層が `moomoo-sim`）を確かめる。
7. 戻すときは env を消す（既定の無効に戻る）。

## 残余

- 料率の単位（#342）が確定するまで空売りは通らない。
- 実 OpenD での照会・口座一覧の形・解錠なしで照会が通るか・同時接続数は未検証（PoC で確かめる）。
- 維持率の束の他の欄は読まない。

## ［2026-10-02 追記 / #1000・独立監査］独立監査の指摘への対応

| 指摘 | 対応 |
| --- | --- |
| 🟡1 監査の発行は Wolverine の `MessageBus.PublishAsync` で、発注執行は永続化した送信箱（durable outbox）を持たない。「監査に残せなければ答えを使わない」はプロセス内の発行の失敗しか捕まえない | コードは変えない。IADR-0482 の残余と機能仕様書・テスト仕様書の残余リスクへ正確に記録した |
| 🟡2 ログの伏せを固定する試験が無い（接続完了のログに全桁を出す変異が緑のまま。試験は `NullLogger`） | T-10-2067 を足した。変異（接続完了のログを全桁へ）で赤、戻して緑を確認 |
| 🟡3 ソース走査がリフレクション・一部の発注側のポートを見ない | 禁止識別子に発注側のポートを足し（母集合は下）、文字列リテラル・`nameof`・リフレクション API の検査を 1 件足した。網羅ではないため IADR-0482 の盲点に「リフレクション」を足した |
| 🟡4 「取引を解錠しない」第 2 の守りは OpenD の運用に依存する（解錠は接続ごとではなく OpenD ごと・実機未検証） | IADR-0482 と機能仕様書・テスト仕様書の残余へ記録した |
| 🟢2 規則 9 の表が IADR-0425 本体を書き換えていないとしていたが差分は日付つき追記を足している | 規則 9 の表へ日付つき追記で正した |
| 🟢1 / 🟢3 | 口座の選び方の Theory に日本株だけ・口座 ID 0 の行、有効化フラグの未知の値に `on` の行を足した |

### 規則 9（🟡3 の母集合）: 発注側のポートの全数

引き方: 発注執行の本番ソースの `interface` 宣言の全数（`grep -rhoE "(public|internal)\s+interface\s+\w+"`）と、
`MMApiMoomooTradeClient`・`MoomooBrokerAdapter`・`MoomooReservationBrokerProbe` が実装するインターフェースの全数（Shared.Contracts の `Ports/` を含む）。

| 区分 | 型 | 扱い |
| --- | --- | --- |
| 既に禁止 | `IMoomooTradeClient`・`IMoomooTradeConnection`・`IMoomooTradeConnectionFactory`・`IBrokerAdapter`・`IOrderAmendmentBroker`・`IClientOrderIdBroker` | 変えない |
| **足した** | `IOrderFeeQuery`・`IProbeOutputRedactor`・`IBrokerAccountSource`・`IBrokerPositionSource`・`IBrokerAvailabilityProbe`・`IProtectiveOrderBroker`・`IAlternativeProtectiveOrderBroker`・`IClassifiedPositionSource`・`IReservationBrokerProbe`（実装 `MoomooReservationBrokerProbe`・`IndeterminateReservationBrokerProbe` も）・`IOrderExpenseSource`・`IExecutedOrderStore`・`IOrderLifecycleStore`・`IOrderReservationStore`・`IProtectiveStopOrderStore`・`IReservationReconciliationSink`・`IReconciledEntryProtection` | 発注・建玉・口座・予約・費用・発注の記録の面。照会側が使う理由が無い |
| 除外 | `IShortPermitSource`・`IMoomooMarginQueryConnection`・`IMoomooMarginQueryConnectionFactory`・`IRealReadOnlyQueryAudit` | 照会側自身の型 |
| 除外 | `IClock` | 時計（照会側も使う） |
| 除外 | `IKLineQuotaQuery`・`IDailyKLineSource`・`IMoomooQotProbeConnection`・`IMoomooQotProbeConnectionFactory` | 相場系（取引の面を持たない） |

型の上の検査（`RealReadOnlyTypeIsolationTests`）にも `IBrokerAccountSource`・`IReservationBrokerProbe` を足した（`IOrderFeeQuery` は既にあった）。

### 変異注入（独立監査の指摘分。実行ごとに `cp` で書き戻し `cmp` で一致を確認）

| 変異 | 修正前の試験 | 修正後の試験 |
| --- | --- | --- |
| A13: 接続完了のログに口座 ID の全桁を出す | 緑（`NullLogger`） | 照会クライアントの試験 12 件中 1 件赤（T-10-2067） |
| A2: 照会のクライアントに `IOrderFeeQuery` を実装する | アーキテクチャ 4 件すべて緑 | アーキテクチャ 5 件中 1 件赤（禁止識別子 `IOrderFeeQuery`） |
| A4: 発注クライアントから `Type.GetType("…MMApiRealMarginQueryClient")?.GetMethod("BuildRealHeader", …)` で実弾ヘッダを作る | アーキテクチャ 4 件すべて緑 | アーキテクチャ 5 件中 1 件赤（文字列リテラル・リフレクション） |
