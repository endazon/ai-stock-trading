---
title: 再実装版への切替計画の再測定と、利用者の判断事項への推奨（#346）
type: spec
status: accepted
related_ids: [NFR-09, NFR-10, FR-08, FR-11, IADR-0287, IADR-0341, MSP:IADR-0459]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs: []
---

# 再実装版への切替計画の再測定と、利用者の判断事項への推奨

## 背景

#346 の準備（移行仕様書・突合スクリプト・テスト・リハーサル）は PR #670（2026-09-03）で済み、2026-09-11 の監査も
「AI が着手できる残作業は無い。残るのは全て利用者承認」と判定した。利用者の指示（2026-09-26・blocked:human の引き取り）で、
承認事項 4 点の前提を測り直し、推奨を添えて利用者が決められる形にする。

🔴 **ハードリミット**: 切替・データの破棄・ブランチ削除・issue のクローズ・稼働クラスタへの書き込みは行わない。

## 再測定（2026-09-26・読み取りのみ）

| 対象 | 方法 | 結果 |
| --- | --- | --- |
| 保全対象 | `bash scripts/cutover-count-reconcile.sh manifest` | 7 DB・38 テーブル（ledger 24／state 12／reserved 1／dedup 1）。移行仕様書と一致 |
| バックアップ | `kubectl get cronjob -A`・`kubectl get pv`・PVC `postgres-data` | CronJob 無し。`postgres-data` は 2Gi・local-path・回収方針 Delete・2026-09-15 作成 |
| 秘密情報 | `kubectl get externalsecret -n ai-stock-trading` | `ast-secrets` / `moomoo-credentials` / `moomoo-rsa` は ExternalSecret（vault-backend）所有 |
| 基盤の切替 | MSP の `docs/migration/cutover-discard-and-rebuild.md`（読み取り専用の隣接クローン） | 基盤アプリ DB（文書 DB を含む）・Qdrant・MinIO・Wiki.js・可観測性データを破棄。Postgres / Keycloak / Vault の PVC は残し、AST の DB と realm と namespace は触らない。realm `platform` の作り直しで AST の 4 クライアントの secret は宣言値へ戻り、実行時に付けたロールの付与（`trading-owner` など）は失われる |
| ブランチ | `git ls-remote --heads origin` | 6 本（develop・main・automation/changelog-update-develop・作業中 PR 3 本） |
| 依存 issue | `gh issue view` | #204 open・#342 open（blocked:env）・#24 open・#344 open |
| KB への再投入経路 | `ReportKnowledgeMapper.ToDocument` の呼び出し元 | 確定エンドポイント（`ConfirmReport/Endpoint.cs`）の 1 か所だけ。確定済み報告書を後から KB へ入れ直す経路は無い |

## 変更

- 移行仕様書に §現況（2026-09-26 再測定）と利用者が決めること を追加（再測定の表と、承認事項 4 点の推奨・決めないと止まるもの）。
  `ast-secrets` の対象外理由・旧実装のブランチの記述に日付つき追記、§承認事項から新節へ誘導。frontmatter の updated・trace を更新。
- 運用仕様書 §バックアップ・リストア（空欄だった）に、未裁定の案（対象・頻度・保管期間・保管先・RPO/RTO・取得とリストア試験のコマンド）を書いた。
  リストア試験は `AST_DB_PREFIX=restore_test_` で同じ manifest を測る。稼働中の取得では `compare` が FAIL 0 にならないことを明記した
  （compare は件数・指紋の差をすべて FAIL にするため）。

## 監査の指摘による是正（2026-09-26）

- リストア試験のブロックを、取得のブロックの変数（`$out`）に頼らない自己完結の形にした（`src` を先頭で定め、`sha256sum -c` で保管中の破損を先に確かめる）。
- 基盤の realm の作り直しで失われるのは client secret だけでなく、**実行時に付けたロールの付与（宣言に無い利用者への `trading-owner` など）**もである
  （MSP の移行仕様書 §破棄の境界。MSP/IADR-0459）。移行仕様書の再測定の表・帰結・推奨 4 の 3 か所に足した。
- `related_ids` の `NFR` を計画の要求一覧（`02_requirements/01_requirements.md`）で確かめ、`NFR-09`（未確定データの無期限保持）と
  `NFR-10`（業務台帳・監査証跡の 7 年保持）へ具体化した。

## 利用者の判断に残したもの

1. 切替の実施可否と日時（推奨: 前段ゲートが閉じるまで実施しない）
2. バックアップの保管先・保管期間・試験頻度（推奨: 切替を待たず今から。7 DB＋Vault、クラスタ外 2 か所、7 年、四半期）
3. 旧実装の廃止の範囲（推奨: ブランチは対応不要、文書追随と issue トリアージは切替の後）
4. 基盤側 KB の扱い（推奨: 基盤の破棄を受け入れ、確定報告書を KB へ入れ直す手段を AST 側に別 issue で用意する）

## 受け入れ基準

- [x] 承認事項の前提を測り直し、変化を記録した
- [x] 各承認事項に推奨と「決めないと何が止まるか」を添えた
- [x] バックアップ・リストアの空欄を未裁定の案で埋めた（コマンドは既存スクリプトの引数と一致する）
- [ ] 利用者が 4 点を決める（#346 の残件）
