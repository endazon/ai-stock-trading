---
title: IADR-0469 Finnhub を呼ぶ名前付き HttpClient に有界の打ち切り（現在値 5 秒・情報収集 15 秒）を置き、打ち切りは各呼び出し側の既存の「取得できない」の経路へ写す（呼び出し側の停止だけは伝える）
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-01, FR-03, FR-10, FR-16, ADR-0020, ADR-0004, IADR-0068, IADR-0066, IADR-0099, IADR-0399, IADR-0064, IADR-0095, IADR-0467]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/ (FR-01 情報収集・FR-02 取引判断の入力・FR-10 時価評価)
---

# IADR-0469: Finnhub の HttpClient の打ち切りと、打ち切りの写し方（#1133）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-01
- 決定者: Claude Code（実装）。統制の値（リスク統制・取引ガードの既定値）は変えない。取得できないときの振る舞いは各呼び出し側の既存の経路のまま

## 起点・関連

- 関連する計画書 ID: FR-02（判断の価格文脈）・FR-01（情報収集）・FR-03（市場監視）・FR-10（時価評価）・FR-16（報告書の評価損益）
- 計画 ADR: ADR-0020 決定 3（情報源の欠測はソース単位で判定へ渡す）・ADR-0004（案A+ の情報源）
- 起票: [#1133](https://github.com/endazon/ai-stock-trading/issues/1133)（監査 F5 と、コードで確かめた追加の監査コメント）
- 関連する実装仕様書: [`.ai-context/specs/20261001_1133_finnhub-timeout.md`](../specs/20261001_1133_finnhub-timeout.md)
- 前提:
  - [IADR-0068](IADR-0068_live-quote-feed-finnhub-extraction.md): `FinnhubQuoteClient`（HTTP）と `FinnhubMarketDataSource`（取得できない＝null への翻訳）の分担
  - [IADR-0399](IADR-0399_monitor-position-row-tolerance.md) 決定 3: 市場監視は 1 銘柄の照会の例外（呼び出し側以外の打ち切りを含む）をその銘柄に閉じる
  - [IADR-0099](IADR-0099_current-price-context-for-decision.md): 判断は現在値が取れなければ新規建てを見送る（`CurrentPriceUnavailable`）
  - [IADR-0095](IADR-0095_watchlist-authoritative-wiring.md): サービス間の照会は短い打ち切り（5 秒）にそろえる作法

## 背景

2026-09-30 の夜、Finnhub への TLS ハンドシェイクが `unexpected EOF` で 3 回失敗した。取引判断は 1 回で 18.5 秒待ってから AAPL を見送った。
Finnhub を呼ぶ名前付き HttpClient はどれも打ち切りを持っておらず、`HttpClient.Timeout` の既定の 100 秒が効いていた。

- `"marketdata"`（取引判断・市場監視・リスク管理・報告書の 4 サービス）: `AddHttpClient("marketdata")` だけ。
- `"collection"`（情報収集）: 名前付きの登録が無く、無名の `AddHttpClient()` の既定のまま。情報源は直列に走る（`SourceFetchRunner`）。

調べると、打ち切りの**写し方**にも穴があった。`HttpClient.Timeout` の打ち切りは `TaskCanceledException`（`OperationCanceledException`）として出る。
`FinnhubMarketDataSource` は `OperationCanceledException` を一律に「停止要求」として再送出していた。このため打ち切りは呼び出し側へ例外のまま届く。

| 呼び出し側 | `OperationCanceledException` の扱い（是正前） | 打ち切りが届いたときに起きること |
| --- | --- | --- |
| 取引判断 `GetCurrentPriceSafeAsync` | `ex is not OperationCanceledException` だけを握る | 判断が例外で落ちる（`CurrentPriceUnavailable` の見送りにならない） |
| リスク管理 `QuoteRefreshService` | 巡回の外側で `OperationCanceledException` を「停止要求」として `break` | **補充の巡回が恒久に止まる** |
| 報告書 `ReportDraftService` | 握らない | ドラフト生成が例外で落ちる |
| 市場監視 `GetQuoteOrNullAsync` | 呼び出し側のトークンのときだけ再送出（IADR-0399） | その銘柄の価格欠落に閉じる（正しい） |
| 情報収集 `SourceFetchRunner` | 呼び出し側のトークンのときだけ再送出 | ソース単位の欠測（正しい） |

既定の 100 秒では打ち切りに届く前に TLS の失敗（`HttpRequestException`）が先に出ていたため、この穴は表に出ていなかった。打ち切りを短くすると表に出る。

## 決定

1. **打ち切りの値は 1 か所に置く。** 共有物 `FinnhubHttpTimeouts`（`AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData`）に、
   `Quote = 5 秒`（`"marketdata"`）と `Collection = 15 秒`（`"collection"`）を置く。各サービスの `Program.cs` はこの値を引く。
   - `"marketdata"` の 4 サービスはすべて `AddHttpClient("marketdata", c => c.Timeout = FinnhubHttpTimeouts.Quote)`。
   - 情報収集は `AddHttpClient("collection", c => c.Timeout = FinnhubHttpTimeouts.Collection)`（無名の `AddHttpClient()` は残す）。
2. **打ち切りは「取得できない」へ写す。** `FinnhubMarketDataSource` は `OperationCanceledException` を 2 つに分ける。
   呼び出し側のトークンが取り消されていれば再送出する（停止要求）。そうでなければ打ち切りであり、Warning を出して `null` を返す。
   これで 4 サービスの呼び出し側は、既存の「取得できない」の経路（判断は `CurrentPriceUnavailable`、市場監視はその銘柄のスキップ、
   リスク管理は前回値または 0、報告書は前回値）へ落ちる。判断のサービス本体と各呼び出し側の分岐は変えない。
3. **情報収集は写し方を変えない。** `SourceFetchRunner` と各情報源（Finnhub の企業ニュース・Google ニュース）は既に呼び出し側のトークンで分けており、
   打ち切りはソース単位の欠測になる。値だけを足し、挙動を試験で固定する。
4. **再試行・回路遮断（Polly / `AddStandardResilienceHandler`）は足さない。** 本リポには resilience handler が 1 つも無い（母集合で確認）。
   レート制限（自制）と巡回の再実行が既にあり、再試行は Finnhub の無料枠を余計に消費する。必要になったら別の決定で足す。
5. **接続（TLS ハンドシェイク）だけの打ち切り（`SocketsHttpHandler.ConnectTimeout`）は分けない。** `HttpClient.Timeout` は接続・TLS・応答本文の読み込みを含む要求全体に効く。
   今回の失敗（ハンドシェイク中の切断）は 5 秒（判断の経路）で打ち切られる。

### 値の根拠

| 値 | 呼び出し側の期限 | 根拠 |
| --- | --- | --- |
| `Quote` 5 秒 | 判断: 期限の設定は無い（LLM の打ち切り `LlmGateway:TimeoutSeconds` の手前で 1 回）。市場監視: 巡回 60 秒（既定）。リスク管理の補充: 60 秒（既定）。報告書: ドラフト生成ごと | `/quote` の平常の応答は 1 秒未満。他のサービス間の照会（`"risk"`・`"monitor"`・`"reports"`）と同じ 5 秒にそろえる。判断の 1 銘柄は最大 5 秒で見送りに倒れる（観測の 18.5 秒・既定の 100 秒より短い） |
| `Collection` 15 秒 | 巡回 1,800 秒（既定）。情報源と銘柄は直列 | 同じクライアントで SEC EDGAR・FINRA などの本文が大きい情報源も読む（要求全体に効くため本文の読み込みも含む）。判断の経路ではないので `Quote` より長く取るが、100 秒は待たない |

## 窓の表（規則 11）

窓 = 「Finnhub の応答時間」と「打ち切り」と「呼び出し側の期限」の時間差。プローブは 2 つ。

- **増える側**: Finnhub の応答時間が伸びて打ち切りを越える（応答が返らない）。期待: 打ち切りの時間で「取得できない」になり、例外で判断・巡回を落とさない。
- **減る側**: 呼び出し側の期限が打ち切りより先に来る（停止・取り消し）。期待: 「取得できない」に化けず `OperationCanceledException` が伝わる。

| 形 | 増える側（応答が返らない） | 減る側（呼び出し側が先に止める） |
| --- | --- | --- |
| 後の端だけを見る（打ち切りを置き、`OperationCanceledException` は一律に「取得できない」へ写す） | ✅ 5 秒で見送り | ❌ 停止が「取得できない」に化ける（停止が遅れ、見送りが記録される） |
| 前の端だけを見る（是正前の写し方：`OperationCanceledException` は一律に再送出。打ち切りの有無に関わらず） | ❌ 打ち切りが例外のまま判断・補充へ届く（補充は巡回が止まる）。打ち切りが無ければ 100 秒待つ | ✅ 停止は伝わる |
| **両端を突き合わせる（採用。打ち切りを置き、呼び出し側のトークンが取り消されていれば再送出、そうでなければ「取得できない」）** | ✅ | ✅ |

## 結果

- 試験: T-10-1860〜T-10-1868（`docs/tests/FR-10_risk-controls-tests.md` の該当節）。
- 自己変異: 打ち切りの値を外す・呼び出し側の停止を握る・打ち切りを例外のまま返す、のいずれも試験が赤になる（作業仕様書に記録）。

## 残余リスク

- TLS の EOF そのものの原因（Finnhub 側・経路・ノードの egress）は本決定の外。頻度は egress 側で観測する（issue の提案 2）。
- 判断は 1 銘柄で最大 5 秒（＋自制の待ち）待つ。複数銘柄の定時サイクルでは銘柄数に比例する（是正前は 1 銘柄で最大 100 秒）。
- 情報収集の 1 巡回は最悪「銘柄数 × 要求数 × 15 秒」まで伸び得る（Finnhub の対象銘柄は巡回あたりの上限で抑えられている。計画 ADR-0043 決定 2 (b)）。
- バックテストの日足（Stooq。`BarDataHttpClientName`）と取引判断の為替（`"fx"`。日銀・FRED）にも打ち切りが無いが、Finnhub ではないため範囲外とした。
