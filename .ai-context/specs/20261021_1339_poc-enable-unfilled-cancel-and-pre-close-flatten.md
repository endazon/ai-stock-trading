---
title: PoC の構成（values-local）で判断由来の決済の未約定取消と閉場前の手仕舞いを有効にする（利用者裁定 2026-10-21・#1339 / #1407）
type: spec
status: done
related_ids: [FR-10, FR-19, SC-02, ADR-0067, ADR-0070, IADR-0539, IADR-0564]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0067_decision-exit-limit-unfilled-cancel-only.md (決定 2・6・フォローアップ 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0070_pre-close-entry-cutoff-and-flatten.md (決定 2「既定は無効とし、方針か設定で有効にする」)
---

# 仕様書: PoC の構成で判断由来の決済の未約定取消と閉場前の手仕舞いを有効にする（#1339 / #1407）

> 本仕様書は着手前に作成した（着手 2026-10-21）。基点は AST `origin/develop` `40de5a61`。
> 計画は project-planning `origin/main` `577d21f` で ADR-0067・ADR-0070 を読んだ。

## 起点

- 起点 ID: **FR-10**・**FR-19**（統制値）。SC-02（保存値が構成より優先される）。
- 計画 ADR: ADR-0067 決定 2・6（取消は既定無効。有効化は利用者が決める）、ADR-0070 決定 2（手仕舞いは既定無効。方針か**設定**で有効にする）。
- 裁定: #1339 の 2026-10-21T14:40Z のコメント（取消を有効にする。N は 15 分のまま）。手仕舞いはオーナー裁定（2026-10-21・依頼元経由）。**#1407 への裁定の記録はマージ前に PoC セッションが残す**（#1407 の 2026-10-21T14:43Z のコメントで依頼済み）。
- 新規 IADR: なし（実装判断を含まない。配備の値だけの変更）。

## 変更

| ファイル | 変更 |
| --- | --- |
| `deploy/helm/ai-stock-trading/values-local.yaml` | risk-management の extraEnv に `PreCloseFlatten__Enabled=true`。order-execution の extraEnv を新設し、values.yaml の 8 要素を同値で写したうえで `DecisionCloseUnfilledCancel__Enabled=true` を足す。冒頭の「order-execution は含めない」を改める |
| `docs/operations/operations.md` / `docs/blocked-tasks.md` | 「配備にも有効化を書いていない」を「本番は無効・PoC の経路は有効」へ（運用仕様書の「有効化するときは同じ変更で直す」の指示どおり） |
| `DecisionCloseUnfilledCancelOptions.cs` | 同じ趣旨のコメントだけを直す（挙動の変更なし） |

- 分数はどちらも既定と同じ（取消 N=15・手仕舞い M=15）なので値を書かない。
- 🔴 trade-decision には `PreCloseFlatten__Enabled` を書かない（IADR-0564 決定 1。書くと起動が止まる）。
- 本番既定（values.yaml）と templates は不変。

## 母集合（規則 9・10）

- values-local を読む試験: `GrpcOwnerClientGateTests`（対象の 6 面に order-execution は入っていない＝今回の変更で値は変わらない）、`Stage0EvaluationWindowPlanValueTests`（values-local を名前で除外・backtest だけ）、`BotRead/WriteGrpcWiringTests`（notification）、`Stage0RecordingLocalProfileTests`（trade-decision）。どれも今回の 2 サービスの差分に触れない。
- helm.yml の「values-local drops no env from prod default」: order-execution の配列を写し忘れると赤になる（変異で確認）。
- `TradingDefaultsTests` は既定（無効・15 分）を固定し、構成ファイルを読まない。
- 「有効化を書いていない」の文言: `docs/operations/operations.md`・`docs/blocked-tasks.md`・`DecisionCloseUnfilledCancelOptions.cs`（IADR-0539 は凍結記録のため直さない）。

## 受け入れ基準

1. values-local の描画で order-execution-service に `DecisionCloseUnfilledCancel__Enabled=true`、risk-management-service に `PreCloseFlatten__Enabled=true` が現れ、ほかの env は 1 本も増減しない。
2. trade-decision-service の描画に `PreCloseFlatten__Enabled` / `__Minutes__*` が無い。
3. 既定（values.yaml のみ）の描画はバイト等価。
4. order-execution の Broker__Provider は values-local でも `paper`。
5. `TradingDefaultsTests`・values-local を読む試験が緑。

## 検証の記録

- 変更前後の `helm template -f values-local.yaml` の全文 diff: 追加 4 行（上の 2 env）だけ。既定描画は `cmp` で一致。
- env 名の照合（helm.yml と同じ抽出）: 既定 273 件の欠落 0。order-execution の `Auth__GrpcOwnerClients__0` を落とす変異で欠落 1 件（赤）。
- `dotnet test` PlatformShim（GrpcOwnerClientGate・ReadinessProbe）32 件、RiskManagement `TradingDefaultsTests` 26 件、Backtest `Stage0EvaluationWindowPlanValueTests` 7 件、いずれも緑。
