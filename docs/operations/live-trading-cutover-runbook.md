---
title: 実弾（live trading・TrdEnv_Real）解禁 Runbook
type: runbook
status: draft
related_ids:
  - FR-05
  - FR-20
  - ADR-0002
  - IADR-0016
  - IADR-0056
  - IADR-0057
  - IADR-0060
  - IADR-0074
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-29
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md"
---

# 実弾（live trading・`TrdEnv_Real`）解禁 Runbook

> リポジトリ単位の運用 Runbook。実弾（本物の資金による発注・moomoo `TrdEnv_Real`）へ切り替える際の
> **前提確認・手順・切り戻し**を定める。運用仕様書 [`operations.md`](operations.md) の
> 「OpenD の本番切替チェックリスト（#132）」段階 4（実弾解禁）を実務手順に落としたもの。

## この文書の位置づけと現在地

- **目的**: 実弾解禁を**容易化しつつ文書化する**こと。切替の入口（config キー）・前提・手順・切り戻しを一箇所に集約する。
- **現在のフラグ状態**: **実弾は無効（off）のまま**。本 Runbook は解禁を*可能にする*変更を一切含まない。
  既定値（`Broker:Provider=paper`・`Broker:Moomoo:TrdEnv=simulate` 固定）は変更しない。
- **本 Runbook はドキュメントのみ**。コード・設定の既定値・IADR 連番には触れていない。実弾解禁の意思決定は
  **別途の実装 ADR（`IADR-XXXX`・未起票）**に委ねる。本 Runbook はその ADR が Accepted 化された後の**手順書**である。

> **⚠️ 重要な事実（誇張しない）**: 実弾は「config を 1 つ書き換えるだけ」では**現状は有効化できない**。
> 下記の閂のうち 3 本（解禁ゲート・SIMULATE のヘッダ固定・`TrdEnv=real` の起動時拒否）は**コードで塞いである**。
> 単一 config フリップは、**解禁 IADR がそれらを緩めた後**に初めて「日々の運用スイッチ」として機能する。
> 現時点で `broker.tier=moomoo-live`（＝`Broker:Environment=live`）や `Broker:Moomoo:TrdEnv=real` を与えても、
> Worker は**起動時に停止する**（前者は Helm 描画時にも止まる）。意図した安全設計である。

## 実弾防止の閂（現行）— 何が config で、何がコードか

実弾は次の多重で塞いである。**config で「実弾を選ぼうとする」ことはできる**が、それは*拒否される入口*であって
*開く入口ではない*（[IADR-0016](../adr/IADR-0016_safe-broker-execution.md) の二重ゲート ＋
[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) §5 の第三の閂 ＋
[IADR-0111](../adr/IADR-0111_broker-tier-selection.md) の解禁ゲート）。

| # | 閂 | 実体 | 実装箇所 | config で通せるか |
| --- | --- | --- | --- | --- |
| 0 | **実弾解禁ゲート** | ブローカ階層が実弾（`Broker:Environment=live`）なら**起動時に `InvalidOperationException`**。`LiveTradingReleased`（`const false`）が唯一の解禁点で、OpenD 接続クライアントを構成する前に停止する | `.../Composable/Adapters/LiveTradingGate.cs` | **いいえ（コード拒否）** |
| 1 | **ブローカ選択ゲート** | `Broker:Provider` 既定 `paper`（実発注しない）。`moomoo` は OpenD 接続クライアント必須で、無ければ**起動時停止**。未知値も停止 | `backend/Services/OrderExecutionService/src/OrderExecutionService.Infrastructure/Composable/Adapters/BrokerFactory.cs` | **はい**（`Broker:Provider=moomoo`）。ただし SIMULATE 発注になるだけ |
| 2 | **SIMULATE のヘッダ固定** | 発注ヘッダ `TrdHeader` に `TrdEnv_Simulate` を**無条件**でセット。`OrderIntent.Mode=Live` でも SIMULATE で発注する | `.../Composable/Adapters/MMApiMoomooTradeClient.cs`（`BuildHeader` の `SetTrdEnv(TrdEnv_Simulate)`）／ `MoomooBrokerAdapter.cs` | **いいえ（コード固定）** |
| 3 | **`TrdEnv=real` の起動時拒否** | `Broker:Moomoo:TrdEnv` が `simulate` 以外なら**起動時に `InvalidOperationException`**。黙って SIMULATE で流さず、運用者の「実弾で動いている」誤認を防ぐ | `.../Composable/Adapters/MoomooBrokerOptions.cs`（`EnsureSimulate`） | **いいえ（コード拒否）** |
| 4 | **SIMULATE 口座のみ採用** | OpenD が返す口座一覧から `TrdEnv_Simulate` の口座だけを掴む。実口座の `accId` は保持しない | `.../Composable/Adapters/MMApiMoomooTradeClient.cs`（`FetchSimulateAccIdAsync`） | **いいえ（コード固定）** |
| 外周 | **Helm 描画時の拒否** | `broker.tier=moomoo-live` は `helm template` の時点で `fail`＝誤設定がクラスタへ届かない | `deploy/helm/ai-stock-trading/templates/deployment.yaml` | **いいえ（描画時 fail）** |

> 番号は `LiveTradingGate.cs` のコメントが定義する閂番号（0〜4）に一致させてある。
> 要約版が [発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md) にもあるが、**本表を単一情報源とする**。
>
> 閂 0・3 は「実弾を可能にする設定の入口」**ではない**。実弾を*拒否する*入口である
> （[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) 決定 5 / トレードオフ、
> [IADR-0111](../adr/IADR-0111_broker-tier-selection.md) 決定 5）。実弾の入口は**別 IADR**でのみ開く。

## 関連する config キー（実コードで確認済み）

環境変数化する場合は `:` を `__` に置換する（例 `Broker__Moomoo__TrdEnv`）。

| キー | 既定 | 挙動 | 出所 |
| --- | --- | --- | --- |
| `Broker:Provider` | `paper` | `paper` / `moomoo`。`moomoo` は OpenD クライアント必須・未提供や未知値は起動時停止 | `BrokerFactory.cs` / `BrokerSelection.cs` |
| `Broker:Environment` | （空＝）`sim` | `sim` / `live`。`live` は**起動時停止**（閂 0）。未知値も停止。`paper` との同時指定も停止 | `BrokerSelection.cs` / `LiveTradingGate.cs` |
| `Broker:Moomoo:TrdEnv` | （空＝）`simulate` | **`simulate` のみ受理**。`real` 等は**起動時停止**（閂 3） | `MoomooBrokerOptions.EnsureSimulate` |
| `Broker:Moomoo:OpenD:Host` | `opend` | OpenD ホスト（in-cluster では Service 名） | `MoomooBrokerOptions.FromConfiguration` |
| `Broker:Moomoo:OpenD:Port` | `11111` | OpenD API ポート | 同上 |
| `Broker:Moomoo:OpenD:RsaPrivateKeyPath` | （空＝非暗号） | cross-network trade に必須の RSA 秘密鍵パス。設定済みでファイル不在なら起動時停止（preflight） | 同上 / `MoomooPreflight.Validate` |
| `Broker:Moomoo:OpenD:ReplyTimeoutSeconds` | `15` | OpenD 応答待ち上限（1〜600 秒）。範囲外は起動時停止 | `MoomooBrokerOptions.ParseReplyTimeout` |

Helm では **`broker.tier`**（単一スイッチ・[IADR-0111](../adr/IADR-0111_broker-tier-selection.md)）が階層を決める。
`moomoo-sim` で `Broker__Provider=moomoo` ＋ `Broker__Environment=sim` ＋ OpenD 接続パラメータが注入される
（[`deploy/helm/ai-stock-trading/README.md`](../../deploy/helm/ai-stock-trading/README.md)）。
`moomoo.enabled=true` は**非推奨エイリアス**で、`broker.tier` 未指定のときだけ `moomoo-sim` として解釈される。
**`moomoo-live` は描画時に `fail` し、`TrdEnv` を values で `real` にできる口も用意していない**
（[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) トレードオフ / [IADR-0111](../adr/IADR-0111_broker-tier-selection.md) 決定 5）。

## 切替の設計（目標像）: 解禁 IADR 後の「単一 config フリップ」

実弾解禁 IADR が Accepted 化され、その中で**閂 0・閂 2・閂 3 を緩める**（`LiveTradingReleased` を `true` にし、
`TrdHeader` を config 駆動にし、`EnsureSimulate` を「解禁時のみ `real` を許可」へ変える）実装が入った**後**、
日々の運用スイッチは**単一の config フリップ**に収束させることを目標とする。

```yaml
# 目標像（解禁 IADR が閂 0・2・3 を緩めた後にのみ有効。現状は helm 描画時と起動時の双方で止まる）
broker:
  tier: moomoo-live   # ← 解禁後の運用スイッチはこの 1 行（= Broker__Provider=moomoo + Broker__Environment=live）
```

- **なぜ単一フリップに寄せるか**: 実弾/SIMULATE の切替が「1 行の値」に閉じていれば、運用者の誤認・手順ミスを最小化でき、
  監査ログ・GitOps 差分でも「いつ実弾に切り替えたか」が 1 行で追える。階層名（`paper` ＜ `moomoo-sim` ＜ `moomoo-live`）が
  そのまま本番近接順を表し、稼働中の階層は `GET /internal/introspection` の `broker` ポートが自己申告する。
- **現状の保証**: 上記 YAML を**今**適用しても実弾にはならない。**Helm 描画が止まり**、仮に環境変数で直接与えても
  閂 0 が**起動を止める**。したがって「うっかり実弾」は起きない。
- **コード変更の要否（正確に）**: 解禁には**コード変更が要る**（閂 0・2・3 の緩和）。閂 0 は `LiveTradingGate` の
  定数 1 つに集約してあるため、解禁の意思決定がどのコード変更に対応するかが 1 対 1 で追える。
  それは*運用手順*ではなく*解禁 IADR の実装*であり、本 Runbook の範囲外である。本 Runbook は
  「その実装が済んだ後、運用としてどう切り替え・戻すか」を定める。

## 解禁前チェックリスト（すべて充足するまで解禁しない）

実弾解禁の前提は [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md) §3 と
[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) 決定 6 に定義される。詳細な状態表（12 項目）は
[`operations.md`](operations.md) の「OpenD の本番切替チェックリスト（#132）> 前提条件」にあり、ここでは
**実弾解禁に直結する項目**を再掲する（重複管理を避け、状態は `operations.md` を単一情報源とする）。

| # | 前提 | 出所 | 確かめ方 |
| --- | --- | --- | --- |
| 1 | **段階ゲート（Stage）が実弾段まで進んでいる** | FR-20 / [#20](https://github.com/endazon/ai-stock-trading/issues/20) | バックテスト（Stage 0→1）・ペーパー実績（Stage 1→）を経て、撤退（kill switch）が発火していないこと。Stage が戻っていないこと |
| 2 | **秘匿情報の Vault / External Secrets 化** | [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md) §3 / [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) 決定 4 | `externalSecrets.enabled=true` で実 Vault/ESO から同期されていること。**受け口の存在は充足ではない**（ストアは #24 管掌） |
| 3 | **発注予約 `Reserved` 滞留の監視＋自動リコンサイル** | [#141](https://github.com/endazon/ai-stock-trading/issues/141) / [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md) | `Reconciliation:Enabled=true` かつ**実照会プローブが配線済み**であること（既定 no-op では自動解消しない）。滞留＝「発注済みか不明な建玉」で実弾では実損リスク |
| 4 | **無人 OpenD 常駐の成立** | [#132](https://github.com/endazon/ai-stock-trading/issues/132) / [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) | 安定ノード固定（egress IP 安定）・デバイス信頼の永続化で無人再ログインが成立。`securityContext`（非 root）実動作確認。**readiness 通過≠ログイン完了**に注意 |
| 5 | **Hetzner（海外 IP）接続・ToS の確認** | ADR-0002 未決 / [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) | 人手の接続確認・契約判断（#24） |
| 6 | **`TradingDefaults`（リスク統制・上限）の実弾向け再確認** | [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md) §3 | 全体前提条件（05_trading-assumptions §5）と一致し、実弾向けに保守的であることを再確認。少額上限から始めること |
| 7 | **発注の冪等化（at-most-once）** | [#131](https://github.com/endazon/ai-stock-trading/issues/131) / [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md) | **充足済み**（発注前 `DecisionId` 予約の 3 相化）。ただし #3 の滞留リコンサイルと併せて運用すること |
| 8 | **監査サインオフ** | [#204](https://github.com/endazon/ai-stock-trading/issues/204)（go-live 前実装監査） | 実環境構築前の実装監査（FR/NFR/UC/SC/ADR トレース・安全性）で Conditional-Go 以上。指摘の未解消がないこと |

> 上表に一つでも未充足があれば、実弾解禁 IADR を Accepted 化してはならない。とりわけ #2〜#5 は
> `operations.md` で 🔴 **未充足**であり（2026-07-19 時点）、**現状は解禁段階に達していない**。

## 実弾 go-live の手順（解禁 IADR 承認後・閂を「正しく」開ける）

前提: 解禁 IADR が Accepted 化され、閂 0・2・3 を緩める実装がマージ済みであること。SIMULATE 常駐（`operations.md`
段階 3）で一巡が確認済みであること。上記チェックリストがすべて充足済みであること。

1. **少額・単一銘柄から**。`TradingDefaults` の上限を実弾向けの最小値に設定して再デプロイする（前提 #6）。
2. **閂 1 を確認**: `broker.tier=moomoo-sim`（＝`Broker:Provider=moomoo`）で OpenD 経由発注が有効なこと。
   稼働中の階層は `GET /internal/introspection` の `broker` ポート（`moomoo-sim`）で確認する。
3. **OpenD のログイン成功を確認**（readiness 通過では判定しない・`operations.md` 前提 #10）。
4. **閂 0・3 を開ける（config）**: `broker.tier=moomoo-live`（＝`Broker:Environment=live`）と
   `Broker:Moomoo:TrdEnv=real` を設定して再デプロイする。
   - 解禁 IADR 実装後は、これで `TrdHeader` が `TrdEnv_Real` になる（閂 2 が緩められている前提）。
   - 起動ログで実弾モードである旨（実装が出力する warning）を確認する。**黙って実弾になってはならない**（明示ログが要件）。
5. **1 件だけ実弾発注→照会→約定→（必要なら）取消**の一巡を最小ロットで確認する。
6. **建玉・約定を台帳（`executed_orders`）と突き合わせ**、`Reserved` 滞留が無いことを確認する（前提 #3 の監視を稼働させたまま）。
7. 段階的にロット・銘柄数を上げる。各段で kill switch（撤退）と `Reserved` 監視を確認する。

> **閂を「正しく開ける」とは**: 閂 0・2・3 を*コードから消す*のではなく、**解禁 IADR が定めた条件下でのみ**
> `real` を許可する形に緩めること。config だけで無条件に実弾へ倒せる状態にはしない（解禁後も
> `Provider=moomoo` ＋ `Environment=live` ＋ `TrdEnv=real` の**多段**を要求し、既定は依然 `paper`/`sim`/`simulate` に保つ）。

## 切り戻し（実弾 → SIMULATE / ペーパー・即時）

実弾運用中に異常（想定外の約定・滞留・リスク統制の逸脱）を検知したら、**即座に**次のいずれかで戻す。
**上位ほど安全側で、影響が小さい**。

1. **kill switch（撤退）で新規建てを止める**（最速・コード変更不要）。既存の運用系 kill switch を起動する。
   新規発注のみ停止し、建玉の手仕舞い判断は人間が行う。
2. **`broker.tier=moomoo-sim`（＝`Broker:Environment=sim`）＋ `Broker:Moomoo:TrdEnv=simulate` に戻して再デプロイ**
   （閂 0・3 を再び閉じる）。以降の発注は SIMULATE に戻る。
3. **`broker.tier=paper`（＝`Broker:Provider=paper`）に戻す**（閂 1）。発注はペーパーに戻り、OpenD 発注経路が外れる。
4. OpenD 自体を落とす（`opend.enabled=false`）のは**最後の手段**。Pod を消すと**デバイス信頼の再確立（有人検証）**が
   要る場合があるため、発注を止めるだけなら 1〜3 に留める（`operations.md` 切り戻し節と同じ判断）。

> 実弾の**約定そのものは不可逆**である。切り戻しは「以降の新規発注を止める」ものであり、
> **既に成立した実弾の約定は取り消せない**。だからこそ「少額から・監視を先に・kill switch を即応」が要る。

## 安全上の警告

- **不可逆**: 実弾の約定は本物の資金移動であり取り消せない。テスト気分で解禁しない。
- **段階ゲートを飛ばさない**: バックテスト（Stage 0）→ ペーパー実績（Stage 1）→ 実弾は**順に**進む（FR-20 / [#20](https://github.com/endazon/ai-stock-trading/issues/20)）。
  撤退（kill switch）が発火したら段は戻り、実弾は継続しない。
- **少額から**: `TradingDefaults` の上限を実弾向け最小値にし、単一銘柄・最小ロットで開始する。
- **監視を先に**: `Reserved` 滞留監視（前提 #3）と費用統制・撤退監視を**稼働させてから**発注する。滞留＝未確定の実弾建玉。
- **「うっかり実弾」を許さない**: 解禁後も既定は `paper`/`sim`/`simulate`。実弾は `Provider=moomoo` ＋ `Environment=live`
  ＋ `TrdEnv=real` の**多段の明示**を要求し、起動時に実弾モードである旨を明示ログに出す。黙って実弾で動く経路を作らない。
  なお `Provider=paper` と `Environment=live` の同時指定は**起動時に拒否**する（擬似発注を実弾と誤認させない）。
- **本 Runbook は解禁しない**: 実弾を有効化する意思決定・コード変更は**別 IADR** に属する。本 Runbook は手順書であり、
  現在のフラグ状態（実弾 off・SIMULATE 固定）を変えない。

## 参照

- [運用仕様書 `operations.md`](operations.md) — OpenD 本番切替チェックリスト（#132）・`Reserved` 滞留 Runbook・データ保持
- [発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md) — paper（内蔵擬似約定）と moomoo SIMULATE の違い・
  どちらの経路で約定したかの識別（#268）
- [IADR-0016](../adr/IADR-0016_safe-broker-execution.md) — 安全既定 paper・実弾防止の二重ゲート
- [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md) — SIMULATE PoC 完了・実弾ゲート（§3 解禁前提）
- [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md) — 発注の冪等化（at-most-once）
- [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) — OpenD 本番化・切替ゲート（決定 5＝第三の閂）
- [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md) — 発注予約の自動リコンサイル（#141）
- [IADR-0111](../adr/IADR-0111_broker-tier-selection.md) — ブローカ階層（provider × environment）・解禁ゲート（閂 0）
