---
title: "#827 SIMULATE の建玉照会で照会市場と一致する建玉だけを採り、US/JP ヘッダの二重計上による偽の建玉乖離を止める"
type: spec
status: done
related_ids: [FR-05, FR-10, UC-02, ADR-0002, IADR-0118, IADR-0124, IADR-0327]
author: endazon (with Claude Code)
created: 2026-09-18
updated: 2026-09-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: #827 SIMULATE の建玉照会の二重計上を止める

## 起点

- #827（Refs #342 / #819）。稼働クラスタで 2026-09-17 15:49Z 以降、監査
  `BrokerPositionsObserved` の本文に**同一の建玉が 2 行**（`AAPL 848 @333.52` ×2）現れ、risk-management が
  `建玉の乖離を検知しました（1 件）: AAPL/UnitedStates 台帳848≠ブローカ1696` を警告した。実際の建玉は 848 株。
- 起点 ID: FR-05（注文状態追跡の拡張としての建玉突合。IADR-0118）、FR-10（台帳は統制の入力。保護逆指値の残数量）。
  issue タイトルは `FR-12` を挙げるが、FR-12 は内蔵 `paper` モードの要求であり本変更（moomoo SIMULATE の建玉照会）に
  当たらないため、件名・コードの起点 ID は FR-05 / FR-10 とする。

## 原因

`MMApiMoomooTradeClient.GetPositionsAsync` が `SupportedMarkets`（US・JP）ごとに `TrdGetPositionList` を呼び、応答を
**そのまま連結**していた。SIMULATE 口座は照会ヘッダの `TrdMarket` を問わず同じ建玉を返すため、US 株の建玉が
US ヘッダと JP ヘッダの応答に 1 回ずつ含まれ、2 回数えられた。`ToPositionSnapshot` は市場を応答行の
`p.TrdMarket` から写すため、2 行とも `UnitedStates` になり、`PositionDriftDetector` が (Symbol, Market) で
**合算**して 1696 になった。

## 設計

### 決定 1（主）: 対応市場どうしでは、照会した市場と応答行の市場が一致する建玉だけを採る

- 各応答行について、**行の `TrdMarket` が対応市場（`SupportedMarkets` = US・JP）のいずれかで、照会ヘッダの
  `TrdMarket` と異なるときだけ捨てる。** US ヘッダの応答に含まれる JP 建玉・JP ヘッダの応答に含まれる US 建玉は、
  もう一方のヘッダの照会で正しく採られる（SupportedMarkets は US・JP の両方を必ず照会する）ため、捨てても欠けない。
- **なぜ重複排除だけでなくフィルタを主にするか**: 「その対応市場の照会で得た、その市場の建玉」だけを採るのが
  照会契約（IADR-0118「全対応市場を列挙する」）の素直な読みであり、US・JP の間の二重計上を、応答が
  ヘッダを無視するという SIMULATE 固有の振る舞いに依存せず判別できる。
- **行の市場が対応市場以外（未設定・`TrdMarket_Unknown`・`TrdMarket_HK` / `*_Fund` / `TrdMarket_Futures_Simulate_*` 等の
  既知の他市場）の場合は捨てない。** SDK（`moomoo-api` 10.8.6808 の `TrdCommon.TrdMarket`）は US・JP 以外に 19 値を持つ
  （リフレクションで確認: Unknown=0, HK=1, US=2, CN=3, HKCC=4, Futures=5, SG=6, Crypto=7, AU=8, Futures_Simulate_HK/US/SG/JP=10〜13,
  JP=15, MY=111, CA=112, HK_Fund=113, US_Fund=123, SG_Fund=124, MY_Fund=125, JP_Fund=126）。
  これらの行は US・JP の**どちらの**照会でも「照会市場と異なる」ため、捨てると両方で捨てられ実在の建玉が消える。
  消えると `ProtectiveStopGuard.RemainingPositionFor` が 0 を返して生きている保護逆指値を取り消しうる側、
  リスク管理が偽の `LedgerOnly` を上げる側へ倒れる（fail-unsafe）。**捨てない＝見える側へ倒す。**
- **対応外の市場の写像は従来どおり**（`ToPositionSnapshot` 以来の「JP 以外は `UnitedStates`」）。照会ヘッダの市場で
  埋めると、同じ行が US・JP の両照会に現れたとき別市場の 2 件になり決定 2 で畳めず二重計上へ戻るため採らない。
  結果として HK 等の建玉は `UnitedStates` の建玉として 1 件だけ現れ、台帳に無ければ `BrokerOnly` として**見える**
  （写像そのものの是正は対応市場の追加と同時に行うもので本 issue の射程外）。

### 決定 2: PositionID があれば (市場, PositionID)、無ければ (市場, 銘柄, 方向) で重複排除する

- フィルタ後に同じキーが 2 回以上現れたら**最初の 1 件だけ**を採る。
- **キーに PositionID を選ぶ理由**: `TrdCommon.Position` は `PositionID`（`HasPositionID`）を持つ＝ブローカ自身が
  付けた建玉行の識別子である。「同一口座・銘柄・方向の建玉は 1 行に集約して返る」は**実測で確かめていない仮定**であり
  （初版の記述を撤回する）、別ロットが別行で返る場合に (市場, 銘柄, 方向) で畳むと数量を取りこぼして
  `QuantityMismatch` を誤報し、保護逆指値ガードの残数量を過小に見る。識別子があるならそれで畳むのが仮定に依存しない。
  別ロットは別の `MoomooPositionSnapshot` として残り、数量の合算は従来どおり下流（(Symbol, Market) で合算する突合・ガード）が行う。
- **PositionID が無い行だけ** (市場, 銘柄, 方向) へ退避する。方向をキーに含めるので、同一銘柄のロングとショートが
  同時に返る場合（信用口座）はどちらも残る。
- **残余**: 同じ建玉が一方の応答では PositionID 付き・他方では無しで返ると 2 件に数える（キーが混ざる）。
  実測が無いため防御は足さず、下の「配備後の確認」で PositionID の有無を観測して判断する。

### 抽出（SDK 非依存の純関数）

- `MoomooPositionRow`（照会ヘッダ市場・行の市場〔不明は null〕・銘柄・ショートか・数量・取得単価・PositionID〔無ければ null〕）を新設し、
  `MMApiMoomooTradeClient.CollectPositions(IEnumerable<MoomooPositionRow>)` が決定 1・2 と符号付き数量への畳み込みを行う。
  protobuf → 行への写像（`ToPositionRow`）だけが SDK 依存として残る。
- 公開 API（`IMoomooTradeClient.GetPositionsAsync` の契約・`MoomooPositionSnapshot`）は変えない。
  **部分列挙を返さない（いずれかの市場の照会失敗で例外）契約も不変。**

## 母集合の走査（規則 1〜6・9）

### A. 同型の「市場ごとに照会して連結」パターン（誤りの側 = 連結）

走査: `git grep -n "SupportedMarkets\|MarketsToTry"`（全追跡ファイル）、`git grep -n "foreach (var .* in .*Markets"`（`backend`）、
`git grep -n "TrdGet\|TrdMarket" -- '*.cs'`、`git grep -ni "positionlist\|GetPositionList" -- ':!*.cs' ':!.ai-context'`（0 件）。

| 箇所 | 形 | 影響 | 扱い |
| --- | --- | --- | --- |
| `GetPositionsAsync`（`TrdGetPositionList`） | 市場ごとに照会し**全行を連結** | **本件** | 決定 1・2 で是正 |
| `QueryOrderAsync` → `FindOrderAsync`（`TrdGetOrderList`） | 市場を順に試し **OrderID 一致の最初の 1 件を返す** | 連結しない。同じ注文が両ヘッダで返っても最初の一致で止まる | 影響なし・変更なし |
| `FindOrderByClientIdAsync` → `FindByRemarkInCurrentAsync` / `FindByRemarkInHistoryAsync`（`TrdGetOrderList` / `TrdGetHistoryOrderList`） | 市場を順に試し **remark 一致の最初の 1 件を返す** | 同上（一致有無の判定のみ） | 影響なし・変更なし |
| `CancelOrderAsync`（キャッシュミス時の `FindOrderAsync`） | 最初に見つかった市場で取消 | SIMULATE でヘッダ市場を問わず見つかる場合、US 注文を JP ヘッダで取り消そうとする可能性はあるが、**二重計上ではなく**本 issue の射程外。発注時に `_orderMarket` を控えるため通常経路はキャッシュヒット | 変更なし（観測事実が無い。記録のみ） |
| `GetAccList`（`FetchSimulateAccountAsync`） | 市場ヘッダなし・最初の SIMULATE 口座を返す | 連結しない | 影響なし |
| 資金（`TrdGetFunds`）・証拠金率（`TrdGetMarginRatio`）・約定一覧（`TrdGetOrderFillList` / 履歴） | **呼び出し経路なし**（`OnReply_*` は no-op） | なし | 対象外 |
| 他のブローカアダプタ | `PaperBrokerAdapter` は `IBrokerPositionSource` を実装しない（IADR-0118 決定 2。照会が起きない）。OpenD の取引照会は `MMApiMoomooTradeClient` のみ | なし | 対象外 |

除外: `.ai-context/`（凍結記録の引用）、テスト内の偽 OpenD（`MoomooAdapterFakeOpenDIntegrationTests` / `MMApiMoomooTradeClientReconnectTests`）は照会する側ではなく応答を作る側のため A の母集合から除いた（B で扱う）。

### B. 建玉の消費者（`IBrokerPositionSource.GetPositionsAsync` / `BrokerPositionsObserved.Positions`）

走査: `git grep -n "GetPositionsAsync\|BrokerPositionsObserved\|message.Positions\|BrokerPositionSnapshot\b"`（`backend`、テスト除く）。

| 消費者 | 二重計上時の影響 | 本修正後 |
| --- | --- | --- |
| `BrokerPositionSnapshotService`（発行） | 2 行をそのまま発行（監査に 2 行） | 源で 1 行になる。変更不要 |
| `AuditService.AuditEntryFactory`（監査本文） | 2 行を列挙 | 変更不要 |
| `PositionDriftDetector` / `PositionDriftTracker`（risk-management） | (Symbol, Market) で**合算**し 1696 → 偽の `QuantityMismatch` | 変更不要（下記「乖離状態の自己回復」） |
| `BuyInInference`（強制買戻し推定） | ショートのみ合算。過大なブローカ数量で消失の推定を**取りこぼす**側 | 変更不要 |
| `ProtectiveStopGuard.RemainingPositionFor` | (Symbol, Market) で合算。`Math.Min(remaining, stop.Quantity)` で上限が掛かるが、建玉が一部減っていても減少を見落とす | 変更不要 |
| `MoomooBrokerAdapter.IsOperationalAsync` | null か否かのみ | 影響なし |

### C. 乖離状態の自己回復（`position_drift_state`）

`PositionDriftDecision.Decide` を読んだ: 観測の乖離が空（シグネチャ空文字）なら `ObservedSignature` /
`ConsecutiveCount` / `ReportedSignature` をすべて空・0 へ戻す。修正後の最初の観測で台帳 848 = ブローカ 848 と
なり乖離が空になるため、**持続化された状態は次の観測 1 回で解消へ戻る**（手作業の DB 是正は不要）。
既発行の `PositionReconciliationDrift`・監査記録は履歴として残る（書き換えない）。

## 受け入れ基準 → テスト

テスト: `backend/Services/OrderExecutionService/Tests/Infrastructure/ExternalServices/MMApiMoomooTradeClientPositionCollectionTests.cs`（純関数）と、
同ディレクトリの偽接続を使う照会経路テスト（`GetPositionsAsync` 全体）。

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 同じ US 建玉が US・JP 両ヘッダの応答に含まれても 1 件（848）に数える | `同じUS建玉がUSとJPの両ヘッダで返っても1件に数える`、`照会経路_SIMULATEが両ヘッダで同じ建玉を返しても1件になる` |
| 2 | JP ヘッダの応答にだけ現れる JP 建玉は残る（Japan） | `JPヘッダにだけ返るJP建玉は残る` |
| 3 | ショートの方向（負の数量）が保たれ、同一銘柄のロングとショートは別に残る | `ショートは負の数量で残り同一銘柄のロングと別に数える` |
| 4 | 行の市場が不明なら捨てず、重複は 1 件に畳む | `行の市場が不明なら捨てずに重複だけ畳む` |
| 5 | PositionID の無い行で、フィルタをすり抜けた同一 (市場, 銘柄, 方向) は最初の 1 件だけ採る | `同じ市場の応答に同じ建玉が2行あっても最初の1件だけ採る` |
| 6 | 対応市場以外の既知の市場（HK・US_Fund・JP_Fund・Futures_Simulate_US）の建玉は捨てず 1 件だけ残る（監査指摘 1） | `対応市場以外の既知の市場の建玉は捨てずに1件だけ残る`（Theory 4 件） |
| 7 | 同じ銘柄・方向でも PositionID が違えば両方残る（数量の合算は下流）（監査指摘 2） | `PositionIDが違えば同じ銘柄と方向でも両方残る` |
| 8 | 同じ PositionID が US・JP の両応答に現れたら 1 件（監査指摘 2） | `同じPositionIDがUSとJPの両応答に現れても1件に数える` |
| 9 | protobuf の PositionID が行へ写り、照会経路でも別ロットが残る。Debug の応答要約は件数と市場値の分布のみで銘柄を含まない | `照会経路_PositionIDが写り別ロットは残り応答要約をDebugに出す` |

## ［2026-09-18 追記 / #827］監査（NEEDS CHANGES）の 2 指摘と扱い

初版（同 PR の最初のコミット）に対するフレッシュな文脈の監査が 2 件の欠陥を指摘した。いずれもテスト先行（赤 → 緑）で是正した。

1. **fail-unsafe な落とし**: 初版の決定 1 は「行の市場が既知で照会市場と異なる行を捨てる」だったため、US・JP 以外の
   既知の市場の行は US・JP の両照会で捨てられ、実在の建玉が消えた（保護逆指値ガードの残数量 0 → 生きた保護逆指値の取消、
   偽の `LedgerOnly`）。→ 捨てる条件を「行の市場が対応市場のいずれかで、かつ照会市場と異なる」に狭めた（決定 1）。
2. **別ロットの併合**: 初版の決定 2 は (市場, 銘柄, 方向) で畳み、「moomoo は 1 行に集約して返す」という未実測の仮定に
   依存していた。→ PositionID があれば (市場, PositionID) で畳み、無いときだけ従来キーへ退避する（決定 2）。

## 配備後の確認

- order-execution の `OrderExecutionService.Infrastructure.ExternalServices.MMApiMoomooTradeClient` カテゴリを Debug にした状態で（Serilog の `MinimumLevel:Override` を環境変数 `Serilog__MinimumLevel__Override__OrderExecutionService.Infrastructure.ExternalServices.MMApiMoomooTradeClient=Debug` 等で一時的に与える）建玉照会を 1 回観測し、
  `建玉照会の応答要約 rows=… byQueriedMarket=… byRowMarket=… distinctCodes=… withPositionId=… collected=…` の 1 行を記録する
  （件数と市場値の分布のみ。銘柄・数量・口座番号は出さない）。これで live SIMULATE の応答行が `TrdMarket` を持つか
  （`byRowMarket` に `unset` があるか）と `PositionID` を持つか（`withPositionId` が `rows` と一致するか）を確かめ、
  決定 1 の判別と決定 2 のキー選択の前提を実測で閉じる。`withPositionId` が 0 と `rows` の間なら決定 2 の残余に当たるため起票する。

## 検証

実行コマンドと出力は PR 本文（監査指摘の是正分は PR コメント）に記す。live（稼働クラスタ）での確認は本 PR では行わない
（配備後の観測で監査 `BrokerPositionsObserved` が 1 行・乖離警告が止むことと、上の「配備後の確認」の応答要約を記録する）。

## デプロイ

order-execution イメージの再ビルド＋rollout のみ。OpenD の再起動・DB 移行・Helm 値の変更は不要。
