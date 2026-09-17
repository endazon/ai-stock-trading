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

### 決定 1（主）: 照会した市場と応答行の市場が一致する建玉だけを採る

- 各応答行について、**行の `TrdMarket` が既知（`HasTrdMarket` かつ `TrdMarket_Unknown`=0 以外）で、照会ヘッダの
  `TrdMarket` と異なるなら捨てる。** US ヘッダの応答に含まれる JP 建玉・JP ヘッダの応答に含まれる US 建玉は、
  もう一方のヘッダの照会で正しく採られる（SupportedMarkets は US・JP の両方を必ず照会する）ため、捨てても欠けない。
- **なぜ重複排除だけでなくフィルタを主にするか**: 「その市場の照会で得た、その市場の建玉」だけを採るのが
  照会契約（IADR-0118「全対応市場を列挙する」）の素直な読みであり、応答がヘッダを無視するという
  SIMULATE 固有の振る舞いに依存しない。重複排除だけだと、ヘッダ市場外の建玉（例: 将来 HK 等の建玉が
  US ヘッダの応答に混ざる）を取り込み続ける。
- **行の市場が不明（未設定・`TrdMarket_Unknown`）の場合は捨てない。** 判定できない行を捨てると、
  建玉があるのに「ブローカに無い」（`LedgerOnly`）と誤報し、保護逆指値ガードが建玉消滅と誤認する側へ倒れる。
  不明の行は従来どおり採り、下の決定 2 の重複排除で 1 件に畳む（市場の写像は従来どおり JP 以外を US へ倒す）。

### 決定 2（防御）: (市場, 銘柄, 方向) で重複排除する

- フィルタ後に同じ `(Market, Symbol, 方向〔Long/Short〕)` が 2 回以上現れたら**最初の 1 件だけ**を採る。
  moomoo は同一口座・同一銘柄・同一方向の建玉を 1 行に集約して返すため、正当な 2 行は存在しない。
- 方向をキーに含めるので、同一銘柄のロングとショートが同時に返る場合（信用口座）はどちらも残る。

### 抽出（SDK 非依存の純関数）

- `MoomooPositionRow`（照会ヘッダ市場・行の市場〔不明は null〕・銘柄・ショートか・数量・取得単価）を新設し、
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
| 5 | フィルタをすり抜けた同一 (市場, 銘柄, 方向) は最初の 1 件だけ採る | `同じ市場の応答に同じ建玉が2行あっても最初の1件だけ採る` |

## 検証

実行コマンドと出力は PR 本文に記す。live（稼働クラスタ）での確認は本 PR では行わない（配備後の観測で
監査 `BrokerPositionsObserved` が 1 行・乖離警告が止むことを確認する）。

## デプロイ

order-execution イメージの再ビルド＋rollout のみ。OpenD の再起動・DB 移行・Helm 値の変更は不要。
