---
title: 注文の承認（OrderApproved）に発行元の印（HMAC）を付け、発注執行で確かめる（段 1 は報告のみ・段 2 で発注しない）
type: spec
status: accepted
related_ids: [NFR, FR-10, UC-01, UC-02, UC-06, ADR-0009, ADR-0003, IADR-0610, IADR-0129, IADR-0568, IADR-0094]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md (決定4 3 統制は新規建てだけを止め、手仕舞いと損切りは止めない)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
---

# 仕様書: 注文の承認に発行元の印を付ける（#1527）

## 起点

- #1527（NFR・クラウド移行前の堅牢化）。
- 発注執行（OrderExecutionService）は、受け取った `OrderApproved` をそのまま発注へ進める。承認が正規の発行元（RiskManagementService）のものかを確かめる手段が無い。
- FR-10 と ADR-0009 決定 4 は「kill switch・日次損失ロックアウト・一時停止は新規建てだけを止め、手仕舞い（Close）と損切りは止めない」と定める。発注執行の停止状態の確認（IADR-0567 決定 3）も新規建てにだけ掛かる。

## 実測（develop `26d0a18a7`）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | `OrderApproved` の発行は RiskManagementService の 3 経路だけ: 審査（`TradeDecisionMadeHandler.cs` の 2 か所）・利用者の手仕舞い（`ClosePosition/Endpoint.cs`）・維持率の自動縮小（`MaintenanceMarginEvaluationService.cs`） | `git grep -n "PublishAsync" -- backend/Services/RiskManagementService` |
| 2 | 発注執行の `OrderExecutionAppService` の中で組む `OrderApproved`（2 か所）は発行しない（プロセス内で保護レグを張るための値） | `OrderExecutionAppService.cs` の `new OrderApproved(`（発行の呼び出しが無い） |
| 3 | 受け手で発注へ進むのは `OrderApprovedHandler`（発注執行）だけ。監査・台帳・活動射影・報告書の購読は記録だけ | `git grep -n "Handle(OrderApproved"` |
| 4 | 停止状態の確認は新規建てだけ（「決済・損切りは止めません」）。決済の承認は kill switch の下でも発注へ進む——計画どおり | `OrderExecutionAppService.FinalTradingHaltCheckAsync` |

## 決定（IADR-0610）

1. **発行元は印を付ける。** 3 経路の発行を `OrderApprovalPublisher`（唯一の口）へ寄せ、HMAC-SHA256 の印をメッセージのヘッダー `ast-order-approval-sig` に載せる。素の `PublishAsync(承認)` は構造テストが止める。
2. **受け手は確かめる。** `OrderApprovedHandler` は、予約・ブローカーに触れる前に `OrderApprovalVerifier.Admit` を呼ぶ。
   - 段 1（`OrderApprovalSigning:Mode=Report`・既定）: 合わない・無い・確かめられない（受け手に鍵が無い）は Warning を残して通す（従前の挙動）。
   - 段 2（`Enforce`）: 合わない・無いものは発注しない。Error を残し、何も発行しない（例外にもしない）。
3. **鍵は Secret で渡す。** `OrderApprovalSigning:Key`（base64・32 バイト以上）。値は Git・values に置かない。段 2 で鍵が無ければ起動で止める。鍵が読めない・短いときは段に依らず起動で止める（文言に値を出さない）。入れ替えの間だけ `OrderApprovalSigning:PreviousKey` を検証に使える。
4. **正準形は項目を明示して組む**（JSON の直列化に依らない）。`OrderApproved` と `OrderIntent`・`EntryBasis` の全項目を覆い、契約の項目の一覧を試験で固定する（足し忘れは赤）。
5. **決済の扱いは変えない。** kill switch の下でも決済は通す（計画どおり）。守るのは「その決済の承認が正規の発行元のものか」であり、印の検証は決済にも新規建てにも同じく掛かる。追加の停止の確認は入れない（入れると計画 ADR-0009 決定 4 に反する）。

## 段の切り替え（運用）

1. 鍵を作る（`scripts/gen-order-approval-signing-key.sh --vault`〔ESO 所有〕か `--k8s`〔従来経路〕）。値は表示されない。
2. risk-management と order-execution を作り直す（両方が鍵を読む）。段 1 のまま、発注執行のログに「発行元を確かめられませんでした」が出ないことを確かめる。
3. `global.orderApprovalSigning.mode=Enforce` で描画する（鍵の Secret が必須になる。無ければ Pod が起動しない）。

## 母集合（規則 9）

- 発行: 実測 1 の 3 ファイル 4 か所。走査語 `PublishAsync(outcome.Approv` / `PublishAsync(approval` / `PublishAsync(approved`。
- 受け手: `OrderApprovedHandler` の 1 つ（実測 3）。直接 new する試験は `OrderApprovedMethodResolvedPublishFailureTests` の 1 つ。
- 配備: `deploy/helm/ai-stock-trading/templates/deployment.yaml`（env の描画・Reloader の注釈）と `values.yaml`（`global.orderApprovalSigning`）。values-local の上書きは要らない（global の既定を継ぐ）。values-cloud は #1525 の管掌（足す値は PR の本文に書く）。
- 文書: `docs/security/security.md`（脅威の表に 1 行）・`deploy/helm/ai-stock-trading/README.md`・`scripts/README.md`。

## 受け入れ基準

- [x] 3 経路の承認すべてに印が付く（`OrderApprovalPublisherTests`・構造テスト）
- [x] 段 1 では印が無い・合わない承認も従前どおり発注される（`OrderApprovalVerificationTests`）
- [x] 段 2 では印が無い・合わない承認は発注されない（決済を含む）
- [x] 段 2 で鍵が無い構成は起動で止まる（`OrderApprovalSignatureTests`）
- [x] 鍵の値はログ・引数・リポジトリに出ない（スクリプトの Bash 試験・例外の文言の試験）

## 範囲外（残余）

- 承認を購読する他の受け手（台帳・活動射影・監査）の検証。発注には進まないが、台帳の記録は残る。段 2 の後に、同じ部品で確かめるかを決める。
- 承認以外の発注に至るメッセージ（決済の取消・訂正など）。同じ部品で広げられる。
- 鍵の定期の入れ替えの自動化（手順はスクリプトの `--rotate` と PreviousKey）。
