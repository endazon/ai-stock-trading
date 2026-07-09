# 変更履歴 (CHANGELOG)

## Unreleased

### 新機能

- **FR-10**: リスク管理サービスのアプリケーション層（設定ストア・kill switch・ロックアウト・スナップショット構築） (#52) (a7bf1bf)
- フロントエンドのCIワークフローを追加し、テストとカバレッジを独立させる (d1cfeb5)
- **FR-10**: 実装リポジトリ初期化とリスクガードコアを実装 (#3) (da3ba1c)
- **FR-10**: 実装リポジトリ初期化とリスクガードコアを実装 (#1) (6957b8c)

### 不具合修正

- **FR-05,FR-12**: PaperBrokerAdapter の入力検証と OrderStatus.Rejected を追加 (#43) (0f5af77)
- **FR-10,FR-19,FR-20**: リスク評価コアの是正（エントリー判定・差金決済・段階資金上限・相場操縦ガード・日次損失基準） (#41) (320ac7b)
- 修正されたソリューションファイル名を使用して脆弱性スキャンを更新 (739bf02)

### リファクタ

- **FR-10**: ポジションサイジングに金額上限・資金のキャップを追加 (#42) (31da7db)

### ドキュメント

- **FR-10,FR-19,FR-20**: 必須仕様書の整備（機能・通信・データ・テスト仕様） (#45) (82e48ba)
- **P3**: テンプレート由来の存在しない Issue/PR/SHA 参照を除去 (#44) (ddcb025)
- **NFR**: CHANGELOG を自動更新 (#40) (25dcddb)
- **NFR**: CHANGELOG を自動更新 (#39) (383fd15)
- **NFR**: CHANGELOG を自動更新 (#38) (84d8eea)
- **FR-10,FR-17,FR-19**: TradingDefaults 逆算値の計画フィードバック記録を追加 (#37) (517a262)
- **NFR**: CHANGELOG を自動更新 (#5) (abc7fc0)
- **NFR**: OpenAPI を自動更新 (#4) (23645a9)

### ビルド

- **deps**: bump peter-evans/create-pull-request from 7 to 8 (#2) (135baf2)

### その他

- update subproject commit reference in planning (50bf53b)
- サブプロジェクトのコミットIDを更新 (4353365)
- フロントエンドのCIワークフローを整理し、重複を排除 (4c379ee)
- update subproject commit reference (d76125d)
- Initial commit (fc103e4)
