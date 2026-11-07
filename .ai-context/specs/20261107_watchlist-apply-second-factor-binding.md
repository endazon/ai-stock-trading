---
title: 監視銘柄の入れ替えの適用を、提供側で「第 2 要素つきで確定済みの案」に結び付けて確かめる（空売りの実弾の解禁の判定の記録にも第 2 要素を課す）
type: spec
status: accepted
related_ids: [NFR-06, FR-13, FR-14, FR-20, ADR-0042, IADR-0634, IADR-0615, IADR-0626, IADR-0433, IADR-0622, IADR-0281, IADR-0631]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 1 案に載った銘柄だけを適用する)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-13・FR-14・FR-20・NFR-06)
---

# 仕様書: 監視銘柄の入れ替えの適用を確定済みの案に結び付ける

## 起点

- 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）。非機能 NFR-06（危険な操作の第 2 要素）。
- 決定は [IADR-0634](../adr/IADR-0634_watchlist-apply-bound-to-confirmed-proposal.md)。

## 1. 現状（develop `3e8ade984`）

| 項目 | 中身 | 場所 |
| --- | --- | --- |
| 適用の口 | `POST /monitor/watchlist/proposal-apply`（OwnerOnly・ボットの許可表）。本文の `changes`・`expectedWatchlist`・`proposalRef` をそのまま使う | `MarketMonitorService/Features/MarketMonitor/ApplyWatchlistProposal/Endpoint.cs` |
| gRPC 面 | 同じ処理関数（`HandleAsync`）を呼ぶ | `WatchlistOwnerWriteGrpcService.cs` |
| 順序の強制 | ボットの中だけ（方針の確定〔第 2 要素つき〕が通ったら台帳の案を読んで適用） | `NotificationService/.../PolicyApprovalCommandHandler.cs` |
| 確定の第 2 要素 | 報告書の確定の直前に `OwnerSecondFactorGate`（`PolicyConfirm`／`ReportConfirm`。状態はリスク管理） | `ReportService/Features/Reports/ConfirmReport/Endpoint.cs` |
| 案の台帳 | 報告書サービス。`Lookup` が「その版で確定されているときだけ」案を返す | `ReportService/Features/Reports/WatchlistProposal/Endpoint.cs` |
| 空売りの解禁の判定 | `POST /risk-controls/stage-gate/transition` の `approval=ShortSellReleaseVerdict`。第 2 要素を通らない（昇格の分岐だけが通る） | `RiskManagementService/Features/RiskManagement/RequestStageTransition/Endpoint.cs` |

## 2. 方式の選び方

- 適用の口で TOTP をもう一度確かめる形は採らない。確定で使ったコードは再利用を拒まれる（IADR-0626 決定 4）ので、ボットは利用者に 2 つ目のコードを求めることになる。
- 採る形: **確定の第 2 要素は報告書で確かめ、適用は市場監視（提供側）が報告書の台帳へ照会して、その確定に結び付ける。**
  - 照会の口は報告書の `GET /reports/policy-revisions/watchlist-proposal/binding`（サービスの身元だけ。ボット・利用者は通さない）。
  - 判定は既存の `Lookup` と同じ。加えて、その案の確定が第 2 要素を通ったか（確定の時の記録 `ProposalSecondFactorVerified`。照会では `secondFactorVerified`）を返す。
    - 照会の時点の構成（段）では判定しない。段 1 で確認コードなしに確定した案を、段 2 へ上げた後に適用させないため。
  - 予約の口は報告書の `POST /reports/policy-revisions/watchlist-proposal/binding/reserve`（サービスの身元だけ）。
    - 予約の時刻 `WatchlistApplyReservedAt` は同時実行のトークンで、1 つの案につき 1 回だけ通る（二重適用を止める）。
  - 市場監視の門（`WatchlistProposalBindingGate`）は `OwnerSecondFactorGate` と同じ形にする。
    - 段は Off / Audit / Enforce（`Watchlist:ProposalBinding:Mode`）。Audit は記録だけ、Enforce は結び付いたときだけ適用する。
    - 照会が届かない・読めないときは `Unavailable` とし、Enforce では拒む。
    - ログの印は `watchlist-proposal-binding:` にする。

## 3. 結び付きの条件（Enforce で全部を満たすときだけ適用）

1. `proposalRef` が `<会話キー>-v<版>` の形で読める（ボットが送る形。`PolicyApprovalCommandHandler`）。
2. 報告書がその版の案を持ち、**その版で確定されている**（`Lookup` の Found）。
3. その案の確定が第 2 要素を通った記録がある（`secondFactorVerified=true`。確定の時に報告書が `ProposalSecondFactorVerified` へ残した値）。
4. 適用の記録がまだ無い（`ApplyRecorded=false`）。
5. 案を作った時点の監視銘柄（`Snapshot`）があり、要求の `expectedWatchlist` と集合として一致する。
6. 要求の `changes` が台帳の `Changes` と、操作と銘柄の並びで一致する（理由の文は比べない）。
7. 1〜6 を満たしたら、台帳で適用を予約する。予約が通ったときだけ適用する（予約済みなら `AlreadyApplied`、届かなければ `Unavailable`）。

結び付いたら、適用と変更履歴には台帳の入れ替え（理由も台帳の確定済みの値）を使う。要求の理由は使わない。
予約の後に適用が失敗した（409・400）案は、予約を戻さない（戻すと二重適用の窓ができる）。入れ替えは設定画面から行う。

拒否は 403（固定の文言。入力を含まない）。gRPC 面は `PERMISSION_DENIED`。ボットは非 2xx を「適用しなかった」と扱う（既存の `Rejected`）。

## 4. 空売りの実弾の解禁の判定（L-10 の扱い）

- 判定の記録も、受理できた（台帳へ追記する）ときだけ、直前に第 2 要素の門を通す。
- 操作の種類は `StagePromote`（実弾へ近づく向きの承認）を使う。対象は `short-sell-release-verdict` とする。
- 操作の種類を新設しない理由: 列挙を足すと、状態・通知・gRPC の写しの面が増える。判定は段階の昇格と同じ向きの承認である。
- 受理できない判定（段階・条件が満たない）は、コードを消費しない。

## 5. 配線

- 市場監視: `Reports:BaseUrl`（s2s のトークン。既存の `ServiceAuth`）と `Watchlist:ProposalBinding:Mode`。
- chart:
  - values.yaml は `Reports__BaseUrl=""`・`Audit`。
  - values-local は `Reports__BaseUrl=http://report-service:8080`・`Audit`。
  - values-cloud は `Reports__BaseUrl=http://report-service:8080`・`Enforce`。
- NetworkPolicy（IADR-0631）: values-cloud の `networkPolicy.services.market-monitor.calls` に `report` を足す。

## 6. 母集合（規則 9）

- `git grep -n 'proposal-apply\|ApplyWatchlistProposalEndpoint.HandleAsync'` の当たり。直すのは市場監視の口・gRPC 面と、その試験。ボット側は形を変えないので触らない。
- `git grep -n 'ShortSellReleaseVerdict'` のうち、記録の口（Endpoint・StageGateService）。gRPC の写しは種別を写すだけなので触らない。
- chart の `market-monitor` の env（values / values-local / values-cloud の 3 つ。helm.yml の「env の写し忘れ」の検査が揃いを見る）と、`networkPolicy.services`。

## 7. 受け入れ基準

1. Enforce: 確定済み・未適用・案と一致する要求だけを適用する。次の要求は 403 で、何も適用しない。
   - 案と違う銘柄
   - 確定されていない版
   - 適用済み
   - `proposalRef` の形の違い
   - 確定の第 2 要素の記録が無い（`secondFactorVerified=false`。段 1 で確認コードなしに確定した案を含む）
   - 照会・予約が届かない
   - 同じ案の 2 度目（予約済み。監視銘柄を変えない案でも止まる）
   適用と変更履歴の理由は台帳の値になる。
2. Audit は、結び付かなくても記録して適用する。Off は照会しない。
3. gRPC 面も同じ判定になる（一致する要求は適用し、403 → `PERMISSION_DENIED`。同じ案の 2 度目も `PERMISSION_DENIED`）。
4. 報告書の照会と予約の口は、サービスの身元だけが通る（ボット・利用者のトークンは 403）。
   - 報告書は、方針の案の確定が `Verified` だったときだけ `ProposalSecondFactorVerified` を true にする。
   - 予約は 1 つの案につき 1 回だけ通る。実 PostgreSQL で 2 つの予約を同時に流しても、通るのは 1 件だけ（統合試験）。
5. 空売りの解禁の判定は、受理できるときだけ第 2 要素を通す（Enforce でコードが無ければ記録しない）。受理できない判定は門を呼ばない。
6. chart: 3 つのプロファイルに env が揃う。values-cloud は Enforce で、NetworkPolicy が市場監視から報告書への通信を許す。

## 8. ［2026-11-07 追記 / レビュー］結び付きを強める 4 点（§2・§3・§7 へ反映済み）

1. 確定の第 2 要素は、照会の時点の構成ではなく、確定の時の記録で判定する。報告書は、方針の案の行の `ProposalSecondFactorVerified` に `Verified` だったかを残す（移行 `AddPolicyProposalSecondFactorAndApplyReservation`）。
2. 二重適用を止めるため、台帳で適用を予約する（`WatchlistApplyReservedAt`・同時実行のトークン・1 回だけ）。市場監視は結び付いた要求だけを予約し、2 度目は拒む。
3. 適用と変更履歴には、台帳の入れ替え（理由も台帳の値）を使う。
4. 門は `GetRequiredService` で解決する（登録が無ければ黙って飛ばさない）。

