# pipeline.json — 取引パイプラインの宣言的バインディング（構成の正）

`pipeline.json` は取引ドメインの**イベント発行・購読バインディング**（どの段がどのイベントを購読し、
どのイベントを発行するか）を宣言する**単一の正**である（ADR-0001／platform ADR-0018 の可変部品規約、
[IADR-0028（platform）] の宣言的パイプライン構成、[IADR-0077](../../../../docs/adr/IADR-0077_declarative-pipeline-binding.md)）。

## 位置づけ

- **構成の正**: 取引サイクルの変換 DAG（`PriceMovementDetected`/`InformationCollected` → `TradeDecisionMade`
  → `OrderApproved`/`OrderRejected` → `OrderExecuted`、および `StopLossTriggered` → `OrderApproved`）を
  段（consumer）単位で宣言する。段名・購読イベント・発行イベント・有効状態を一箇所で表現する。
- **横断オブザーバは段に含めない**: 監査（全イベント購読）・通知（一部購読）・読み取りモデル射影・
  市場監視のベースライン更新は変換段ではないため宣言しない（platform の pipeline.json が監査・通知を
  含めないのと同じ方針。[IADR-0077](../../../../docs/adr/IADR-0077_declarative-pipeline-binding.md) 参照）。

## 検証（CI ゲート）

構造・接続性・循環を `scripts/validate-pipeline-config.js`（V1〜V6）で検証する。CI（`ci.yml` の
`pipeline-config` ジョブ）が本ファイルを実引数に検証する。ローカル検証:

```sh
node scripts/validate-pipeline-config.js deploy/helm/ai-stock-trading/files/pipeline.json
```

## GitOps 適用とロールバック

- 本ファイルは Helm チャートの一部として **ArgoCD が適用**する。`configmap-pipeline.yaml` が
  ConfigMap `ai-stock-trading-pipeline`（キー `pipeline.json`）としてクラスタへ公開する。
- **ロールバック**は Git revert（履歴不変の原則）。revert した時点で ArgoCD が旧宣言へ同期し直す。
- 段の有効/無効の切替は `enabled` の変更（構成のみ）。**入力イベント型の変更は段のコード改版を伴う**
  ため構成のみでは行わない（platform 規約と同じ）。

## 未整備（境界・後続）

- **起動時 fail-fast**（宣言と実装の照合による起動拒否）は本リポジトリ未導入。現状は本宣言を
  CI 検証＋各サービスの自己申告（`GET /internal/introspection`・#22 後続 PR）でドリフト検知する。
- ステージング→本番の環境昇格ゲートは単一環境のため未整備（platform IADR-0049 の繰延条件と同じ）。
