---
title: IADR-0277 CLAUDE.md の 3 段化記述を platform ADR-0065 へ追随させ、Hosted/ を AST 固有の第4の頂点として確定する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0259
  - IADR-0265
  - MSP:ADR-0065
  - MSP:ADR-0068
author: endazon (with Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md
  - planning:projects/microservices-platform/06_technical/12_backend-application-stack.md
---

# IADR-0277: CLAUDE.md の 3 段化記述を platform ADR-0065 へ追随させ、Hosted/ を AST 固有の第4の頂点として確定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: endazon（方針）/ Claude Code（実測・起案）

## 起点・関連

- **起点 ID: `NFR`（無採番）。** 本件は #613 の文書面の前提整備＝規約整備・文書統制のメタ作業であり、
  `.claude/rules/traceability.md` 無採番許容ケース 2 に当たる（[IADR-0259](IADR-0259_single-project-vsa-structure.md)
  と同じ判断）。
- 関連する計画書 ID: platform `ADR-0065`（サービスの標準構成を単一プロジェクト＋VSA へ改定）決定 2・決定 6・
  フォローアップ 7／platform `ADR-0068`（3 段目の判定基準）
- 関連する実装仕様書: [20260902_527_613_vsa-docs-prerequisites](../specs/20260902_527_613_vsa-docs-prerequisites.md)
- 関連 issue: [#527](https://github.com/endazon/ai-stock-trading/issues/527)（Tests 1 プロジェクト化。実測で解消しクローズ）・
  [#613](https://github.com/endazon/ai-stock-trading/issues/613)（本 IADR はその文書面の前提。フォルダ移動は別 PR）
- 関連 IADR: [IADR-0259](IADR-0259_single-project-vsa-structure.md)（決定 5 が `Hosted/` をルート直下に置くと定めた先行決定。
  本 IADR はその決定を platform `ADR-0065` 確定後の樹形に照らして追認する）

## コンテキストと課題

`CLAUDE.md` §技術スタック別ルール › C#/.NET のソリューション行は、AST バックエンドの VSA 移行後の樹形を
次のように書いている（IADR-0259 時点の記述。2026-08-28）。

> 新は `Services/<Name>/` 直下の `<Name>.csproj` ＋ `Features/<集約>/ Domain/ Infrastructure/ Hosted/ Common/
> Tests/`（**3 段目のスライス分割は MSP も未実装のため採らない**）

この但し書きは、2026-08-28 時点で「MSP 側も 3 段化していない」という実測を根拠にしていた。**その後
（2026-08-30）platform `ADR-0065` 決定 2 が 3 段化（`Features/<集約>/<操作>/`）を規範として確定し**、
同 ADR の実測（フォローアップ 7）が「ai-stock-trading `CLAUDE.md` の『3 段目のスライス分割は MSP も未実装のため
採らない』を決定 2 に合わせて訂正する」と名指しで求めている。**根拠（MSP 未実装）が消えたため、
但し書きの撤回が要る。**

加えて #613 補足は、`CLAUDE.md` が標準要素として挙げる `Hosted/` が **platform `ADR-0065` の樹形
（`Features/Domain/Infrastructure/Common/Tests` の 5 つ）に無い**ことを指摘し、
(a) 現状維持（第 4 の頂点として認める）(b) `Infrastructure/Hosted/` へ寄せる (c) `Features/<集約>/<操作>/`
の一部として扱う、のいずれかを実装側で判断し根拠を IADR に残すことを求めている。

## 実測（`Hosted/` の中身。2026-09-02・`develop` 分岐元 `5fb778e7`）

`Hosted/` を持つのは 11 サービス中 6 サービスである。

| サービス | `Hosted/` の中身 | 参照する Features |
| --- | --- | --- |
| `CostControlService` | `ProcessedMessageRetentionService.cs` | `CostControlService.Features.*` |
| `InformationCollectionService` | `CollectionOptions.cs` `CollectionPollingService.cs` `DegradationStateTracker.cs` | `InformationCollectionService.Features.InformationCollection` |
| `MarketMonitorService` | `MonitorOptions.cs` `MonitorPollingService.cs` | `MarketMonitorService.Features.MarketMonitor` |
| `OrderExecutionService` | `BrokerAvailabilityProbeService.cs` `BrokerPositionSnapshotService.cs` `OrderFillPollingService.cs` `OrderReservationReconciliationService.cs` `OrderReservationRetentionService.cs` `ProtectiveStopGuardService.cs` | `OrderExecutionService.Features.OrderExecution` |
| `ReportService` | `ReportAutoGenerationOptions.cs` `ReportAutoGenerationService.cs` | `ReportService.Features.*` |
| `RiskManagementService` | `ObservedDrawdownRefreshOptions.cs` `ObservedDrawdownRefreshService.cs` `QuoteRefreshService.cs` `WithdrawalEvaluationOptions.cs` `WithdrawalEvaluationService.cs` | `RiskManagementService.Features.RiskManagement` |

すべて `BackgroundService` 派生 ＋ その `Options` 型で構成される。中身（例: `MonitorPollingService` /
`WithdrawalEvaluationService` / `ProtectiveStopGuardService` のコード内コメント）を読むと、
**各 `BackgroundService` は自サービスの単一集約の AppService を定時に呼び出すトリガー**であり、
1 巡回の中で複数の操作（発行イベント・判定・更新）にまたがる（例: `MonitorPollingService.RunOnceAsync` は
損切り検知と価格変動検知の両方を評価する）。

`microservices-platform` 側は `Hosted/` を 1 件も持たない（`find <MSP clone> -iname Hosted` 0 件・
2026-09-02 実測）。

## 検討した選択肢

### 論点 1: `CLAUDE.md` の 3 段化の但し書き

3 段化そのもの（`Features/<集約>/<操作>/` への実移送）は #613 本体の実装 PR のスコープであり、
本 IADR は**記述の訂正のみ**を決める。

1. **但し書きを削除し「3 段目のスライス分割は採る」と明記する（採用）** — platform `ADR-0065` 決定 2・
   フォローアップ 7 に忠実。移送が未完了である事実は「移行中」の既存の書きぶり（旧新混在の注記）で表現できる。
2. **但し書きをそのまま残す** — 却下。根拠（MSP 未実装）が実測で消えており、規約文書に誤った理由づけが残る。
3. **但し書きを削除するだけで代替の理由を書かない** — 却下。`ADR-0065` 決定 2 を根拠として明示しないと、
   次に読む人が「なぜ 3 段化するのか」を追えない。

### 論点 2: `Hosted/` の位置づけ

1. **(a) 現状維持 —— AST 固有の第 4 の頂点として `CLAUDE.md` に明記する（採用）**
2. **(b) `Infrastructure/Hosted/` へ寄せる** — 却下。全 `BackgroundService` が自サービスの `Features/<集約>/`
   を `using` している（実測表）。`Infrastructure/` へ移すと、platform `ADR-0065` 決定 7「`Infrastructure` は
   `Features` を参照しない」に**移した瞬間から違反する**。`DomainSourceDependencyTests`（[IADR-0256](IADR-0256_domain-dependency-inspection-by-source-scan.md)）
   の走査対象を素通りさせるための新たな許可リストが要り、依存規律を弱める側の変更になる。
3. **(c) `Features/<集約>/<操作>/` の一部として扱う** — 却下。実測のとおり、各 `BackgroundService` は
   1 巡回で**複数の操作にまたがる**（例: `MonitorPollingService` は損切り検知と価格変動検知の両方を評価）。
   platform `ADR-0068` 決定 2「そのファイルが 1 つの操作にしか使われないか」で判定すると、**2 つ以上の
   操作が使うものは 2 段目（集約直下）に残す**が原則であり、`BackgroundService` を特定の 1 操作フォルダへ
   下ろすことはこの判定基準そのものに反する。集約直下に置く案も検討したが、`BackgroundService` は
   `Program.cs` からの起動登録に紐づく**ホスト構成**であり、`Features/<集約>/` 直下（操作の合成点である
   登録表と同格）に置くより、ホスト構成として `Program.cs` の隣（ルート直下）に置くほうが実体に合う。

## 決定

**決定 1**: `CLAUDE.md` §技術スタック別ルール › C#/.NET のソリューション行から
「（3 段目のスライス分割は MSP も未実装のため採らない）」を削除し、
「（3 段目のスライス分割は platform ADR-0065 決定 2 に沿って採る。移送は #613 で実施）」に置き換える。
3 段化の実フォルダ移送は行わない（#613 本体の実装 PR のスコープ）。

**決定 2**: `Hosted/` は **AST 固有の第 4 の頂点として現状維持する**。platform `ADR-0065` の樹形
（`Features/Domain/Infrastructure/Common/Tests` の 5 要素）はそのまま踏襲しつつ、`CLAUDE.md` に
「`Hosted/`（`BackgroundService`。本リポ固有の追加要素）」と明記し、根拠（各サービスが同一ホストで
HTTP 面と定時巡回を併せ持つハイブリッド構成であり、platform `ADR-0065` 決定 6 の `Api`/`Worker` 排他が
想定する「実行入口の形の違い」に当たらないこと）を本 IADR に残す。ファイル移動は行わない。

[IADR-0259](IADR-0259_single-project-vsa-structure.md) 決定 5（`BackgroundService` はルート直下 `Hosted/`
に置く）を**追認し、platform `ADR-0065` 確定後の樹形に照らして根拠を補強するもの**であり、supersede しない。

## 理由

- **決定 1**: フォローアップ 7 が名指しで訂正を求めており、根拠（MSP 未実装）自体が実測で消えている。
  規約文書に誤った理由づけを残すこと自体が次の作業者を誤導する。
- **決定 2**: `Infrastructure/` への移動は参照方向の規律（決定 7）を移した瞬間に破る。`Features/<集約>/<操作>/`
  への収容は判定基準（`ADR-0068` 決定 2）そのものに反する（2 つ以上の操作が使うものは 2 段目に残る規則の
  対偶として、複数操作を横断する `BackgroundService` を 3 段目へは下ろせない）。**残る現状維持が、
  参照規律・3 段化の判定基準のいずれとも矛盾しない唯一の案である。**

## 結果

- **良い影響**: `CLAUDE.md` が platform `ADR-0065` 決定 2 と整合する。`Hosted/` の位置づけが明文化され、
  #613 本体の実装 PR が「`Hosted/` をどうするか」を再検討せずに 3 段化・鏡写しへ進める。
- **悪い影響 / トレードオフ**:
  - `Hosted/` は platform 側の樹形に無い要素として残り続ける（AST とMSP の樹形の完全一致は成立しない）。
    ただし MSP 自身が `Hosted/` に相当する構成を持たない（ハイブリッド構成が存在しない）ため、
    「基盤との逸脱」ではなく「基盤が扱っていない構成への拡張」である。
  - `BackgroundService` の内部が複数操作にまたがる実装のままであり続ける限り、3 段目への分割は将来も
    選べない。1 `BackgroundService` が 1 操作しか呼ばなくなった場合は、`ADR-0068` 決定 2 に従い
    再検討の余地がある（本 IADR はその将来の再検討を妨げない）。
- **フォローアップ**: なし（#613 本体の実装 PR は本 IADR の決定を前提に `Features/<集約>/<操作>/` の
  3 段化・`Tests/` の鏡写し・`Domain/` 欠け 3 件の是正のみを行う）。

## 関連

- Supersedes: なし
- Superseded by: なし
