---
title: 発注経路の区別と識別 Runbook（paper 内蔵擬似約定 / moomoo SIMULATE）
type: runbook
status: draft
related_ids:
  - FR-05
  - FR-12
  - ADR-0002
  - IADR-0016
  - IADR-0056
  - IADR-0060
  - IADR-0111
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md"
---

# Runbook: 発注経路の区別と識別（paper ＝内蔵擬似約定 / moomoo SIMULATE ＝OpenD 経由）

> リポジトリ単位の運用 Runbook。**「約定した」と見えたものが、どちらの経路の約定なのか**を取り違えないための
> 対比・識別手順を定める。起点: [#268](https://github.com/endazon/ai-stock-trading/issues/268) /
> [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)（証券会社連携）。
>
> ⚠️ **どちらの経路も実弾ではない。** 実弾（`TrdEnv_Real`）は多重の閂で拒否される
> （[実弾切替 Runbook](live-trading-cutover-runbook.md) が単一情報源。本書「実弾には行かない」節も参照）。

## なぜこの文書があるか

検証中に「SIMULATE で発注したのに moomoo 側に何も届かない」という取り違えが起きた（#268）。
両者は**どちらも「実弾ではない」**が、**約定の主体・残高・注文履歴の所在がまったく別**である。

- `broker.tier=paper`（既定・`Broker__Provider=paper`）は **AST プロセス内蔵の `PaperBrokerAdapter`** による擬似約定で、
  **moomoo（OpenD）へは 1 リクエストも出さない**。
- `broker.tier=moomoo-sim`（`Broker__Provider=moomoo` ＋ `Broker__Environment=sim`）は **OpenD 経由で moomoo の
  模擬（SIMULATE）口座へ実際に発注**する。

paper の擬似約定を moomoo 模擬口座の残高・履歴で探しても**構造的に見つからない**。逆も同様である。

## 2 経路の対比

| 観点 | `paper`（内蔵擬似約定・**既定**） | `moomoo-sim`（moomoo SIMULATE・OpenD 経由） |
| --- | --- | --- |
| 発注先 | どこへも出さない（プロセス内で完結） | OpenD → moomoo の**模擬口座** |
| 約定の主体 | AST 自身。板寄せせず**参照価格（`OrderIntent.Price`）で即時全量約定** | moomoo 側の模擬取引エンジン（板・約定条件は moomoo が決める） |
| 外部通信 | **無し**（OpenD・moomoo への接続を一切しない） | 有り（`opend:11111` へ接続。cross-network の trade は RSA 暗号化必須） |
| 状態遷移 | 発注＝即 `Filled`（`CompletedAt` も同時刻）。不正注文（数量/価格 ≤ 0）は `Rejected` | 発注直後は `Accepted`（moomoo の `Submitted`）。約定は**後追い**で `PartiallyFilled` / `Filled` |
| 残高の所在 | AST の内部台帳のみ（risk-management の `trade_fills` を起点とする射影）。**現金・建玉は仮想** | **moomoo 模擬口座の残高・建玉が権威**。AST の台帳は現状これを取り込まない（[#270](https://github.com/endazon/ai-stock-trading/issues/270)） |
| 注文履歴の確認先 | order-execution DB の `executed_orders` / `order_lifecycle_events`、および `OrderExecuted` イベント | 上記に加えて **moomoo アプリ / OpenD の注文照会**（moomoo 側が権威） |
| 訂正・取消 | 訂正・取消の口（`IOrderAmendmentBroker`）を**paper だけが実装**する（ただし既定の即時約定では常に終端のため成立しない） | **その口を実装しない**＝訂正・取消の配管（`OrderAmendmentService`）を構成上そもそも登録しない（fail-safe・[IADR-0067](../adr/IADR-0067_order-lifecycle-telemetry.md)） |
| 前提（運用） | 無し（起動するだけ） | **OpenD 常駐＋ログイン済み**、`moomoo-credentials` / `moomoo-rsa` Secret |
| 実弾か | いいえ（そもそも外へ出さない） | いいえ（`TrdEnv_Simulate` 固定） |

> **どちらの経路でも判断・記録・報告のフローは同一**である（`PaperBrokerAdapter` と `MoomooBrokerAdapter` は
> 同じ `IBrokerAdapter` を実装し、不正注文・不達はいずれも終端 `Rejected` に倒してフローを止めない）。
> 「フローが同じ」ことは Stage 0/1 の検証価値を保つための設計であり、**約定の実体が同じという意味ではない**。

## どの設定でどちらになるか

Helm では**単一スイッチ `broker.tier`**（[IADR-0111](../adr/IADR-0111_broker-tier-selection.md)）で選ぶ。
アプリ側は provider（証券会社）× environment（取引環境）の 2 キーで受ける。環境変数化は `:` を `__` に置換する。

| `broker.tier` | 注入される env | 経路 |
| --- | --- | --- |
| `paper`（**既定**） | `Broker__Provider=paper` | 内蔵擬似約定。moomoo へは接続しない |
| `moomoo-sim` | `Broker__Provider=moomoo` ＋ `Broker__Environment=sim` ＋ OpenD 接続構成 | OpenD 経由の SIMULATE 発注 |
| `moomoo-live` | — | **実弾。未解禁につき `helm template` が `fail` する** |

- `Broker:Moomoo:TrdEnv` は**未設定（＝`simulate`）のままにする**。`real` 等を与えると**起動時に停止**する（閂 3）。
- `moomoo.enabled=true` は**非推奨エイリアス**で、`broker.tier` 未指定のときだけ `moomoo-sim` と解釈される。
- 詳細（注入される env の全量・OpenD 接続パラメータ・Secret の要件）は
  [chart README「ブローカ階層（`broker.tier`）と moomoo（OpenD）発注」](../../deploy/helm/ai-stock-trading/README.md)。

## 識別: その約定はどちらの経路か

**上から順に確認する。1 と 2 で確定するため、通常はそこで終わる。**

### 1. 稼働中の階層を自己申告させる（最も確実・事前確認）

`order-execution` の introspection が階層をそのまま返す（`paper` / `moomoo-sim`）。

```bash
kubectl -n ai-stock-trading port-forward svc/order-execution-service 8080:8080 &
curl -s localhost:8080/internal/introspection | tr ',' '\n' | grep -A1 '"broker"'
#   "port":"broker"
#   "implementation":"paper"      ← 内蔵擬似約定。"moomoo-sim" なら OpenD 経由
```

### 2. ログを見る（moomoo 経路だけが出す行）

moomoo 経路は**OpenD への接続時**（遅延接続＝初回の発注・照会時）と**発注ごと**に固有の行を出す。
paper 経路は**これらを一切出さない**（＝出ていなければ paper で回っている）。

```bash
kubectl -n ai-stock-trading logs deploy/order-execution-service | grep -E "OpenD|moomoo"
```

| ログ | 意味 |
| --- | --- |
| `OpenD へ接続します <host>:<port> encrypt=...` | moomoo 経路の接続開始（**接続は遅延**＝初回の発注・照会時に張る。起動直後には出ない） |
| `OpenD 接続完了・SIMULATE 口座 accId=<数値>` | **SIMULATE 口座を掴んだ**。この行が無ければ moomoo へは出ていない |
| `moomoo SIMULATE 発注成功 orderId=<数値> <side> <symbol> x<qty>@<price>` | moomoo へ 1 件送った（**注文ごとに 1 行**） |
| `moomoo 発注に失敗したため Rejected に倒します ...` | moomoo 経路だが**不達**（OpenD 未接続等）。moomoo 側に注文は無い |

### 3. `OrderId` の形で見分ける（事後・DB / イベントから）

| 経路・結果 | `OrderId` の形 | 例 |
| --- | --- | --- |
| paper の約定・拒否 | **32 桁 hex**（`Guid` の `"N"` 書式・ハイフン無し） | `3f2a9c1e4b7d48a0b1c2d3e4f5061728` |
| moomoo が受け付けた注文 | **moomoo 採番の数値**（10 進・19 桁程度） | `9049618348733212748` |
| moomoo 経路だが**不達・不正注文** | **32 桁 hex**（AST が終端 `Rejected` を自前採番するため） | 同上（`Status=Rejected` で区別する） |

> ⚠️ **形だけで断定しない。** 32 桁 hex は「paper の約定」と「moomoo 経路の不達 `Rejected`」の両方に現れる。
> `Status` を併せて見る（`Filled` の 32 桁 hex ＝ paper、`Rejected` の 32 桁 hex ＝ moomoo 不達の可能性あり）。
> 確実な判定は 1・2（introspection とログ）で行う。

order-execution DB から確認する（発注結果は**経路に依らず** `executed_orders` に記録される）:

```sql
SELECT "OrderId", "DecisionId", "Symbol", "Status", "FilledQuantity", "ExecutedAt"
FROM executed_orders ORDER BY "ExecutedAt" DESC LIMIT 20;
-- OrderId が 32 桁 hex か数値かで経路の当たりを付け、Status と併せて判定する
```

### 4. moomoo 側で目視する（moomoo 経路のときのみ）

moomoo アプリ（模擬取引口座）の注文履歴に、上記ログの `orderId=` と同じ番号の注文がある。

- **注文の備考（remark）に `DecisionId` が入る**。ハイフン無しの 32 桁 hex（`Guid` の `"N"` 書式）で、
  AST 側の `executed_orders."DecisionId"` と 1 対 1 に対応する（[IADR-0092](../adr/IADR-0092_reservation-broker-probe-moomoo.md)）。
  これが AST の判断と moomoo の注文を突き合わせる唯一のキーである。
- **moomoo アプリに何も無い＝paper で回っていた**、が最も多い原因である（既定が paper のため）。

## moomoo SIMULATE を使うための前提

`broker.tier=moomoo-sim` は**設定だけでは成立しない**。次が揃って初めて発注が通る。

1. **OpenD が常駐し、ログイン済みであること**。listen しているだけでは使えない。
   OpenD は**初回のみ有人のデバイス検証**（画像 CAPTCHA / SMS）が要る:

   ```bash
   kubectl -n ai-stock-trading attach -it deploy/opend
   ```

   dev は `deploy/opend/k8s` の生 manifest、本番配備は chart の `opend.enabled=true`
   （[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）。
   無人再ログインの成立条件は「デバイス信頼の永続化（PVC）＋ egress IP の安定（＝ノード固定）」である。
2. **資格情報**: Secret `moomoo-credentials`（`login-account` / `login-pwd-md5`）。
3. **RSA 秘密鍵**: Secret `moomoo-rsa`（OpenD と**同一の鍵**）。cross-network（worker → opend）の trade は暗号化必須で、
   構成済みなのにファイルが無ければ `order-execution` は**起動時 preflight で停止**する。
4. **階層の切替**:

   ```bash
   helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
     --set broker.tier=moomoo-sim
   ```

秘匿情報の投入手順は [Vault 秘匿 runbook](vault-secrets-runbook.md)、
配備の詳細は [chart README](../../deploy/helm/ai-stock-trading/README.md) を参照。

## 実弾には行かない（閂の要約）

**paper / moomoo-sim のどちらを選んでも実弾（`TrdEnv_Real`）にはならない。** 実弾は次で多重に塞いである。

| # | 閂 | 実体 |
| --- | --- | --- |
| 0 | 実弾解禁ゲート | `Broker:Environment=live` なら**起動時停止**。解禁点は `LiveTradingGate.LiveTradingReleased`（`const false`）ただ 1 つ（IADR-0111） |
| 2 | SIMULATE のヘッダ固定 | 発注ヘッダ `TrdHeader` に `TrdEnv_Simulate` を**無条件**でセット（`MMApiMoomooTradeClient.BuildHeader`） |
| 3 | `TrdEnv=real` の起動時拒否 | `Broker:Moomoo:TrdEnv` が `simulate` 以外なら**起動時停止**（`MoomooBrokerOptions.EnsureSimulate`） |
| 4 | SIMULATE 口座のみ採用 | OpenD の口座一覧から `TrdEnv_Simulate` の口座だけを掴む（`MMApiMoomooTradeClient.FetchSimulateAccIdAsync`） |
| 外周 | Helm 描画時の拒否 | `broker.tier=moomoo-live` は `helm template` の時点で `fail` |

> 上表は要約である。閂 1（ブローカ選択ゲート＝`Broker:Provider` の既定 paper・未知値停止）を含む全体像・実装箇所・
> 「config で通せるか」・解禁手順は [実弾切替 Runbook](live-trading-cutover-runbook.md) を**単一情報源**とし、
> 番号（0〜4）も同表および `LiveTradingGate.cs` のコメントと一致させてある。
> 解禁には別の実装 ADR と [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md) §3 の前提充足が要る。

## 既知の制約（検証結果の読み方）

**moomoo 経路では、約定が AST の台帳へ反映されない**（[#270](https://github.com/endazon/ai-stock-trading/issues/270)）。

- moomoo は発注時に `Accepted`（未約定）を返し、AST は `OrderExecuted` を**約定数 0** で発行する。
  risk-management の台帳（`trade_fills`）は `Filled` かつ約定数 > 0 のみを記録するため、行が入らない。
- 約定状態を後から取りに行く定期経路が無い（`GetOrderAsync` の呼び出し元は訂正・取消と、既定無効の
  リコンサイル [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md) / [IADR-0092](../adr/IADR-0092_reservation-broker-probe-moomoo.md) のみ）。
- **帰結**: moomoo 経路では `/risk-controls/sizing-context` の残枠が減らず、統制上限（同日再エントリ・日次発注上限・
  段階残枠）が次サイクルで実効しない。**「残枠が減っていない＝発注されていない」と読んではいけない。**
  moomoo 模擬口座側には注文が積み上がっている。
- paper 経路は即時 `Filled` のためこのギャップが露呈しない。**moomoo へ切り替えて初めて表面化する。**

検証で残高・建玉を確認する際は、経路に応じて見る場所を変えること（paper ＝ AST の台帳 / moomoo-sim ＝ moomoo 模擬口座）。

## 参照

- [実弾（live trading）解禁 Runbook](live-trading-cutover-runbook.md) — 閂の全体像・config キー・解禁手順（単一情報源）
- [運用仕様書](operations.md) — OpenD の本番切替チェックリスト（#132）
- [chart README](../../deploy/helm/ai-stock-trading/README.md) — 経路B（ローカル SIMULATE）の有効化手順
- [ローカル実行手順（docker compose）](../how-to/local-run.md)
- [IADR-0016](../adr/IADR-0016_safe-broker-execution.md)（安全既定のブローカ執行）/
  [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（SIMULATE PoC 完了・実弾はゲート）/
  [IADR-0111](../adr/IADR-0111_broker-tier-selection.md)（ブローカ階層）
- 作業仕様書: [20260729_268_paper-vs-moomoo-simulate-distinction](../specs/20260729_268_paper-vs-moomoo-simulate-distinction.md)
- 関連 issue: [#269](https://github.com/endazon/ai-stock-trading/issues/269)（ブローカ階層化・`broker.tier`）/
  [#270](https://github.com/endazon/ai-stock-trading/issues/270)（moomoo 経路で約定が台帳へ反映されない）
