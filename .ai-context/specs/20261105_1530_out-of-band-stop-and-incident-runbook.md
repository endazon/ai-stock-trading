---
title: Discord を経ずに SSH から新規建てを止める手段（既存の kill switch の保持）と、障害対応の手順書
type: spec
status: accepted
related_ids: [NFR, NFR-03, NFR-05, NFR-06, FR-10, FR-09, UC-06, ADR-0006, ADR-0009, ADR-0024, IADR-0613, IADR-0062, IADR-0567, IADR-0344]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 全停止スイッチ・NFR-03 停止した系は自分の停止を通知できない)
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md (統制の優先順位・解除の非対称)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (§認証・認可 4 の確認ステップ)
---

# 仕様書: Discord を経ない停止と障害対応の手順書（#1530）

## 起点

- #1530。AST をクラウドの単一ノードへ移すと画面を持たず Discord だけで操作する。kill switch の窓口が Discord のボットだけになり、Discord に届かないときに取引を止める手段が無い。障害時の流れも 1 か所にまとまっていない。
- 基盤側の外部の死活監視（heartbeat・Alertmanager から Discord）は MSP#2402（別のセッションが実装中）。本件は AST 側の部分だけを持つ。

## 決定（IADR-0613）

1. **保持（operator hold）は構成で入れる**（`RiskManagement:OperatorKillSwitchHold:Enabled` / `Reason`）。既定は無効で、従来の振る舞いを変えない。
2. 保持が入っていれば、**risk-management の起動時に既存の kill switch を起動する**（`KillSwitchService.ApplyOperatorHold`。actor `operator-hold`・設定の変更履歴に残る。既に起動していれば上書きしない）。
3. 保持の間は**解除を拒む**（`KillSwitchHeldByOperatorException` → REST 409・gRPC ABORTED。REST と gRPC は同じ処理関数を通る）。起動は受け付ける。
4. 外しても kill switch は起動したまま残る（DB の状態）。再開は従来どおり Discord の解除（確認ボタン＋確認フレーズ）。
5. **構成の運び方は Secret `ast-operator-hold` の optional の secretKeyRef**（chart の risk-management の env。values.yaml と values-local.yaml）。Secret が無ければ env も無い。Secret は chart・ESO・ArgoCD の管理の外にあり、オーナーが SSH から作る・消す。
6. **スクリプト `scripts/ast-trading-stop.sh`**: `status`（変えない）・`--live hold --reason`・`--live release`・`--live isolate-discord`・`--live restore-discord`。Deployment の spec は変えず、risk-management の Pod を消して作り直させる（`kubectl set env` は helm の所有権と、ArgoCD の selfHeal と食い違う）。保持が効いたかは新しい Pod のログの印（`operator-kill-switch-hold: active`）で確かめる。理由は 0600 の一時ファイルから Secret へ渡し、プロセスの引数へ載せない。
7. 手順書 2 本（`docs/operations/`）: 停止の Runbook（本仕組み＋クラスタの外からの検知の分担）・障害対応の Runbook（検知・初動・証跡の保全・資格情報の入れ替え・復旧・連絡・振り返り。OpenD と証券口座の固有の手順を含む）。

## 母集合（規則 9）

- **解除の経路**: `git grep -ln 'Disengage(' -- 'backend/Services/*' ':!*Tests*'` → `DisengageKillSwitch/Endpoint.cs`（REST と gRPC の共有の処理関数）・`KillSwitchService.cs`。gRPC は `RiskControlsOwnerWriteGrpcService.DisengageKillSwitch` が同じ処理関数と `RiskControlEndpoints.MapException` を通る。呼び出し側（ボット）は `HttpKillSwitchController` / `GrpcKillSwitchController` で、失敗は「解除に失敗しました（HTTP 409）」として返る（変えない）。
- **kill switch の状態の読み手**: `IKillSwitchStore` の消費者（`TradingHaltService`・`PortfolioSnapshotBuilder`・`KillSwitchService`）。保持は DB の状態として起動するので、読み手は変えない。
- **状態の書き手**: `git grep -n 'SetState(' -- backend/Services/RiskManagementService ':!*Tests*'` → kill switch は `KillSwitchService` の 2 箇所だけ。解除は `Disengage` だけ。
- **`KillSwitchService` の生成**: `git grep -n 'new KillSwitchService(' -- backend` 8 箇所（試験）。引数は省略可にしたので変えない。DI は `Program.cs` の 1 箇所。
- **chart の profile**: `values.yaml`・`values-local.yaml`（risk-management の extraEnv を全要素写す＝両方に足す）・`values-grpc-measurement.yaml`（risk-management の extraEnv を持たない＝対象外）。**`values-cloud.yaml` は develop に無い**（並行の #1525）。同ファイルが risk-management の extraEnv を写すなら、同じ 2 行が要る（オーケストレーターへ報告）。
- **手順書の索引**: `docs/operations/operations.md` §関連文書・`scripts/README.md`。

## 受け入れ基準

- [x] スクリプトが既存の kill switch を起動し、状態の確認・ボットの停止を選べる（`scripts/ast-trading-stop.test.sh` 36 件。kubectl はスタブ。`scripts.repo.test.js` から起動）。
- [x] スクリプトの経路で起動した停止は、Discord（REST・gRPC の解除）から解けない。外すと従来どおり解除できる（`OperatorKillSwitchHoldTests` 8 件・`OperatorKillSwitchHoldWiringTests` 2 件）。
- [x] 値を設定しない既定の配備では、リスク管理の振る舞いが変わらない（`保持が無ければ起動しない_既定の振る舞いを変えない`・既存の kill switch・エンドポイントの試験が緑）。
- [x] 手順書が trace ブロックの規約を満たし、文書系の検査が通る。
- [x] 稼働クラスタ・OpenD・Discord には触れない。

## 範囲外

- 危険な操作の第 2 要素・ボットの権限の分割（オーナーの裁定待ち）。
- 外部の監視サービスの選定と heartbeat（基盤の作業）。
- 保持の自動化（警報で自動に保持を入れる）。保持は人が入れる。
