---
title: IADR-0114 経路B のパリティ回復は「実効するトグルだけ」を values-local で入れ、SEC の連絡先 UA は ast-secrets 経由で供給する
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-10, FR-15, FR-20, ADR-0004, ADR-0008, IADR-0064, IADR-0100, IADR-0103, IADR-0109]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0114: 経路B のパリティ回復は実効するトグルに限り、SEC の連絡先 UA は `ast-secrets` 経由で与える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-29
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-01（情報収集）、FR-10（リスク統制・時価評価）、FR-15（バックテスト＝段階実績）、FR-20（段階ゲート・撤退基準）、
  ADR-0004（情報源の選定＝案A+）、ADR-0008（段階ゲートと撤退基準）
- 対象 Issue: [#279](https://github.com/endazon/ai-stock-trading/issues/279)（傘）、[#164](https://github.com/endazon/ai-stock-trading/issues/164)、[#9](https://github.com/endazon/ai-stock-trading/issues/9)
- 関連する実装仕様書: [20260729_279_values-local-parity](../specs/20260729_279_values-local-parity.md)
- 関連 IADR: [IADR-0064](IADR-0064_official-source-connectors.md)（公式コネクタ・ソース単位の縮退・規約由来の義務を構成必須化）、
  [IADR-0100](IADR-0100_route-b-values-local-standing-config.md)（経路B の恒常設定＝本 ADR が拡張する土台）、
  [IADR-0103](IADR-0103_observed-drawdown-supply.md)（実DD 供給ドライバ・3 段の opt-in）、
  [IADR-0083](IADR-0083_withdrawal-evaluation-driver.md)（撤退の定期評価ドライバ＝本作業では有効化しない）、
  [IADR-0109](IADR-0109_deploy-secret-preservation.md)（`ast-secrets` の差分パッチ同期＝新規キーの追加先）、
  [IADR-0102](IADR-0102_discord-env-ids-via-values.md)（環境固有値を values 経路で与える先例＝本件が採らなかった代替案）、
  [IADR-0058](IADR-0058_helm-chart-ci-gate.md)（Helm 描画の CI ゲート）

## 背景

監査（[#279](https://github.com/endazon/ai-stock-trading/issues/279)）で、経路B（ローカル k8s / SIMULATE）には
**実装済みだが現環境で未結線／無効化のまま**の機能が複数残っていることが分かった。これらを有効化して
「本番同等ローカル dogfood」に近づけたい。一方、候補として挙がったトグルを実コードで検証すると、
**設定を変えても実効しないもの**と、**結線するとかえって退行するもの**が混在していた。

「有効化した」という見かけだけが増えると、休眠に気づけない状態が悪化する（統制が効いているつもりで効いていない）。
したがって「何を入れるか」と同じ重さで「**なぜ入れないか**」を記録に残す必要がある。

## 決定

### 決定1: 経路B で有効化するのは「実効することを実コードで確認できたトグル」だけに限る

本作業で `values-local.yaml` に入れるのは次の 3 つに限る。いずれも**新たな外部資格情報を要求しない**。

| トグル | 対象 | 実効することの根拠 |
| --- | --- | --- |
| `ObservedDrawdownRefresh__Enabled="true"` | risk-management | IADR-0103 が挙げる不活性条件（時価評価が無効）は values-local では既に解消済み（`MarketData__EnableMarkToMarket="true"`）。かつ経路B の既定ブローカは paper で擬似約定が台帳へ入るため `DrawdownRatio` が動く |
| `Collection__Source__Provider` へ `sec-edgar` 追加＋`Ciks__0`／`UserAgent` | information-collection | `InformationSourceFactory` は列挙されたソースのみ生成し、UserAgent と CIK の**両方**が非空のときだけ SEC EDGAR を有効化する |
| `Collection__Source__Provider` へ `fred` 追加＋`SeriesIds__0/__1` | information-collection | 同上。API キーは Fx 換算（IADR-0107 / #262）で**既に投入済み**の `ast-secrets/fred-api-key` を再利用する |

### 決定2: SEC EDGAR の連絡先入り User-Agent は `ast-secrets` 経由で供給する（values に直書きしない）

SEC は規約で連絡先（実在のメールアドレス）入りの User-Agent を要求し、IADR-0064 決定4 はこれを構成必須化している。
これは**環境固有の個人情報**であり、リポジトリへコミットしてはならない。供給経路として次を比較した。

| 案 | 内容 | 評価 |
| --- | --- | --- |
| (a) values へ直書き | `values-local.yaml` にメールアドレスを書く | **却下**。個人情報のコミット。CLAUDE.md の禁止事項に該当 |
| (b) chart の設定点＋`--set-string` | IADR-0102（Discord ID）と同じ template 上書き方式 | 却下。templates の改修が要り、本番描画のバイト等価を都度証明し直す負担が増える。値は 1 つで、汎用化の必要が無い |
| (c) `ast-secrets` の新規キー（採用） | `sec-edgar-user-agent`（`optional: true`）＋`SEC_EDGAR_USER_AGENT` env | **採用**。templates 不変＝バイト等価が自明。IADR-0109 の差分パッチ同期（保持・空上書き中断）を**追加実装なしで継承**する |

機密ではない値を Secret に置くことになるが、`ast-secrets` は本リポにおいて「運用者がデプロイ時に与える環境固有値」の
既存の受け口であり（`service-auth-client-id` のような非機密値も既に同居している）、新たな供給経路を増やすより一貫する。

- fail-safe: キー未設定／空 → env が空 → `InformationSourceFactory` が **SEC EDGAR だけ**を警告つきで除外する
  （finnhub・fred は有効なまま。IADR-0064 決定1「1 ソースの構成不備で案A+ 全体を止めない」）。

### 決定3: 撤退の実行側（`WithdrawalEvaluation:Enabled`）も有効化し、撤退基準を実際に発火させる

実DD の供給（決定1）だけでは撤退基準の**入力**が観測・記録されるにとどまり、自動停止は起こらない。
`WithdrawalEvaluation:Enabled=true` を併せて入れることで、ADR-0008 の撤退基準が実際に評価・発火する状態になる。

**代償を明記する**: 条件成立時は撤退判定が**自動で kill switch を起動**し、解除には確認フレーズが要る（IADR-0097）。
すなわち **dogfood は人手で解除するまで停止する**。これは「止まってよいか」という運用判断であって実装判断ではないため、
利用者へ提示して**明示的な承認を得たうえで**有効化した。停止時の解除手順は chart README に記す。

IADR-0103 が述べる「自動停止までに 3 つの明示的な有効化を要する」構造は、経路B では 3 つとも満たされる
（時価評価＝IADR-0100 で既存、実DD 供給＝決定1、撤退評価＝本決定）。本番 `values.yaml` は 3 つとも既定 false のまま。

### 決定4: 本番 `values.yaml`・`templates/`・`Chart.yaml` は不変とし、バイト等価を描画で検査する

ArgoCD は `valueFiles` を持たず `values.yaml` のみを描画する（IADR-0100）。本作業の変更は
`values-local.yaml`（`-f` で明示したときだけ効く）と `scripts/`・`.github/` に閉じる。

`.github/workflows/helm.yml` の既存 2 ステップへ、①既定描画に本作業の有効化痕跡（実DD 供給・撤退評価・LLM 単価・
公式情報源の実値）が現れないこと ②values-local 描画でそれらが実際に ON になり、単価が期待値どおりであること
を追加する。さらに **Helm がリストを「置換」する**性質への防御として、
**values-local 描画が既定描画の env 名を 1 つも失っていないこと**を検査するステップを新設する
（`extraEnv` を上書きする values-local では、本番側にキーが増えたときに写し忘れると当該キーが**消える**）。

### 決定5: 「入れない」と判断したものは根拠つきで記録する

以下は候補に挙がったが、実コードでの検証の結果 **設定変更では実効しない／結線すると退行する**ため入れない。
将来「なぜ未結線なのか」を再調査しないで済むよう、根拠をここに固定する。

1. **`KnowledgeBase:Search:BaseUrl`（RAG 検索）— 三重に不活性**
   - AST の `HttpKnowledgeBaseSearch` は送信本文に ABAC `Scope` を含めない（IADR-0072 決定5 が明記）。
     platform 側 `HybridSearchService` は `Scope is not { GrantsAccess: true }` で**無条件に空を返す**（deny-by-default）。
   - `HttpKnowledgeBaseWriter` が使う `POST /documents` は**カタログ登録（メタデータ）のみ**で本文を受け取らない。
     本文取り込みによる検索可能化は IADR-0069 のスコープ外。つまり検索対象の本文が KB に存在しない。
   - RAG 文脈のプロンプトインジェクション対策が [#252](https://github.com/endazon/ai-stock-trading/issues/252) で未着手。
     結線は未サニタイズの外部収集文を LLM プロンプトへ入れることを意味する。
   - なお information-collection は `IKnowledgeBaseSearch` を**消費していない**（DI 登録のみ）ため、そちらは元より無意味。

2. **`MarketMonitor:BaseUrl`（watchlist の権威源）— 結線すると取引サイクルが沈黙する**
   - market-monitor の watchlist は `MonitorDefaults.CreateSettings()`（`MonitoredSymbols = []`）で**空にシード**される。
   - `HttpWatchlistProvider` の fallback は非 2xx・timeout・null 応答に限られ、**200＋空配列は正常応答として `[]` を返す**。
     結果、定時サイクルの判断対象がゼロになる（構成ベース watchlist へは戻らない）。
   - 結線するには watchlist の seed が前提だが、`Add` は OwnerOnly かつ actor／reason 必須で、
     デプロイスクリプトから自動化するには owner トークンが要る。別作業として切り出す。

3. **`Reconciliation:Enabled`（発注予約の自動リコンサイル）— paper では自己修復のみ**
   - 実照会プローブは `Broker:Provider=moomoo` かつ `Reconciliation:UseBrokerProbe=true` のときだけ配線される（IADR-0092）。
     経路B の既定 paper では `IndeterminateReservationBrokerProbe`＝解放も終端化もしない。
   - 巡回間隔は `Math.Clamp(IntervalHours, 1, …)` で**下限 1 時間**にクランプされ、短周期での検証もできない。
   - 対象領域が [#270](https://github.com/endazon/ai-stock-trading/issues/270)（moomoo 経路の約定伝播）と重複する。

4. **CronJob（`tradingCycle.cronjob.enabled`）**: 既定 disabled が正。in-process ポーリング（IADR-0023）が現行の正経路で実害なし。
   有効化は収集の run-once エンドポイント（#121）実装が前提。

5. **Prometheus の scrape target**: AST chart には metrics／scrape 設定が**存在しない**。AST 側 values で是正できる対象が無い。

### 決定6: LLM 単価は経路B 限定で実値を与え、出典・換算率・時点を values のコメントに残す

`LlmPricing:InputPer1kTokens` / `OutputPer1kTokens` は `deploy/` のどこにも設定されておらず既定 0 だった。
そのため `PublishingLlmUsageReporter` は毎回 **¥0 を計上**し、費用統制の月次上限（¥15,000）が
**構造的に発火しない**（台帳は動くが金額が積み上がらない）。経路B で実単価を与えて統制を実効化する。

実コードで確認したスキーマ（投入前の必須確認事項）:

| 観点 | 実際 | 出典 |
| --- | --- | --- |
| 粒度 | **global 単一ペア**（per-model ではない）。`TradeDecisionService.Worker/Program.cs` が DI 時に 1 組だけ読む | `ParsePricePer1k(cfg["LlmPricing:InputPer1kTokens"])` |
| 単位 | **円 / 1,000 トークン** | `LlmPricing.Compute` のコメント「いずれも円」・月次上限が ¥15,000 |
| 解釈 | `InvariantCulture` で解析し、**正値でなければ 0** に倒す | `ParsePricePer1k` |

したがって per-model 表ではなく、**主用途 = trade-decision の実効モデル 1 つ分**を投入する。
実効モデルは `Decision:PrimaryModel`／`SecondaryModel` が未設定（null）でゲートウェイ既定に従うため、
MSP/ADR-0025 により **`claude-opus-5`**（IADR-0101）。ADR-0011 が意図する固定先 `claude-opus-4-8` も**同単価**のため、
どちらに解決されても投入値は変わらない。

- 公開単価（2026-07 時点）: opus 系 = 入力 **$5** / 出力 **$25**（いずれも 1M トークン）
- 1k 換算: $0.005 / $0.025 → USD→JPY 換算 **163.71**（システムの為替源 FRED `DEXJPUS` と同一系列・IADR-0107）
- 投入値: `0.005 × 163.71 = 0.81855 ≒ **0.819**` / `0.025 × 163.71 = 4.09275 ≒ **4.093**`（切り上げ側＝統制に安全）

**恒久値ではない**: 為替も公開単価も変動する。sonnet-5 の $2/$10 は 2026-08-31 までの導入価格であり、
将来 `Decision:PrimaryModel` を sonnet 等へ切り替えるなら本値の再計算が要る。時点・出典・換算率を values のコメントへ残し、
再評価は [#243](https://github.com/endazon/ai-stock-trading/issues/243) に委ねる。

本番 `values.yaml` には置かない（外部価格の変動をリポジトリの本番既定に固定しない）。
なお本値が効くのは**判断側のみ**で、report-service の散文費用は計上経路自体が無い（[#282](https://github.com/endazon/ai-stock-trading/issues/282)）。

## 根拠

- **見かけの有効化は休眠より悪い**。実効しないトグルを入れると「結線済み」という記録だけが残り、
  次の監査で同じ調査をやり直すことになる。実コードで実効性を確認できたものだけを入れ、
  残りは根拠つきで「入れない」と宣言するほうが、状態の可視性が高い。
- **fail-safe の一貫性**。投入するトグルはいずれも「供給が無ければ従来どおり no-op」に倒れる
  （実DD は台帳が空なら 0 のまま、SEC/FRED は必須構成を欠けば当該ソースのみ除外、単価は不正値なら 0）。
  唯一の例外が撤退評価（決定3）で、これは**能動的に停止させる**トグルであるため利用者承認を要件とした。
- **本番への影響ゼロを構造で保証する**。templates を触らない選択（決定2 (c)）により、
  バイト等価の証明が「values.yaml と templates を変更していない」という事実に還元される。

## 影響

- 経路B で SEC EDGAR（AAPL の開示）と FRED（DEXJPUS / DGS10）が収集対象に加わり、収集件数が増える。
  KB 保存・`InformationCollected` 発行・報告書への反映が実データで動く。LLM 費用は増えない（収集は LLM を使わない）。
- 実DD が段階実績台帳へ latch され、ADR-0008 の撤退基準が**実際に発火する**（決定3）。条件成立時は
  自動で kill switch が起動し、**解除するまで新規建てが止まる**（解除は確認フレーズ必須・IADR-0097）。
  これは経路B（ローカル SIMULATE）限定で、本番既定は従来どおり無効。
- LLM 費用が実単価で計上され、月次費用上限（¥15,000）の 80%／100% 判定が実効化する（決定6）。
  従来は毎回 ¥0 計上で発火し得なかった。ただし report-service の散文費用は計上経路が無いままで、
  依然として**実消費より少なく**見積もられる（[#282](https://github.com/endazon/ai-stock-trading/issues/282)）。
- `ast-secrets` に `sec-edgar-user-agent` が増える。未設定の既存環境では空のまま＝挙動は従来と同じ。
- 本番（ArgoCD＝`values.yaml` のみ）の描画は不変。実弾の閂（IADR-0111 / IADR-0060）にも触れない。

## 代替案（検討したが採らなかったもの）

- **候補を全部入れる**: 却下。決定5 の 1・2 は実効しないか退行する。とくに 2 は取引サイクルを停止させる。
- **SEC の UA を chart の設定点にする（IADR-0102 方式）**: 却下（決定2 (b)）。templates 改修に見合う汎用性が無い。
- **`WithdrawalEvaluation` は実DD 供給だけ入れて据え置く**: 却下（決定3）。入力を観測するだけでは撤退基準は
  発火せず、「統制が効いているつもりで効いていない」状態が続く。停止の代償を提示して利用者承認を得た。
- **LLM 単価を本番 `values.yaml` にも置く**: 却下（決定6）。為替・公開単価は変動し、sonnet-5 の $2/$10 は
  2026-08-31 までの導入価格。変動する外部価格をリポジトリの本番既定に固定すると陳腐化が検出されない。
- **`LlmPricing` を per-model 化する**: 本作業の範囲外。現行スキーマは global 単一ペアで、
  複数モデルを使い分けるなら計上側（`PublishingLlmUsageReporter`）が応答の `Model` を見て単価を引く改修が要る。
- **RAG 検索を「実効するように」直す**: 本作業の範囲外。ABAC Scope の送出・KB 本文取り込み・#252 のサニタイズが揃って初めて意味を持つ。
