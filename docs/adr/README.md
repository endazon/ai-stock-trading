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
> 注（`IADR-0052`〜`0055` の暫定欠番）: 本ブランチ（feat/13-moomoo-adapter）時点で `IADR-0052`〜`0055` は**並行ブランチが採番中で未マージ**（0052=chart/feat/122・0053=OpenD/feat/124・0054=取引サイクル/feat/122・0055=費用計測/feat/79）。番号衝突を避けるため本 PR の IADR は `IADR-0056` を用いる。これらのブランチが `develop` にマージされると欠番は充足される（上記 0037〜0041 と同じ運用）。

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
| IADR-0056 | moomoo SIMULATE PoC 完了に基づき実アダプタを実装（実弾は引き続きゲート） | Accepted |
