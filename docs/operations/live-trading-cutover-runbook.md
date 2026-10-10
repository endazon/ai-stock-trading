---
title: 実弾（live trading・TrdEnv_Real）解禁 Runbook
type: runbook
status: draft
created: 2026-07-19
updated: 2026-10-10
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-05, FR-20, NFR-09, FR-10, FR-15]
adrs: [ADR-0002, ADR-0045, ADR-0040, ADR-0050, ADR-0014, ADR-0054, ADR-0016]
iadrs: [IADR-0016, IADR-0056, IADR-0057, IADR-0060, IADR-0074, IADR-0111, IADR-0428, IADR-0441, IADR-0444, IADR-0466, IADR-0318, IADR-0498, IADR-0210, IADR-0482, IADR-0342, IADR-0524]
specs: [20260927_1051_release-gate-per-trading-env, 20260930_1121_s1-vs-decision-close, 20261007_1196_stage0-two-tier-recording, 20261007_1202_cutover-runbook-adr0040-checks, 20261009_1228_planning741-ruling-records, 20261010_1295_claude-5-5-models]
issues: [#20, #24, #131, #132, #141, #204, #268, #853, #856, #1051, #1121, #1196, #1202, #1214, #1228, #1275, #1295, #1296, planning#676, planning#704, planning#741, planning#783]
-->


# 実弾（live trading・`TrdEnv_Real`）解禁 Runbook

> リポジトリ単位の運用 Runbook。実弾（本物の資金による発注・moomoo `TrdEnv_Real`）へ切り替える際の
> **前提確認・手順・切り戻し**を定める。運用仕様書 [`operations.md`](operations.md) の
> 「OpenD の本番切替チェックリスト」段階 4（実弾解禁）を実務手順に落としたもの。

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
*開く入口ではない*（安全既定（ペーパー）と PoC までのゲートによる二重ゲート ＋
OpenD 本番化を「既定 no-op の整備」として先行させる決定の §5 が置く第三の閂 ＋
provider × environment の 2 軸表現が置く解禁ゲート）。

| # | 閂 | 実体 | 実装箇所 | config で通せるか |
| --- | --- | --- | --- | --- |
| 0 | **実弾解禁ゲート** | ブローカ階層が実弾（`Broker:Environment=live`）なら**起動時に `InvalidOperationException`**。`LiveTradingReleased`（`const false`）が唯一の解禁点で、OpenD 接続クライアントを構成する前に停止する | `.../Infrastructure/ExternalServices/LiveTradingGate.cs` | **いいえ（コード拒否）** |
| 1 | **ブローカ選択ゲート** | `Broker:Provider` 既定 `paper`（実発注しない）。`moomoo` は OpenD 接続クライアント必須で、無ければ**起動時停止**。未知値も停止 | `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/BrokerFactory.cs` | **はい**（`Broker:Provider=moomoo`）。ただし SIMULATE 発注になるだけ |
| 2 | **SIMULATE のヘッダ固定** | 発注ヘッダ `TrdHeader` に `TrdEnv_Simulate` を**無条件**でセット。`OrderIntent.Mode=Live` でも SIMULATE で発注する | `.../Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs`（`BuildHeader` の `SetTrdEnv(TrdEnv_Simulate)`）／ `MoomooBrokerAdapter.cs` | **いいえ（コード固定）** |
| 3 | **`TrdEnv=real` の起動時拒否** | `Broker:Moomoo:TrdEnv` が `simulate` 以外なら**起動時に `InvalidOperationException`**。黙って SIMULATE で流さず、運用者の「実弾で動いている」誤認を防ぐ | `.../Infrastructure/ExternalServices/MoomooBrokerOptions.cs`（`EnsureSimulate`） | **いいえ（コード拒否）** |
| 4 | **SIMULATE 口座のみ採用** | OpenD が返す口座一覧から `TrdEnv_Simulate` の口座だけを掴む。実口座の `accId` は保持しない | `.../Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs`（`FetchSimulateAccIdAsync`） | **いいえ（コード固定）** |
| 外周 | **Helm 描画時の拒否** | `broker.tier=moomoo-live` は `helm template` の時点で `fail`＝誤設定がクラスタへ届かない | `deploy/helm/ai-stock-trading/templates/deployment.yaml` | **いいえ（描画時 fail）** |

> 番号は `LiveTradingGate.cs` のコメントが定義する閂番号（0〜4）に一致させてある。
> 要約版が [発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md) にもあるが、**本表を単一情報源とする**。
>
> 閂 0・3 は「実弾を可能にする設定の入口」**ではない**。実弾を*拒否する*入口である
> （OpenD 本番化の決定 5 とそのトレードオフ、およびブローカー選択の 2 軸表現の決定 5）。
> 実弾の入口は**別の実装 ADR**でのみ開く。

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

Helm では **`broker.tier`**（単一スイッチ。ブローカー選択は provider × environment の直交 2 軸で表現する）が階層を決める。
`moomoo-sim` で `Broker__Provider=moomoo` ＋ `Broker__Environment=sim` ＋ OpenD 接続パラメータが注入される
（[`deploy/helm/ai-stock-trading/README.md`](../../deploy/helm/ai-stock-trading/README.md)）。
`moomoo.enabled=true` は**非推奨エイリアス**で、`broker.tier` 未指定のときだけ `moomoo-sim` として解釈される。
**`moomoo-live` は描画時に `fail` し、`TrdEnv` を values で `real` にできる口も用意していない**
（OpenD 本番化のトレードオフ、およびブローカー選択の 2 軸表現の決定 5）。

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

実弾解禁の前提は、実アダプタ実装（実弾は引き続きゲート）の実装 ADR §3 と、
OpenD 本番化の実装 ADR 決定 6 に定義される。詳細な状態表（16 項目）は
[`operations.md`](operations.md) の「OpenD の本番切替チェックリスト> 前提条件」にあり、ここでは
**実弾解禁に直結する項目**を再掲する（重複管理を避け、状態は `operations.md` を単一情報源とする）。

| # | 前提 | 出所 | 確かめ方 |
| --- | --- | --- | --- |
| 1 | **段階ゲート（Stage）が実弾段まで進んでいる** | 段階ゲートの要求 / [#20](https://github.com/endazon/ai-stock-trading/issues/20) | バックテスト（Stage 0→1）・ペーパー実績（Stage 1→）を経て、撤退（kill switch）が発火していないこと。Stage が戻っていないこと |
| 2 | **秘匿情報の Vault / External Secrets 化** | 実アダプタ実装の実装 ADR §3 / OpenD 本番化の実装 ADR 決定 4 | `externalSecrets.enabled=true` で実 Vault/ESO から同期されていること。**受け口の存在は充足ではない**（ストアは #24 管掌） |
| 3 | **発注予約 `Reserved` 滞留の監視＋自動リコンサイル** | [#141](https://github.com/endazon/ai-stock-trading/issues/141) / 自動リコンサイルの実装 ADR | `Reconciliation__Enabled=true` かつ**実照会プローブが配線済み**であること（配備の values は充足済み）。🔴 **さらに実弾では、実弾の解放の門（`Reconciliation__ReleaseOnNotPlaced__Real`）を開けてよいかを、実弾の実機の記録で判定すること**（運用仕様書「解放の門を開けるときの記録」の (a)(b)。SIMULATE の記録では開けない。門は取引環境ごとに分かれ、SIMULATE の門を開けても実弾の予約は解放されない）——閉じたままなら「未発注」の滞留は人手解決のままである。突合が「発注済み」と確定したエントリーには承認時の手法で保護レグを張る（2026-09-25 のオーナー裁定。旧: 張らなかった）——**このとき突合を起点にブローカーへ逆指値・取消・成行が送られ得る**ことを、実弾前に確認しておくこと。滞留＝「発注済みか不明な建玉」で実弾では実損リスク |
| 4 | **無人 OpenD 常駐の成立** | [#132](https://github.com/endazon/ai-stock-trading/issues/132) / OpenD 本番化の実装 ADR | 安定ノード固定（egress IP 安定）・デバイス信頼の永続化で無人再ログインが成立。`securityContext`（非 root）実動作確認。**readiness 通過≠ログイン完了**に注意 |
| 5 | **Hetzner（海外 IP）接続・ToS の確認** | 証券会社連携の計画 ADR の未決事項 / OpenD 本番化の実装 ADR | ToS は判断済み（2026-10-09・#342。固定 IP から直接接続し、VPN・プロキシ・Tor を挟まない構成に限る）。残るのは契約後の人手の接続確認 |
| 6 | **`TradingDefaults`（リスク統制・上限）の実弾向け再確認** | 実アダプタ実装の実装 ADR §3 | 全体前提条件 §5 と一致し、実弾向けに保守的であることを再確認。少額上限から始めること |
| 7 | **発注の冪等化（at-most-once）** | [#131](https://github.com/endazon/ai-stock-trading/issues/131) / 発注の冪等化の実装 ADR | **充足済み**（発注前 `DecisionId` 予約の 3 相化）。ただし #3 の滞留リコンサイルと併せて運用すること |
| 8 | **監査サインオフ** | [#204](https://github.com/endazon/ai-stock-trading/issues/204)（go-live 前実装監査） | 実環境構築前の実装監査（要求・非機能要件・ユースケース・画面・意思決定のトレースと安全性）で Conditional-Go 以上。指摘の未解消がないこと |
| 9 | **判断の手仕舞いと保護逆指値の併存**（ブローカー側の逆指値が売れる数量を押さえるか） | 損切りの実行機構の計画 ADR の実弾解禁前の確認（2026-09-30 の部分改定で追加） / [#1121](https://github.com/endazon/ai-stock-trading/issues/1121) | SIMULATE は逆指値を拒否するため確かめられない。🔴 **実弾口座・最少数量で、逆指値を置いた建玉に全量の手仕舞い（指値）を出し、受理されるかを確かめる。** 受理されれば充足。🔴 **受理されない（逆指値が数量を押さえる）なら、解禁の前に「手仕舞いの前に保護逆指値を取り消す経路」を起案・実装すること**——無いまま実弾へ進むと、保護逆指値を持つ建玉への判断・利用者の手仕舞いが常に「建玉が足りない」で拒否され、手仕舞いが止まる。確かめた結果（受理／拒否と拒否の文言）は運用仕様書の前提条件 #13 に記録する |
| 10 | **両層の組（一次スクリーニング `claude-haiku-5-5` ＋ 本判断 `claude-sonnet-5-5`）での Stage 0 合格** | 取引判断の割当モデルを層別にした計画 ADR の決定 3（モデルを変えたら Stage 0 を再実施し、その通過を実弾解禁の必須ゲートとする計画 ADR の決定 3 を両層へ及ぼした）/ go-live 前実装監査（[#204](https://github.com/endazon/ai-stock-trading/issues/204)）の指摘 / [#1196](https://github.com/endazon/ai-stock-trading/issues/1196) | Stage 0 の記録を**本番と同じ二段**（一次で見送れば本判断を呼ばない）で採り、記録再生（`recorded-replay`）の評価が**合格**であること。記録の各判断には一次の結果と**両層の実効モデル**（応答が名乗ったモデル）が別々に載る。🔴 **一次を記録していない旧記録での評価は「評価不能」（`ScreeningNotRecorded`）であり合格ではない**（合格にも 7 条件の不合格にも数えない。記録を採り直す）。🔴 実効モデルがピンと違う（不明を含む）判断は判定母集団から外れ、除外の集計に「実効モデル不一致」として件数が出る——外した結果の合格は「両層の組での合格」の射程が狭まったものとして読むこと。**どちらの層のモデルを変えても再実施する**（一次だけを軽く差し替えることはできない）。🔴 **2026-10-10 の利用者裁定で組を 5.5 系へ改めた**（旧組は `claude-haiku-4-5` ＋ `claude-sonnet-5`）。**旧組で採った記録・旧組での合格は本条件を満たさない**——割当表は基盤の切り替えが済むまで直前世代も受ける（取引を止めないため。[#1296](https://github.com/endazon/ai-stock-trading/issues/1296) で外す）が、Stage 0 の両層の照合は受けない（旧組の判断は「実効モデル不一致」として母集団から外れる）。学習カットオフ日も `2026-06-30` へ引き直したうえで 5.5 系の組で採り直す。閂 0 の告知文（起動時の停止の例外文）にも同じ項目が並ぶ |
| 11 | **保護逆指値そのものの挙動**（①証券会社が逆指値を受理する ②損切りラインで約定する ③取消・失効を検知して再発注できる） | 損切りの実行機構の計画 ADR の実弾解禁前の確認（表の 1 行目） / [#1202](https://github.com/endazon/ai-stock-trading/issues/1202) | 🔴 **SIMULATE では代替できない**——模擬取引は逆指値（`OrderType_Stop`）を `Paper trading does not support Stop order` で拒否する。S3 の代替種別（ストップリミット・トレーリングストップ）は別の注文種別であり、代わりの証拠にならない。🔴 **読み取り専用の照会だけでは足りない**——①② は発注しないと観測できない。本システムは閂 0〜4 で実弾へ発注できず、実弾口座の読み取り専用の照会（借株可否と維持率の束）は注文を照会しない。したがって**実弾口座・最少数量で、本システムの外（moomoo アプリ等）から人が発注して確かめる**: (a) 最少数量の建玉に逆指値を置き、受理されること（拒否なら拒否の文言を控える）。(b) 発火価格を現値の近くに置き、発火して約定すること（発火価格・約定価格・約定までの時間を控える）。(c) 逆指値を取り消す、または失効させ、そのときの注文状態（OpenD の `OrderStatus` の数値）を控える。**この状態の確認は読み取り専用の照会（アプリの注文履歴・OpenD の注文一覧）で足りる。** 控えた値が発注アダプタの状態の写像で終端（取消＝14/15/24・失敗＝3/21/22/23）へ写ることを確かめる——**照会不能（4＝TimeOut や未知の値）へ写るなら、保護逆指値ガードは据え置いて再発注しない**ため、解禁の前に写像を直すこと。再発注そのもの（ガードの分岐）は単体試験で固定してあり、実弾での一巡は go-live 手順 5 の最小ロットで確かめる。結果は運用仕様書の前提条件 #14 に記録する（下の「確認結果（verdict）の記録先と有効期限」） |
| 12 | **保護逆指値の有効期限**（GTC〔取り消すまで有効〕の逆指値が翌営業日も残るか） | 損切りの実行機構の計画 ADR の実弾解禁前の確認（表の 2 行目）・同 ADR のフォローアップ 3 / [#1202](https://github.com/endazon/ai-stock-trading/issues/1202) | 🔴 **SIMULATE では代替できない**——模擬取引の注文は当日限りである。🔴 **現行の実装は保護逆指値に有効期限を指定していない**（発注に `TimeInForce` を載せない＝証券会社の既定の当日限り）。失効は #11 の保護逆指値ガードが検知して再発注するが、**場の終了後に失効した逆指値を次の場の前に出し直したとき受理されるか・次の場で有効かは確かめていない**。確かめ方: 実弾口座・最少数量で、**本システムの外から人が GTC を指定した逆指値を置き**（ここは発注が要る）、**翌営業日の場の開始後に同じ注文が提出済みのまま残っていることを読み取り専用の照会で確かめる**（アプリの注文一覧・OpenD の注文一覧。翌日の確認は読み取り専用で足りる）。あわせて当日限り（現行）の逆指値が場の終了後にどの注文状態になるかを控える（#11 の (c) と同じ値を使う）。🔴 **結果が「GTC 不可（翌営業日に残らない・GTC を受理しない）」なら、保護逆指値を日次で置き直す経路を [#1214](https://github.com/endazon/ai-stock-trading/issues/1214) で起案・実装するまで解禁しない。** 「GTC 可」でも、現行は当日限りで出しているため、保護レグに GTC を指定するか（当日限り＋ガードの再発注で足りるか）を同じ #1214 で決めてから解禁する。結果は運用仕様書の前提条件 #15 に記録する |
| 13 | **起動時の損切りの手法の照会と停止**（実弾で、逆指値を置かない手法・ソフトウェア逆指値・代替注文種別のまま起動しない） | 損切りの実行機構の計画 ADR の決定 1（2026-10-09 の補完。起動時の停止は維持し、実弾解禁の実装 ADR の受入条件とする） / [#1275](https://github.com/endazon/ai-stock-trading/issues/1275) | 🔴 **未実装（意図して実弾解禁の時点まで入れない）。閂 0 を外す変更と同じ時点で揃える**——閂 0 だけを外すと、設定行の手編集などで設定側の拒否が破られたとき、最初の承認まで止まらない。実弾の階層で起動したら発注執行がリスク管理の手法の設定を照会し、ブローカー側逆指値（S0）以外、または照会できなければ**起動時に停止する**こと（SIMULATE・内蔵ペーパーでは照会しない）。それまでは承認ごとの見送り（`StopLossMethodNotPermitted`）・設定側の 2 方向の拒否・閂 0 が担う（いずれも外さない）。試験で固定されていること、運用仕様書の前提条件 #16 が充足に更新されていることを確かめる |

> 上表に一つでも未充足があれば、実弾解禁 IADR を Accepted 化してはならない。とりわけ #2〜#5 は
> `operations.md` で 🔴 **未充足**であり（2026-07-19 時点）、**現状は解禁段階に達していない**。

### 確認結果（verdict）の記録先と有効期限（#9・#11・#12）

#9・#11・#12 は、損切りの実行機構の計画 ADR が持つ「実弾解禁前の確認」の表の 3 行である。確認が「済んだ」という結論（verdict）の形式は、空売りの実弾解禁前の確認と同じものを援用する。

| 項目 | 定め |
| --- | --- |
| 誰が出すか | **利用者の承認**。AI は確認の手順を整え、観測値を控えるところまでを担う |
| 記録先（計画） | 段階ゲートの承認記録と同じ経路（専用の記録・専用の API を作らない） |
| 🔴 記録先（現在の実現手段） | **承認記録の verdict の種別は、空売りの実弾解禁の 1 種類しか実装されていない**（[段階ゲートの機能仕様書](../functional/FR-20_staged-gates.md) §3-1）。#9・#11・#12 の種別は無く、承認記録に載せる手段はまだ無い。**載せ方は実弾解禁の実装 ADR が決める。** それまでは運用仕様書の前提条件 #13（#9）・#14（#11）・#15（#12）の行に、**確認日・承認した利用者・結果・控えた観測値**（拒否の文言・注文状態の数値・約定価格）を書いて記録とする |
| 有効期限 | 確認日から **30 日**（30 日ちょうどは有効）。**解禁する時点で 30 日以内の verdict が要る** |
| 再検証の契機 | ①**期限切れ** ②**情報源の変更** ③**戦略の変更**の 3 つ。いずれか 1 つで verdict は無効になり、確かめ直す。計画はこの 3 行での②③の読み方を定めていないため、保守側に読む——②は確認に使った口座・証券会社の注文仕様（OpenD・SDK の版の更新を含む）が変わったとき、③は損切りの実行手法や保護逆指値の注文種別・有効期限の指定を変えたとき |

🔴 **確認の後始末（#9・#11・#12 共通）**: 観測値を控えたら、その日のうちに**試験で置いた逆指値をすべて取り消し、最少数量の試験建玉を手仕舞う**（アプリの注文一覧で未約定の逆指値が残っていないことを読み取り専用の照会で確かめる）。試験の逆指値は**買い建玉に対する売りの逆指値**として置く。建玉が無くなった後に GTC の売り逆指値が残ると、発火したとき信用口座では**反対方向の建玉（空売り）を生む**。#12 は翌営業日の確認が済むまで試験の逆指値を残すため、その確認の直後に同じ後始末をする。

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
- **段階ゲートを飛ばさない**: バックテスト（Stage 0）→ ペーパー実績（Stage 1）→ 実弾は**順に**進む（段階ゲートの要求 / [#20](https://github.com/endazon/ai-stock-trading/issues/20)）。
  撤退（kill switch）が発火したら段は戻り、実弾は継続しない。
- **少額から**: `TradingDefaults` の上限を実弾向け最小値にし、単一銘柄・最小ロットで開始する。
- **監視を先に**: `Reserved` 滞留監視（前提 #3）と費用統制・撤退監視を**稼働させてから**発注する。滞留＝未確定の実弾建玉。
- **「うっかり実弾」を許さない**: 解禁後も既定は `paper`/`sim`/`simulate`。実弾は `Provider=moomoo` ＋ `Environment=live`
  ＋ `TrdEnv=real` の**多段の明示**を要求し、起動時に実弾モードである旨を明示ログに出す。黙って実弾で動く経路を作らない。
  なお `Provider=paper` と `Environment=live` の同時指定は**起動時に拒否**する（擬似発注を実弾と誤認させない）。
- **本 Runbook は解禁しない**: 実弾を有効化する意思決定・コード変更は**別 IADR** に属する。本 Runbook は手順書であり、
  現在のフラグ状態（実弾 off・SIMULATE 固定）を変えない。

## 参照

- [運用仕様書 `operations.md`](operations.md) — OpenD 本番切替チェックリスト・`Reserved` 滞留 Runbook・データ保持
- [発注経路の区別と識別 Runbook](broker-execution-paths-runbook.md) — paper（内蔵擬似約定）と moomoo SIMULATE の違い・
  どちらの経路で約定したかの識別
- 実装ADR: 発注執行は安全既定（ペーパー）とし、moomoo 実発注は PoC までゲートする — 安全既定 paper・実弾防止の二重ゲート
- 実装ADR: moomoo SIMULATE PoC 完了に基づき実アダプタを実装する（実弾は引き続きゲート） — §3 が解禁前提を定める
- 実装ADR: 発注の冪等化は「発注前 `DecisionId` 予約」の 3 相で行い、不明な窓は再発注せず拒否する — at-most-once
- 実装ADR: OpenD 本番化は「既定 no-op の整備」として先行し、切替はゲート＋チェックリストで人手に残す — 決定 5＝第三の閂
- 実装ADR: Reserved 滞留の自動リコンサイルはプローブ・ポート＋fail-safe 既定 no-op で行い、実照会は後続へ分離する
- 実装ADR: ブローカー選択は provider × environment の直交 2 軸で表現する — ブローカ階層・解禁ゲート（閂 0）
