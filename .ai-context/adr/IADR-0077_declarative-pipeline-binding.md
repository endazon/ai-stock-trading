---
title: IADR-0077 取引パイプラインの発行・購読バインディングを pipeline.json で宣言し、変換 DAG のみを段として表現して CI 検証＋GitOps 適用する
type: impl-adr
status: Accepted
related_ids: [ADR-0001, FR-02, FR-04, FR-05, IADR-0028]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# IADR-0077: 取引パイプラインの発行・購読バインディングを pipeline.json で宣言し、変換 DAG のみを段として表現して CI 検証＋GitOps 適用する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **ADR-0001**（platform 再利用＝可変部品への組み込み）、FR-02（取引サイクル）、FR-04（取引判断）、FR-05（発注執行）
- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)（platform 拡張規約への準拠・受け入れ基準②「宣言的バインディング」）
- platform 規約（原典・[隣接リポ](../../../microservices-platform)）: `IADR-0028`（宣言的パイプライン構成＝JSON 単一宣言＋起動時 fail-fast）、
  `docs/tech/composable-component-guide.md` §2.1（段の宣言）、`IADR-0049`（共通エンベロープ/契約テスト/ステージング適用の**段階適用と繰延**）
- 関連する実装仕様書: [20260718_ADR-0001_declarative-pipeline-binding](../specs/20260718_ADR-0001_declarative-pipeline-binding.md)

## コンテキストと課題

`#22` は platform 可変部品規約への準拠を求める。そのうち受け入れ基準②「発行・購読の宣言的バインディング定義が
GitOps で適用・ロールバックできる」に対応する。platform の該当規約は**確定・利用可能**である:

- 宣言は `pipeline.json`（`events`／`sources`／`steps{name,service,consumer,input,outputs,enabled}`）で表現する。
- 検証器 `scripts/validate-pipeline-config.js`（V1〜V6: 構造・一意性・イベント整合・接続性・循環・型名形式）は
  本リポジトリに移植済みだが、CI は `--self-test` のみで**自リポの pipeline.json が存在しなかった**
  （`ci.yml` のコメントが「構成ファイルを持つサービスを追加した時点で検証ステップを復活させること」と明記）。

現状の取引ドメインは 10 Worker が MassTransit `IConsumer<T>` を各 `Program.cs` で登録するのみで、発行・購読の
関係は**コードに散在し宣言が無い**。段の挿抜・有効化可否を運用（GitOps）で扱える単一の正が必要である。

## 決定

1. **取引パイプラインの発行・購読バインディングを `deploy/helm/ai-stock-trading/files/pipeline.json` に宣言する。**
   platform と同一スキーマ（`events`/`sources`/`steps`）を用い、検証器 V1〜V6 に合格させる。

2. **段（`steps`）として宣言するのは変換 DAG のみとする。** 取引サイクルの変換段
   （`decide-on-price-movement`／`decide-on-information`／`risk-approve`／`risk-stop-loss`／`execute-order`）を
   宣言する。**横断オブザーバ（監査＝全イベント購読・通知＝一部購読・読み取りモデル射影・市場監視の
   ベースライン更新）は段に含めない。** 根拠: platform の pipeline.json も監査・通知を段に含めず、変換 DAG と
   終端副作用段のみを表現している（`convert`/`catalog`/`ingest`/`wiki-sync`/`wiki-delete`）。オブザーバは
   イベント契約に後方互換で追随する購読であり、パイプラインの段挿抜の対象ではない。

3. **CI で実ファイルを検証する。** `ci.yml` の `pipeline-config` ジョブに実 `pipeline.json` の検証ステップを
   追加し、`scripts/scripts.test.js` にも実ファイルが検証器に合格する回帰テストを加える。

4. **GitOps 適用点として ConfigMap を公開する。** `templates/configmap-pipeline.yaml` が
   `ai-stock-trading-pipeline`（キー `pipeline.json`）としてクラスタへ公開する。ArgoCD がチャート同期時に適用し、
   **ロールバックは Git revert**（履歴不変の原則）。自己申告エンドポイント（#22 後続 PR）が本 ConfigMap を
   マウントして実効構成（有効な段）を照会する。

## 根拠 / 代替案

- **段として全 consumer を宣言しない**: 監査・通知・射影まで段に含めると、宣言が「イベントごとの購読者一覧」に
  膨張し、(service, queue) の一意性・接続性検証の意味（変換 DAG の健全性）が薄まる。platform の適用範囲に
  合わせ、変換 DAG に限定する方が「段の挿抜を安全に扱う」という pipeline.json の目的に忠実である。
- **起動時 fail-fast を今回は導入しない**: platform は宣言と実装を起動時照合して不整合なら起動拒否する
  （`AddPlatformPipelineStep<T>`）。本リポジトリの consumer は `IPipelineStep` を実装しておらず、全 Worker への
  波及（consumer 改修＋合成ルート結線）が大きい。本 PR は**宣言＋CI 検証＋GitOps 適用**（受け入れ基準②の
  「定義・適用・ロールバック」）に絞り、実装との照合は各サービスの自己申告（#22 後続 PR の introspection）で
  ドリフト検知する。起動時 fail-fast の全面導入は実需要（段の挿抜が頻発）到来時の後続とする。
- **エンベロープには踏み込まない**: 共通エンベロープは platform でも `IADR-0049` で繰延中であり、本 IADR の
  対象外（#22 の受け入れ基準①として別途扱う）。

## 影響

- 追加: `deploy/helm/ai-stock-trading/files/pipeline.json`・`files/README.md`・`templates/configmap-pipeline.yaml`。
- CI: `ci.yml` の `pipeline-config` ジョブに実ファイル検証ステップ、`scripts/scripts.test.js` に回帰テスト。
- コード（C#）変更なし・イベント契約変更なし（後方互換・既定挙動不変）。

## フォローアップ

- #22 後続 PR: 各サービスの自己申告（`GET /internal/introspection`）で実効構成（有効な段・選択中ポート実装・
  ガード設定バージョン）を照会（受け入れ基準③）。本宣言（ConfigMap）を実効構成の「宣言」側の源泉にする。
- 起動時 fail-fast（`IPipelineStep` 全面導入）は実需要到来時に別 IADR で起票。
- ArgoCD 実適用・ステージング昇格ゲートは実基盤整備後（platform IADR-0049 の繰延条件と同じ）。
