---
title: 作業仕様書 — 強制買戻しを建玉消失と約定履歴の突合で事後推定し、30 日の空売り禁止を供給する（ADR-0016 決定4 の 2026-08-06 改訂）
type: work
status: review
related_ids: [FR-10, FR-11, FR-06, UC-06, ADR-0016, ADR-0019, IADR-0131, IADR-0134, IADR-0158, IADR-0159]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
related_specs:
  - ../adr/IADR-0159_buy-in-post-hoc-inference.md
  - ../adr/IADR-0158_short-sell-borrow-permit-primary-gate.md
  - ../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md
  - ../adr/IADR-0131_short-selling-controls-fail-closed.md
  - ../adr/IADR-0118_broker-position-reconciliation.md
  - ../../docs/functional/FR-10_risk-controls.md
  - ../../docs/tests/FR-10_risk-controls-tests.md
  - ../../docs/blocked-tasks.md
  - ../../docs/DEFINITION_OF_DONE.md
---

# 作業仕様書: 強制買戻しの事後推定と 30 日禁止の供給（#419）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（空売り専用統制・強制買戻し後の 30 日禁止）／ **FR-11**（監査ログ）／ FR-06（日報・月報）
- ユースケース（UC）: UC-06
- 関連 ADR: **ADR-0016 決定4（2026-08-06 改訂＝事後推定）**／ **ADR-0016 決定15（2026-08-06 追記＝発生有無・発生回数の集計元）**／
  ADR-0016 決定10（拒否理由 9 種・`BuyInBanned` はクラス A・`BorrowUnavailable` へ写像しない）／
  ADR-0016 決定14（Stage 1 で検証できない統制の表）／ ADR-0019 決定1 項目5（PoC・未確認）
- 実装 ADR: **[IADR-0159](../adr/IADR-0159_buy-in-post-hoc-inference.md)（本作業）**／
  [IADR-0158](../adr/IADR-0158_short-sell-borrow-permit-primary-gate.md)（一次ゲート）／
  [IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md)（`BuyInBanned` の新設）／
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（空売り統制のフェイルクローズ）／
  [IADR-0118](../adr/IADR-0118_broker-position-reconciliation.md)（建玉突合）
- 起点 issue: [#419](https://github.com/endazon/ai-stock-trading/issues/419)
- 計画 submodule: `e36b592` → **`06fa163`**（本 PR で更新。取り込むのは **ADR-0016 決定15 の追記**であり、
  同時に更新された他 ADR への追随は本 issue の範囲外）

## 目的・背景

`BuyInBanned`（拒否理由・クラス A）と 30 日の禁止期間判定は #374 で実装済みだが、
**`BuyInBanUntil` を立てる供給経路が存在しない**（`docs/blocked-tasks.md`「実装済みだが発動しない機能」）。

計画側は 2026-08-06 に決定4 を改訂し、**イベント検知の供給元が無い**（SIMULATE では原理的に発生せず、
専用の通知 API も見当たらない）ことを受けて次の代替を定めた。

> - **建玉の消失を約定履歴と突合し、強制買戻しを事後に推定する。** 自らの決済指示に対応しない建玉の消失を
>   強制買戻しとみなし、禁止銘柄リストへ追加する。
> - **これは検知ではなく推定である。** イベント検知より遅く、取り違えもあり得る。ただし本決定の目的
>   （**同じ銘柄で繰り返さない**）は果たせる。
> - 30 日間の禁止期間・`BuyInBanned`（クラス A）での記録は変更しない。
> - **推定であることを運用者へ示す**（日報・通知の文言で「強制買戻しと推定」と明示し、確定事実として扱わない）。

さらに 2026-08-07 に同期した計画（`06fa163`）で **決定15 に「発生有無・発生回数の集計元」の追記**が入った。

> - 集計元は、**決定4 の事後突合が推定した強制買戻しの件数**である。**`BuyInBanned`（拒否理由）の件数ではない。**
> - `BuyInBanned` は禁止期間中の発注拒否であり、**1 回の強制買戻しに対して 30 日のあいだ何度でも発生し得る**。
> - **月報でも「強制買戻し（推定）N 件」と明示する。** 日報の「発生有無」も同じ集計元。
> - **推定経路が入るまで発生回数は供給されない。供給が無い間は 0 件と表示してはならない。**

本作業はこの供給経路を作る。

## 対象範囲

| 含む | 含まない |
| --- | --- |
| 建玉消失の突合による**推定**（純関数） | 30 日という期間・`BuyInBanned` のクラス分類の変更（決定4 が「変更しない」と明示） |
| 推定した銘柄の **30 日空売り禁止**の登録・永続化・失効 | `BuyInBanned` の `BorrowUnavailable` / `BannedSymbol` への写像（決定10 が明示的に禁止） |
| 推定の**根拠**の監査ログ（FR-11）記録 | **実弾口座側の強制買戻し通知・履歴 API の調査**（未調査・見つかれば本代替を置き換える） |
| 通知・日報・月報の文言（**「推定」と明示**） | 借株照会（`TrdGetMarginRatio`）の供給元実装（#331 / #342 系） |
| 発生回数の**集計元**を推定件数に固定（決定15） | 報告書サービスから権威源（リスク管理）への結線（未供給のまま＝`NotSupplied`） |

## 設計

### 1. 何を「消失」とみなすか — 突合の相手は取引台帳（＝自らの決済指示）

`PositionDriftDetector`（#292 / IADR-0118）と同じ入力を使う。

- **台帳側**: `PortfolioProjection.ProjectOpenPositions(ledger.GetFills())` ＝ **自らの約定履歴の射影**。
  自分で手仕舞い・損切りをすれば、その約定は台帳に入り、台帳の空売り建玉が減る。
- **ブローカ側**: `BrokerPositionsObserved`（発注執行サービスが定期照会して発行する観測）。
- **処理中の決済**: `ledger.GetInFlightCloseQuantity(symbol, market, now - 30 分)` ＝
  **承認済みだがまだ約定が台帳へ届いていない決済指示**。約定履歴より前に立つ「自らの決済指示」そのものである。

```
未説明の消失 = max(0, 台帳の空売り数量 − ブローカの空売り数量 − 処理中の決済数量)
```

**この式が、正常な手仕舞い・損切りと強制買戻しを区別する唯一の手段である。** 自分で決済したなら、
その数量は約定（台帳）か承認（処理中）のどちらかに必ず現れ、右辺で相殺されて 0 になる。

### 2. どちらへ倒すか — **過剰推定（fail-closed）**

決定4 の目的は「**同じ銘柄で繰り返さない**」である。したがって非対称に倒す（[IADR-0159](../adr/IADR-0159_buy-in-post-hoc-inference.md) 決定1）。

| 誤りの向き | 結果 | 採否 |
| --- | --- | --- |
| 過剰推定 | 不要な 30 日禁止（**機会損失**）。空売り 1 銘柄が 30 日建てられないだけで、資金は失わない | **こちらへ倒す** |
| 過少推定 | 同じ銘柄で強制買戻しを繰り返す（**決定4 の目的が果たせない**）。踏み上げ中の強制決済が反復する | 採らない |

具体的な帰結:
- ブローカの応答に**銘柄が現れない**なら数量 0 として扱う（＝全量消失＝推定する）。
- **消失＝ゼロになった時点ではなく、数量差で突合する**（下記 3）。
- 手動売買・外部要因による消失も強制買戻しとして推定され得る（**既知の取り違え**。監査ログに根拠を残し、
  人が事後に検証できるようにする）。

### 3. 部分約定・分割決済の扱い — **数量差で突合し、増分だけを新たな推定とする**

「建玉が 0 になった時点」を待たない。待てば、ブローカが一部だけ買い戻した場合に推定が起きず（過少推定）、
決定4 の目的が果たせない。

- 未説明の消失が **前回までに推定済みの数量（帰属数量）を超えたときだけ**、超過分を新たな推定とする。
  同じ乖離を毎巡回で観測しても**推定は 1 回だけ**である（禁止期間が毎巡回で 30 日ずつ延びるのを防ぐ）。
- 段階的に消失した場合（40 株 → さらに 60 株）は、**増分ごとに推定**し、そのつど禁止期限を更新する。
- 自らの決済が**部分約定**した場合（100 株の決済指示のうち 60 株が約定）は、台帳の空売り建玉も 60 株減るため
  未説明の消失は 0 であり、**推定しない**（最重要の否定形）。
- 乖離が解消した（台帳が是正された・建玉が建て直された）ときは帰属数量を 0 へ戻す**リセット記録**を残す。
  **禁止期限は戻さない**（30 日は経過でしか解けない）。

### 4. 照会自体が失敗した場合の振る舞い

**推定しない。ただし「起きていない」とも言わない。**

| 事象 | 振る舞い | 理由 |
| --- | --- | --- |
| ブローカ建玉の照会が失敗（`null`） | `BrokerPositionSnapshotService` が**何も発行しない**（既存の fail-safe）。推定は動かない | 照会不能を「建玉ゼロ」と読むと**全建玉が消失**扱いになり、全銘柄が 30 日禁止になる。これは過剰推定の許容範囲を超える（統制ではなく事故） |
| 観測が届かない間 | **既存の禁止は有効なまま**（期限は日付でのみ失効する） | 観測の不在で禁止が解けると、fail-open になる |
| 日報・月報の発生有無／発生回数 | **`NotSupplied`（未供給）** と明示。**0 件と書かない** | 決定15「供給が無い間は 0 件と表示してはならない」 |

「突合データが取れなかったから何もしない」は**推定の側では fail-open** である。これを塞ぐのは
「照会不能でも空売りは通らない」という**別の統制**である——文脈（`ShortSellOrderContext`）が `null` である限り
すべての新規売り建ては `BorrowUnavailable` で拒否される（IADR-0131 決定2 / IADR-0158）。
すなわち**推定が動かない間、同じ銘柄で空売りを繰り返す経路自体が存在しない**。この二重性を IADR に記録する。

### 5. 30 日禁止の登録と消費

- 記録先は**追記専用の推定台帳**（`buy_in_inferences` テーブル・リスク管理サービス専有 DB）。
  1 行 = 1 回の推定（またはリセット）。**発生回数の集計元はこの行数**である（決定15）。
- 禁止期限 = 当該銘柄の行の `BanUntil` の**最大値**。期間は `ShortSellingLimits.BuyInBanDurationDays`（30 日・既存）。
- 判定は既存の `ShortSellEvaluator`（`context.BuyInBanUntil`）と**同じ純関数** `BuyInBanPolicy.IsBanned` を単一情報源とする。
- **文脈（`ShortSellOrderContext`）は借株照会が無いため今も組めない。** そこで禁止期限だけを
  `RiskEvaluator` へ**単独で供給**する（`BuyInBanSupply`）。文脈が組める日が来ても**二重計上しない**
  （既に `BuyInBanned` が列挙されていれば追加しない）。**値を発明しない**——供給できるのは禁止期限だけであり、
  維持率・エクスポージャを 0 で埋めた偽の文脈は作らない。

### 6. 記録先（推定であることを運用者へ示す）

| 記録先 | 内容 | 「推定」の明示 |
| --- | --- | --- |
| 監査ログ（FR-11） | `BuyInInferred` イベント（消失した建玉・突合した約定履歴・推定日時・禁止期限） | イベント名・内容が「推定」であることを表す |
| Discord 通知（FR-09） | 銘柄・消失数量・根拠・禁止解除日 | 本文に「**強制買戻しと推定**」「確定した事実ではありません」 |
| 日報（FR-06・決定15） | 当日の**発生有無**（推定） | 「**強制買戻し（推定）**」の見出しと本文 |
| 月報（FR-06・決定15） | 当月の**発生回数**（推定） | 「**強制買戻し（推定）N 件**」 |

未供給（推定経路の記録を照会できない）ときは **「照会できませんでした（供給元がありません）」**と書き、
**0 件・なしと書かない**（決定15）。

## 実装対象

| 層 | 追加・変更 |
| --- | --- |
| Shared.Contracts | `Events/BuyInInferred.cs`（新規イベント）・`Trading/BuyInCoveringFill.cs`（突合した自らの決済約定） |
| RiskManagement.Domain | `BuyInBanPolicy`（禁止期間判定の単一情報源）・`BuyInBanSupply`・`ShortSellEvaluator`/`RiskEvaluator` から利用 |
| RiskManagement.Application | `Services/BuyInInference`（純関数）・`Services/BuyInInferenceService`（束ね）・`Services/BuyInOccurrenceAggregation`（決定15 の集計）・`Ports/IBuyInInferenceStore`・`Adapters/InMemoryBuyInInferenceStore`・`State/BuyInInferenceRecord` |
| RiskManagement.Infrastructure | `EfBuyInInferenceStore`＋行＋`DbContext`＋マイグレーション `AddBuyInInferences`・`BrokerPositionsObservedHandler` で推定を実行し発行 |
| Notification | `NotificationFormatter.From(BuyInInferred)`・`BuyInInferredNotificationHandler` |
| Audit | `AuditEntryFactory.From(BuyInInferred)`・`BuyInInferredAuditHandler` |
| Report | `ReportView.BuyInInferences`・`ReportRenderer` の §4「強制買戻し（推定）」・`IBuyInInferenceRecordSource`＋既定 `UnsuppliedBuyInInferenceRecordSource`（**null＝未供給**） |

## 受け入れ基準

- [ ] 自らの決済指示（約定・処理中の承認）に**対応する**建玉消失を強制買戻しと推定しない（**最重要の否定形**）
- [ ] 対応する決済指示が**無い**消失を推定し、30 日の空売り禁止を登録する
- [ ] 同じ乖離を繰り返し観測しても推定は 1 回（禁止期間が延び続けない）／段階的な追加消失は増分だけ推定する
- [ ] 部分約定・分割決済の境界（自分の部分約定は推定しない／ブローカの部分買戻しは推定する）
- [ ] `BuyInBanned` は**クラス A** のままであり、クラス C（統制違反の計上対象）に混ざらない
- [ ] `BuyInBanned` は `BorrowUnavailable` へ写像されない（両向き）
- [ ] 推定の根拠（消失した建玉・突合した約定履歴・推定日時）が監査ログに残る
- [ ] 通知・日報・月報の文言に「**推定**」が含まれる（確定事実として出さない）
- [ ] 発生回数の集計元は**推定件数**であり、`BuyInBanned` の拒否件数ではない（決定15）
- [ ] 供給が無い間は **0 件と表示しない**（`NotSupplied` と 0 件を区別する）
- [ ] ブローカ照会が失敗しても既存の禁止は有効なまま（観測の不在で禁止が解けない）
- [ ] `dotnet build` 警告ゼロ・`dotnet test` 全緑・`dotnet format` 済み

## テスト

テスト仕様書 [FR-10](../../docs/tests/FR-10_risk-controls-tests.md) に **T-10-215 〜 T-10-231** として追記する。

## 未決事項・残件

- **実弾口座側の強制買戻しの通知・履歴 API は未調査**（ADR-0019 PoC 項目5・本 issue の範囲外）。
  見つかれば本代替（推定）を置き換える。`docs/blocked-tasks.md` に残件として登録する。
- **報告書サービスから推定台帳への結線が無い**（権威源への照会 API が無い）。既定は **`NotSupplied`** であり、
  日報・月報は「照会できませんでした（供給元がありません）」と表示する。0 件とは書かない。
- 手動売買・外部要因による建玉消失を強制買戻しと取り違える可能性は**残る**（過剰推定側の既知のトレードオフ）。
