---
title: 危険な操作の第 2 要素の試行回数を制限し、失敗を通知し、状態を 2 サービスで共有し、確かめを操作に結び付ける
type: spec
status: done
related_ids: [NFR-06, FR-14, FR-11, FR-09, UC-06, ADR-0047, ADR-0009, IADR-0626, IADR-0615, IADR-0622, IADR-0623]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06・FR-14)
---

# 仕様書: 第 2 要素の試行回数の制限（NFR-06）

> 本仕様書は実装着手前に作成する。実装の判断は IADR-0626。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: NFR-06（利用者本人のみ）。機能要求: FR-14（Discord からの操作）・FR-11（監査）・FR-09（通知）。
- ユースケース: UC-06（統制の操作）。
- 関連 ADR: ADR-0047（ボットのトークンはサービスの身元）・ADR-0009（止める向きと再開の非対称）。
- 起点: クラウドへの移行の前の点検の指摘（詳細は非公開）。利用者裁定（2026-11-07）「稼働前にすべて直す」。

## 現状（着手前に読んだもの）

- 門: `backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/SecondFactor/OwnerSecondFactorGate.cs`。リスク管理と報告書の `Program.cs` が singleton で 1 つずつ作る。
- **失敗の数え方**: 連続の失敗（`Invalid`・`Replayed`）を数え、`MaxConsecutiveFailures`（既定 5）で `LockoutMinutes`（既定 15）分止める。止めるたびに数を 0 に戻す（止める長さは延びない・上限なし）。欠け（`Missing`）は数えない。止めている間は `LockedOut`。
- **時刻の窓**: RFC 6238・30 秒・前後 1 段（3 つ）を受け付ける。時計は `TimeProvider`（Program は `TimeProvider.System`）。
- **使い回し**: 受け付けた段より新しい段だけを通す（`_lastAcceptedStep`）。状態はプロセスの中なので、リスク管理と報告書で別々（同じコードを両方で 1 回ずつ使える）。再起動で消える。
- **操作**: `Check(operation, code)` の `operation` はログに出すだけで、対象は持たない。
- 通知・監査: 無い（ログの印 `owner-second-factor:` の Warning だけ）。
- 呼び手: リスク管理の 5 操作（`RiskSecondFactor.BeforeCommit` → サービスが書き込みの直前に呼ぶ。IADR-0615 決定 9）と、報告書の 2 操作（`ConfirmReportEndpoint.SecondFactorBeforeCommit`）。callback は同期。
- 報告書 → リスク管理の同期照会は既にある（`risk-ledger` の HttpClient・`RiskManagement:BaseUrl`・サービスの身元のトークン。クラウドも REST）。
- 段の値: values-cloud は両サービスとも `Enforce`、values / values-local は `Audit`。

## 対象範囲

- 対象:
  - 状態の置き場をリスク管理の DB の単一行へ（EF のマイグレーションを足す）。報告書はリスク管理の新しい口へ問い合わせる。
  - 段階的な停止（15・30・60 分、4 回目で手動の解除）。手動の解除はシークレットの作り直し。
  - 確かめた結果のイベント `OwnerSecondFactorChecked`（監査・通知のハンドラ・整形）。
  - 門の `Check` に対象（`target`）を足し、7 か所の呼び手で対象を渡す。
  - 手順書（`docs/operations/owner-second-factor-runbook.md`）と登録のスクリプトの説明の追随。
- 対象外（範囲を変えない）:
  - ボットの画面・7 操作の流れ（要求 ID の往復は作らない。IADR-0626 残る制約 1・フォローアップ）。
  - メッセージングの配線（輸送・経路の設定・署名）。新しいイベントは既存の慣習の経路（conventional routing）に載るだけで、配線は触らない。
  - chart の値（段の形・Secret の参照）。helm は触らない。
  - 確かめの口を呼べるクライアントの一覧（azp）。

## 受け入れ基準

- [x] AC-1 連続 5 回の失敗で 15 分止め、止めている間は正しいコードも拒む。止めている間の試行は数えない。
- [x] AC-2 止めるたびに倍に延ばし（15・30・60 分）、4 回目は時間では解けない。成功で戻る。欠けは数えない。
- [x] AC-3 手動の解除はシークレットの作り直しで解け、再起動（門の作り直し）では解けない。状態は DB に残る。
- [x] AC-4 失敗・停止の始まり・手動の解除まで閉じたこと・成功（操作と対象）を発行し、監査が記録し、通知がオーナーへ送る。止めている間の試行は通知しない。コードは載せない。
- [x] AC-5 報告書とリスク管理の失敗を 1 つに数える。一方で使ったコードは他方で通らない。
- [x] AC-6 確かめは操作と対象に結び付き、一度使ったコードは別の操作・別の対象に使えない。
- [x] AC-7 確かめの口はサービスの身元だけが呼べ、報告書の 2 操作の外は 400。届かない・知らない応答は `Unavailable`（Enforce は拒む）。
- [x] AC-8 変異（失敗を数えない）で試験が落ちる。

## 試験（受け入れ基準への写像）

| AC | 試験 |
| --- | --- |
| 1・2・3・6 | `Shared.Infrastructure.Tests/SecondFactor/OwnerSecondFactorAttemptLimitTests`（差し替えた時計で固定） |
| 3・5 | `RiskManagementService.Tests/.../OwnerSecondFactorSharedStateTests`（DB に残る・2 サービスの合算・使い回し） |
| 4 | 同上（発行）・`NotificationTemplateGoldenTests`・`OwnerSecondFactorCheckedNotificationHandlerTests`・`AuditEntryFactoryTests` |
| 5・7 | `ReportService.Tests/.../ReportConfirmSharedSecondFactorTests`・`OwnerSecondFactorAttemptLimitTests`（持ち主の応答） |
| 8 | 変異の手順（下） |

## 変異の確認（AC-8）

- `TotpOwnerSecondFactorVerifier` の失敗の数え上げ（`ConsecutiveFailures + 1`）を外すと、共有の試験 10 件・リスク管理の試験 3 件が落ちる。
- 止める長さの倍（`Math.Pow(2, …)`）を外すと 1 件、使い回しの拒否を外すと 5 件が落ちる。いずれも元へ戻して緑を確かめた。

## 母集合（規則 9・10）

- 誤りの側の語（「15 分ロック」「1 回ずつ使える」「プロセスの中」「連続 5 回」）で `docs/`・`deploy/`・`scripts/`・`.ai-context/adr/` を走査した。追随が要るのは手順書 1 本（限界の節・失敗の分岐・段の節）と、IADR-0615 への追記（決定 8・残る制約 2 を本 IADR が置き換える）。chart・他の運用文書は止め方・共有に触れていない（除外）。作業仕様書（凍結）は直さない。

［2026-11-07 追記］監査の指摘で、報告書が Enforce で宛先を持たないときにプロセスの中の状態へ落ちる形を閉じる向きへ直した（`UnavailableOwnerSecondFactorVerifier`。試験は `ReportConfirmSharedSecondFactorTests` の「宛先が無ければ…」）。values-local・values-cloud の報告書から Secret の参照を外し、`ast-owner-totp-enroll.test.sh` の本数の検査と、values-cloud の描画で報告書が宛先を持ち Secret を読まないことの検査を足した（helm は手元に無く、描画の検査は合成の YAML で awk の抽出だけを確かめた）。対象外の「chart の値は触らない」はこの追記で改める。
