# ai-stock-trading k8s Helm chart（MSP 連結ローカル k8s dev）

> 起点: [IADR-0052](../../../.ai-context/adr/IADR-0052_k8s-helm-chart-shared-infra.md) /
> 作業仕様書 [`.ai-context/specs/20260713_122_k8s-helm-chart.md`](../../../.ai-context/specs/20260713_122_k8s-helm-chart.md) /
> Issue #122・#24・#121

AST の 10 Worker を k8s へデプロイする chart。**共有インフラ（Postgres/RabbitMQ/Keycloak/otel）は
MSP 側 `platform-infra`** を参照する（MSP `deploy/local` / IADR-0066 と対）。単体では動かない。

## 前提

MSP 側で `platform-infra` と AST 用 DB（user `ai`・`*_svc`）・Keycloak realm `ai-stock-trading` を
起動済みであること。MSP リポの `scripts/k8s-local-up.sh` がこれを用意する
（AST realm は submodule 検出時に同梱 import される）。

## デプロイ

k8s ランタイムは MSP 側と同じく **Rancher Desktop（内蔵 k3s・推奨）** か **Docker Desktop + k3d**。
スクリプトが `nerdctl`/`k3d` の有無で自動判定する（`K8S_LOCAL_RUNTIME=rancher|k3d` で明示可）。
> 注: 判定は **`nerdctl` の存在**を優先し rancher 経路を選ぶ。両方入っている等で意図と異なる場合は
> `K8S_LOCAL_RUNTIME` で明示指定する。

```bash
scripts/k8s-local-deploy.sh              # build（Rancher=nerdctl/k3d=docker+import）→ ast-secrets → helm install（-f values-local.yaml）
kubectl -n ai-stock-trading get pods
```

## 経路B（ローカル SIMULATE）の機能有効化: `values-local.yaml`

> 起点: [IADR-0100](../../../.ai-context/adr/IADR-0100_route-b-values-local-standing-config.md) /
> 作業仕様書 [`.ai-context/specs/20260725_route-b-values-local-standing-config.md`](../../../.ai-context/specs/20260725_route-b-values-local-standing-config.md) /
> Issue #238

経路B（ローカル k8s / SIMULATE）で取引サイクルを end-to-end 検証するための **local/SIMULATE 有効化プロファイル**を
`values-local.yaml` として同梱する。`scripts/k8s-local-deploy.sh` が `helm upgrade -f values-local.yaml` で**標準手順として
自動適用**する（臨時 overlay を手当てする必要はない）。有効化する内容:

- **①時価評価（mark-to-market）**: risk-management `MarketData__EnableMarkToMarket=true` / `Provider=finnhub`、
  market-monitor・report の `MarketData__Provider=finnhub`（FR-10/16・IADR-0068）。
- **②実 LLM**: trade-decision・report の `LlmGateway__BaseUrl`（MSP LlmGateway・ADR-0010 / IADR-0061）。
- **③実 KB 保存**: information-collection・report の `KnowledgeBase__Documents__BaseUrl`（MSP DocumentService）＋
  `KnowledgeBase__Auth`（MSP レルムの `ai-stock-trading-kb-writer`・IADR-0093）。
- **Discord 通知**: notification `Notifications__Provider=discord-webhook` / `Bot__Enabled=true`（FR-09/14・IADR-0062）。
- **価格文脈（#236 / IADR-0099）**: trade-decision へ現在値を供給し権威価格でサイジング
  （`MarketData__Provider=finnhub`＋鍵で `ICurrentPriceProvider.IsEnabled` が真・鮮度 `MaxQuoteStalenessSeconds=300`）。
- **為替換算（#257 / #364 / IADR-0107 / IADR-0152）**: trade-decision の `Fx__Provider=fred`＋`Fx__Fred__ApiKey`。
  **日本株を取引するための必須前提**（下記「為替換算」参照）。未設定だと JPY 建て銘柄は LLM 呼び出し前に全件見送りになる。
  **#364 で基準通貨が USD へ移行したため、必須となる市場が US 株から日本株へ入れ替わった**（主ターゲットの US 株は本キー無しで回る）。
- **サイクル配線**: 収集の finnhub＋AAPL、trade-decision の watchlist（AAPL/UnitedStates）・`Reports`/`RiskManagement` BaseUrl。
- **実DD（観測最大ドローダウン）の供給（#279 / [IADR-0114](../../../.ai-context/adr/IADR-0114_route-b-parity-observed-drawdown-and-official-sources.md) / IADR-0103）**:
  risk-management `ObservedDrawdownRefresh__Enabled=true` ＋ `WithdrawalEvaluation__Enabled=true`。前者が営業日の定時に
  建玉台帳の `DrawdownRatio` をサンプリングして段階実績台帳へ単調 latch し、後者が ADR-0008 の撤退基準を評価する。
  **自動 kill switch の発火は Stage 2/3（実弾段階）に限られ、現在の Stage 0 では起きない**（下記「撤退評価と自動 kill switch」）。
  経路B の既定ブローカ paper では擬似約定が台帳へ入るため実効し、moomoo SIMULATE 経路は約定が台帳へ伝播しないため
  [#270](https://github.com/endazon/ai-stock-trading/issues/270) が入るまで DD は 0 のまま（不活性・安全側）。
- **LLM 費用の単価（#303 / IADR-0122 / #279 / IADR-0114 決定6 / IADR-0055）**: trade-decision
  `LlmPricing__PerModel__<model-id>__InputPer1kTokens` / `__OutputPer1kTokens`（**円 / 1,000 トークン**・**モデル別**）。
  未設定（既定 0）だと毎回 ¥0 計上で月次費用上限（¥15,000）が構造的に発火しない。下記「LLM 費用の単価」参照。
- **公式情報源の収集（#279 / IADR-0114 / IADR-0064）**: `Collection__Source__Provider="finnhub,sec-edgar,fred"`。
  SEC EDGAR は CIK `0000320193`（Apple）＋連絡先入り UA（下記 `SEC_EDGAR_USER_AGENT`）、FRED は `DEXJPUS` / `DGS10`
  （鍵は Fx と同じ `fred-api-key`）。必須構成を欠くソースだけが警告つきで除外される（他ソースは有効なまま）。

**本番（ArgoCD）はバイト等価**: `deploy/argocd/application.yaml` は `valueFiles` を持たず `values.yaml` のみを描画するため、
`values-local.yaml` は本番描画に一切関与しない。`helm.yml` の CI が「既定描画に経路B有効化が漏れていないこと」と
「`values-local` 描画で①②③＋Discord＋価格文脈＋実DD 供給＋公式情報源が ON かつ Broker=paper・opend/ExternalSecret 不在であること」を
両検証する。加えて「**`values-local` が既定描画の env を 1 つも落としていないこと**」も検査する（#279 / IADR-0114 決定4）——
Helm は**リストを置換する**ため、`extraEnv` を上書きしているサービスでは本番 `values.yaml` にキーが増えたときの写し忘れが
**当該 env の消失**になり、「有効化したつもりで別の機能を落とす」事故になるため。

**secret は平文で埋め込まない**: `values-local.yaml` の鍵・トークンはすべて `secretKeyRef`（`ast-secrets`・`optional`）参照。
実値は下記 env で `ast-secrets` へ与える（未設定=空=no-op の fail-safe）か、ESO(Vault) 同期（IADR-0094）に委ねる。

| 環境変数 | `ast-secrets` キー | 用途 | 既定 |
| --- | --- | --- | --- |
| `MARKETDATA_FINNHUB_API_KEY` | `marketdata-finnhub-api-key` | ①時価・価格文脈（情報収集の `FINNHUB_API_KEY` とは**別枠**の opt-in・IADR-0068。フォールバックしない＝収集鍵の設定だけで①が黙って有効化されない）。**同一の Finnhub アカウント鍵を両方へ設定するとレート予算を共有する**（IADR-0274 実測で確認済みの構成。既定のレート予算〔情報収集30/分＋市況5/分×4サービス=50/分〕はこの共有を前提に実測上限〔60/分・固定60秒ウィンドウ〕内へ調整済み。別アカウントの鍵を使うなら市況側の `RequestsPerMinute` を引き上げてよい） | 空=NoOp |
| `FRED_API_KEY` | `fred-api-key` | **日本株取引の必須前提**（基準通貨〔USD〕への換算レート源＝FRED `DEXJPUS` の**逆数**・IADR-0107 / IADR-0152）。収集ソース（FRED）にも同じ鍵を使う | **空=JPY 建て銘柄が全件見送り**（米国株は無影響）。下記「為替換算」参照 |
| `EDINET_SUBSCRIPTION_KEY` | `edinet-subscription-key` | 収集ソース（任意） | 空=当該ソース無効 |
| `SEC_EDGAR_USER_AGENT` | `sec-edgar-user-agent` | 収集ソース SEC EDGAR。**機密ではない**が SEC 規約が求める**連絡先（実在のメールアドレス）入り**の User-Agent＝環境固有の個人情報のため values へ直書きせず本経路で与える（#279 / IADR-0114 決定2）。例: `AiStockTrading/1.0 (you@example.com)` | 空=**SEC EDGAR だけ**が収集対象から外れる（finnhub/FRED は有効なまま） |
| `KB_AUTH_CLIENTSECRET` | `kb-auth-client-secret` | ③KB 書き込みの s2s（`kb-auth-client-id` は dev 既定 `ai-stock-trading-kb-writer`） | 空=401→未保存（fail-safe） |
| `DISCORD_BOT_TOKEN` | `discord-bot-token` | Discord Bot（双方向） | 空=Gateway に接続しない |
| `DISCORD_BOT_KILLSWITCH_PHRASE` | `discord-bot-killswitch-phrase` | kill switch 確認フレーズ | 空=kill switch 起動不可（安全側） |

> **Discord の環境固有 ID**（`GuildId` / `ChannelId` / `AllowedUserIds` / `UserMapping`）は**空既定**であり、
> 下記「Discord の環境固有 ID」の env（`DISCORD_BOT_*`）で与える（[#245](https://github.com/endazon/ai-stock-trading/issues/245) /
> [IADR-0102](../../../.ai-context/adr/IADR-0102_discord-env-ids-via-values.md)）。**空のまま**だと IADR-0062 の
> 安全既定（空 GuildId/ChannelId/AllowedUserIds は「全許可」ではなく**全拒否**）で Bot は接続しても操作を受け付けない。
> Discord を使わないなら `Notifications__Provider=""` / `Bot__Enabled="false"` に戻す。

### 鍵の供給と再実行時の挙動（`ast-secrets`）

> 起点: [#263](https://github.com/endazon/ai-stock-trading/issues/263) /
> [IADR-0109](../../../.ai-context/adr/IADR-0109_deploy-secret-preservation.md) /
> [IADR-0052](../../../.ai-context/adr/IADR-0052_k8s-helm-chart-shared-infra.md)

`ast-secrets` は**手動作成の Secret**（既定は Vault 非依存）。`scripts/k8s-local-deploy.sh` が上表の env を読み、
**キー単位の差分パッチ**で同期する。**env を export せずに再実行しても、投入済みの値は失われない。**

| env の状態 | `ast-secrets` の当該キー | 挙動 |
| --- | --- | --- |
| 未設定 | 値あり | **触らない（保持）**。「保持: `<キー名>`」として表示される |
| 未設定 | 空/不在 | 既定値で設定（多くは空。dev 既定を持つキーはその既定） |
| 非空を指定 | 任意 | 指定値で上書き |
| **空を明示指定**（`export KEY=`） | 値あり | **キー名を列挙して中断**（下記） |
| 空を明示指定 | 空/不在 | 空のまま（失うものが無い） |

```console
$ scripts/k8s-local-deploy.sh
==> [2/3] namespace & ast-secrets (fail-safe 空既定・既存値は保持)
  ast-secrets: 設定 8 件 / 既存値を保持 5 件（値は表示しません）
    保持: fred-api-key
    保持: marketdata-finnhub-api-key
```

```console
$ FRED_API_KEY= scripts/k8s-local-deploy.sh
ERROR: 次のキーは ast-secrets に値がありますが、環境変数が**空**で指定されています。
       空で上書きすると投入済みの値を失うため中断しました（#263 / IADR-0109）:
         - fred-api-key ($FRED_API_KEY)
       - export し忘れなら当該変数を unset して再実行する（現在値がそのまま保持されます）。
       - 意図した消去なら --force-empty-secrets を付けて再実行する。
```

現在どのキーに値が入っているかは**キー名だけ**で確認できる（値は出さない）:

```bash
kubectl -n ai-stock-trading get secret ast-secrets \
  -o go-template='{{range $k,$v := .data}}{{if $v}}{{$k}}{{"\n"}}{{end}}{{end}}'   # 値ありのキー
kubectl -n ai-stock-trading get secret ast-secrets \
  -o go-template='{{range $k,$v := .data}}{{if not $v}}{{$k}}{{"\n"}}{{end}}{{end}}'  # 空のキー
```

> **鍵の実値は端末外へ出さない**（リポジトリ・ログ・Issue/PR に貼らない）。スクリプトは既存値を読み出さず、
> 表示は常にキー名のみである。挙動は `scripts/k8s-local-deploy.test.sh` が CI で固定する。
>
> **ESO（Vault）同期を有効化した環境**（`externalSecrets.appSecrets.enabled=true`・IADR-0094）では
> `ast-secrets` は `ExternalSecret` が所有する。値の投入は
> [Vault 秘匿 runbook](../../../docs/operations/vault-secrets-runbook.md) 側で行い、本スクリプトの env は使わない
> （両方から書くと所有が割れる）。既定はオフ＝手動 Secret 直運用。

### 為替換算（`FRED_API_KEY`）— **日本株取引の必須前提**

> 起点: [#262](https://github.com/endazon/ai-stock-trading/issues/262) / [#257](https://github.com/endazon/ai-stock-trading/issues/257) /
> [#364](https://github.com/endazon/ai-stock-trading/issues/364) /
> [IADR-0107](../../../.ai-context/adr/IADR-0107_base-currency-conversion.md) /
> [IADR-0152](../../../.ai-context/adr/IADR-0152_usd-base-currency-migration.md) /
> 作業仕様書 [`.ai-context/specs/20260728_262_263_fx-key-required-and-secret-preservation.md`](../../../.ai-context/specs/20260728_262_263_fx-key-required-and-secret-preservation.md) /
> [`.ai-context/specs/20260805_364_usd-base-currency.md`](../../../.ai-context/specs/20260805_364_usd-base-currency.md)

統制の金額判定（1 注文金額・日次発注累計・段階資金上限）は**基準通貨＝米ドル**で行う（計画 §3・IADR-0152 決定1）。
非基準通貨（JPY 建て）の銘柄は、USD への換算レートが解決できない限り**新規建てを見送る**（IADR-0107 決定3 の
fail-safe＝「古い/無いレートで発注しない」）。したがって **`FRED_API_KEY` は日本株を取引するための必須前提**であり、
「任意の収集ソース鍵」ではない。

> **#364 で必須となる市場が入れ替わった。** 旧（基準通貨＝JPY）では US 株が本キーを要したが、
> 現在は**主ターゲットの US 株が本キー無しで回り**、日本株が本キーを要する。

| 項目 | 値 | 実装上の根拠 |
| --- | --- | --- |
| 設定点 | `Fx__Provider=fred` ＋ `Fx__Fred__ApiKey`（`values-local.yaml` は `ast-secrets/fred-api-key` を `secretKeyRef`） | `FxRateSourceFactory` |
| 系列 | `DEXJPUS`（円/ドル・系列は**営業日次**だが**公表は H.10 週次**＝月曜・前週金曜まで一括収載）。基準通貨が USD のため **JPY のレートは本系列の逆数**（IADR-0152 決定2・丸めない） | `FredFxOptions.SeriesId` 既定 |
| 鮮度上限（**停止**） | **30 日**（超過した観測は採らない＝レート無し扱い＝新規建てを見送る）。計画 ADR-0022 決定5 の**絶対上限**（#381 / IADR-0174 決定2。旧値 14 日＝#271 / IADR-0112 は FRED の週次公表からの逆算であり、根拠が計画へ移った）。`Fx__MaxRateAgeDays` で変更可・0 以下は既定へ・**30 日超は 30 日へ丸める** | `FxOptions.MaxRateAgeDays` 既定 |
| 鮮度警告（**続行**） | **5 日**（超過すると WRN を出すが、**新規建ては止めず直近レートで続行する**）。計画 ADR-0022 決定4 / §5 の 3 段縮退の中段（#381 / IADR-0174 決定1）。`Fx__StaleRateWarningDays` で変更可・0 以下は既定へ・**鮮度上限超は上限へ丸める**（警告が上限を超えると警告が一度も出ないまま停止するため）。<br>⚠️ **FRED 単独では最新観測の齢が最大 12.84 日まで積み上がるため、警告域に常駐しうる**（日銀アダプタ＝#381 の残りが入るまでの既知の状態） | `FxOptions.StaleRateWarningDays` 既定 |
| キャッシュ TTL | 6 時間（日次系列のため判断サイクルごとに叩かない） | `FxOptions.CacheTtlSeconds` 既定 |
| 既定（未設定時） | `NoOpFxRateSource`（外部へ 1 リクエストも出さない・**起動は落とさない**） | `Fx:Provider` 空/`none`/未知/キー無し |

**米国株（基準通貨）は本キー無しでも従来どおり取引できる。** ドル建て市場はレート 1 が定義から決まるため
FX 源へ問い合わせない。すなわち症状は「**日本株だけ何も起きない**」という形（沈黙）で出る。

**未設定時の観測ログ**（症状 → 原因の辿り方）:

```text
# trade-decision（レート源が未接続）— 判断サイクルごとではなく初回 1 回だけ出る
warn: NoOpFxRateSource を使用中: 為替レート源が未接続のため Usd 建て銘柄の新規建ては見送られます（IADR-0107）。

# Fx__Provider=fred だが鍵が空（＝「有効化したつもり」の典型）
warn: Fx:Provider に fred が指定されていますが、APIキー（Fx:Fred:ApiKey）が未設定のため為替レートを取得しません（no-op へフォールバック・IADR-0107）。

# 実際に見送られた銘柄（LLM 呼び出しより前・銘柄ごと）
warn: 基準通貨への換算レートが解決できないため見送り（発注抑止・安全側）: AAPL market=UnitedStates
```

**切り分け**（ログを待たずに現状を確認する）— 自己申告の `fx-rate` ポートが `none` なら効いていない:

```bash
kubectl -n ai-stock-trading port-forward svc/trade-decision-service 8080:8080 &
curl -s localhost:8080/internal/introspection | tr ',' '\n' | grep -A1 fx-rate
#   "port":"fx-rate"
#   "implementation":"none"     ← 未接続（鍵未設定・provider 未指定・未知の値）。"fred" なら接続済み
```

`implementation` は実際に選択された実装を申告する（`FxRateSourceFactory.ResolveProvider` が単一情報源）。
**`Fx__Provider=fred` を設定していても鍵が空なら `none` を申告する**ため、「設定したのに効いていない」を
ここで検知できる。

```bash
export FRED_API_KEY=<FRED の API キー>   # https://fred.stlouisfed.org/docs/api/api_key.html
scripts/k8s-local-deploy.sh              # ast-secrets/fred-api-key へ反映（既存の他キーは保持される・#263）
```

> 換算を正した結果 AAPL は 1 株あたり約 5.2 万円となり、本番既定の 1 注文金額上限 35,000 円を超えて
> **数量 0＝見送り**になる。これは通貨を正した後の正しい帰結である。経路B では
> [IADR-0108](../../../.ai-context/adr/IADR-0108_simulator-risk-profile.md) の SIMULATE 限定プロファイル
> （`values-local.yaml` の `Risk__SimulatorProfile__Enabled=true`・本番既定は false）が上限をシミュレータ残高に
> 合わせるため発注まで到達する。本番既定での上限見直し・銘柄選定は運用判断として #257 に残置。

### 撤退評価と自動 kill switch（#279 / IADR-0114 決定3）

経路B では実DD 供給（`ObservedDrawdownRefresh__Enabled`）と撤退評価（`WithdrawalEvaluation__Enabled`）を**ともに true** にしている。
ADR-0008（計画リポ） の撤退基準に該当すると、
**撤退評価が自動で kill switch を起動する**（[IADR-0083](../../../.ai-context/adr/IADR-0083_withdrawal-evaluation-driver.md)）。

**発火するのは実弾段階だけ**（`StageGate.AssessWithdrawal`）。過度に身構えないための整理:

| 段階 | 判定 | kill switch |
| --- | --- | --- |
| Stage 0（検証） | `Triggered: false` | **起動しない** |
| Stage 1（ペーパー） | 乖離が説明不能なら `Triggered: true` だが `HaltNewEntries: false` | **起動しない**（降格提案＋通知のみ・[IADR-0085](../../../.ai-context/adr/IADR-0085_paper-withdrawal-notification-dedup.md)） |
| Stage 2/3（実弾） | 実DD ≥ バックテスト最大DD × 倍率 | **起動する** |

現在の段階は Stage 0 で、Stage 2/3 は実弾未解禁（`LiveTradingReleased=false`・IADR-0111 の閂 0）のため到達不能。
つまり**いまの経路B で自動停止が起きることはない**。有効化の実利は「Stage 1 到達後の乖離検出」と
「将来 Stage 2/3 が解禁されたとき最初から効いていること」。以下は実際に起動した場合の手順。

- 起動すると**新規建てが止まる**。既存建玉の損切りは止まらない。
- **解除は自動では起きない**。確認フレーズの入力が要る（[IADR-0097](../../../.ai-context/adr/IADR-0097_killswitch-disengage-confirmation-phrase.md)）。
  フレーズは `DISCORD_BOT_KILLSWITCH_PHRASE`（`ast-secrets/discord-bot-killswitch-phrase`）で与えた値で、
  **未設定なら解除できない**（摩擦を下げない設計）。解除の導線は Discord Bot の制御コマンドか SC-03 の統制状態画面。
- 「dogfood が動かない」ときは、まず kill switch 状態を疑う（`kubectl -n ai-stock-trading logs deploy/risk-management-service` に
  撤退トリガの記録が出る）。**止まったこと自体は統制が効いた結果**であり、無効化ではなく原因（DD の悪化）を確認する。

撤退評価を止めたい場合は `values-local.yaml` の `WithdrawalEvaluation__Enabled` を `"false"` に戻す
（実DD の供給＝観測・記録だけは続き、自動停止のみ起きなくなる）。

### LLM 費用の単価（#303 / IADR-0122 ／ #279 / IADR-0114 決定6）

`LlmPricing__PerModel__<model-id>__InputPer1kTokens` / `__OutputPer1kTokens` は **円 / 1,000 トークン**の**モデル別**単価。
未設定（既定 0）だと `PublishingLlmUsageReporter` が毎回 ¥0 を計上し、費用統制の月次上限（¥15,000）の
80%／100% 判定が**構造的に発火しない**（台帳は動くが金額が積み上がらない）。

用途別モデル割当（計画 `ADR-0014` / MSP/IADR-0112）で `trade-decision`=sonnet-5 / `report-monthly`=fable-5 /
`report-weekly`=opus-5 / `report-daily`=sonnet-5 とモデルが混在するため、単価は**応答が名乗った実効モデル**
（`CompletionApiResponse.Model`）で引く。要求側の希望モデルは根拠にしない（ゲートウェイは越境ルーティング
〈ADR-0010〉で別モデルへ着地し得る）。

| モデル | 公開単価 $/1M（入力/出力） | 投入値 ¥/1k（入力/出力） | 用途 |
| --- | --- | --- | --- |
| `claude-fable-5` | 10 / 50 | `1.637` / `8.186` | `report-monthly`（＝表の最大単価） |
| `claude-opus-5` | 5 / 25 | `0.819` / `4.093` | `report-weekly`・ゲートウェイ既定 |
| `claude-opus-4-8` | 5 / 25 | `0.819` / `4.093` | ADR-0011 が意図する固定先 |
| `claude-sonnet-5` | 2 / 10（恒久化確認済み・2026-08-28。#243） | `0.327` / `1.637` | **`trade-decision`**・`report-daily` |
| `claude-haiku-4-5` | 1 / 5 | `0.164` / `0.819` | （基盤の `diagram-coding`） |

USD→JPY 換算は **163.71**（システムの為替源 FRED `DEXJPUS` と同一系列・IADR-0107）、小数第 3 位で四捨五入。
例: `0.002×163.71=0.32742 ≒ 0.327`。

**fail-safe（IADR-0122 決定3・「安全側 = 0」ではない）**: 表に無いモデル・モデル名なしは**表の最大単価**
（現行 fable-5）へ倒れる。0 に倒すと未知モデルが素通りして月次上限が効かなくなるため、過小計上を作らない側へ倒す。
表そのものが空なら従来キー `LlmPricing__InputPer1kTokens` / `__OutputPer1kTokens`（global 単一ペア・未設定 0）へ倒れる
＝ per-model を持たない既存デプロイは従来どおり動く。

**恒久値ではない**: 為替も公開単価も変動する。`claude-sonnet-5` の $2/$10 は当初「2026-08-31 までの導入価格」
だったが、**Anthropic が 2026-08-28 の確認時点でこれを恒久価格にすると公式発表しており**（2026-09-01 予定
だった $3/$15 への改定は行われない。出典: [Anthropic 公式 Pricing ドキュメント](https://platform.claude.com/docs/en/about-claude/pricing)
の注記 `claude-sonnet-5-introductory-pricing`）、**本表の値は変更不要**である（[#243](https://github.com/endazon/ai-stock-trading/issues/243)）。
他モデルの単価・為替レートは引き続き変動し得るため、乖離が出たら本値を更新する。
本番 `values.yaml` には置かない（変動する外部価格を本番既定に固定しない）。

> **過少申告が残る点**: report-service の実 LLM 散文費用は計上経路自体が無いため、単価を入れても実消費より
> 少なく見積もられる（[#282](https://github.com/endazon/ai-stock-trading/issues/282)）。本表は #282 の解消後に
> 報告書側にもそのまま効く（単価は共有され、種別ごとのモデルで正しく引かれる）。

### Discord の環境固有 ID（`kubectl set env` は使わない）

`GuildId` / `ChannelId` / `AllowedUserIds` / `UserMapping` は**非機密**の識別子であり、chart の設定点
`discord.bot.*`（空既定）から与える。`scripts/k8s-local-deploy.sh` が下表の env を読み、`helm upgrade` へ
`--set-string discord.bot.*` として渡す（未設定=空=差し替えなし＝全拒否のまま）。

| 環境変数 | chart 値 | 設定キー | 備考 |
| --- | --- | --- | --- |
| `DISCORD_BOT_GUILD_ID` | `discord.bot.guildId` | `Notifications:Discord:Bot:GuildId` | 運用サーバー（ギルド）ID |
| `DISCORD_BOT_CHANNEL_ID` | `discord.bot.channelId` | `…:ChannelId` | 運用チャンネル ID |
| `DISCORD_BOT_ALLOWED_USER_IDS` | `discord.bot.allowedUserIds` | `…:AllowedUserIds` | 許可ユーザー ID（カンマ区切り） |
| `DISCORD_BOT_USER_MAPPING` | `discord.bot.userMapping` | `…:UserMapping` | `discordUserId:keycloak利用者名` のカンマ区切り |

```bash
export DISCORD_BOT_GUILD_ID=<ギルドID>                        # 18〜19 桁の数字
export DISCORD_BOT_CHANNEL_ID=<チャンネルID>
export DISCORD_BOT_ALLOWED_USER_IDS=<ユーザーID>,<ユーザーID>   # カンマ区切り
export DISCORD_BOT_USER_MAPPING=<ユーザーID>:owner,<ユーザーID>:ops
scripts/k8s-local-deploy.sh
```

> ⚠️ **`kubectl set env deploy/notification-service ...` で注入しないこと。** `kubectl set env` は当該 env を
> フィールドマネージャ `kubectl-set` の所有にするため、次回の `helm upgrade`（＝`k8s-local-deploy.sh`）が
> 同じフィールドを Helm 所有として apply しようとして **`conflict with "kubectl-set"` で失敗する**。
> **既に競合している場合の解消**（該当 env を削除してから再デプロイ）:
>
> ```bash
> kubectl set env deploy/notification-service -n ai-stock-trading \
>   Notifications__Discord__Bot__GuildId- Notifications__Discord__Bot__ChannelId- \
>   Notifications__Discord__Bot__AllowedUserIds- Notifications__Discord__Bot__UserMapping-
> scripts/k8s-local-deploy.sh
> ```
>
> （`KEY-` は当該 env の削除。削除で `kubectl-set` の所有が外れ、以後は Helm が単独所有する。）

> 手で `helm --set-string` を打つ場合の注意: **`--set` ではなく `--set-string`**（18〜19 桁の snowflake は
> `--set` だと float64 に解釈され `1.234567890123456e+18` に化ける）。また `AllowedUserIds` / `UserMapping` の
> **カンマは `\,` にエスケープ**する（helm の `--set` パーサはカンマを要素区切りとして解釈する）。
> `k8s-local-deploy.sh` 経由ならどちらもスクリプトが処理する。
>
> **機密は values に載せない**: Bot Token・kill switch 確認フレーズ・OwnerAuth 資格情報は従来どおり
> `ast-secrets`（`secretKeyRef`）で与える。

> 値そのものの制約（本設定点より下流・`DiscordBotOptionsReader` のコンパクト形式）: `AllowedUserIds` /
> `UserMapping` はアプリ側が **`,` で要素分割**するため、**keycloak 利用者名に `,` は使えない**（helm への受け渡しは
> エスケープされるが、アプリのパース時に分割される）。`:` は最初の 1 つだけが区切りなので利用者名に含めてよい。
> 形式不正の要素は**黙って捨てられる**（推測で対応付けを作らない＝拒否側・IADR-0062）。

> **プロンプト全量ログは既定オフ（opt-in）**: `values-local.yaml` の `LlmGateway__LogPrompts` は安全既定 `""`（記録しない・
> IADR-0061 決定1）。②実 LLM の要求/生応答（判断根拠・監視銘柄等の機微を含む）をログ基盤に残してプロンプトレベルで
> 検証したいときだけ、report / trade-decision の当該値を `"true"` に変える（ローカル専用の上乗せ values でも可）。

> **G4（日報の確定）はランタイム操作**であり設定化しない。定時サイクルの判断が確定済み日報方針を要求するため、
> 検証時は Discord Bot（`/report confirm` 等・FR-07/14）または該当 API で当日方針を確定してから回す
> （[通知・Bot 運用](../../../docs/operations/operations.md) 参照）。

> **旧 `overlay-cycle.yaml` は不要**: MSP 側ホストの臨時 overlay（`microservices-platform/overlay-cycle.yaml`）を
> `helm upgrade -f overlay-cycle.yaml` で手当てする従来運用は、本プロファイルの committed 化により**不要**になった
> （overlay には #236 の価格文脈が欠けていた点も本プロファイルで解消済み）。overlay ファイルの物理削除は任意。

## 外部連携（fail-safe 既定）

`ast-secrets`（未設定=空=no-op）を明示設定した時のみ有効化する。

| 環境変数 | `ast-secrets` キー | 対象 | 既定 |
| --- | --- | --- | --- |
| `FINNHUB_API_KEY` | `finnhub-api-key` | information-collection | 空=NoOp（#81） |
| `DISCORD_WEBHOOK_URL` | `discord-webhook-url` | notification | 空=NoOp（#15） |
| `DISCORD_OWNERAUTH_CLIENTID` | `discord-owner-auth-client-id` | notification | dev 既定 `ai-stock-trading-owner`（Bot 制御コマンドの OwnerAuth・#226 / IADR-0098） |
| `DISCORD_OWNERAUTH_CLIENTSECRET` | `discord-owner-auth-client-secret` | notification | dev 既定 `dev-only-owner-secret`（realm-export.json と一致・本番は Secret/Vault） |
| （chart 値）`Notifications__Discord__OwnerAuth__TokenEndpoint` | — | notification | AST レルム token エンドポイント（#226。空だと IsEnabled=false→401） |
| （chart 値）`Broker__Provider` / `Broker__Environment` | — | order-execution | `paper`（実発注しない・#13。`broker.tier` で切替・IADR-0111） |
| （chart 値）`LlmGateway__BaseUrl` | — | trade-decision | 空=Placeholder LLM（呼ばない＝常に Hold・#11） |

> **LLM プロバイダ鍵は AST では扱わない**。鍵は MSP の LlmGateway 側（`Llm:ApiKey`）が保持し、AST は
> `LlmGateway__BaseUrl` 経由でゲートウェイを呼ぶだけである（ADR-0010 / IADR-0061 決定6）。
> `ast-secrets` に LLM 鍵を足さないこと。

## #13, #267: ブローカ階層（`broker.tier`）と moomoo（OpenD）発注

発注先は**単一スイッチ `broker.tier`** で選ぶ（IADR-0111）。運用の本番近接順は次の 3 階層である。

| `broker.tier` | 注入される env | 意味 |
| --- | --- | --- |
| `paper`（既定） | `Broker__Provider=paper` | 内蔵擬似発注。実発注しない（fail-safe・IADR-0016） |
| `moomoo-sim` | `Broker__Provider=moomoo` ＋ `Broker__Environment=sim` ＋ OpenD 接続構成 | OpenD 経由の **SIMULATE 発注**。実弾は撃たない |
| `moomoo-live` | — | **実弾。未解禁につき `helm template` が `fail` する**（外周の閂・IADR-0111） |

- `tier` は provider（証券会社）と environment（取引環境）の直交 2 軸へ展開される。将来の証券会社追加は
  tier 値を 1 つ足すだけで済む（ADR-0002 が挙げる立花証券 e支店 API 等）。
- 未知の tier も描画時に `fail` する。アプリ側も未知値・`paper`＋`live` の矛盾を起動時に停止させる。
- 稼働中の階層は `GET /internal/introspection` の `broker` ポートが自己申告する（`paper` / `moomoo-sim`）。
- **`moomoo.enabled` は非推奨エイリアス**である。`broker.tier` を指定していないときだけ `moomoo-sim` として
  解釈され（既存構成の互換維持）、両方を矛盾して指定すると描画時に止まる。新規は `broker.tier` を使うこと。

### ⚠️ `paper` と `moomoo-sim` は「どちらも実弾でない」だけで**別物**である（#268）

検証で最も踏みやすい取り違えである。**`paper` の約定を moomoo の模擬口座で探しても構造的に見つからない。**

| 観点 | `paper`（既定） | `moomoo-sim` |
| --- | --- | --- |
| 約定の主体 | **AST プロセス内蔵**（`PaperBrokerAdapter`）。参照価格で即時全量約定 | moomoo 側の模擬取引エンジン |
| 外部通信 | **無し**（OpenD へ 1 リクエストも出さない） | 有り（`opend:11111`） |
| 状態遷移 | 発注＝即 `Filled` | 発注直後は `Accepted`、約定は**後追い** |
| 残高・注文履歴 | AST の内部台帳（`executed_orders` / `trade_fills`）のみ。現金・建玉は仮想 | **moomoo 模擬口座が権威**（moomoo アプリで目視可）。AST 台帳は現状これを取り込まない（[#270](https://github.com/endazon/ai-stock-trading/issues/270)） |
| `OrderId` の形 | 32 桁 hex（`Guid` の `"N"`） | moomoo 採番の数値（例 `9049618348733212748`） |

**どちらで動いているかの確認**（詳細・識別手順は
[発注経路の区別と識別 Runbook](../../../docs/operations/broker-execution-paths-runbook.md)）:

```bash
# 1) 自己申告（"paper" / "moomoo-sim"）
kubectl -n ai-stock-trading port-forward svc/order-execution-service 8080:8080 &
curl -s localhost:8080/internal/introspection | tr ',' '\n' | grep -A1 '"broker"'

# 2) moomoo 経路だけが出すログ（出ていなければ paper で回っている）
kubectl -n ai-stock-trading logs deploy/order-execution-service | grep -E "OpenD 接続完了・SIMULATE 口座 accId=|moomoo SIMULATE 発注成功"
```

moomoo 側の注文は**備考（remark）に `DecisionId`**（ハイフン無し 32 桁 hex）が入るため、AST の判断と 1 対 1 で
突き合わせられる（IADR-0092）。

以下は `moomoo-sim` 階層（＝旧 `moomoo.enabled=true`）の詳細である。既定 **無効**（`Broker__Provider=paper`
＝実発注しない・fail-safe・IADR-0016）。有効化すると `order-execution` へ moomoo 発注構成が注入される
（**SIMULATE 限定**・実弾は撃たない）。

> ⚠️ **前提**（#13・#124 はマージ済み。以下は**運用側で揃える**もの）:
> - **常駐 OpenD が稼働し、ログイン済みであること**（下記「#132: OpenD の本番配備」または `deploy/opend/k8s` の
>   生 manifest）。OpenD は初回に**有人**のデバイス検証が要る。listen だけしていてもログイン前は使えない。
> - **Secret `moomoo-rsa`**（OpenD と**同一の RSA 秘密鍵**）。cross-network（worker→opend）の trade は RSA 暗号化必須。
>   鍵が構成済みで不在なら `order-execution` は**起動時 preflight で停止する**（#132。従来は黙って非暗号化に倒れ、
>   「接続はするが trade だけ失敗する」形でしか表面化しなかった）。

**有効化**（OpenD 常駐＋`moomoo-rsa` Secret 作成後）:
```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set broker.tier=moomoo-sim
```

有効化すると `order-execution` へ `Broker__Provider=moomoo` ＋ `Broker__Environment=sim` ＋
`Broker__Moomoo__OpenD__{Host=opend,Port=11111, RsaPrivateKeyPath=/opt/opend/rsa/opend_rsa.pem}` が注入され、
`moomoo-rsa` が read-only（`defaultMode: 0400`）でマウントされる。発注時に OpenD 未接続なら
**アダプタが per-order `Rejected` に倒す**（fail-safe）。
実結合は #13 で実 OpenD の SIMULATE 口座に対し live 検証済み（IADR-0056）。

> **実弾（`TrdEnv_Real`）は撃たない。** `broker.tier=moomoo-sim` にしても取引環境は `TrdEnv_Simulate` 固定である
> （IADR-0016 / IADR-0056）。実弾は多重に塞いである: `broker.tier=moomoo-live` は**描画時に `fail`**（IADR-0111）、
> 環境変数で `Broker__Environment=live` を直接与えても `LiveTradingGate`（閂 0）が**起動時に停止**、
> `Broker:Moomoo:TrdEnv` に `real` 等を与えても**起動時に停止する**
> （黙って SIMULATE で流して「実弾で動いている」と誤認させないための閂・#132 / IADR-0060）。実弾解禁には別 IADR と
> 前提条件の充足が要る（[運用仕様書の本番切替チェックリスト](../../../docs/operations/operations.md#opend-の本番切替チェックリスト132)・
> [実弾切替 Runbook](../../../docs/operations/live-trading-cutover-runbook.md)）。

## #132: OpenD の本番配備（`opend.enabled`）

既定 **無効**（何も描画しない＝fail-safe）。dev の現行経路は `deploy/opend/k8s` の生 manifest で、そちらは残してある
（IADR-0060 決定 1）。本 chart 経路は**本番配備**用で、既定値では生 manifest と同等に描画される。

```bash
# 前提: イメージのビルド＆import（scripts/opend-build.sh）、Secret moomoo-credentials / moomoo-rsa の作成。
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set opend.enabled=true \
  --set opend.nodeSelector."kubernetes\.io/hostname"=<安定ノード名>
# 初回のみ有人のデバイス検証（画像 CAPTCHA / SMS）:
kubectl -n ai-stock-trading attach -it deploy/opend
```

> ⚠️ **ノードを固定すること**。無人再ログインの成立条件は「デバイス信頼の永続化（PVC）＋ **egress IP の安定**」で、
> egress IP はノードの NAT 後 IP である（**Pod IP は無関係である**。ADR-0024 決定1）。ノードを跨いで
> 再スケジュールされたときに再検証が要るかは **ADR-0024 決定5-1 で未検証**であり、**安全側に「有人の再検証に戻る」と想定する**
> （マルチノード/クラウドでの実測は **#132 で未了**）。

### 非 root 実行へ切り替える（既定は root・**未検証**）

OpenD はデバイス信頼を `$HOME/.com.moomoo.OpenD` に書くため、非 root 化は **HOME の再調整とセット**で行う。
イメージ側は uid/gid **10001** と `/home/opend` を用意済み（`USER` は切り替えていない＝既定 root）。

```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set opend.enabled=true \
  --set opend.home=/home/opend \
  --set opend.securityContext.runAsNonRoot=true \
  --set opend.securityContext.runAsUser=10001 \
  --set opend.podSecurityContext.fsGroup=10001 \
  --set opend.rsaSecretDefaultMode=288   # 0440（--set は 10 進で渡す。values ファイルなら 0440 と書ける）
```

> ⚠️ **既定 ON にしていない理由**（IADR-0060 決定 2）: HOME が変わると**確立済みのデバイス信頼を失い、有人検証から
> やり直しになる恐れ**がある。実 OpenD でしか確かめられず、**#132 の実測フェーズで未検証**。切替時は既存 PVC の
> `.com.moomoo.OpenD` を新 HOME へ移すか、再検証を覚悟すること。
>
> ⚠️ `rsaSecretDefaultMode` は **k8s のファイルモード（8 進を 10 進で表す整数）**。values ファイルでは `0400` / `0440`
> と 8 進で書ける（YAML が解釈する）が、`--set` では **10 進**（`256` / `288`）で渡すこと。非 root では Secret の所有 uid が
> root のままなので、`fsGroup` を付けたうえで**グループ読み（0440）**が要る。

## #132: 秘匿情報の External Secrets（Vault）受け口（`externalSecrets.enabled`）

既定 **無効**。有効化すると `moomoo-credentials` / `moomoo-rsa` を Vault から同期する `ExternalSecret` を描画する。

```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set externalSecrets.enabled=true \
  --set externalSecrets.secretStoreRef.name=vault-backend
```

> ⚠️ **これは Vault 化の充足ではない**（IADR-0060 決定 4）。ストア（Vault / External Secrets Operator）は **#24 の管掌**で
> 本リポジトリには無く、CRD が無いクラスタで有効化すると apply が失敗する。IADR-0056 §3 が実弾解禁の前提に挙げる
> 「秘匿情報の Vault 化」は**未充足のまま**である。

### #24, IADR-0094: API 鍵群（`ast-secrets`）の Vault 同期（`externalSecrets.appSecrets.enabled`）

既定 **無効**。`externalSecrets.enabled=true` と併せて有効化すると、API 鍵群の Secret `ast-secrets` を Vault の
単一 KV から `dataFrom.extract` で同期する `ExternalSecret` を描画する（**平文の鍵は values にも manifest にも無い**）。

```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set externalSecrets.enabled=true \
  --set externalSecrets.appSecrets.enabled=true \
  --set externalSecrets.secretStoreRef.name=vault-backend
```

> Vault 側のプロパティ名は **Secret キー名**（`finnhub-api-key` / `service-auth-client-id` 等）に一致させる。
> 欠けた鍵は同期されず、消費側 `secretKeyRef.optional=true` で許容する（fail-safe）。既定オフ＝手動 Secret 直運用を維持。
> 手順は [`docs/operations/vault-secrets-runbook.md`](../../../docs/operations/vault-secrets-runbook.md)。GitOps（ArgoCD）は
> [`deploy/argocd`](../../argocd/README.md)、可観測性は [`docs/observability/observability.md`](../../../docs/observability/observability.md)。

## #121: 取引サイクル CronJob

既定 **無効**（在来 in-process ポーリング IADR-0023 を維持＝fail-safe）。有効化:

```bash
helm upgrade --install ast deploy/helm/ai-stock-trading -n ai-stock-trading \
  --set tradingCycle.cronjob.enabled=true
```

有効化すると CronJob が収集の run-once（`POST /internal/collection/run-once`）を叩き、同時に情報収集へ
`Collection__Trigger=External` が注入され in-process ポーリングは停止する（二重起動防止）。既定（無効）は
in-process 維持＝fail-safe。休場日ガードは下流 TradeDecision の市場カレンダー（IADR-0023）が担保する。
run-once/モード切替は実装済み（#121 / IADR-0054）。
