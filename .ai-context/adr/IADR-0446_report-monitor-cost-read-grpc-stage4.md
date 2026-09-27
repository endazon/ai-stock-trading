---
title: IADR-0446 east-west gRPC 段 4 —— 日報の方針・監視銘柄・費用統制の判定の読み取りは提供側ごとに 1 service で REST と並走させ、未確定は失敗でなく「無い」応答で運び、行の解釈と呼び出しの規則を呼び出し元ごとに 1 つにする
type: impl-adr
status: Accepted
related_ids: [NFR, FR-01, FR-02, FR-04, FR-07, FR-13, FR-15, MSP:ADR-0029, MSP:ADR-0075, IADR-0031, IADR-0095, IADR-0264, IADR-0284, IADR-0328, IADR-0331, IADR-0420, IADR-0427, IADR-0435, IADR-0440, IADR-0442, IADR-0445, IADR-0447]
author: endazon (with Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md
---

# IADR-0446: east-west gRPC 段 4（Report・MarketMonitor・CostControl の読み取り）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-27
- 決定者: Claude Code（実装）／ endazon（段の切り方は #753・段 2・段 3 の雛形）

## 起点・関連

- 関連する計画書 ID: **`MSP/ADR-0029`**（同期通信の使い分け基準。🔴 本リポの裸の `ADR-0029` は資料再編であり別物）、
  **`MSP/ADR-0075`**（移行順序＝基盤先行）。経路ごとの起点は FR-04・FR-07（日報の方針）、FR-02・FR-13・FR-15（監視銘柄）、FR-01（情報収集と費用統制）。
- 関連する実装仕様書: [`.ai-context/specs/20260927_1061_grpc-stage4-report-monitor-cost-read.md`](../specs/20260927_1061_grpc-stage4-report-monitor-cost-read.md)
- 前提: [IADR-0284](IADR-0284_east-west-grpc-scope-and-order-ruling.md) 決定 5（段の切り方）、
  [IADR-0328](IADR-0328_east-west-grpc-foundation-stage0.md)（土台）、
  [IADR-0427](IADR-0427_risk-read-grpc-stage2.md)（段 2 ＝原則 A・解釈の共有・チャネルの所有）、
  [IADR-0445](IADR-0445_audit-read-grpc-stage3.md)（段 3 ＝呼び出しの規則の共有）、
  [IADR-0031](IADR-0031_cost-poller-wiring.md)（費用統制ゲートの安全既定）、[IADR-0095](IADR-0095_watchlist-authoritative-wiring.md)・
  [IADR-0440](IADR-0440_structured-watchlist-in-decision-prompt.md)・[IADR-0435](IADR-0435_watchlist-driven-finnhub-collection-symbols.md)・
  [IADR-0442](IADR-0442_stage0-asof-watchlist-reconstruction.md)（監視銘柄の読み手ごとの扱い）
- 関連 issue: #1061（本件・段 4）、#753（段 2〜6 の受け皿・`Refs`）

## コンテキストと課題

段 4 は提供側が 3 つ（報告書・市場監視・費用統制）、呼び出し元が 2 つ（取引判断・情報収集）で、段 2・段 3 と違うのは次の 4 点である。

1. **REST の日報の方針は未確定を 404 で返す**。未確定は毎朝の平常の状態で、REST の受け手は警告も出さずに「取引しない」へ倒す。
2. **監視銘柄の読み手は 4 つあり、欠けた行の扱いが読み手ごとに違う**（定時サイクルは寛容・判断のプロンプトと情報収集は一覧ごと不明・
   当時の一覧は再構成の可否つき）。どれも #1041・#1049・#1015 で是正した規則である。
3. **費用統制の判定は「停止の欠落」と「停止していない」を分ける**（#915）。proto3 の bool の既定値 false はこの区別を消す。
4. **取引判断には段 2 の輸送がすでに居て、情報収集には gRPC の呼び出しがまだ無い**。

## 検討した選択肢

| 論点 | 案 | 評価 |
| --- | --- | --- |
| PR の分け方 | **A（採用）: 1 PR（決定 5 の「1 PR」のまま）** | 経路は 5 本・rpc 4 本で段 2（10 本・rpc 8 本）より小さい。提供側ごとに割ると呼び出し元の Program.cs を 2 本の PR が触り直列化が要る |
| | B: 提供側ごとに 3 PR | 各 PR は小さいが、取引判断・情報収集の Program.cs・索引・テスト仕様書で衝突する |
| service の数 | **A（採用）: 提供側ごとに 1 つ（`DailyPolicyRead`・`WatchlistRead`〔rpc 2 本〕・`CostStateRead`）** | REST の read サブグループ（1 つの認可ポリシー）と 1 対 1 |
| 日報の未確定 | **A（採用）: `policy` の無い応答** | 平常の状態を失敗として記録・再試行の判定へ流さない。REST の 404 と同じく警告なしで「取引しない」 |
| | B: `NOT_FOUND` | 呼び出しの規則が毎朝「照会に失敗」と警告する。倒れ先は同じでも観測が嘘になる |
| 監視銘柄の解釈 | **A（採用）: REST のアダプタの解釈を `internal static` に切り出し、gRPC は同じ nullable の行へ写す** | 4 つの読み手の規則がそれぞれ 1 箇所に残る（段 2 決定 5 と同じ） |
| 呼び出しの規則 | **A（採用）: 呼び出し元サービスごとに 1 つ（取引判断は段 2 の輸送から `TradeDecisionGrpcCalls` へ切り出し、情報収集は `InformationCollectionGrpcCalls` を新設）** | 同じサービスの中で同じ規則を 2〜3 つ書かない（段 3 の `ReportGrpcCalls` と同じ） |
| | B: サービスを跨いで共通化 | `*.Client` を廃した裁定（IADR-0264 決定 1）に反する |

## 決定

### 決定 1 — 段 4 の射程は「決定 5 の 3 ルート（射程表の 3 本）＋ 表の後に追加された 2 本」。Notification は段 5

着手時に母集合を引き直すと（作業仕様書の軸 1〜4）、決定 5 の 3 ルートの呼び出し元は射程表の 3 本（取引判断の日報の方針・監視銘柄、
情報収集の費用統制）に、表の後に追加された 2 本 —— 取引判断の当時の監視銘柄（`GET /monitor/watchlist/as-of`・#1049）と情報収集の
監視銘柄（`GET /monitor/watchlist`・#1015）—— を加えた 5 本である。どちらも同じ提供側の OwnerOrService の読み取りで、段 5・6 に入る場所が無い。

- **Notification の 3 クラス（報告書の review・方針の改訂・監視銘柄の入れ替え案の適用）は段 5**: owner マップ機密クライアントのトークンで呼び、
  同じクラスに OwnerOnly の書き込みを持つ（段 2 の Notification `stage-gate` と同じ扱い。IADR-0427 決定 1）。
- **BFF の中継は射程外**（north-south）。

### 決定 2 — 提供側は各 1 service。REST と同じサービス・同じ認可・同じ入力の検証

`DailyPolicyReadGrpcService`（報告書）・`WatchlistReadGrpcService`（市場監視）・`CostStateReadGrpcService`（費用統制）。REST の read サブグループと
**同じ**サービス・純関数を呼び、認可はクラス属性の `OwnerOrService`。REST 面の振る舞いは変えていない。

- 当時の監視銘柄の時刻の検証（オフセット必須）は REST のエンドポイントから `GetWatchlistAsOfEndpoint.TryParseAt` に切り出して共有し、
  誤りは `INVALID_ARGUMENT`（REST の 400 と同じ文言）。
- 履歴の照会（OwnerOnly）・設定の書き込みは gRPC に出さない。
- 各 Program.cs は 2 行（`AddAiStockTradingGrpcListener` と `MapGrpcService`）。`Grpc:Port` 未設定なら h2c は立たない。

### 決定 3 — 線上表現（原則 A）

| 契約 | 線上 | 受け手の写し |
| --- | --- | --- |
| 日報の方針 | `DailyPolicyRecord policy`（message ＝存在を持つ）。日付・要約は `optional string` | **`policy` が無ければ未確定**（警告なしで取引しない）。日付・要約の欠落・読めない日付は既定値で作らず「取引しない」（契約の食い違いとして Error） |
| 監視銘柄の行 | 銘柄は `optional string`、市場は `MARKET_UNSPECIFIED = 0` を持つ列挙 | 銘柄の欠落は null、市場の未指定・未知は null（**名前で**写す。C# の 0 ＝日本は線上で 1）。その後は REST と同じ解釈 |
| 当時の一覧 | 可否は `optional bool`、一覧は存在を持つ入れ物 `WatchlistItems`、理由は `optional string` | 可否の欠落・入れ物の欠落は REST と同じ nullable の形へ写し、REST と同じ解釈（再構成できない・理由つき） |
| 費用統制の判定 | 停止は `optional bool`、倍率は `optional string`（不変文化の 10 進） | 欠落・空は null（0・false にしない）。REST と同じ写し（#915）。読めない倍率は Normal |

- **提供側**は C# の null を設定しない・在る false と在る 0（Halted の倍率）は設定する。
- **message 名を送り手の型名にしない**（`ConfirmedDailyPolicy` → `DailyPolicyRecord`、`MonitoredSymbol` → `WatchlistItem`、
  `CostControlDecision` → `GetCostStateResponse`。IADR-0420 決定 3 の ⑤）。

### 決定 4 — 解釈は REST のアダプタと共有する

| 呼び出し元 | 共有した解釈 |
| --- | --- |
| 取引判断 | `HttpWatchlistProvider.ToCycleWatchlist` / `ToAuthoritativeWatchlist`、`HttpAsOfWatchlistSource.Interpret` / `Unavailable` / `WireInstant` |
| 情報収集 | `HttpMarketMonitorWatchlistReader.Interpret`、`HttpCostControlGate.Map` |

日報の方針は写しが自明（日付と要約）なので共有する解釈を持たない。切り出しで判定は変えていない（REST の要求・応答の読み方はバイト等価）。

### 決定 5 — 呼び出し元の切替は段 2 と同じ規則。呼び出しの規則は呼び出し元ごとに 1 つ

| 構成キー | 呼び出し元 | 既定 | 意味 |
| --- | --- | --- | --- |
| `Reports:Grpc` | 取引判断 | 未設定＝**REST** | 日報の方針の gRPC の宛先 |
| `MarketMonitor:Grpc` | 取引判断・情報収集 | 未設定＝**REST** | 監視銘柄（取引判断は当時の一覧も）の gRPC の宛先 |
| `CostControl:Grpc` | 情報収集 | 未設定＝**REST** | 費用統制の判定の gRPC の宛先 |
| `<上記>:GrpcTimeoutSeconds` | 〃 | 5 | **試行ごとの** deadline。各 REST の HttpClient.Timeout と同値 |
| `<上記>:GrpcMaxAttempts` | 〃 | 1 | 試行回数。再試行するのは `UNAVAILABLE` / `DEADLINE_EXCEEDED` だけ |

- 宣言があれば輸送を singleton で 1 つ登録し、各ポートの工場が `GetService` で有無を見て `Grpc*` 実装を選ぶ（**BaseUrl より優先**）。
  宣言してあるのに使えない値（相対・`https`・scheme 無し）は起動時に落とす。**チャネルは輸送が所有する**（IADR-0427 決定 4）。
- **呼び出しの規則**: 取引判断は段 2 の `RiskManagementGrpcTransport` から `TradeDecisionGrpcCalls` へ切り出し、3 つの輸送（リスク管理・
  報告書・市場監視）が共有する（段 2 の公開面・構成キー・既定値・試験は不変。T-10-1058 は変更なしで緑）。情報収集は本段が最初の gRPC なので
  `InformationCollectionGrpcCalls` を新設し 2 つの輸送が共有する。
- helm: 既定描画と values-local の描画は変えない（`grpcPort` も `*__Grpc` も置かず、values.yaml に有効化の手順をコメントで書くだけ）。

## 理由

- **並走の正が REST のまま動かない。** 既定の構成を 1 つも変えていない（helm の描画は変更前とバイト等価）。切り戻しは構成の削除。
- **平常の状態を失敗として観測しない。** 未確定を NOT_FOUND にすると、倒れ先が同じでも毎朝の警告が「照会に失敗した」と嘘をつく。
- **是正済みの規則（#915・#1041・#1049・#1015）を 2 箇所に書かない。** 輸送を差し替えても規則は 1 つのまま。

## 結果

- 良い影響: 段 5（Notification の OwnerOnly）の前に、読み取りの east-west はすべて gRPC でも読める。
- 悪い影響・トレードオフ:
  - REST のアダプタに `internal static` の解釈が増えた（取引判断 2 クラス・情報収集 2 クラス）。
  - 実配備での h2c 往復は未実測（段 1〜3 と同じ）。
  - 実効構成の自己申告（introspection の `AddPortFromBaseUrl`）と、情報収集の起動時の日次要求の見積り（`EstimateAtStartup`）は
    `MarketMonitor:BaseUrl` しか見ない。gRPC だけを宣言した構成では、実装名と見積りの前提（監視銘柄に追随するか）を誤って申告する
    （段 1〜3 と同じ種類の既存の欠落。段 6 までに輸送に追随させる）。
- フォローアップ:
  1. 段 5（Notification の OwnerOnly・owner トークンの運び方）を #753 から切る。
  2. introspection と情報収集の起動時の見積りを輸送に追随させる（段 6 までに）。

## ［2026-09-27 追記 / #1063］決定 3・4 の 2 点を IADR-0447 が改めた

PR #1062 の監査の残り（#1063）を [IADR-0447](IADR-0447_grpc-read-latent-residuals.md) で直した。本 IADR の次の 2 点は、同 IADR の決定が正である。

- **決定 3 の費用統制の行**「読めない倍率は Normal」は、**停止していない応答についてだけ**成り立つ。停止の旗が true なら、倍率が読めなくても停止を守る（REST も同じ。IADR-0447 決定 2）。
- **決定 4 の定時サイクルの読み方**（`ToCycleWatchlist`）は、市場の欠けた・値域外の行を日本として読まず**落とす**（REST も同じ。IADR-0447 決定 1）。

## 関連

- Supersedes: なし
- Superseded by: なし
