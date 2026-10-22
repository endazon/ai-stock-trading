---
title: 連結ローカルの起動器にメッシュ参入（サイドカー注入）の受け口を足し、再実行で引き継ぐ。OpenD は常に注入対象外（Refs GHSA-j4qv-2w99-xg5p）
type: spec
status: done
related_ids: [NFR, IADR-0570, IADR-0314, IADR-0283, IADR-0457, IADR-0439]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
---

# 仕様書: 起動器のメッシュ参入の受け口と引き継ぎ（Refs GHSA-j4qv-2w99-xg5p）

> 本仕様書は実装着手前に作成した（着手 2026-10-23。AST `origin/develop` `cacfa796`）。

## 起点（トレーサビリティ）

- 起点: Refs GHSA-j4qv-2w99-xg5p（基盤側の段 PR4a＝MSP#2092 がメッシュの mTLS の既定を STRICT にした。
  基盤の IADR-0590 のフォローアップ 1「AST の配備スクリプトが `mesh.sidecarInjection.enabled` を再実行で引き継げるようにする」）。非機能＝ `NFR`。
- IADR: 新規 **IADR-0570**。IADR-0314（Proposed）へ日付つきの追記（決定 2 の描画条件の改め。本文は書き換えない）。
- 計画との矛盾: なし（AST の計画はメッシュを定めず、基盤の再利用〔計画 ADR-0001〕に乗る。本変更は AST をメッシュへ入れる受け口であり、既定は変えない）。

## 作業の範囲（AST 側）

1. `scripts/k8s-local-deploy.sh`:
   - `resolve_ast_mesh_injection`: `AST_MESH_INJECTION=1|0`（`true|false` 可）→ 現行の helm リリースの
     `mesh.sidecarInjection.enabled` → 既定（無効・helm へ何も足さない）。それ以外の値は exit 2。前回値が真偽値として
     読めなければ、明示が無い限り止める。有効なら `mesh.sidecarInjection.excludeOpend=true` を必ず渡す。
   - `ast_apply_mesh_namespace_label`: 有効なら名前空間へ `istio-injection=enabled` を貼る（無ければ）。明示の 0 なら剥がす。
     それ以外は触らない（手のラベルが残れば WARN）。状態が変わった回は `AST_MESH_INJECTION_CHANGED=1`。
   - `ast_restart_step`: `AST_MESH_INJECTION_CHANGED=1` なら絞り込みを解いて OpenD を除く全件を restart する。
   - 本体: `[3/5]` で解決とラベルを行い（helm upgrade・restart より前）、`AST_HELM_VALUE_ARGS` に `AST_MESH_OVERRIDES` を足す。
2. chart `templates/opend.yaml`: 注入対象外の注釈を `excludeOpend` だけで描く（`enabled` に依らない）。`values.yaml` の注記。
3. 試験: `scripts/k8s-local-deploy.test.sh` に T-mesh-01〜11（kubectl スタブに `get namespace` / `label namespace` を足す）。
   `.github/workflows/helm.yml` に「注入の有無で OpenD の描画が変わらない」検査（陰性対照つき）。
4. 文書: chart README の #627 節、`docs/operations/operations.md`（配備の行・差の確認の行）、`docs/infra/infra.md`、`scripts/README.md`。

**範囲外**: 基盤側の STRICT への切り替え（MSP の起動器の `ISTIO_MTLS_MODE`）。実クラスタでの疎通確認（PoC の手順として PR 本文に置く）。
CronJob の従来型サイドカーの Job 完了（IADR-0314 の残余リスクのまま）。

## 受け入れ基準と試験の写像

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 既定（未指定・新規／本変更より前の配備）は helm の引数・名前空間を変えない | T-mesh-01 |
| 2 | 明示の 1 で有効・OpenD を対象外に固定・ラベルを貼る | T-mesh-02 |
| 3 | 未指定の再実行は現行を引き継ぐ（true・false とも） | T-mesh-03 / 04 / 06 |
| 4 | 明示の 0 で無効・ラベルを剥がす | T-mesh-05 |
| 5 | 不正な値・読めない前回値・ラベルの読み書きの失敗で止まる | T-mesh-07 / 08 / 09 |
| 6 | 状態が変わった回は OpenD を除く全件を作り直す（絞り込みがあっても） | T-mesh-10 |
| 7 | 本体の順序（解決とラベルが helm upgrade の前・値が helm に載る） | T-mesh-11 |
| 8 | 注入の切り替えで OpenD の描画が変わらず、OpenD は常に注入対象外 | `helm.yml` の IADR-0570 の検査 |

## 母集合（規則 9・10）

走査語: `sidecarInjection`・`istio-injection`・`excludeOpend`（`.ai-context/specs/`・`CHANGELOG.md` を除く）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `deploy/helm/ai-stock-trading/templates/opend.yaml` | 描画条件を改める |
| 2 | `deploy/helm/ai-stock-trading/values.yaml` | 注記を足す |
| 3 | `deploy/helm/ai-stock-trading/templates/namespace.yaml` | 変えない（`namespace.create=true` の構成のため） |
| 4 | `deploy/helm/ai-stock-trading/templates/cronjob.yaml` | 変えない（Job Pod は毎回作られる） |
| 5 | `deploy/helm/ai-stock-trading/README.md` #627 節 | 起動器の受け口・表・OpenD の 1 度きりの作り直しを足す |
| 6 | `docs/infra/infra.md` メッシュ注入の節 | 起動器の受け口を足す |
| 7 | `docs/blocked-tasks.md` A-12 | 変えない（解消済みの発覚時の記録） |
| 8 | `.ai-context/adr/IADR-0314` | 日付つきの追記 |
| 9 | `scripts/README.md`・`docs/operations/operations.md` | 起動器の説明に足す |

## 判断の要点（詳細は IADR-0570）

- 注釈の描画条件を `enabled` から外すのは、起動器（`namespace.create=false`）では注入を決めるのが名前空間のラベルであり、
  chart の `enabled` は OpenD の注釈と CronJob の注釈しか変えないためである。`enabled` に結ぶと、切り替えるたびに OpenD の
  Pod テンプレートが変わり作り直される。
- その代わり、注釈を初めて描く配備では `opend.enabled=true` の OpenD が 1 度だけ作り直される（PR 本文の配備前の手順）。
