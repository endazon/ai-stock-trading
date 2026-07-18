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
