---
title: moomoo の日足 K 線の取得枠（単位・回復周期）と復権区分ごとの出来高の扱いを実機で確かめる、読み取り専用の 1 回実行の検証口（#1117）
type: spec
status: accepted
related_ids: [FR-02, FR-15, UC-01, ADR-0048, ADR-0023, ADR-0002, IADR-0464, IADR-0157, IADR-0300, IADR-0327, IADR-0016, IADR-0451]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0048_decision-volume-from-daily-kline-within-existing-source.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
---

# 仕様書: 日足 K 線の取得枠と復権の扱いの検証口（#1117）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（取引サイクル。判断へ渡す出来高）／FR-15（バックテスト。日足 OHLC の履歴源）
- ユースケース（UC）: UC-01（手順 3: 収集情報を文脈にして判断する）
- 画面（SC）: なし
- 関連 ADR: ADR-0048 決定 3（K 線を判断へ流すのは、取得枠の確認と分割の確認が済んでから）・フォローアップ 1・2／
  ADR-0023 決定 5（確認 1: 取得枠 `remainQuota` の単位と回復周期）／ADR-0002（moomoo 採用）
- 関連する実装ADR: IADR-0464（本作業の判断。新規）／IADR-0157（履歴 K 線アダプタ。取得枠の照会は実装していない）／
  IADR-0300（2026-09-29 追記: 注文費用照会の検証口＝本作業のお手本）／IADR-0327（接続オブジェクトのシーム）／
  IADR-0016（SIMULATE 固定）／IADR-0451（出来高は「未提供」と明示する＝暫定手段。変えない）
- 起票: [#1117](https://github.com/endazon/ai-stock-trading/issues/1117)（背景は planning#702 の裁定）

## 目的・背景

planning#702 の裁定（ADR-0048、2026-09-30）で、判断へ渡す出来高の出所は日足 K 線（前日の出来高と、その 20 日平均に対する比）になった。
ただし決定 3 は、K 線を判断へ流す前に次の 2 点を済ませることを条件にした。

1. **ADR-0023 決定 5 の確認 1**: 取得枠（`remainQuota`）の単位と回復周期。
2. **株式分割をまたいでも 20 日平均比が歪まないこと。** 前復権（`RehabType_Forward`）が出来高を調整するかは未確認。

どちらも実機の OpenD が要る。本 PR は、注文費用照会の検証口（`--probe-order-fee`・#1086）と同じ形で、
**読み取り専用で 1 回だけ撃つ検証口 `--probe-kline-quota`** を置く。実行は利用者が行う（`kubectl exec` が要る）。
判断へ出来高を流す経路（ADR-0048 フォローアップ 3）は**本 PR の対象外**である。

## 調査結果（着手前の実測）

| 問い | 結果 |
| --- | --- |
| moomoo の市況（Qot）接続を持つサービス | **BacktestService だけ**（`MMApiMoomooHistoryKLineClient`・`IMoomooQotConnection`。`grep -rln "MMAPI_Qot\|MMSPI_Qot" backend` → 本体 2・試験 1）。MarketMonitorService は moomoo の Qot を使っていない |
| BacktestService の Pod で OpenD に届くか | **届かない（構成が無い）。** `values.yaml` の backtest は `Backtest__BarData__Provider=""`（no-op）で、OpenD の host・port・RSA 鍵の env もマウントも無い（`templates/deployment.yaml` が `moomoo-rsa` をマウントするのは `order-execution` かつ moomoo 階層のときだけ） |
| OrderExecutionService の Pod で OpenD に届くか | **届く。** `broker.tier=moomoo-sim` のとき `Broker__Moomoo__OpenD__Host/Port/RsaPrivateKeyPath` が入り、`moomoo-rsa` が読み取り専用でマウントされる。`--probe-order-fee` もここで動く |
| 暗号化した Qot 接続が OpenD に受け付けられるか | **受け付けられた実績がある。** 2026-09-02 の読み取り専用 probe（作業仕様書 `20260902_397_342_moomoo-readonly-probes`）は `moomoo-rsa` を使って `opend:11111` へ暗号化接続し、`QotRequestHistoryKL` と `QotRequestHistoryKLQuota` の両方で `retType=0` を得た |
| SDK（`moomoo-api` 10.8.6808）の取得枠の照会 | **在る**（リフレクションで確認）。`MMAPI_Qot.RequestHistoryKLQuota(QotRequestHistoryKLQuota.Request)`、`C2S` = `BGetDetail`（bool）＋ `Header`、`S2C` = `UsedQuota` / `RemainQuota`（int）＋ `DetailList`（`DetailItem` = `Security` / `Name` / `RequestTime`（string）/ `RequestTimeStamp`（int64））。コールバックは `MMSPI_Qot.OnReply_RequestHistoryKLQuota`（BacktestService では no-op） |
| SDK の K 線の項目 | `QotCommon.KLine` = `Time` / `IsBlank` / `Open/High/Low/ClosePrice` / `LastClosePrice` / `Volume`（int64）/ `Turnover`（double）/ `TurnoverRate` / `Pe` / `ChangeRate` / `Timestamp`。`KLFields_Turnover` = 64。`RehabType` = None 0 / Forward 1 / Backward 2 |
| 既存の取得枠の実測 | 2026-08-05 の PoC: `usedQuota=0 remainQuota=300`。2026-09-02: 別銘柄を 1 リクエストずつ取るたびに `usedQuota` が 1 ずつ増え、失敗した要求は消費しなかった。**同じ銘柄を 2 回取る観測は無く、「銘柄数単位」か「リクエスト数単位」かは切り分けられていない**。回復周期は数分では観測できなかった |
| 既存の復権の実測 | 2026-09-02: AAPL（分割なし）で前復権と無復権の終値が約 1.2% 違った（配当の調整）。**出来高は比べていない。分割のあった銘柄も見ていない** |

## 置き場所の判断（IADR-0464 決定 1）

**OrderExecutionService に置く。** 理由:

1. **利用者が `kubectl exec` の 1 行で打てるのはこの Pod だけである。** OpenD の接続先・RSA 鍵が env とマウントで揃っているのは
   order-execution（moomoo-sim 階層）だけであり、BacktestService に置くと、打つ前に helm の値・Secret のマウント・再配備が要る
   （BacktestService の Pod に RSA 鍵をマウントすることは、本 PR の射程〔読み取り専用の検証口〕より重い配備の変更である）。
2. **お手本（`--probe-order-fee`）と同じ場所・同じ作法になる**（起動引数の分岐・Host を組まない・構成はサービス本体と同じ）。
3. **BacktestService の Qot クライアントは参照できない**（サービス間の直接参照は禁止）。よって OrderExecutionService に
   **検証口専用の最小の Qot クライアント**を置く。呼び方（Security・KLType_Day・RehabType・期間・`MaxAckKLNum`・`NeedKLFieldsFlag`・
   空白足）は `MMApiMoomooHistoryKLineClient` に合わせる。重複はこの 2 つの要求の組み立てと no-op のコールバックの並びに限られる。

採らなかった案: BacktestService に置き helm で RSA 鍵をマウントする（配備の変更が要り、利用者がすぐ打てない）／
共有ライブラリへ Qot クライアントを移す（moomoo SDK を Shared へ持ち込む大きな構造変更で、本 PR の射程外）。

## 対象範囲

- 対象（`backend/Services/OrderExecutionService/`）:
  - `Features/OrderExecution/ProbeKLineQuota/IKLineQuotaQuery.cs`（新規）— 読み取り専用ポート（メソッド 2 つ: 枠の照会・日足の取得）と SDK 非依存の結果型
  - `Features/OrderExecution/ProbeKLineQuota/KLineQuotaProbeCommand.cs`（新規）— 引数の解釈・手順・自制レート・出力・判定・終了コード
  - `Infrastructure/ExternalServices/IMoomooQotProbeConnection.cs`（新規）— SDK の `MMAPI_Qot` への薄いシーム（Qot の面だけ）
  - `Infrastructure/ExternalServices/MMApiMoomooKLineProbeClient.cs`（新規）— `IKLineQuotaQuery` の実装（`MMSPI_Qot` だけ。`MMSPI_Trd` を実装しない）
  - `Infrastructure/ExternalServices/KLineQuotaProbeComposition.cs`（新規）— 構成から照会口を組む（moomoo 以外・実弾階層は拒否）
  - `Infrastructure/ExternalServices/MoomooApi.cs`（新規）— `MMAPI.Init()` をプロセスで 1 回だけ呼ぶ共通の口。`MMApiMoomooTradeClient` の同等の処理をここへ寄せる（挙動は同じ）
  - `Program.cs` — `--probe-kline-quota` のときだけ Host を立てずに実行して終了する分岐
  - テスト: `Tests/Features/OrderExecution/ProbeKLineQuota/KLineQuotaProbeCommandTests.cs`・`Tests/Infrastructure/ExternalServices/KLineQuotaProbeEndToEndTests.cs`（新規）
- 手順書: `docs/operations/kline-quota-probe-runbook.md`（新規。trace ブロック）
- IADR-0464（新規）と索引行。
- `.claude/rules/traceability.repo.md` の計画 ADR レンジを `ADR-0001..0048` へ引き直す（ADR-0048 を trace ブロックで引くため。別紙へ実測を追記）
- 対象外: 判断へ出来高を流す経路（ADR-0048 フォローアップ 3）・BacktestService の履歴源（`MoomooHistoricalBarSource`）・helm / values・
  前復権と費用モデルの整合（ADR-0023 決定 5 の確認 2。ADR-0048 決定 3 は判断の条件にしない）

## 設計

1. **形**: order-execution のイメージに同梱する起動引数 `--probe-kline-quota [オプション]`。`Program.cs` の先頭（注文費用照会の分岐の隣）で
   判定し、Host を組まない。構成は `WebApplication.CreateBuilder().Configuration`（引数は構成へ渡さない）。
2. 🔴 **書き込み系を呼べない構造**: 検証口が受け取るのは**読み取り専用ポート `IKLineQuotaQuery`（メソッド 2 つ）の生成関数だけ**。
   実装 `MMApiMoomooKLineProbeClient` は `MMSPI_Trd` を実装せず、`IMoomooTradeConnection` / `IMoomooTradeClient` を参照しない。
   接続のシーム `IMoomooQotProbeConnection` の面は Qot の 2 要求（`RequestHistoryKL` / `RequestHistoryKLQuota`）と接続の管理だけである。
   **検証口のプロセスは発注の接続（`MMAPI_Trd`）を 1 本も作らない**（口座も選ばない＝口座番号は出力の経路に載らない）。試験で固定する。
3. **手順**（すべて 1 回ずつ。再試行しない）:
   1. 枠の照会（詳細つき）＝`before`
   2. 各銘柄（既定 `AAPL,MSFT`）: 前復権の日足を 1 回取得 → 枠の照会（詳細なし）
   3. 分割の比較（既定 `NVDA` の `2024-05-28`〜`2024-06-21`＝2024-06-10 の 10:1 分割をまたぐ期間）: 無復権・前復権・後復権の順に 1 回ずつ取得し、各取得の後に枠の照会（詳細なし）
   4. 枠の照会（詳細つき）＝`final`
   - 往復は `2 + 2 × 銘柄数 + 2 × 3`（既定 12 回）。接続が失敗したら、その後は撃たない。
4. **自制レート**: OpenD への要求の間隔を **2.5 秒以上**あける（24 回/分。30 回/分の内側）。最初の要求の前には待たない。
   待ちは差し替え可能にし、試験で回数と間隔を固定する。打ち切り（全体）は 3 分（12 往復 × 2.5 秒 ＋ 返信待ち）。
5. **直近 N 本**: 期間を `今日（UTC）− (N × 2 + 14) 日`〜`今日` にとり、`MaxAckKLNum=1000`・1 ページだけ取得し（続きの鍵は追わない。`hasMore=` で出す）、
   空白足を除いた末尾 N 本を出す。項目は日時・始高安終・出来高・売買代金（`KLFields` = High|Open|Low|Close|Volume|Turnover）。
6. **取得枠の読み方**（出力の `quota.unit.reading=`）: 取得ごとに前後の `usedQuota` の差をとり、その銘柄が「既知」（`before` の詳細一覧に在る、
   またはこの実行で既に取った）か「未知」かで分ける。未知で +1・既知で 0 がそろえば `per-security`、すべて +1 なら `per-request`
   （既知の取得が 1 件以上あるとき）、それ以外・判定材料が足りなければ `inconclusive`。取得や前後の照会が失敗した段は数えない。
   **回復周期は 1 回の実行では分からない**。`before` / `final` の詳細一覧（銘柄と要求時刻）を出し、日を置いて打ち直したときに
   古い要求が一覧から消えた時刻から読む（手順書）。
7. **復権の読み方**（出力の `split.<rehab>.*`）: 日付ごとに無復権・前復権・後復権の終値と出来高を 1 行に並べ、無復権と比べて
   終値・出来高が異なる日数を数える（`closeDiffers=n/m`・`volumeDiffers=n/m`）。分割の前の日で出来高が異なれば「出来高も調整される」、
   終値だけ異なれば「出来高は調整されない（分割の前後の足を揃える必要がある）」と読む（判定は手順書。検証口は数えるまで）。
8. **出力**（標準出力・1 行 1 事実・`key=value`）。🔴 **秘密を出さない**: 構成で与えた接続先（host:port・host）と RSA 鍵のパスを
   `<伏せ>` に置き換え、例外文だけ 6 桁以上の数字の並びを末尾 2 桁以外伏せる（注文費用照会の検証口と同じ順序・同じ規則）。
   K 線の検証口は口座を選ばないため、口座番号はそもそも経路に無い。
9. **終了コード**: 0 = すべての要求が `retType=0` ／ 1 = いずれかの要求が非成功（`retType≠0`）・接続失敗・タイムアウト ／
   2 = 引数不正・構成不正（moomoo 以外・実弾階層・RSA 鍵の未マウント）。
10. **引数の検証**: `--probe-kline-quota` が先頭で、以降は `--symbols <A,B>`（1〜5 銘柄・米国株のコード。`US.` の接頭辞は外す・重複不可）・
    `--count <1〜100>`・`--split-symbol <S>`・`--split-from <yyyy-MM-dd>`・`--split-to <yyyy-MM-dd>`（3 つそろえて指定。from ≦ to・
    期間は 120 日以内・to は今日以前）。未知のオプション・値の欠け・重複は拒否し、**照会口を組まない（接続しない）**。
11. **構成の拒否**: `Broker:Provider` が moomoo でない、または実弾階層なら照会口を組まずに終了コード 2（`LiveTradingGate` はそのまま効く）。

## 母集合（規則 9・10: 誤りの側で走査する）

- 「取得枠の照会は実装していない」と書く箇所（本 PR で OrderExecutionService に照会を足すと偽になり得る側）:
  `grep -rn "RequestHistoryKLQuota\|取得枠の照会" --include=*.cs --include=*.md .` →
  - `BacktestService/.../MMApiMoomooHistoryKLineClient.cs`（「取得枠の照会は実装していない」）: **偽にならない**（BacktestService のアダプタの記述であり、本 PR は変えない）。据え置く
  - `IADR-0157` 本文: 同上（アダプタの決定）。凍結記録であり据え置く
  - `.ai-context/specs/20260911_743_...`: 凍結記録。据え置く
  - `docs/blocked-tasks.md` §A-3 の「実装しなかったもの: 取得枠の照会」: #382 の実装の記述であり偽にならないが、**確認の道具ができたことを A-3 へ 1 行足す**（未了のまま＝実機での実行が要る）
- `MMAPI.Init()` の呼び出し（共通の口へ寄せると変わる側）: `grep -rn "MMAPI.Init" backend --include=*.cs` → OrderExecutionService 1（`MMApiMoomooTradeClient`）・BacktestService 1。
  OrderExecutionService の 1 つを `MoomooApi.EnsureInitialized()` へ置き換える。BacktestService は別サービス・別プロセスのため触らない
- `Program.cs` の早期分岐: 注文費用照会の 1 つ（#1086）。隣に 1 つ足す。2 つの旗は同時に指定できない（先に判定した方が動くのではなく、両方が在れば使い方の誤り）
- 計画 ADR のレンジ（`ADR-0001..0047`）: 計画リポの `origin/main`（`2b0716c`）を `git archive` で展開した木で `node tools/doc-checks/gen-plan-ranges.js --check` → exit 0、
  ai-stock-trading は ADR **[1, 48]・48 件・欠番なし**。`traceability.repo.md` の表記 1 箇所を `0048` へ、別紙へ実測を追記する

## 受け入れ基準

- [ ] 枠の照会・日足の取得を**それぞれ 1 回ずつ**撃つ（再試行しない）。取得の前後で枠を照会し、1 回の取得で `usedQuota` がいくつ動いたかを出す
- [ ] 日足は直近 N 本（既定 25）を日時・始高安終・出来高・売買代金で出す
- [ ] 同じ銘柄を無復権・前復権・後復権で取り、日付ごとに終値・出来高を並べ、無復権と異なる日数を出す。分割の銘柄と期間を引数で指定できる
- [ ] **発注・訂正・取消の API に届かない**（ポートの型・実装の型・偽 OpenD のいずれでも固定）
- [ ] **秘密を出さない**（接続先・RSA 鍵のパス・鍵の内容が出力に現れない）
- [ ] 要求の間隔が 2.5 秒以上（30 回/分の内側）
- [ ] 引数不正・構成不正は終了コード 2 で、照会口を組まない（接続しない）
- [ ] 起点 ID コメント（FR-02 / ADR-0048 / ADR-0023）
- [ ] 手順書（kubectl exec の 1 行・出力の読み方・枠の単位と回復周期の判定・分割の歪みの判定）

## テスト方針

- `KLineQuotaProbeCommandTests`: 偽の `IKLineQuotaQuery` で、手順の順序と回数・自制レートの待ち・出力の整形（K 線・枠・復権の並び）・
  枠の単位の読み（per-security / per-request / inconclusive）・失敗時の継続と終了コード・引数不正・構成不正・伏せ・ポートの型。
- `KLineQuotaProbeEndToEndTests`: 検証口 ＋ 構成からの組み立て ＋ 実物の `MMApiMoomooKLineProbeClient` ＋ 偽の OpenD（`IMoomooQotProbeConnection`）で、
  送信の中身（市場・コード・KLType・RehabType・期間・項目の旗・詳細の旗）・応答の写像・接続先と鍵の伏せ・paper / 実弾階層で組まないこと・
  実装の型が発注の面を持たないこと。
- 配線: `Program.cs` の分岐（旗の判定・Host より前・構成の組み立て）を試験で固定する。
- 変異（自己変異）で赤を確認: 発注系を呼べるようにする・秘密を出す・枠の前後照会を外す・自制レートを外す・再試行を足す・復権を固定する。

## 計画書との差異

無し（本 PR は ADR-0048 決定 3 の確認を実機で行う道具を足すだけで、判断へ出来高を流さない）。

## 残余（実機で確認が要る点）

- **取得枠の単位と回復周期そのもの**（検証口は観測を出すだけ。答えは利用者の実行結果による）。
- **`RequestHistoryKLQuota` の詳細一覧の `RequestTime` の時刻帯**（OpenD の表示の時刻帯か UTC か）。
- **後復権（`RehabType_Backward`）の要求が米国株で成功するか。** 失敗しても他の段は続け、終了コード 1 と `retType` / `retMsg` で分かる。
- **分割の前後で出来高が調整されるか**（本検証口の目的そのもの）。
- **Qot の要求の頻度制限の実値**（手順書では 2.5 秒間隔・連続して打たない、に留める）。
- 取得枠は稼働中のサービスと**共有**である（本検証口 1 回で最大 3〜7 銘柄ぶんを消費し得る。既定は 3 銘柄）。

## 採番

新規 **IADR-0464**（develop の最大は IADR-0462、並行 PR #1116 が IADR-0463 を使う）。
テスト ID は振らない（FR-02 は網羅裁定 #211 の必須範囲外でテスト仕様書が無い。注文費用照会の検証口〔FR-11〕と同じ扱い）。
