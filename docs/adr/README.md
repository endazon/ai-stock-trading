# 実装ADR（Implementation ADR）

本リポジトリ内の意思決定記録（Implementation ADR）の索引である。実装に閉じた技術・設計・運用の決定を `IADR-XXXX` として記録する（必須）。

## 計画ADR との違い

| | 計画ADR | 実装ADR |
| --- | --- | --- |
| 場所 | 計画リポ `projects/<name>/07_adr/` | 本リポ `docs/adr/` |
| ID | `ADR-XXXX` | `IADR-XXXX` |
| 対象 | 上流の意思決定（プロダクト全体） | 実装レベルの意思決定（内部設計・ライブラリ選定等） |

> 計画に影響する決定は、実装ADR に記録するのではなく `/plan-feedback` で計画側へ環流する。

## 運用ルール

- 1 ファイル = 1 意思決定。`IADR-<連番4桁>_<タイトル>.md`（雛形 `docs/templates/adr_template.md`、`/new-spec adr` で採番作成）。
- 連番はリポジトリ内で一意・昇順・欠番なし。
- 状態は `Proposed / Accepted / Deprecated / Superseded`。既存決定を覆す場合は新 IADR を作り、旧 IADR に `Superseded by IADR-XXXX` を追記する。
- 重要な実装判断は必ず IADR に残す（必須）。

## 一覧

> 注（採番の経緯）: 本 issue #14（対話的確定・明細）の IADR は、作成時点で `IADR-0037`〜`IADR-0041` を並行作業中の複数ブランチが採番中だったため、番号衝突回避として調整済みの `IADR-0042` を用いる。当時暫定欠番だった `IADR-0037`〜`IADR-0041` は並行ブランチの `develop` マージにより充足済みで、上記「欠番なし」ルールは現時点で満たされている（下表参照）。今後の並行採番時も、`develop` の最新版号を確認して連番を採る。
>
> 注（並行採番の充足）: `IADR-0052`〜`0056` は複数の並行ブランチが採番していたが、develop へのマージで順次充足された（本 PR の `IADR-0055`＝費用計測を含め欠番なし）。今後の並行採番時も `develop` の最新版号を確認して連番を採る（上記 0037〜0041 と同じ運用）。
>
> 注（`IADR-0059` の番号衝突と解消）: 並行ブランチの PR #144（重複排除ストアのパージ）と PR #143（OpenD 本番化）が、いずれも `develop` の最新版号を見ないまま `IADR-0059` を採番し、両者のマージで**同番号 2 ファイル**が生じた。先着（#144・先にマージ）が `IADR-0059` を保持し、後着（#143）を採番し直して解消した（後着の現在の番号は `IADR-0060`。当時は下記の経緯で一旦 `0061` を用いた）。
>
> 注（`IADR-0060` の欠番と解消）: 上記の採番し直しは、当時未マージだった PR #145（FR-17・バージョン付き全体前提条件の s2s 読み取り）が `IADR-0060` を採番中だったため、`0060` を避けて `0061` を用いた。しかし #145 は最終的に `0060` ではなく **`IADR-0064` としてマージされた**（`c0442e1`）ため、予約扱いだった `0060` が**恒久的な欠番**として残った。この欠番は、`0061`〜`0065` を 1 つずつ繰り下げて `0060`〜`0064` とすることで解消済みである（既存の連続部分 `0000`〜`0059` は不変。5 件の決定内容は変更していない）。**現在、本索引は「一意・昇順・欠番なし」を満たす**（`IADR-0000`〜`IADR-0064` の 65 件）。経緯は [作業仕様書](../specs/20260717_iadr-0060-gap-renumber.md) を参照。
>
> 注（他リポジトリの IADR との区別）: 本リポの文書には microservices-platform（上流）の IADR を**無修飾で**参照する箇所がある（`IADR-0046` / `IADR-0048` / `docs/specs/20260712_107_runtime-scaffold.md` / `docs/specs/20260712_109_dependabot-gitsubmodule.md` / `docs/specs/20260712_ADR-0001_unit-repo-layout.md`）。**platform の採番空間は本リポとは別物**であり、同じ番号でも別の決定を指す。本リポの番号を動かす作業では、これらを巻き込まないこと。新たに上流を参照するときは `microservices-platform IADR-XXXX` のようにリポジトリ名で修飾する。
>
> 注（採番前の確認手順）: 上記 2 件の衝突はいずれも「`develop` の最新版号しか見ずに採番した」ことが原因である。**採番前には `develop` に加えて未マージの全ブランチ**を確認する（例: `for b in $(git branch -r); do git ls-tree --name-only $b docs/adr/; done` で使用中の番号を洗う）。あわせて**採番した PR で必ず本索引に行を追記する**（索引が更新されていれば次の採番者が衝突に気づける）。

| IADR | タイトル | 状態 |
| --- | --- | --- |
| IADR-0000 | 実装意思決定の記録方針 | Accepted |
| IADR-0001 | リポジトリ構成と技術スタック | Accepted |
| IADR-0002 | TradingDefaults の既定値は全体前提条件からの逆算値として明示する | Accepted |
| IADR-0003 | ポジションサイジングは取引判断サービスが行い、RiskEvaluator は検証のみとする | Accepted |
| IADR-0004 | エントリー/手仕舞いは建玉効果（PositionEffect）で判定し、売買方向から分離する | Accepted |
| IADR-0005 | 段階資金上限は保有取得額合計＋当該注文額（コストベース累計）で判定する | Accepted |
| IADR-0006 | 相場操縦パターン禁止はガード設定＋判定ポートの拡張点として用意する | Accepted |
| IADR-0007 | 証券会社拒否は OrderStatus.Rejected で表し、リスク事前拒否と区別する | Accepted |
| IADR-0008 | 日次損失上限は実現損益と含み損益の合算で判定する | Accepted |
| IADR-0009 | 非同期イベント契約は Markdown 通信仕様で管理し、OpenAPI は同期 API 専用とする | Accepted |
| IADR-0010 | リスク管理サービスの層構成とホスト化スライス方針 | Accepted |
| IADR-0011 | 基盤ランタイム Foundation は最小移植しコピー＋AiStockTrading 命名で持つ | Accepted |
| IADR-0012 | リスク管理設定は単一行 JSON＋バージョン列で永続化し楽観的排他制御する | Accepted |
| IADR-0013 | platform 由来 Foundation は本番非使用の最小 shim として TestSupport に物理分離する | Accepted |
| IADR-0014 | 市場監視は検知しイベントを発行、損切り執行はリスク管理が担う（責務境界とイベント契約） | Accepted |
| IADR-0015 | 損切りの決済注文はスクリーニングを通さず無条件に Close 承認を発行する | Accepted |
| IADR-0016 | 発注執行は安全既定（ペーパー）とし、moomoo 実発注は PoC まで構成でゲートして実弾を撃たない | Accepted |
| IADR-0017 | 取引判断サービスは LLM をポートで抽象化し、方針/LLM 不在は取引しない安全既定・サイジングは残枠 min で行う | Accepted |
| IADR-0018 | ポートフォリオ状態は追記専用取引台帳からの純射影で供給する | Accepted |
| IADR-0019 | 監査ログは専用サービスが全ドメインイベントを購読し追記専用台帳へ記録する | Accepted |
| IADR-0020 | 通知は既定で外部送信しない安全既定とし、実 Discord 送信は構成で明示有効化する | Accepted |
| IADR-0021 | 全体前提条件は専用の設定サービスが所有し、バージョン管理・変更履歴・イベント発行で一元管理する | Accepted |
| IADR-0022 | 情報収集は既定で外部接続せず、取得テキストをデータとして分離する | Accepted |
| IADR-0023 | 定時/イベント駆動サイクルは取引判断で合流させ、開場日ゲートは市場カレンダーで行う | Accepted |
| IADR-0024 | 報告書サービスが確定管理と確定済み日報方針を所有し、確定はイベントで通知する | Accepted |
| IADR-0025 | 損益集計は前提条件と取引台帳を入力とする純関数で行い、税は利益にのみ課す | Accepted |
| IADR-0026 | 注文相関を持たないイベントは自然キーから決定的 UUID（v5）で相関させる | Accepted |
| IADR-0027 | 費用統制は専用サービスが月次費用台帳を持ち、純関数で間隔延長/停止を判定する | Accepted |
| IADR-0028 | 取引判断は確定済み日報方針を報告書サービスから同期 API で照会する | Accepted |
| IADR-0029 | サイジング文脈はリスク管理が導出・所有し、取引判断は同期 API で照会する | Accepted |
| IADR-0030 | 保有ポジションはリスク管理が #63 台帳から射影・所有し、市場監視は同期 API で照会する | Accepted |
| IADR-0031 | 費用統制の間隔延長/停止は定時サイクル poller が同期照会して適用する | Accepted |
| IADR-0032 | 報告書生成は数値をコード集計・純関数でテンプレート化し、散文のみ LLM ドラフトに委ねる | Accepted |
| IADR-0033 | 符号付き在庫・平均取得単価法の畳み込みを Shared.Contracts.Trading の純関数へ集約する | Accepted |
| IADR-0034 | 費用計上の並行 RMW は原子的な台帳メソッド＋月単位アドバイザリロックで直列化する | Accepted |
| IADR-0035 | 損切り価格を OrderIntent に載せ #63 台帳へ永続化し、open-positions の近似を実値化する | Accepted |
| IADR-0036 | 含み損益は現在値入力から純関数で算出し、DD はピーク入力から算出する（実供給は後続） | Accepted |
| IADR-0037 | 非同期イベント契約は当面 AsyncAPI を採用せず、共有 C# 契約＋Markdown を継続し、軽量な URN 回帰ガードで補強する | Accepted |
| IADR-0038 | ドテン/部分決済は取引判断が符号付きポジションのゼロ跨ぎで Close+Open に分解する | Accepted |
| IADR-0039 | 多数決はドメイン純関数・二段（一次スクリーニング→二次多数決）はアプリのオーケストレータ・モデル選択はポート引数でゲートウェイへ委譲 | Accepted |
| IADR-0040 | 相場操縦パターン検知は自口座の直近発注統計に対する純関数ヒューリスティックで判定し、既定しきい値を保守側に置く | Accepted |
| IADR-0041 | 段階遷移は承認ゲートを構造で強制し、撤退は「自動停止＋降格提案」に分離する（段階状態＝履歴の畳み込み） | Accepted |
| IADR-0042 | 対話的確定は純関数の版番号付きレビュー状態機械で表し、取引履歴明細は純関数でテンプレート化する | Accepted |
| IADR-0043 | バックテスト基盤は純ドメイン中心に構成し、実データ源/ホストは後続に切り分ける | Accepted |
| IADR-0044 | 過剰適合補正はウォークフォワード＋DSR＋PBO(CSCV)で構成し、純関数で実装する | Accepted |
| IADR-0045 | Stage 0 合格判定は 7 条件の合成とし、FR-20 へは昇格推奨・キルスイッチで接続する | Accepted |
| IADR-0046 | ユニットリポジトリレイアウト（ルート直下 backend/・import-chain フォールバック props）を採る | Accepted |
| IADR-0047 | kit テンプレート更新には追随し、restore 系 CI/スクリプトは slnx 自動発見形を採る（IADR-0046 決定 4 の部分変更） | Accepted |
| IADR-0048 | ユニット実行環境スキャフォールド（docker-compose / appsettings / .env.example）の構成方針 | Accepted |
| IADR-0049 | 実コンテナ統合 E2E は Testcontainers を基盤とし、CI から分離する | Accepted |
| IADR-0050 | マルチサービス/認証つき統合 E2E の構成（extern alias・共有 DB・実 Keycloak トークン） | Accepted |
| IADR-0051 | サービス間同期照会の s2s 認証（client_credentials・呼び出し側トークン伝播・least-privilege サービスロール） | Accepted |
| IADR-0052 | AST の k8s デプロイは Helm chart（10 Worker 同型テンプレート）とし、共有インフラは MSP platform-infra を ExternalName で参照する | Accepted |
| IADR-0053 | moomoo OpenD はダウンロード方式の Docker Image で常駐させ k8s に opend としてオプトイン配備する | Proposed |
| IADR-0054 | 取引サイクルの本番スケジューラは収集の run-once HTTP トリガ＋Collection:Trigger モードで実現する | Accepted |
| IADR-0055 | 実 LLM 費用計測はイベント（LlmCostIncurred）で計上する（HTTP /costs/record は OwnerOnly のため使わない） | Accepted |
| IADR-0056 | moomoo SIMULATE PoC 完了に基づき実アダプタを実装（実弾は引き続きゲート） | Accepted |
| IADR-0057 | 発注の冪等化は「発注前 DecisionId 予約」の3相で行い、不明な窓は再発注せず拒否する | Accepted |
| IADR-0058 | Helm chart の CI ゲートは helm 単体で完結させ、既定 disabled のフラグ ON 派生も描画する | Accepted |
| IADR-0059 | 重複排除ストアは「終端行のみ・保持期間 90 日・下限クランプ付き」でパージし、未確定の行には触れない | Accepted |
| IADR-0060 | OpenD 本番化は「既定 no-op の整備」として先行し、切替はゲート＋チェックリストで人手に残す | Accepted |
| IADR-0061 | 実 LLM 接続の実運用化は既定オフの全量ログ・タイムアウト構成化・空既定の設定サーフェスに限定し、s2s トークンとリトライは足さない | Accepted |
| IADR-0062 | Discord Bot は Gateway 常駐＋多層認証とし、既定 no-op・owner トークンで kill switch を呼ぶ | Accepted |
| IADR-0063 | バージョン付き全体前提条件は s2s 読み取り API ＋共有クライアント（キャッシュ・イベント無効化・last-known-good）で解決する | Accepted |
| IADR-0064 | 公式ソースは「ソース単位で有効化する多ソース合成＋ソース単位レート制限」で束ね、推測実装はしない | Accepted |
| IADR-0065 | 費用統制の月次上限はバージョン付き前提条件から解決し、上限ポートを非同期化する（fail-safe は last-known-good を継承） | Accepted |
| IADR-0066 | 現在値は moomoo 非依存の既定 no-op ポートで供給し、時価評価は既定オフのゲートで切り替える | Accepted |
| IADR-0067 | 注文履歴テレメトリは「イベント追加＋Risk 専有 DB への射影」で供給し、訂正・取消の口はペーパー専用ポートに閉じる | Accepted |
| IADR-0068 | 実市況は Finnhub の HTTP 層を共有物へ抽出して供給し、構成で opt-in・既定は no-op のままとする | Accepted |
| IADR-0069 | KB 保存・RAG 取得は共有クライアントの疎な境界で platform 文書管理／検索を包み、既定 no-op・構成で opt-in とする | Accepted |
| IADR-0070 | 段階ゲートの遷移を追記専用台帳＋単一行実績で永続化し、承認は OwnerOnly エンドポイント、撤退は kill switch 自動起動に結線する | Accepted |
| IADR-0071 | 報告書サービス残スコープは ReportService に閉じ、実 LLM/実 KB を既定オフ・opt-in、対話的確定は状態機械の薄い HTTP 結線で実装する | Accepted |
| IADR-0072 | RAG 文脈は Application 抽象ポートで受け、本判断プロンプトのみに参考情報として注入し、既定 no-op・取得失敗は文脈なしへ縮退する | Accepted |
| IADR-0073 | 情報収集の実 KB 保存 opt-in はデプロイ面（compose/helm/.env.example）への env 露出のみで開け、既定は空＝no-op のまま据え置く | Accepted |
| IADR-0074 | Reserved 滞留の自動リコンサイルはプローブ・ポート＋fail-safe 既定 no-op で行い、実 OpenD 照会は後続へ分離する | Accepted |
| IADR-0075 | 取引の一時停止(pause)を kill switch と同経路の別状態として新設し、監査は既存の設定変更履歴で満たす | Accepted |
| IADR-0076 | 取引判断の採算評価は既存の概算費用関数を再利用し、opt-in の純ドメインゲートで採算不成立・見積り不能を安全側 Hold に倒す | Accepted |
| IADR-0077 | 取引パイプラインの発行・購読バインディングを pipeline.json で宣言し、変換 DAG のみを段として表現して CI 検証＋GitOps 適用する | Accepted |
| IADR-0078 | 各サービスの実効構成を無認可・メッシュ内部限定の自己申告エンドポイントで公開し、有効な段は pipeline.json 宣言から導出する | Accepted |
| IADR-0079 | イベント契約の後方互換を snapshot 比較の CI 契約テストで機械化し、共通エンベロープ型は上流確定まで繰延に準拠する | Accepted |
| IADR-0080 | フロントエンドは platform unit-template 規約に準拠し、単独リポの型検査/テストを @foundation スタブ＋ローカル vitest で自己完結させ、設定画面は FR-17 前提条件の閲覧/変更に限定する | Accepted |
| IADR-0081 | 段階ゲートの Discord コマンドは Bot 側で Risk の OwnerOnly エンドポイントを呼ぶだけの薄い追加とし、数値 enum 整形を Worker に隔離する | Accepted |
| IADR-0082 | 段階遷移イベントは Worker 発行点でバス発行し中央監査へ集約する（契約は primitive・Risk 専有台帳を権威に据え置く） | Accepted |
| IADR-0083 | 撤退の定期評価は背景ドライバで駆動し、新規自動停止時のみ通知イベントを発行する（kill switch 状態を durable な冪等鍵にする） | Accepted |
| IADR-0084 | FR-13 リスク設定と #20 統制状態は Risk の既存 OwnerOnly 契約を消費する参照優先の別 feature とし、破壊的操作は Bot 側に委ね、数値 enum の写像はフロントに閉じる | Accepted |
| IADR-0085 | 撤退の非停止（ペーパー乖離）降格提案は durable な通知済みシグネチャで重複排除し、ドライバ側で 1 回だけ通知する | Accepted |
| IADR-0086 | SC-02 のガード変更 UI は既存 PUT /settings/guard を全置換で消費し、危険な緩和は明示確認で fail-safe にする（監視銘柄はバックエンド未整備で分離・段階直接変更は開かない） | Accepted |
| IADR-0087 | フロント E2E は src の実 feature を test-only ハーネスへマウントし、@foundation はスタブ・apiClient のみ実 fetch へ差し替えてモック BFF で検証する | Accepted |
| IADR-0088 | 監視銘柄（watchlist）設定 API は権威データ源の MarketMonitorService に置き、Risk 設定の作法（owner サブグループ認可・理由必須・楽観排他・ローカル変更履歴）をミラーする | Accepted |
| IADR-0089 | バックテスト verdict は BacktestEvaluated イベントで発行し Risk が read-modify-write で射影する（s2s 同期照会を退け fail-safe を保つ） | Accepted |
| IADR-0090 | SC-02 の監視銘柄変更 UI は MarketMonitor /monitor/watchlist を個別操作で消費し、削除は明示確認で fail-safe にする（別サービスとして独立ロード・実 BFF プロキシは MSP 後続） | Accepted |
| IADR-0091 | AST の BFF エンドポイント（assumptions/risk-controls/monitor）を unit-owned プロジェクト AiStockTrading.Bff.Endpoints として保持し、DTO 非結合の FrameworkReference のみで自己完結させる | Accepted |
| IADR-0092 | Reserved 滞留の実照会プローブは DecisionId を moomoo remark に伝播し SIMULATE の現在＋履歴注文を照合する（解放は「確実に未発注」の既知窓のみ・他は Indeterminate・既定 no-op / opt-in） | Accepted |
| IADR-0093 | KB 保存・検索の s2s は MSP レルムの専用 confidential client（platform-operator）でクロスレルムに認証し、AST レルムの ServiceAuth とは分離した inline ハンドラで発行する（既定 no-op・秘密は Secret 経由・realm 定義は MSP 側 PR） | Accepted |
| IADR-0094 | ローカル（経路B）の Vault 秘匿参照・可観測性・GitOps は AST リポ内の opt-in manifest／docs として整備し（既定オフ・平文秘密なし・`dataFrom.extract`）、共有スタックの stand-up と Hetzner 実デプロイ（Tier 3）は分離する | Accepted |
| IADR-0095 | TradeDecision の watchlist 供給を権威源 MarketMonitor の s2s 同期照会（GET `/monitor/watchlist` を `OwnerOrService` に開放）へ一本化し、構成ベース（`TradeCycle:Watchlist`）は BaseUrl 未設定/照会失敗時の fail-safe フォールバックへ降格する（新イベント無し・`Shared.Contracts` 不変） | Accepted |
| IADR-0096 | 日報未確定による取引スキップは DailyPolicyUnconfirmed イベントで通知し、営業日単位の in-memory dedup で抑止する（発火は DecideAsync の policy-null 分岐・既定 no-op / opt-in・durable にしない） | Accepted |
| IADR-0097 | kill switch 解除にも起動と同一の確認フレーズ検証（`Verify`）を要求し、Gateway は解除もモーダル導線（ID 分離）へ揃える。安全既定は解除も拒否（未設定＝フレーズ不要にしない・摩擦を下げない）・監査/冪等は既存経路踏襲・`Shared.Contracts` 不変 | Accepted |
| IADR-0098 | Discord Bot 制御コマンドの OwnerAuth は AST レルムの専用 confidential client `ai-stock-trading-owner`（service-account に trading-owner 単独）で認証し、helm では TokenEndpoint を明示して IsEnabled を成立させる（realm-export.json＋helm＋docs に閉じる・既定 Bot 無効/opt-in・dev secret のみ） | Accepted |
| IADR-0099 | 取引判断へ共有 `IMarketDataSource`（IADR-0068）を Application ポート `ICurrentPriceProvider` 経由で供給し、定時プロンプトに現在値を注入し発注の参照価格（サイジング/損切り/採算 notional）を権威ある現在値へアンカリングする。既定 no-op（`IsEnabled=false`＝現行挙動）・有効化時のみ取得不可/鮮度切れを Hold へ倒す・実弾 triple-latch 不変 | Accepted |
| IADR-0100 | 経路B（ローカル SIMULATE）の ①時価②実LLM③実KB＋Discord＋価格文脈(#236) を、臨時 overlay ではなく chart 内 `values-local.yaml` の恒常設定へ落とし込み、標準手順 `k8s-local-deploy.sh` が `-f values-local.yaml` で適用する。本番（ArgoCD＝valueFiles 無し）は `values.yaml` のみ描画でバイト等価・secret は secretKeyRef のまま・Discord 環境固有値は空既定・実弾 OFF 不変。`helm.yml` が本番バイト等価と local 有効化の両検証を担う | Accepted |
| IADR-0101 | 基盤既定モデルの Opus 5 化に備え、LLM 呼び出しの MaxTokens を 1024 → 4096 へ引き上げる | Accepted |
| IADR-0102 | Discord Bot の環境固有 ID（GuildId/ChannelId/AllowedUserIds/UserMapping・非機密）は chart の設定点 `discord.bot.*`（空既定）から `extraEnv` の値を上書きする形で与え、`k8s-local-deploy.sh` が env → `--set-string` で渡す。`kubectl set env` を運用から排除しフィールドマネージャ競合（`conflict with "kubectl-set"`）を根絶する。空既定では一切差し替えないため本番 `values.yaml` 描画はバイト等価・機密は `secretKeyRef` 据置・空＝全拒否（IADR-0062）不変 | Accepted |
| IADR-0103 | 実DD（観測最大ドローダウン）は Risk 内の定時サンプリング＋単調 latch で段階別実績へ供給し（s2s・イベント不要・Database per Service 不変）、ADR-0008 の撤退基準を作動可能にする。フィールド所有権は IADR-0089 の鏡像で分離（実DD 以外は温存）・観測窓のリセットは「受理された差し戻し」のみ・既定は opt-in 無効で時価評価/撤退評価と合わせて 3 段の明示的有効化を要する・取得失敗時は既存値を維持（実弾 triple-latch 不変） | Accepted |
| IADR-0104 | LLM ゲートウェイ応答の `stopReason` を共有語彙 `LlmStopReasons`（`Events` 名前空間外＝イベント契約テスト不変）で受け、**本文を読む前に**評価する。`refusal` は本文が非空でも破棄して Hold／プレースホルダ散文へ倒し（上流の破棄に依存しない多層防御）、Hold 理由を送信不可/応答不正/拒否/空応答/上限到達で 5 系統に分離して監査で切り分け可能にする。`max_tokens` の本文は破棄しない（IADR-0101 の劣化観測維持）・拒否は課金済みのため費用計測へ含める・`DecisionAggregator` は Hold 勝利時も代表票の根拠を保つ（`Action` 不変）・`StopReason` 未設定は現行挙動 | Accepted |
| IADR-0105 | バックテストの実過去データ源は非同期ポート `IHistoricalBarSource` で取得し、スナップショット `MaterializedBarDataSource` へ固定して同期・純粋な `IBarDataSource` へ供給する（決定性の保全）。実装は Stooq（ADR-0004 の検証・学習用データ源）で、`Backtest:BarData:Provider` 既定 `none`＝外部へ 1 リクエストも出さない・構成不備/未知 provider/不正 URL は警告して no-op へ倒す。欠測は銘柄と理由（`HistoricalBarGap`）で残し解析は部分採用しない・取得対象は PIT ユニバース（`MembersBetween`）から導出・`InMemoryBarDataSource` はテスト用に限定。閾値較正は実データ実測が要るため #208 に残置 | Accepted |
| IADR-0106 | `IConsumer<T>` 実装のクラス名はサービスを跨いで一意にする。MassTransit の既定 `DefaultEndpointNameFormatter` はキュー名を consumer クラス名のみ（`Consumer` 接尾辞落ち・namespace 非包含）から導くため、別サービスの同名 consumer は同一キューを共有し pub/sub のつもりが competing consumer になる（`RiskManagementService` と `MarketMonitorService` がともに `TradeDecisionMadeConsumer` を持ち、取引判断を取り合って無言で取りこぼした・#258）。MarketMonitor 側を `TradeDecisionMadeBaselineConsumer` へ改名してキューを分離し（RiskManagement 側は据え置き＝孤児キューが出ない）、`ConsumerEndpointNameTests` が実測値でキュー名を固定、`scripts/check-consumer-endpoint-names.js` が CI で衝突を止める。全サービスへの `IEndpointNameFormatter` プレフィックス導入は 40 本のキュー移行と孤児キュー滞留を伴うため棄却。ADR-0013 の Wolverine 移行時に前提の再検証が必要 | Accepted |
| IADR-0107 | 統制の金額判定は基準通貨（JPY）で行い、換算は判断境界の 1 点（TradeDecisionService）だけで実施して換算レートを `OrderIntent.FxRateToBase`（既定 1＝現行等価）に同伴させる。`OrderIntent.Price` は執行価格の権威としてローカル通貨に確定し（`MoomooBrokerAdapter` の注文価格を壊さない）、統制・台帳は `NotionalInBase` と同伴レートで基準通貨に積む。非基準通貨でレートが解決できなければ新規建ては見送り（fail-safe・過大発注を招かない）。レート源は FRED `DEXJPUS`（`Fx:Provider` 既定 `none`＝外部接続なし・鮮度上限超過は採らない）。含み損益は建玉の加重平均約定時レートによる近似（計画 §3 の日次終値レートからの逸脱・#257 に残置） | Accepted |
| IADR-0108 | SIMULATE（ペーパー検証）限定のリスク上限プロファイルを moomoo シミュレータ残高（USD $1M＋JPY ¥20M＝基準通貨 ¥170,000,000）に基づいて定め、金額系のみ本番既定の 1,700 倍へスケールする（1 注文 ¥59,500,000／日次 ¥170,000,000）。比率系・保有銘柄数・取引ガードは本番既定と同一、**実弾段階（Stage 2/3）の資金上限は有効時も不変**。値は Domain 定数 `SimulatorTradingDefaults` が単一情報源で構成からは有効/無効のみ（`Risk:SimulatorProfile:Enabled` 既定 false）、供給は読み取り時デコレータ（DB を書き換えず可逆）。設定点は Development／`values-local.yaml` のみで本番 `values.yaml` には置かない＝既定描画はバイト等価（`helm.yml` が漏れと未有効化の双方を検査） | Accepted |
| IADR-0109 | ローカルデプロイ（`scripts/k8s-local-deploy.sh`）の `ast-secrets` は「env からの再作成」をやめ、キー単位の差分パッチ（`kubectl patch --type=merge`）で同期する。env 未設定のキーは触らない＝投入済みの値を保持し（export し忘れによる無言破壊の根絶）、`export KEY=` の**明示的な空指定**が既存の非空値を消す場合だけ対象キー名を列挙して中断する（`--force-empty-secrets` で許可）。既存値は読み出さず「非空キー名」だけを `go-template` で取得し、書き込みは base64 の `data` を `umask 077` の一時ファイル経由でパッチする（平文をコマンドライン引数・ログへ載せない／値のエスケープ問題を構造的に消す）。新規環境は `kubectl create`（不在時のみ）＋パッチで後方互換。`apply` は last-applied との 3-way merge で同じ破壊を再発させるため使わない | Accepted |
| IADR-0110 | Stage 0 の最小試行数（`MinTrials`）を暫定値 1 から **20** へ較正する。1 では `ExpectedMaxSharpe` が 0 を返し（trials<2）多重検定補正が恒等的に消えるため、探索を過少申告した判定を素通しさせていた。決定論モンテカルロ（種固定・真のエッジ 0・標本 252 日・反復 20,000）で実測: 200 候補を 1 件だけ記録すると偽陽性率 100%・2 件で 57.20%・**20 件で 0.62%**（単一試行の名目 5.06% より一桁低い）、SR0 の変動係数も N=2 の 75.9% から N=20 で 16.3% へ収束。他 3 閾値は測定のうえ据え置き（DSR 0.95 は名目 5% と実測整合／PBO 0.5 は雑音の中心＝平均 0.5055 だが厳格化しても既知エッジを同率で落とすため見送り／最大DD 0.15 は計画書由来で自由変数でない）。較正ハーネスはテスト専用・CI 対象外。**Stooq は 2026-07-28 時点でボット検知チャレンジを返し取得不可**（回避はしない）→ 実データでの水準確認は #208 に残置 | Accepted |
| IADR-0111 | ブローカー選択を「プロバイダ（`Broker:Provider`＝`paper`/`moomoo`/将来の他証券）」×「取引環境（`Broker:Environment`＝`sim`/`live`）」の直交 2 軸で表現し、正準名 `Tier`（`paper` ＜ `moomoo-sim` ＜ `moomoo-live`）で本番近接順の 3 階層を設定から任意に切り替える。Helm 面は単一 value `broker.tier` に畳み（`moomoo.enabled` は非推奨エイリアスとして温存・既定描画はバイト等価）、証券会社の追加を enum 1 値＋アダプタ＋switch 1 腕＋tier 値 1 つの最小差分に保つ。fail-safe は全て発注抑止側＝未設定は `paper`/`sim`・未知値・`paper`＋`live` の矛盾は起動時停止（黙って倒さない）。**実弾は本 IADR で解禁しない**: `LiveTradingGate.LiveTradingReleased`（`const false`）が閂 0 として live 選択を起動時に止め、Helm は `broker.tier=moomoo-live` を描画時に `fail` させる（外周の閂）。既存の閂 1〜4（provider ゲート・`SetTrdEnv(Simulate)` 固定・`EnsureSimulate`・SIMULATE 口座採用）は一行も変更せず、live は「型として表現できるが到達不能」。将来の解禁は `LiveTradingReleased=true` の 1 ファイル変更＋別 IADR に集約される | Accepted |
| IADR-0112 | 為替レートの鮮度上限は「データ源の公表周期」から導く。FRED `DEXJPUS` は系列こそ営業日次だが公表は **H.10 週次リリース**（月曜 16:15 ET・前週金曜まで一括収載／月曜が祝日なら火曜）であり、最新観測の齢は `週次間隔 7 ＋ 公表ラグ 3 ＋ 祝日ずれ 2 ＋ 公表時刻 ≒ 12.84 日` まで積み上がる。既定 `Fx:MaxRateAgeDays` 7 日は**予定どおりの公表でも毎週必ず超える**（実測 2026-07-27: 10 日前の観測で米国株が全件見送り）ため **14 日へ較正**する。線引きは非対称＝予定どおりの遅延は全て吸収し、リリース 1 回欠落（17.84 日）は従来どおり見送る。許容する誤差は 14 日の USD/JPY 変動（1σ ≒ 2%・テールで数%）＝統制金額に対し同オーダーの摂動であり、IADR-0107 が是正した約 150 倍とは 3 桁違う。あわせて設定値に**上限 31 日のクランプ**（IADR-0059 の下限クランプと対称・構造で guard を守る）と**取得窓 ≥ 受容窓**（`ObservationLimit` 10→23＝設定できる最大の受容窓 31 日の営業日換算・欠測が `"."` で返っても窓の端まで届く）を置く。営業日ベース判定は `MarketCalendar` の休場日集合が既定で空＝連休を吸収できず棄却、公表カレンダー内蔵はスケジュール変更が即・無音停止に化けるため棄却。IADR-0107 決定3（レート無し＝非基準通貨の新規建て見送り）は一切緩めない | Accepted |
| IADR-0113 | 非同期に約定するブローカー（moomoo）の注文は、非終端の `executed_orders` を短周期（既定 30 秒）で `IBrokerAdapter.GetOrderAsync` により追跡し、状態変化・約定数増加のたびに記録を更新して `OrderExecuted` を再発行する。台帳側は受け口を `Status == Filled` から **`FilledQuantity > 0`** へ改め、`AppendFill` を `OrderId` 主キーの**単調 upsert**（累積約定数が増えたときだけ更新）にする＝ moomoo の `FillQty` が累積値である事実の忠実な写像で、再配送・巡回重複・複数レプリカでも二重計上せず、部分約定のまま取消された注文の過少計上も解消する。滞留リコンサイル（IADR-0074/0092）とは対象集合（`Reserved` 対 確定済み）も時間尺度も異なるため相乗りさせない。照会不達・不明は**何も書かず据え置き**（ブローカー状態を推測しない）・約定数は巻き戻さない。既定は例外的に有効（`FillPolling:Enabled=true`）＝統制が実効するための必要条件であり副作用は読み取り照会のみ、paper は moomoo 限定登録＋即時終端で二重に非干渉。`Shared.Contracts` 不変・新規イベント無し・DB スキーマ変更無し・SIMULATE / 実弾 OFF 不変（発注・訂正・取消の呼び出しを 1 つも増やさない） | Accepted |
| IADR-0114 | 経路B（ローカル SIMULATE）の本番パリティ回復では「実コードで実効を確認できたトグル」だけを `values-local.yaml` に入れる: 実DD 供給（`ObservedDrawdownRefresh:Enabled`＝時価評価が既に真のため latch が動く）と公式情報源 2 つ（SEC EDGAR／FRED＝新規の資格情報を要さない）。SEC 規約が求める連絡先入り User-Agent は個人情報のため values へ直書きせず、`ast-secrets` の新規キー `sec-edgar-user-agent`（`optional: true`）経由で与える（templates 不変＝本番バイト等価が自明・IADR-0109 の差分パッチ同期を追加実装なしで継承）。撤退の実行側（`WithdrawalEvaluation:Enabled`）は自動 kill switch 起動で dogfood が人手介入まで止まるため運用判断として保留。**入れない判断も根拠つきで固定**: RAG 検索は三重に不活性（ABAC `Scope` 未送出で deny-by-default／`POST /documents` はカタログのみで本文が KB に無い／#252 未サニタイズ）、`MarketMonitor:BaseUrl` は空 watchlist の 200 応答が fallback せず取引サイクルを沈黙させる、リコンサイルは paper で自己修復のみ＋巡回下限 1 時間。Helm 描画検査に「values-local が既定描画の env 名を失っていないこと」（リスト置換による欠落の検出）を新設 | Accepted |
| IADR-0115 | 報告書の自動生成は「ドラフト生成→提示（`PendingApproval`）」までで停止し、確定（`Confirm`）は OwnerOnly の対話経路に残す（ADR-0003「完全無人での方針変更は行わない」に従い、SIMULATE 限定の自動確定フラグも棄却）。生成境界は JST 固定・営業日基準で、判定は純関数 `ReportSchedule.Due` に閉じる（日報＝閉場境界を過ぎた直近営業日 1 件・週報/月報＝当週/当月の最終営業日／バックフィルは当期のみ）。冪等の根拠は `PeriodKey` の存在のみ＝専有 DB が単一情報源でプロセス内に「生成済み」を持たない（再起動・多重レプリカ耐性）。自動ドラフトの `PolicySummary` は上位方針の継続案に留め LLM に新方針を提案させない（レビューの形骸化を避ける）。期間の約定は権威源 risk-management の `GET /risk-controls/fills`（OwnerOrService・IADR-0095 同型）へ s2s 照会し、供給不達は空＝数値 0 に倒して生成は止めない。既定無効（opt-in）で Helm / values は不変 | Accepted |
| IADR-0116 | 自動生成し提示（`PendingApproval`）まで到達した報告書ドラフトを、`ReportConfirmed` と対になる新イベント `ReportDraftPresented` で通知し、既存の通知経路（Discord provider）へ載せる。既存イベントは不変＝後方互換の追加のみで、契約ガード 3 点（`event-schemas.baseline.json` 再生成・`EventMessageUrnTests` の URN 固定・監査 Consumer）に追随する。発行は常駐から `IBus`（Application 層は MassTransit 非依存）・best-effort（失敗は警告ログで生成/提示を壊さない）・未提示（`NotPresented`）は通知しない（承認待ちに無いものを「確認してください」と言わない）。dedup は生成の `PeriodKey` 冪等が担うため新設しない。`NotifyOnDraftPresented` の既定は **true**（発行点が既定無効の常駐の内側にあり、二段目の opt-in は「作られたのに届かない」無言状態を作る）。**`PromptSafetySanitizer` は共有化しない**（`Sanitize` は本文を `<<<UNTRUSTED_DATA…>>>` で囲う関数で、人間が読む Discord 投稿には誤り／分解は情報収集の本番経路に回帰リスク）→ 同じ防御思想の `ReportSummarySanitizer` を Report ドメインに置き、制御文字除去・`@everyone`/`<@…>` の mention 無害化（U+200B）・境界語除去・長さ上限を適用する。要約の数値はコード集計値のみで、サニタイズは組み立て関数の内側で必ず適用する。Discord からの確定コマンドは OwnerAuth を要する別の面のため対象外 | Accepted |
| IADR-0117 | 建玉の手仕舞い（Close）は利用者専用の同期エンドポイント `POST /risk-controls/positions/close`（OwnerOnly）で受け、リスク管理が既存の `OrderApproved` を発行して**既存の注文パスへそのまま載せる**（決済専用の約定経路を作らない＝台帳・枠回復・通知・監査の受け口を二重化せず、`OrderId` 単調 upsert〈IADR-0113〉と `DecisionId` 予約〈IADR-0057〉の冪等をそのまま再利用する）。発注前スクリーニングは通さない＝ kill switch・日次損失ロックアウト・一時停止・取引ガード・段階資金上限で手仕舞いは止まらない（FR-10 本文「いずれも手仕舞い（Close）と損切りは止めない」の実装であり逸脱ではない）。構造的保証として `PositionCloseService` は統制ストアを依存に持たず、テストで固定する。売買方向は要求に含めずサーバが建玉方向の反対売買として決め、`FxRateToBase` は建玉の加重平均約定時レートを引き継ぐ（IADR-0107）。過剰決済ガードは「利用可能数量 = 建玉 − Σ max(0, 決済承認数量 − 当該 DecisionId の約定累計)」で、台帳が**約定でしか動かない**ため多重投入が在庫超過（意図しないショート化）を作れる問題を塞ぐ。集計は**既定 30 分の時間窓**内の承認に限る（窓が無いと #270 破損期のような永久未約定の滞留承認が建玉を恒久ロックし、手仕舞い手段を作ったのに手仕舞えなくなる）。監査は新イベント `PositionCloseRequested`（Actor・Reason 付き）を `OrderApproved` より**先に**発行する（`OrderApproved` はアクターも理由も持たない／「監査があるのに操作が無い」は同一 DecisionId の後続不在で検知できるが逆は検知できない）。`DecisionId` は要求ごとに新規（損切り〈IADR-0015〉の決定的採番と異なり、利用者の各要求は独立した注文で、正当な連続決済を冪等に潰さない）。構成キー・Migration・Helm/values は**一切足さない**（利用者の明示操作でしか動かず、無効化スイッチは「手仕舞えない状態を作れるスイッチ」にしかならない）。実弾ゲート（閂 0〜4）差分ゼロ＝ブローカ呼び出しを 1 つも増やさない | Accepted |
| IADR-0118 | AST 取引台帳とブローカ実ポジションの突合は「発注執行が建玉を定期観測して `BrokerPositionsObserved` を publish → リスク管理が台帳射影と突き合わせて `PositionReconciliationDrift` を publish」の**イベント経路**で行う（発注執行は HTTP/s2s 配線を持たず、逆方向は認証サーフェスの新設を伴う／台帳の権威はリスク管理にある／#164 と同じ「s2s ではなくイベント射影」の流儀）。建玉照会は `IBrokerAdapter` ではなく新ポート `IBrokerPositionSource` に置き、実装しないアダプタ（paper）では DI に現れず常駐が登録されない＝**1 度も照会が起きない**構造的な非干渉とする。契約の中核は **`null`（照会不能＝不明）と空列（建玉ゼロ）の厳格な区別**で、moomoo 実装はいずれかの市場の照会失敗で例外を送出し（部分列挙を返さない）アダプタが `null` へ倒す（IADR-0092 と同型）。比較は `(Symbol, Market)` の**符号付き数量のみ**（平均取得単価は手数料・端数・為替で必ずズレるため判定に使わない）で、乖離は `BrokerOnly`／`LedgerOnly`／`QuantityMismatch` の 3 種。**是正はしない**＝検知・記録・通知のみ（外部要因の乖離に対し自律的に発注する経路を作らない。テストで drift 発行時に `OrderApproved` が 0 件であることを固定）。雑音の抑制は構成キーではなく構造で行い、`PositionDriftTracker`（インメモリ）が「連続 2 回同一シグネチャでのみ報告」（発注〜約定反映の正当なズレを弾く）と「前回報告と同一なら再報告しない・解消したら忘れる」を担う（シグネチャは順序非依存の正準形＝列挙順の差を内容変化と誤認しない）。既定 `Reconciliation:Positions:Enabled=true`（IADR-0113 と同じ理由＝副作用は読み取り照会のみで、検知器を既定オフにすることは「乖離が見えない状態」を既定にすること）・間隔 600 秒（60〜3600 クランプ）。**#141/IADR-0092 とは補完関係**（あちらは「AST が出した注文が発注されたか」＝注文粒度・clientOrderId・ブローカが権威・終端化する／こちらは「帳簿がブローカと一致するか」＝建玉粒度・(Symbol,Market)・どちらも正としない・是正しない）で、#141 が全部緑でも手動売買由来の乖離は 1 件も検出できない。Migration・Helm/values 不変、実弾ゲート（閂 0〜4）差分ゼロ | Accepted |
| IADR-0119 | 判断由来の発注の `PositionEffect` を `Open` 固定から**保有建玉から決定的に導く**形へ改める（純関数 `PositionEffectResolver`）: 建玉の反対売買は `Close`・数量は**保有数の全量**（ロング×Sell / ショート×Buy）、同方向は `Open`（サイジング）、**保有なし・不明の売りは見送る**。従来は LLM の `Sell` が「保有ロングの決済」ではなく新規ショート建てとして扱われ、①`RiskEvaluator.isEntry=(PositionEffect==Open)` により AI の売却が kill switch・pause・ロックアウト・段階資金上限・同日再エントリーで**ブロックされ**（FR-10「手仕舞いは止めない」と正反対）②数量が保有数と無関係（`PositionSizer` の新規建てサイズ）③保有ゼロなら**裸の新規ショート**がブローカへ飛び（ガードは方向を見ない）④存在しない建玉の損切り価格が台帳へ載る、という 4 つの実害があった。決済は**サイジング・採算ゲート（IADR-0076）・損切り幅検証を通さない**（出口の数量は保有数／最小期待利益で撤退を止めてはならない／決済注文に損切りは無い）。残す検証は `referencePrice>0` のみ。効果を **LLM に出力させない**（`PositionEffect` はどの統制が効くかを決める値であり、AI が申告できると「Close と言えば kill switch を回避できる」経路になる＝ADR-0003 違反）／**リスク管理側で読み替えない**（`TradeDecisionMade` の監査記録と実際の効果が恒久的に食い違う）。建玉照会は新ポート `IHeldPositionProvider` ＋ リスク管理の**既存** `GET /risk-controls/open-positions`（新規 endpoint・event・構成キーを作らず `RiskManagement:BaseUrl` と `risk` HttpClient を再利用）で、**空配列＝0（保有なし）／失敗＝null（不明）を厳格に区別**する（市場監視の `HttpPositionStore` は失敗を空列へ倒すが、ここで同じことをすると裸の新規売りを通す＝安全側の向きがサービスごとに異なる）。ゼロ跨ぎは起こらない（Close は保有数ちょうど）ため IADR-0038 の Close+Open 2 意図は発生しない。**既存挙動の変更 1 点**＝ `RiskManagement:BaseUrl` 未設定環境では AI が売れなくなる（従来は裸の新規売り）。`Shared.Contracts`・DB・Helm/values・実弾ゲート（閂 0〜4）は不変 | Accepted |
| IADR-0120 | 利用者が**月報/週報/日報を「次の取引に活かす方針書」**と位置づけ、種別ごとの割当モデルを仕様指定した（#291 / #293）。監査で 2 つの欠落が確定した。(1) `Program.cs` は `IReportNarrativeDrafter` を**単一 purpose**（`LlmGateway:Purpose` 既定 `report-narrative`）で登録し、`ReportNarrativeContext.Kind` はプロンプト文面にしか届かずルーティングに影響しないため、基盤に該当エントリが無く **3 種別すべてが `DefaultModel` へ着地**していた。(2) `ReportAutoGenerator` は `GetLatestConfirmed(parentKind)` で上位報告書を**取得しているのに `PeriodKey` しか使わず**、`PolicySummary`（上位方針の本文）を破棄していた。計画 `04_workflows/03_reporting-cycle`（fixed）は「AI がドラフト生成＝上位方針の目標との差異評価」を求めるが、本文が無ければ差異評価は書けない——**参照連鎖はリンクとしては存在するが生成には効いていなかった**。決定1: `ReportKind`→purpose の純関数 `ReportNarrativePurpose.For` を Application に置き（`report-daily`/`report-weekly`/`report-monthly`。基盤の `PurposeModels` キーと文字列一致が必須＝不一致は無音失効）、drafter が要求ごとに決める。`Model` は引き続き `null`＝**モデルの決定権は基盤の LlmRouter に残す**（AST がモデル ID を持つと `NonZdrModels` 除外・版数改定へ追随できない）。決定2: `LlmGateway:Purpose` は既定値だけ外し**上書き**として残す（設定済みデプロイを壊さない）。決定3: 上位方針は `ReportNarrativeContext` 経由で**散文の文脈としてのみ**渡し、`PolicySummary`（確定すると取引に効く）へは混ぜない（[[IADR-0115]] 決定4「自動生成では新しい方針を機械に提案させない」・ADR-0003）。プロンプトに上位方針の節と差異評価の指示を足し、上位未確定なら**その旨を明記**する（捏造しない）。月報の上位は前月の月報（`ParentKind(Monthly)==Monthly`）。決定4: 数値はコード集計が唯一の権威（FR-16）という指示文は不変。決定5: 手動ドラフト経路は任意フィールドで追随（非破壊）。棄却案: DI で 3 drafter を登録（1 行の写像に対し過重）、AST 側で `Model` 明示指定（許可一覧との整合を運用で担保しにくい。platform IADR-0102/0112 が同理由で棄却済み）、`CarryOver` の方針文へ織り込む（機械が合成した方針が承認待ちに並び ADR-0003 の「確定には対話を要する」が形骸化）、KB(RAG) 経由での取得（手元のストアに構造化されて存在するものを検索の当たり外れに委ねない。KB は #288 で別途）。**日報→取引の結線（`GetConfirmedDailyPolicy`）は実装済みで差分なし**（[[IADR-0028]]）。回帰は T-1〜T-8（`ReportNarrativePurposeTests` / `ReportNarrativePromptBuilderTests` / `ReportAutoGeneratorTests` / `HttpReportNarrativeDrafterTests`）で固定。**本 PR 単体では割当モデルは変わらない**（基盤 microservices-platform#422 と揃って初めて実効化。それまでは未知 purpose として `DefaultModel` 着地＝現行と同じ）。残: 基盤 PR とのマージ順、入力トークン増と種別別単価の実測（#243 / #282）、散文費用の計上（#282）、KB 検索の結線（#288）、取引判断モデルのドキュメント追随（#285）。 | Accepted |
| IADR-0124 | 建玉乖離トラッカー（[[IADR-0118]]）の追跡状態を**プロセス内から DB 単一行 `position_drift_state` へ durable 化**し、単一レプリカ前提という暗黙の運用依存を `IsConcurrencyToken`（並行トークン）という明示的な保証へ置き換える（#305）。`BrokerPositionsObserved` は consumer クラス名から導かれる単一キューで受ける（[[IADR-0106]]）ため `replicas>1` では観測が Pod へラウンドロビンで分散し、各 Pod のカウンタが 1 のまま「連続 2 回」条件に到達せず、**例外もログも出ないまま乖離が恒久未報告**になり得た（統制系で最も避けたい静かな縮退）。決定1: 状態は `(ObservedSignature, ConsecutiveCount, ReportedSignature, Version)` の単一行に置き、判定は純関数 `PositionDriftDecision.Decide` へ分離、`PositionDriftTracker` は singleton → **scoped** で `Get → Decide → TrySave` を束ねる（[[IADR-0085]] の `IWithdrawalNotificationStore` と同型・[[IADR-0012]] と同型の楽観的排他）。決定2: **並行更新に負けた観測は捨てる（リトライしない）**——競合しても必ずどれか 1 つは勝つため状態は単調に前進し、捨てた内容は乖離が解消するまで毎巡回で再観測される＝失うのは最大 1 巡回分の時間であって報告そのものではない。同一 `DbContext` での再読込は `ChangeTracker.Clear()` を要し同スコープの台帳追跡まで巻き込むため割に合わない。競合は `LogDebug` に残して**無言にしない**。決定3: 判定意味論は 1 つも変えない（連続 2 回・順序非依存シグネチャ・解消で忘れる・**是正しない**）。既存 9 テストが store 注入のみで緑＝同値の証拠。変わるのは「再起動をまたいで状態が保持される」1 点（IADR-0118 が許容していた再起動後 1 回の再報告も消える＝改善方向）。決定4: `replicas: 1` は変えない＝本 ADR は水平スケールを認定せず、このコンポーネントの無言縮退だけを消す（調査の結果、Risk の他の跨ぎ状態＝撤退通知・実DD latch は既に durable、`QuoteCache` は純キャッシュで、本トラッカーが判定を担う唯一のインメモリ跨ぎ状態だった）。棄却案: 明記のみ（読んだ人しか守らず、`kubectl scale` 1 つで無言に破れる）、`replicas>1` の fail-loud（k8s API 依存の対価に見合わず、スケール不能を固定するだけ）、リーダー選出（交代時の引き継ぎに結局 durable な状態が要る）。**発注執行側の多重化**は連続条件が早く満たされる＝報告が増える向きのため対象外。Migration 1 件（テーブル追加）・`Shared.Contracts`／Helm／values／構成キーは不変・実弾ゲート（閂 0〜4）差分ゼロ | Accepted |
