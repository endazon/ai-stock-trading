---
title: IADR-0144 moomoo PoC（ADR-0019）の実測に基づき空売り経路の実装方針を確定する — 発注は Sell・照会は実弾ヘッダ・借株料閾値は発火しない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-12, FR-15, FR-20, ADR-0002, ADR-0016, ADR-0019, ADR-0023, IADR-0111, IADR-0118, IADR-0119, IADR-0138]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
---

# IADR-0144: moomoo PoC の実測に基づき空売り経路の実装方針を確定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-05
- 決定者: endazon（利用者・実機実行）/ Claude Code（起案・probe 作成）

## 起点・関連

- 計画書 ID: **ADR-0019**（PoC 6 項目・期限 2026-08-31）、**ADR-0016**（空売り段階解禁。決定 3・4・7・8 が PoC 結果に条件付き）、**ADR-0023**（米国株日足 OHLC 履歴源）
- 対象 Issue: [#342](https://github.com/endazon/ai-stock-trading/issues/342)（PoC 本体）/ [#382](https://github.com/endazon/ai-stock-trading/issues/382)（履歴源）
- 作業仕様書: [20260805_342_moomoo-poc-plan](../specs/20260805_342_moomoo-poc-plan.md)（probe の出力を全て収録）
- 関連 IADR: [IADR-0111](IADR-0111_broker-tier-selection.md)（provider × environment の直交 2 軸）・[IADR-0118](IADR-0118_broker-position-reconciliation.md)（ブローカ実ポジション突合）・[IADR-0119](IADR-0119_decision-derived-close.md)（AI の Sell が新規ショート扱いになる問題）・[IADR-0138](IADR-0138_stage0-drawdown-tightening.md)（Stage 0 DD 厳格化・実効が #382 に依存）

## コンテキストと課題

ADR-0019 は「PoC の結果次第で ADR-0016 の決定 3・4・7・8 を見直す」とし、各項目に**不成立時の帰結**を併記した。実装は結果を待つ状態にあり、空売りの再実装（#329・#331）が塞がっていた。

2026-08-05、実 OpenD（常駐中・ログイン済み）に対して読み取り中心の probe を実行した。**probe は `moomoo-api` 10.8.6808（本番コードと同一の SDK）を用い、クラスタ内の使い捨て Pod として動かした**（`moomoo-rsa` Secret をマウントできるため秘密鍵をクラスタ外へ取り出さずに済む）。発注を伴う操作と実弾口座への照会は**利用者が実行**した。

**本 IADR の起草過程で、実装方針の判定を 3 回訂正している。** 経緯は作業仕様書に残した。とくに次の 1 件は方法論上の教訓として本 IADR に引く。

> **SDK の型定義に列挙値が存在することは、ブローカがその値を受理することを意味しない。**
> 起案時、`TrdSide` に `SellShort` / `BuyBack` があることを根拠に「空売り専用区分が存在する＝項目 2 は到達可能」と判定したが、
> 実際に発注すると `Order side must be BUY or SELL.` で拒否された。**静的な契約からの推論は実測の代わりにならない。**

## 決定

### 決定 1: 空売りの発注は `TrdSide_Sell`（建玉なし）で行う。`SellShort` / `BuyBack` は使わない

moomoo の約束事は「**建玉を持たない `Sell` が新規ショート、ショート保有中の `Buy` が手仕舞い**」である（実測。`SellShort` は拒否される）。

- **現行の `MoomooSide { Buy, Sell }` の 2 値を維持する。** SDK の列挙に合わせて 4 値へ広げない——ブローカが受理しない値を扱う型になる。
- 起案時に「2 値では空売りを表現できないので #331 へ引き継ぐ」とした指摘は**取り下げる**。

### 決定 2: 空売りと通常売却が API 上で区別されないことを、統制の前提として明記する

**発注時点で「新規ショートか手仕舞いか」を決めるのは我々の側であり、ブローカは建玉の状態から暗黙に解釈する。** 取り違えれば、手仕舞いのつもりの `Sell` が**裸の新規ショート**になる。

- [IADR-0119](IADR-0119_decision-derived-close.md)（#298）が是正した「AI の Sell が新規ショート扱いになる」問題は、**ブローカ側の実仕様として実在することが実測で確認された**。推測に対する防御ではない。
- したがって [IADR-0118](IADR-0118_broker-position-reconciliation.md)（ブローカ実ポジションとの突合）は**空売り解禁の前提条件**として扱う。建玉状態の取り違えが直接「裸の売り」を生む。

### 決定 3: 借株可否・借株料・証拠金率の照会は**実弾口座のヘッダ**で行う

`TrdGetMarginRatio` は **SIMULATE 口座では使えない**（`Get Margin Trading Data does not support Stocks in US Market`）。同じ市場・同じ銘柄が**実弾口座では成功する**。

| 値 | フィールド | 実弾 | SIMULATE |
| --- | --- | --- | --- |
| 借株可否 | `IsShortPermit` | ✅ | ❌ |
| 借株料 | `ShortFeeRate` | ✅ | ❌ |
| 借株在庫 | `ShortPoolRemain` | ✅ | ❌ |
| 空売り初期/維持証拠金率 | `ImShortRatio` / `MmShortRatio` | ✅ 50 / 30 | ❌ |

**エラー文言は市場を理由にしているが、実際の制約は口座環境にある。** 額面どおり読むと「空売りは恒久的に不可」という誤った結論に至る。切り分けなしにこの文言を根拠にしてはならない。

- **帰結として、SIMULATE で発注しながら実弾口座へ照会する構成になる。** これは [IADR-0111](IADR-0111_broker-tier-selection.md) の「provider × environment の直交 2 軸」で環境が 1 つに定まる前提を崩す。**照会用の環境と発注用の環境を別に持つ**必要があり、実装時に IADR-0111 を部分改定する。
- **実弾ヘッダでの照会は読み取り専用に限る。** `PlaceOrder` / `ModifyOrder` を実弾環境で呼ぶ経路は作らない。既存の閂（`LiveTradingGate.LiveTradingReleased = false` ほか）は一切変更しない。

### 決定 4: 一次ゲートは `IsShortPermit` とする。借株料の 20% 閾値は「発火しない統制」として扱う

空売り可能な 5 銘柄（AAPL / MSFT / GME / MSTR / RIOT）すべてで `ShortFeeRate=1.5` だった。`ShortPoolRemain` は 120 万〜2,600 万と 20 倍以上開くのに料率は動かない。一方 AMC・SPCE は `IsShortPermit=False` / `ShortPoolRemain=0` / `ImShortRatio=100` を返す。

**API は銘柄を区別するが、借株料は一律である公算が高い。**

- **`IsShortPermit=False` を拒否の一次条件とする。** これは実測で機能する。
- **ADR-0016 決定 3 の「年率 20% を超える銘柄への空売りは拒否する」は実装するが、この情報源では発火しない見込みである。** 閾値判定を落とすのではなく、**発火しないことを既知として記録し計画へ環流する**（落とすと料率が銘柄別になったときに無防備になる）。
- `1.5` の単位（年率 1.5% か否か）は未確定である。**単位が確定するまで、この値を費用計算（FR-17 の採算評価）へ流し込まない。**

### 決定 5: `TrdGetMarginRatio` の照会結果はキャッシュし、失敗時に即時リトライしない

レート制限を実測した — **30 秒あたり 10 回**（`Maximum 10 times per 30 seconds`）。

**失敗した照会も枠を消費する。** SIMULATE 側の失敗 3 回が同じ 30 秒窓に入り、8 銘柄目が制限で落ちた（3 + 8 = 11 回）。**エラー時に素朴なリトライを入れると、リトライ自体が枠を食って連鎖的に失敗する。**

- 決定 3 が「発注前に照会する」ことを求める以上、監視銘柄数 × 判断頻度が上限に触れ得る。**銘柄単位のキャッシュを前提に設計する。**
- 失敗時はバックオフを置く。**照会不能は「空売りしない」へ倒す**（ADR-0016 決定 3 の fail-safe と同じ向き）。

### 決定 6: 米国株日足 OHLC 履歴源として moomoo を第一候補にする

`QotRequestHistoryKL` で AAPL の日足が **2006-07-24 まで**遡れた（前復権・OHLCV・1 リクエスト 1,000 件・`NextReqKey` でページング）。取得枠は `remainQuota: 300`。

[ADR-0023](../../planning/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md) は Stooq が取得不能であることと回避実装の禁止を決めたが、**代替源を定めていない**。moomoo がその答えになる。これにより [IADR-0138](IADR-0138_stage0-drawdown-tightening.md) が「実効は #382 の解決に依存する」とした条件が解ける見込みが立つ。

**ただし本決定は候補の特定までであり、採用は #382 で行う。** 未確定事項が 2 つある。

1. **取得枠の単位と回復周期**（銘柄数かリクエスト数か）。バックテストで多数銘柄を遡ると枠を使い切り得る。
2. **前復権（`RehabType_Forward`）の調整方式がバックテストの前提と一致するか。** 分割・配当の扱いが違えば成績が変わる。

## 影響

- **肯定的**:
  - 空売りの再実装（#329・#331）を塞いでいた PoC が解け、実装方針が確定する。
  - 誤った実装を 2 つ未然に防いだ——`SellShort` を使う実装（ブローカが拒否する）と、`MoomooSide` を 4 値へ広げる変更（不要かつ有害）。
  - #382 に道筋がつき、Stage 0 の合格判定が構造的に成立し得る状態になる。
- **制約 / 残余リスク**:
  - **決定 3 により環境をまたぐ構成が必要になる。** IADR-0111 の部分改定が要る。
  - **決定 4 により ADR-0016 決定 3 の統制は実質的に可否ゲートのみになる。** 計画側の裁定を要する（環流済み）。
  - **項目 5（強制買戻しの検知）は未確認のままである。** SIMULATE では原理的に発生せず（ADR-0016 決定 14）、確認できるのは受信経路の疎通までである。`BuyInBanned`（#374）の供給元は依然として無い。
  - **項目 6（長期常駐・強制アップデート頻度）は 7 日 20 時間・5 回の再起動の観測にとどまる。** 単一ノード（egress IP 安定）での観測であり、Hetzner 等へ移す場合の外挿にはならない。
  - **維持率（決定 7）は Stage 1 では検証できない。** 照会 API が SIMULATE で使えず、かつ SIMULATE の `Funds` は建玉が無いと埋まらない。ADR-0016 決定 14 の想定範囲だが、決定 14 の記述（「年率 6% 固定のため 20% を超えない」＝値は取れる）より**実態は厳しい**。

## 備考

**副産物として、計画書に出所が無かった値を 2 つ裏取りした。** ADR-0016 決定 14 は「SIMULATE はショート 初期 60% / 維持 50% と規制より厳しく」「実弾の規制値はショート 初期 50% / 維持 30%」と記載しながら根拠を示していなかった。

- 実弾: `ImShortRatio=50` / `MmShortRatio=30` を直接取得した（決定 14 の記述と一致）。
- SIMULATE: `MaxSellShort = 自己資金 ÷ (株価 × 0.6)` が 3 銘柄で成立し、**初期証拠金 60%** が導かれる（同上）。

決定 14 の「三者比較では SIMULATE だけが保守側の外れ値になる」という指摘が実測で裏付けられた。

なお `TrdGetMaxTrdQtys.MaxSellShort` は**借株の在庫情報を含まない**（上記のとおり純粋な証拠金計算である）。起案時にこれを借株可否の代替と判定したのは誤りであり、訂正した。**銘柄ごとに値が違うことを「銘柄別の情報を持っている」根拠にしてはならない**——株価が違えば値は違う。
