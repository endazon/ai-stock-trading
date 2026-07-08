---
title: 取引ドメイン契約とリスク管理コア（初期実装スライス）
type: spec
status: review
related_ids: [FR-10, FR-12, FR-19, FR-20, UC-01, UC-02, UC-06, ADR-0003, ADR-0007, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 取引ドメイン契約とリスク管理コア（初期実装スライス）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制）、FR-12（ペーパートレード）、FR-19（取引ガード）、FR-20（段階ゲート）
- ユースケース（UC）: UC-01, UC-02（取引サイクルの検証段）、UC-06（設定変更・緊急停止）
- 画面（SC）: なし（画面は計画側未着手）
- 関連 ADR: ADR-0003（AI判断のガードレール）、ADR-0007（取引ガードのソフト設定強制）、ADR-0008（段階ゲート）
- 計画書リンク: `../../planning/projects/ai-stock-trading/`（隣接クローン参照）

## 目的・背景

システムの安全性の要である「リスク管理サービスの決定的判定コア」を最初に実装する。
アーキテクチャ概要の設計上の要点に従い、生成AIの判断がどうであれ制約違反の注文が発注段へ到達しない構造の中核を、
外部依存（moomoo API・RabbitMQ・LLM）なしで単体テスト可能なドメインライブラリとして先行実装する。

moomoo PoC（ADR-0002、Proposed）が未実施のため、証券会社アダプタはポート定義（`IBrokerAdapter`）と
ペーパートレード実装（FR-12）のみを本スライスで実装する。

## 対象範囲

- 対象:
  - `AiStockTrading.Shared.Contracts` — 取引ドメインの共有契約
    - ドメイン型: 市場（JP/US）・売買区分・商品種別（現物/信用）・注文意図（OrderIntent）・注文状態
    - ポート: `IBrokerAdapter`（発注・状態照会・取消）、`IMarketDataSource`（現在値取得。最小定義）
    - イベント: `TradeDecisionMade` / `OrderApproved` / `OrderRejected` / `OrderExecuted`（platform のイベント契約規約に準拠した record）
  - `RiskManagementService.Domain` — 決定的判定コア
    - 取引ガード（FR-19）: 商品種別可否・市場別有効/無効・取引禁止銘柄・差金決済防止（同一銘柄の同日再エントリー禁止）
    - リスク統制（FR-10）: kill switch・1注文金額上限・1日発注金額上限・保有銘柄数上限・日次損失上限（2%）・
      最大DD上限・連敗時縮小・1取引リスクに基づくポジションサイジング（ATR連動）
    - 段階ゲート（FR-20）: Stage 0〜3 の動作モード（ペーパー/実弾）と資金上限の強制
    - 既定値は全体前提条件（05_trading-assumptions §5）どおり。設定は不変オブジェクトとして注入し、AI側から変更不可
  - `AiStockTrading.Shared.Infrastructure` — `PaperBrokerAdapter`（FR-12。現在値で即時約定する仮想実装）。
    基盤リポの規約に合わせ、可変部分（コネクタ/アダプタ）の実装は `Composable/Adapters/<種別>/` 配下に置く
    （本アダプタは `Composable/Adapters/Broker/PaperBrokerAdapter.cs`）
  - 上記の単体テスト（xUnit）
- 対象外（後続スライス）:
  - 各サービスのホスト（ASP.NET Core / Worker）、MassTransit 配線、PostgreSQL 永続化
  - moomoo アダプタ（ADR-0002 PoC 後）、情報収集・市場監視・取引判断・報告書・通知サービス
  - 相場操縦パターン検知の高度化（過剰訂正/取消の検知は注文履歴の統計を要するため、本スライスでは
    ガード設定のフラグとポイント（判定インターフェース）のみ用意する）

## 設計

- リポ構成は基盤実装リポ `microservices-platform` の規約を踏襲する（`src/{Services,Shared,Tests}`、
  slnx、Directory.Build.props（net10.0）、Central Package Management）。詳細は IADR-0001。
- 判定コアは純粋関数的に設計する: `RiskEvaluator.Evaluate(OrderIntent, RiskSettings, PortfolioSnapshot) → OrderScreeningResult`
  - `OrderScreeningResult` は 承認（承認済み数量を含む）／拒否（拒否理由コードの列挙）のいずれか
  - 拒否理由はコード化（`RejectionReason`）し、監査ログ（FR-11）と Discord 通知（FR-09）で後続利用できる形にする
  - 検証順序: kill switch → 段階ゲート → 取引ガード（FR-19）→ リスク上限（FR-10）。最初の分類で止めず
    全違反を列挙する（監査性優先）
  - フェイルセーフ（NFR）/ ADR-0003: kill switch・日次損失上限・最大DD・金額上限（1注文/日次）は
    「新規発注（エントリー＝買い）」にのみ適用し、保有ポジションの手仕舞い（売り）注文はブロックしない。
    段階資金上限・保有数上限・同日再エントリーも同様にエントリー限定。禁止銘柄は銘柄コードと市場の両方で照合する
- ポジションサイジング: `PositionSizer.Calculate(資金, 1取引リスク%, 損切り幅) → 株数`。連敗・DD 連動の縮小係数を適用。
  サイジングの実行責務は取引判断サービス（呼び出し元）が持ち、`RiskEvaluator` は確定済み意図の検証のみを行う（IADR-0003）
- 既定値（`TradingDefaults`）の一部は全体前提条件 §5 からの逆算値。逆算根拠は IADR-0002 に明示する
- `PaperBrokerAdapter` は `IBrokerAdapter` 実装。渡された現在値で即時全量約定し、注文状態遷移
  （受付→約定）をメモリ内で追跡する。判断・記録・報告のフローは実発注と同一（FR-12）

## 受け入れ基準

- [ ] リスク上限を超える注文意図が `Rejected` になり、拒否理由が列挙される（計画書の受け入れ基準に対応）
- [ ] kill switch 有効時、すべての新規注文（買い）が拒否される
- [ ] フェイルセーフ: kill switch・日次損失上限・最大DD・金額上限の到達時でも、保有ポジションの手仕舞い（売り）注文は承認される
- [ ] 取引ガードに反する注文（禁止銘柄・無効化された商品種別・無効市場・差金決済該当）が拒否され、理由が記録される
- [ ] Stage 0/1 では実弾モードの注文が拒否される（ペーパーのみ許可）。段階別資金上限を超える注文が拒否される
- [ ] 既定値（日次損失2%・1取引リスク0.5〜1%・DD上限・3〜5連敗でサイズ半減）が前提条件どおり適用される
- [ ] `PaperBrokerAdapter` で発注→即時約定→状態照会が一貫して動作する
- [ ] `dotnet build` / `dotnet test` が全緑

## テスト方針

- 受け入れ基準を xUnit の `[Fact]`/`[Theory]` に1対1で写像する（TDD: テスト先行）
- 取引ガード・リスク上限は境界値（上限ちょうど・上限+1）を `[Theory]` で検証する
- 連敗縮小・DD縮小は状態（PortfolioSnapshot）の入力パターンで検証する

## 計画書との差異

- 差異: あり
  - 計画書は「.NET 8」を明記するが、基盤実装リポ（microservices-platform）は net10.0 へ更新済みのため、
    本リポも net10.0 に揃える（IADR-0001。計画の意図は「基盤スタックへの追従」であるため差異は軽微。
    必要なら /plan-feedback で計画側の表記更新を提案する）

## 未決事項

- moomoo アダプタの実装は ADR-0002 の PoC 完了待ち（ポート定義のみ先行）
- 相場操縦パターン検知（過剰な訂正/取消）の具体的な閾値は運用データが必要なため後続スライスで確定
- 設定ストア（PostgreSQL）への永続化と構成情報API（platform FR-15）への自己申告は platform 統合スライスで実装
