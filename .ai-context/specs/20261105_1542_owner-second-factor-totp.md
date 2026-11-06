---
title: 取引を再開・緩める Discord の操作に、提供側で確かめる TOTP の確認を足す（段 1: 記録だけ）
type: spec
status: accepted
related_ids: [NFR-06, FR-14, FR-10, FR-20, UC-06, ADR-0047, ADR-0009, IADR-0615, IADR-0062, IADR-0450]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06・FR-14)
  - planning:projects/ai-stock-trading/06_technical/07_discord-bot-design.md (§認証・認可 4)
---

# 仕様書: 危険な操作の第 2 要素（TOTP）の段 1（#1542）

## 起点

- #1542。オーナー裁定（2026-11-05）: TOTP・提供側で検証・再利用の拒否・5 回でロック・段 1 は記録だけ／段 2 で拒む・対象 7 操作。

## 決定（IADR-0615）

1. 共有の `Totp`（RFC 6238）と `OwnerSecondFactorGate`（Shared.Infrastructure）。段は `OwnerSecondFactor:Mode`（Off 既定／Audit／Enforce）。
2. リスク管理: kill switch の解除・再開・段階の昇格（目標が今の段より上のときだけ）・GFV 停止の解除・乖離の取り込みの処理関数（REST と gRPC が共有）の頭で門を呼ぶ（`RiskSecondFactor.Require`）。Enforce で通らなければ `SecondFactorRejectedException` → 403。
3. 報告書: 確定の処理関数で門を呼ぶ。方針の改訂案の版（`IPolicyRevisionLedger.FindProposed`）なら `PolicyConfirm`、それ以外は `ReportConfirm` として記録する。
4. 要求の欄: REST `secondFactorCode`（任意・末尾）、proto `optional string second_factor_code`（6 つの要求に末尾追加）。
5. chart: risk-management と report に `OwnerSecondFactor__Mode=Audit` と `OwnerSecondFactor__Secret`（Secret `ast-owner-totp` の optional の参照）。values.yaml と values-local.yaml。
6. 登録: `scripts/ast-owner-totp-enroll.sh`（`--live`・`--rotate`・`status`）。
7. **門の位置（AI レビューの推奨を受けて段 1 のうちに直した）**: 入力の検証・冪等の判定の後、状態を変える直前（IADR-0615 決定 9）。`RiskSecondFactor.BeforeCommit` が callback を返し、`KillSwitchService.Disengage`（解除済みなら呼ばない）・`PauseService.Resume`（再開済みなら呼ばない）・`StageGateService.RequestTransition`（受理された昇格の台帳への追記の直前）・`GoodFaithViolationClearingService.Clear`（解除する記録がある時だけ）・`PositionDriftAdoptionService.Adopt`（台帳への追記の直前）・`ReportAppService.Confirm`（対象が在り・未確定・版が一致するときだけ）が呼ぶ。Off は callback を作らない。

## 母集合（規則 9）

- **7 操作の処理関数**: リスク管理の `DisengageKillSwitch`・`ResumeTrading`・`RequestStageTransition`・`ClearGoodFaithViolations`・`AdoptPositionDrift` の `Handle*`（REST と `RiskControlsOwnerWriteGrpcService` が共有）、報告書の `ConfirmReportEndpoint.HandleAsync`（REST と `ReportOwnerWriteGrpcService` が共有。`/report approve` と `/policy` の確定はどちらもこれを通る。監視銘柄の適用〔市場監視の `ApplyWatchlistProposal`〕は確定の後の処理で、確定が門を通る）。
- **止める向き（課さない）**: `EngageKillSwitch`・`PauseTrading`・段階の差し戻し（目標が今の段以下）・`EvaluateWithdrawal`。
- **例外の写し**: `RiskControlEndpoints.MapException`・`ReportEndpoints.MapException`（gRPC の写しも同じ関数を通り、403 → PERMISSION_DENIED）。
- **proto**: `risk_controls_owner_write.proto` の 5 つの要求・`report_owner_write.proto` の `ReportConfirmationRequest`。
- **chart の profile**: values.yaml・values-local.yaml（両サービスの extraEnv を写す）。values-grpc-measurement.yaml は両サービスの extraEnv を持たない。

## 受け入れ基準

- [x] 7 操作は、Enforce でコードが無い・違う・再利用なら 403 で何も変えない。正しいコードなら通る（`OwnerSecondFactorGateTests`・`OwnerSecondFactorWiringTests`・`ReportConfirmSecondFactorTests`）。
- [x] 止める向きの操作は、Enforce でもコードなしで通る。
- [x] Audit は記録だけで止めない。Off（既定）は従来どおり。
- [x] 登録のスクリプトは値を引数・標準出力に出さない（`ast-owner-totp-enroll.test.sh` 19 件）。
- [x] 受理できない要求・確定済みの再送・存在しない対象はコードを消費せず、失敗の数にも入らない。再送は従来どおり 200（`OwnerSecondFactorWiringTests`・`ReportConfirmSecondFactorTests`）。
- [x] Off は方針の台帳を読まない。Audit で台帳が読めなくても確定を壊さない。
- [x] gRPC の面でも Enforce の拒否は PERMISSION_DENIED（Risk・Report 1 本ずつ）。

## 範囲外

- ボットの確認の画面での 6 桁の入力・提供側への受け渡し（続き）。段 2 への切り替え。ボットの権限の分割（#1528 の後）。

## ［2026-11-06 追記 / #1552］古くなった記述の読み替え（本文は書き換えない）

- **決定 2・3 は決定 7 で置き換わった。** 門は処理関数の頭ではなく、サービスが書き込みの直前に呼ぶ（`RiskSecondFactor.BeforeCommit` が callback を返す。`RiskSecondFactor.Require` という関数は無い）。報告書は `ReportAppService.Confirm` が、確定へ遷移し得るときだけ呼ぶ。
- **決定 5 の profile は 3 つ。** values.yaml・values-local.yaml に加え、values-cloud.yaml にも同じ env がある（#1550 の検査が抜けを止める）。
- **受け入れ基準の `ast-owner-totp-enroll.test.sh 19 件` は、その時点の件数である。** 試験は後から増えている（values-cloud の確認・helm の描画）。件数は導出値で腐るため、以後の記録には書かない。
- ボットの 6 桁の入力（範囲外の 1 つ目）は #1552（仕様書 `20261106_1552_bot-second-factor-input`）。
