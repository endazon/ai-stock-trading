# 変更履歴 (CHANGELOG)

## Unreleased

### 新機能

- **FR-04**: 実 LLM egress を LLM ゲートウェイ /complete へ委譲する (#79) (#125) (39cbcf3)
- **FR-05**: moomoo ブローカアダプタ（SIMULATE 限定） (#130) (684d981)
- **ADR-0002**: moomoo OpenD の Docker/k8s 試作一式 (#124) (#126) (eeebdfe)
- **IADR-0052**: AST k8s Helm chart（10 Worker・共有インフラ参照・CronJob骨子 #121） (#123) (81a2ff4)
- **IADR-0051**: サービス間同期照会の s2s 認証（呼び出し側トークン伝播・最小権限 trading-service） (#117) (4d4876d)
- **FR-15,FR-20**: バックテスト基盤 Slice C（Stage 0 合格判定・段階昇格推奨・撤退キルスイッチ） (#101) (138cf6a)
- **FR-15**: バックテスト基盤 Slice B（過剰適合補正ハーネス: ウォークフォワード・DSR・PBO） (#100) (69571c4)
- **FR-15**: バックテスト基盤 Slice A（シミュレーションコア・コストモデル・結果集計） (#99) (081f618)
- **FR-06,FR-07,FR-16**: 報告書の対話的確定ロジックと取引履歴明細レンダリングを実装（fake データ・Refs #14・IADR-0042） (#95) (ad90597)
- **FR-20**: 段階ゲートの遷移管理を純ロジック化（承認ゲート＋撤退フェイルセーフ・IADR-0037） (#98) (d69bd25)
- **FR-19**: 相場操縦パターン検知アルゴリズムの実装（IADR-0037） (#97) (04fda8e)
- **FR-04**: 取引判断の多数決・二段オーケストレーションを実装（fake LLM でテスト・IADR-0037） (#96) (6e7856e)
- **FR-10**: 含み損益・ドローダウンの時価評価を純関数化（現在値/ピーク入力・IADR-0036） (#91) (a9b55a9)
- **FR-04,FR-03**: 損切り価格を権威データ化（OrderIntent 拡張・台帳永続化で 3% 近似を実値化・IADR-0035） (#90) (82011f8)
- **FR-06,FR-16**: 週報・月報テンプレート生成（ReportRenderer を ReportKind 拡張・IADR-0032 適用） (#88) (d56e9c8)
- **FR-06,FR-16**: 日報生成（数値コード集計の組み立て＋テンプレート化・散文はLLMドラフト抽象・IADR-0032） (#86) (c4e1d32)
- **FR-11**: 監査購読の残り2イベント（CostThresholdReached/InformationCollected）を監査台帳へ記録 (#85) (9d62c75)
- **NFR,FR-02**: 費用統制の間隔延長/停止を定時サイクル poller へ配線（IADR-0031） (#84) (fb18650)
- **FR-03,FR-10**: 保有ポジションをリスク管理から同期照会（IPositionStore 実データ化・IADR-0030） (#75) (70b962b)
- **FR-04,FR-10**: サイジング文脈をリスク管理から同期照会（ISizingContextProvider 実データ化・IADR-0029） (#74) (1d98766)
- **FR-04**: 確定済み日報方針を報告書サービスから同期照会（IDailyPolicyProvider 実データ化・IADR-0028） (#73) (7893ed6)
- **NFR**: 費用統制サービス（LLM 月次上限の間隔延長/停止・費用台帳・しきい値通知・IADR-0027） (#72) (bdee107)
- **FR-11**: 監査ログの購読拡張（AssumptionsChanged/ReportConfirmed を監査台帳へ記録） (#71) (b798857)
- **FR-16**: 損益集計のコア（PnlAggregator 純関数・実現損益/費用/税/評価損益をコード集計・IADR-0025） (#70) (b78941a)
- **FR-06,FR-07**: 報告書サービス Slice A（確定管理・確定済み日報方針の照会・確定通知・IADR-0024） (#69) (faf735e)
- **FR-02**: 取引サイクルの配線 Slice A（定時/イベント駆動の合流・市場カレンダー・IADR-0023） (#68) (58a8a98)
- **FR-01**: 情報収集サービス Slice A（正規化・プロンプト安全化・許可リスト・収集オーケストレーション・収集完了イベント・IADR-0022） (#67) (658f258)
- **FR-17**: 設定管理サービス Slice A（全体前提条件のバージョン管理・変更履歴・利用者変更・概算費用関数・変更通知・IADR-0021） (#66) (c2cfb5f)
- **FR-09**: 通知サービス Slice A（イベント購読→Discord アウトバウンド通知・安全既定・IADR-0020） (#65) (4542b1a)
- **FR-11**: 監査ログサービス（全ドメインイベントの時系列記録・DecisionId 相関照会・IADR-0019） (#64) (e97c731)
- **FR-10,FR-05**: ポートフォリオ状態を取引台帳の純射影で実データ化（IPortfolioStateProvider 実装・IADR-0018） (#63) (0e54c66)
- **FR-04**: 取引判断サービスのコア（LLM判断ポート・構造化出力・PositionSizer結線・TradeDecisionMade発行） (#62) (48d6d21)
- **FR-05**: 発注執行サービス（OrderApproved購読→ペーパー発注→OrderExecuted発行・注文/スリッページ永続化） (#61) (49aa1fc)
- **FR-10**: 損切りの機械執行（StopLossTriggered購読→LLM迂回でClose注文発行） (#60) (a583b70)
- **FR-03**: 市場監視 Worker ホスト（ポーリング・市場開場判定・MassTransit発行・基準値更新・永続化） (#58) (548f9bc)
- **FR-03**: 市場監視サービスのコア（変動判定・損切り検知・イベント契約・オーケストレーション） (#57) (a736dc0)
- **FR-10**: リスク管理 Worker ホスト（MassTransit消費者・PostgreSQL永続化・Keycloak認可エンドポイント） (#55) (ab50afe)
- **FR-10**: リスク管理サービスのアプリケーション層（設定ストア・kill switch・ロックアウト・スナップショット構築） (#52) (a7bf1bf)
- フロントエンドのCIワークフローを追加し、テストとカバレッジを独立させる (d1cfeb5)
- **FR-10**: 実装リポジトリ初期化とリスクガードコアを実装 (#3) (da3ba1c)
- **FR-10**: 実装リポジトリ初期化とリスクガードコアを実装 (#1) (6957b8c)

### 不具合修正

- **NFR**: Dependabot/submodule のトークン参照を PLANNING_REPO_TOKEN に統一 (#112) (8f965d1)
- **NFR**: 費用計上の並行 RMW を原子化ししきい値通知の重複/取りこぼしを解消（IADR-0034） (#89) (f1fa1c1)
- **FR-10**: 設定ストアの初回シードのレース窓を是正（IADR-0012 踏襲パターン共通） (#59) (badf227)
- **FR-05,FR-12**: PaperBrokerAdapter の入力検証と OrderStatus.Rejected を追加 (#43) (0f5af77)
- **FR-10,FR-19,FR-20**: リスク評価コアの是正（エントリー判定・差金決済・段階資金上限・相場操縦ガード・日次損失基準） (#41) (320ac7b)
- 修正されたソリューションファイル名を使用して脆弱性スキャンを更新 (739bf02)

### リファクタ

- **ADR-0001,IADR-0046**: ユニットリポジトリレイアウトへ再編する（src/ → backend/、platform ADR-0019 準拠） (#103) (d13e557)
- **FR-10,FR-16**: 平均取得単価法の畳み込みを共有純関数へ集約（SignedInventory・IADR-0033） (#87) (777dfba)
- **ADR-0001**: platform由来Foundationを本番非使用のTestSupport shimへ物理分離（IADR-0013） (#56) (21478fc)
- **FR-10**: ポジションサイジングに金額上限・資金のキャップを追加 (#42) (31da7db)

### ドキュメント

- **FR-04**: 実 LLM 費用計測の設計（IADR-0055・イベント計上） (#79) (#127) (18e4c13)
- **IADR-0051**: planning ADR-0007 参照ファイル名を実体に修正（owner-only-controls→trading-guard-and-margin） (#120) (1183fc5)
- **NFR**: CHANGELOG を自動更新 (#110) (94005b5)
- **NFR**: CHANGELOG を自動更新 (#54) (bebf9f9)
- **FR-04,FR-05**: 建玉効果の注文分解方針（ドテン/部分決済）を IADR-0037 で確定 (#94) (a0752cd)
- **IADR-0037**: 非同期契約の AsyncAPI 採用可否を再評価し当面不採用を確定（IADR-0009 再検討トリガ） (#93) (f55a4da)
- **NFR**: CHANGELOG を自動更新 (#46) (ebb0e25)
- **FR-10,FR-19,FR-20**: 必須仕様書の整備（機能・通信・データ・テスト仕様） (#45) (82e48ba)
- **P3**: テンプレート由来の存在しない Issue/PR/SHA 参照を除去 (#44) (ddcb025)
- **NFR**: CHANGELOG を自動更新 (#40) (25dcddb)
- **NFR**: CHANGELOG を自動更新 (#39) (383fd15)
- **NFR**: CHANGELOG を自動更新 (#38) (84d8eea)
- **FR-10,FR-17,FR-19**: TradingDefaults 逆算値の計画フィードバック記録を追加 (#37) (517a262)
- **NFR**: CHANGELOG を自動更新 (#5) (abc7fc0)
- **NFR**: OpenAPI を自動更新 (#4) (23645a9)

### テスト

- **IADR-0051**: s2s トークン伝播つき同期照会の実コンテナ E2E（#82 を締める） (#118) (7c5d5e3)
- **IADR-0050**: 統合 E2E 基盤 Slice B/C（Keycloak OwnerOnly 認証・マルチサービス通しパイプライン） (#116) (1c19347)
- **IADR-0049**: 実コンテナ統合 E2E 基盤（Testcontainers・compose healthcheck・CI 分離） (#114) (d070638)

### ビルド

- **deps**: bump actions/setup-node from 6 to 7 (#128) (6bea0cd)
- **deps**: bump planning from `07db93f` to `da20fc4` (#113) (c65ed86)
- **deps**: bump peter-evans/create-pull-request from 7 to 8 (#2) (135baf2)

### CI

- planning サブプロジェクトのコミットを更新する (4c5e0e7)
- planning サブプロジェクトのコミットを更新する (ea7d163)
- **NFR**: kit テンプレート更新を反映する — planning リンク定期検査（#104）と restore 自動発見化 (#105) (7971d43)

### その他

- **NFR**: dependabot に submodule pin 自動更新（gitsubmodule）を追加 (#111) (fdbb068)
- **IADR-0048**: ユニット実行環境スキャフォールド（docker-compose / appsettings / .env.example）を整備 (#108) (b1c91eb)
- commit-allowlist を現行 develop の実在 SHA へ是正（幻 SHA 除去） (#92) (6db0b4d)
- **ADR-0001**: 基盤ランタイム Foundation の最小移植（MassTransit再試行・可観測性・ヘルスチェック・Keycloak認証・相関ID） (#53) (a6b8c0b)
- update subproject commit reference in planning (50bf53b)
- サブプロジェクトのコミットIDを更新 (4353365)
- フロントエンドのCIワークフローを整理し、重複を排除 (4c379ee)
- update subproject commit reference (d76125d)
- Initial commit (fc103e4)
