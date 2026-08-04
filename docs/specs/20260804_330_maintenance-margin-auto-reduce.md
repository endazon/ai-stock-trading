---
title: 作業仕様書 — 維持率割れによる建玉の自動縮小（閾値+5pt 回復・必要証拠金降順・AI 非介在）
type: work
status: review
related_ids: [FR-10, FR-11, UC-06, ADR-0003, ADR-0009, ADR-0016, IADR-0130, IADR-0131, IADR-0132, IADR-0133]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
related_specs:
  - ../adr/IADR-0133_maintenance-margin-auto-reduce.md
  - ../adr/IADR-0131_short-selling-controls-fail-closed.md
  - ../adr/IADR-0132_product-type-tri-state-and-guard-scope.md
  - ../functional/FR-10_risk-controls.md
  - ../tests/FR-10_risk-controls-tests.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: 維持率割れによる建玉の自動縮小（#330）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（リスク統制。「維持率が (4) の閾値を割り込む前に、リスク管理サービスが自動で
  建玉を縮小する」）／ FR-11（監査ログ）
- ユースケース（UC）: **UC-06** 代替フロー「維持率割れによる建玉の自動縮小（システム自動。利用者の操作を待たない）」
- 関連 ADR: **ADR-0016 決定7**（維持率閾値＝40% と規制要求の厳しい方）・決定4（強制買戻し）／
  **ADR-0003**（AI 判断のガードレール＝縮小に AI を介在させない）／
  **ADR-0009**（3 統制はいずれも手仕舞いを止めない＝本統制はその手仕舞いの側である）
- 実装 ADR: [IADR-0133](../adr/IADR-0133_maintenance-margin-auto-reduce.md)（本作業）／
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（回復目標値の解決メソッドは #329 が確定済み）
- 起点 issue: [#330](https://github.com/endazon/ai-stock-trading/issues/330)（親: [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 計画書リンク: [05_trading-assumptions §5](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md)
  （「維持率割れによる自動縮小の回復目標」「同 対象選択」2 行・2026-08-02 追補）／
  [03_usecases UC-06](../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md) ／
  [04_report-templates](../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md)（日報 §4・月報 §6）

## 目的・背景

**マージンコールは口座を失う唯一の経路**であり、米国市場の開場は日本時間の深夜（22:30〜翌 5:00）である。
利用者の応答を待つ余地が無いため、計画は「維持率が閾値を**割り込む前に**、システムが自動で建玉を縮小する」
統制を置いた（UC-06・FR-10）。本統制は 3 統制（kill switch / 日次損失ロックアウト / 一時停止）と異なり
**「動かす」統制**である——システムが自ら決済注文を発注し、その誤作動は**不可逆**である（UC-06 の比較表）。

したがって規則は**完全に機械的**でなければならない。UC-06 は「入力（各建玉の必要証拠金と現在の維持率）が
同じであれば、出力（決済する建玉と数量）は常に同じになる。利用者の承認操作も LLM 呼び出しも介在しない」と
明記している。#329（[IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)）は**閾値と回復目標の
値**（`MaintenanceMarginThresholdFor` / `MaintenanceRecoveryTargetFor`）だけを確定し、縮小の実行は本 issue に
残した。本作業はその決定的コア（何をどれだけ決済するか）と、4 つの記録先への結線を実装する。

## 計画書との突合（原文で確認した確定値）

| 論点 | 計画原文（出典） | 実装 |
| --- | --- | --- |
| 閾値 | 「**40% と規制上の要求（FINRA Rule 4210(c)）のうち厳しい方**」（§5・ADR-0016 決定7） | `ShortSellingLimits.MaintenanceMarginThresholdFor(株価)`（#329 実装済み・**再実装しない**） |
| 回復目標 | 「**上行の閾値 + 5 ポイント**（閾値 40% なら **45%**、規制側が効いて閾値 45% なら **50%**）」「回復目標は上行の閾値に**連動する**」（§5） | `MaintenanceRecoveryTargetFor(株価)`（#329 実装済み・オフセット 0.05 は `TradingDefaults`） |
| 縮小量 | 「**回復目標に達するまでに必要な最小限**の建玉を決済する」「目標を満たした時点で決済を止め、それ以上は決済しない」（§5・UC-06） | 回復目標に到達する**最小の株数**まで（端株が無いため株数単位で切り上げ）。一律 50% 減・建玉 1 件丸ごとは**採らない**（計画が明示的に棄却） |
| 対象選択 | 「**必要証拠金の大きい建玉から順に決済する**」「含み損の降順・建玉時刻の昇順は採らない」（§5・UC-06） | `RequiredMarginUsd` の**降順**。含み損・建玉時刻は入力にすら持たない |
| 発動条件 | 「維持率が閾値を**割り込む前に**発動」（§5）／「閾値に**接近**した場合、割り込む前に」（UC-06）／「維持率が (4) の閾値を割り込む前に」（FR-10） | **維持率 ≦ 閾値**で発動（閾値ちょうどで発動＝まだ割り込んでいない時点で動く）。「閾値 + α」の α は計画に無いため**発明しない**（[IADR-0133](../adr/IADR-0133_maintenance-margin-auto-reduce.md) 決定3） |
| AI・承認 | 「**利用者の承認を待たない**」「**縮小の判断に AI を介在させない**」（UC-06） | 判定は純関数（`MaintenanceMarginReducer`）。サービスは LLM・承認・スクリーニングのいずれにも依存しない |
| 3 統制との関係 | 「**3 統制のいずれかが成立していても自動縮小は動く**」（UC-06） | 統制ストア（kill/lockout/pause）を依存に持たない＝構造的に止められない（`PositionCloseService` と同じ規律） |
| 記録先 | 監査ログ・Discord 通知（UC-06）／日報に**発動の有無・決済した建玉・決済前後の維持率**／月報に**当月の発動回数**（04_report-templates 日報 §4・月報 §6） | 4 記録先すべてへ結線（後述） |
| 日報の記載項目 | 「# / 時刻 / 決済前の維持率 / 閾値 / 回復目標（閾値+5pt） / 決済した建玉（銘柄・方向・数量・必要証拠金） / 決済後の維持率」（04_report-templates 日報 §4 の表） | イベント `MaintenanceMarginReductionExecuted` が**この 7 列すべて**を持つ |
| 発動が無い日／月 | 「発動が無い日も「なし」と明記する（**空欄と「なし」を区別する**）」「発動が無い月も「0 件」と明記する」 | 描画側で「なし」「0 件」を明示。**照会できなかった場合は「なし」と書かない**（区別する） |

**計画に無く実装が決めた事項**（すべて [IADR-0133](../adr/IADR-0133_maintenance-margin-auto-reduce.md) に記録）:
維持率の算式（純資産 ÷ 建玉評価額合計）・複数建玉があるときの適用閾値（**最も厳しい建玉のもの**）・
発動条件の等号の扱い・目標に到達できない場合の振る舞い。

## 対象範囲

### 対象

1. **縮小計画の決定的コア**（`MaintenanceMarginReducer`。純関数・`RiskManagementService.Domain`）
   — 発動判定・回復目標・必要証拠金降順の選択・最小限の株数・決済後維持率の算出
2. **縮小の実行組み立て**（`MaintenanceMarginReductionService`。`Application`）
   — 建玉方向の反対売買（`PositionEffect.Close`）の `OrderApproved` 列と、記録用イベントの生成
3. **記録先 4 つへの結線**
   - 監査ログ: 新イベント `MaintenanceMarginReductionExecuted` ＋ 監査ハンドラ＋ `AuditEntryFactory` 写像
   - Discord 通知: 通知ハンドラ＋ `NotificationFormatter`
   - 日報: `ReportRenderer` の「維持率割れによる自動縮小の記録（当日）」節（表 7 列）
   - 月報: `ReportRenderer` の「維持率割れによる自動縮小の発動回数」行
4. 上記の 3 点セット（境界値・プロパティ・否定形）と、機能仕様書・テスト仕様書への追記

### 対象外（担当を明記）

| 事項 | 理由・担当 |
| --- | --- |
| **維持率・純資産・必要証拠金の供給元** | ブローカー（moomoo）からの取得経路が存在しない。ポート `IMaintenanceMarginSnapshotSource` を定義し、既定は「供給なし（`null`）＝発動しない」。実装は **#342**（moomoo PoC）・**#331**（発注執行） |
| **定期評価のドライバ（常駐）** | 上記の供給元が無い間は常に何もしないため置かない。維持率の観測経路と同時に置くのが正しい（**#331 / #342**） |
| **決済注文の実発注** | `OrderApproved` を組み立てるところまで。発注執行の経路は **#331** |
| **日報・月報のデータ供給** | ポート `IMarginReductionRecordSource`（既定 no-op＝空列＝「なし」）まで。権威源からの照会（監査台帳 or リスク管理の照会 API）は供給元と同時に結線する（**#331**） |
| **建玉一覧画面の維持率表示** | SC-03（**#340**） |
| 強制買戻し・借株照会 | ADR-0016 決定3/4。**#342** |

## 設計

### 1. 入力（供給元は後続 issue）

```csharp
MaintenanceMarginSnapshot { decimal NetEquityUsd; IReadOnlyList<MarginPosition> Positions; }
MarginPosition { Symbol; Market; Side; ProductType; Quantity; PriceUsd; RequiredMarginUsd; }
```

- **維持率 ＝ 純資産（`NetEquityUsd`）÷ 建玉評価額の合計**（IADR-0133 決定1）。
  規制側の実効維持率 `max($5.00 ÷ 株価, 30%)` が「維持証拠金 ÷ 株価」であることと分母が揃う。
- `NetEquityUsd` は**日中の実測値**であり、統制上限の基準 equity（`PortfolioSnapshot.Capital`＝
  前営業日終値時点・日中不変。IADR-0130 決定2）とは**別物**である。混同すると、日中に維持率が落ちても
  判定が動かない（IADR-0133 決定1 の注記）。
- `RequiredMarginUsd` は**ブローカー由来**（規制式の適用結果）。実装が推定しない。
- 供給が無い（`null`）ときは**発動しない**。「データが無いのに決済する」ことは許されない。
  一方で**維持率が確認できない状態での積み増しは #329 が既に塞いでいる**（`MaintenanceMarginBreach`・
  T-10-169）。両者の組で「報告しなければ回避できる」経路が閉じる。

### 2. 判定と縮小量（`MaintenanceMarginReducer.Plan`）

```
適用閾値 T0 = max(建玉 i について MaintenanceMarginThresholdFor(株価_i))     … 最も厳しいものが効く（FR-10）
回復目標 T  = T0 + 5pt（= MaintenanceRecoveryTargetFor の最大値）
維持率  R  = NetEquity / Σ(数量_i × 株価_i)
発動条件    R ≦ T0                                                          … 割り込む前に動く
必要な評価額の削減 X = V − NetEquity / T                                      （R ≧ T なら X ≦ 0 ＝ 発動しない）
```

必要証拠金の**降順**（同値は評価額降順 → 銘柄コード → 市場で決定的に整列）に建玉をたどり、
残りの削減量 `X` を満たすまで決済する。最後の 1 件は**部分決済**で、必要株数 `⌈残り ÷ 株価⌉` のみを決済する
（建玉 1 件を丸ごと閉じる規則を計画が棄却しているため。株数は端株が無いので切り上げが最小限）。
目標に到達した時点で打ち切る。

### 2-2. 壊れた入力の扱い（IADR-0133 決定8・#330 レビュー指摘の是正）

株価・数量が **0 以下**、必要証拠金が**負**の建玉は市場・口座の実態としてあり得ず、**フィードが壊れて
いることの証拠**である。1 件でも混じれば**スナップショット全体を信頼せず決済しない**。

- **該当建玉だけを除外しない**——除くと分母（建玉評価額の合計）が縮んで**維持率が実際より良く見え**、
  過少縮小へ倒れる（統制として危険な向き）。
- **例外で中断させない**——`ShortSellingLimits.MaintenanceMarginThresholdFor` は非正の株価に対して
  `ArgumentOutOfRangeException` を投げる。入口で検査しないと `Plan` 全体が例外で落ち、定期評価ドライバ
  （#331）の評価ループごと死ぬ。
- **黙って何もしない状態にしない**——「健全ゆえの無動作」と区別できる状態（`SnapshotUntrusted`）を返し、
  警告ログを残す。記録先は増やさない（新しいイベント種別は計画に無いため作らない）。

### 3. 実行（`MaintenanceMarginReductionService`）

- 計画の各明細から、建玉方向の**反対売買**（`PositionEffect.Close`）の `OrderApproved` を組み立てる
  （`PositionCloseService` / `StopLossExecutionService` と同じ層・同じ出力）。
- **発注前スクリーニング（`RiskEvaluator`）を通さない**。本統制自体が手仕舞いであり、
  統制で止めることは ADR-0009 違反になる。統制ストアを依存に持たないことが構造的な保証である。
- 同時に `MaintenanceMarginReductionExecuted` を生成する（記録先 4 つの単一の情報源）。
- `DecisionId` は決定的に採らない（各縮小は独立した決済であり、重複排除は発注執行側の
  `DecisionId` 予約が担う。`PositionCloseService` と同じ）。

### 4. 記録先への結線

| 記録先 | 実装 | 発動が無いとき |
| --- | --- | --- |
| 監査ログ | `MaintenanceMarginReductionExecutedAuditHandler` ＋ `AuditEntryFactory.From`（相関は `"margin-reduction"` の決定的 GUID＝発動どうしを 1 本で辿れる。`BrokerPositionsObserved` と同じ作法） | 記録なし（イベントが無い） |
| Discord | `MaintenanceMarginReductionExecutedNotificationHandler` ＋ `NotificationFormatter.From`（**決済前後の維持率と閾値・回復目標を本文に出す**） | 通知なし |
| 日報 | `ReportRenderer`「## 4. リスク統制の記録 → ### 維持率割れによる自動縮小の記録（当日）」（7 列の表） | **「なし」と明記**。照会できなかった場合は「取得できず（要確認）」と区別する |
| 月報 | `ReportRenderer`「## 4. リスク統制の記録 → 維持率割れによる自動縮小の発動回数」 | **「0 件」と明記**（同上） |

### 変更するファイル

| ファイル | 変更 |
| --- | --- |
| `Shared.Contracts/Events/MaintenanceMarginReductionExecuted.cs` | 新規（イベント＋明細 `MaintenanceMarginReductionLeg`） |
| `RiskManagementService.Domain/MarginPosition.cs` ほか | 新規（入力・計画・純関数コア） |
| `RiskManagementService.Application/Ports/IMaintenanceMarginSnapshotSource.cs` | 新規（供給元のポート） |
| `RiskManagementService.Application/Adapters/UnavailableMaintenanceMarginSnapshotSource.cs` | 新規（既定＝供給なし） |
| `RiskManagementService.Application/Services/MaintenanceMarginReductionService.cs` | 新規 |
| `RiskManagementService.Api/Program.cs` | 上記 2 つの DI 登録（供給元が入れば動く形にしておく） |
| `AuditService`（`AuditEntryFactory` / `AuditEventHandlers`） | 追記 |
| `NotificationService`（`NotificationFormatter` / `NotificationHandlers`） | 追記 |
| `ReportService`（`ReportView` / `ReportRenderer` / `IMarginReductionRecordSource` / no-op / `ReportDraftService` / `ReportAutoGenerator`） | 追記 |
| テスト（Domain / Application / Audit / Notification / Report） | 3 点セット |

## 受け入れ基準

1. 維持率が閾値**ちょうど**で発動し、閾値**超**では発動しない（割り込む前に動く）
2. 縮小量が回復目標（**閾値 + 5pt**）に達する**最小限**である——1 株少ないと目標に届かず、
   実装は目標を超えて決済しない（**過剰・過少の両方向**を境界で固定）
3. 閾値 40% のとき目標 45%、規制側が効いて閾値が上がるとき目標も**同じだけ**上がる（連動）
4. 対象は**必要証拠金の降順**であり、含み損順・建玉時刻順に退行しない
5. 縮小の判定に AI・利用者の承認・発注前スクリーニングが**介在しない**（構造で担保）
6. 同じ入力からは常に同じ出力が出る（機械的規則）
7. 縮小直後に小幅な価格変動（不利方向 2%）が起きても再発動しない（**+5pt の余裕が効いている**）
8. 1 回の発動が**監査ログ・Discord・日報・月報**の 4 記録先すべてに残る（日報は 7 列・月報は回数）
9. 発動が無い日は日報が「なし」、無い月は月報が「0 件」と**明記**する（空欄と区別する）
10. 維持率の供給が無いときは縮小しない。かつ、その状態で**空売りを積み増せない**（#329 と組で塞ぐ）
11. `dotnet build` 0 warning / 0 error、`dotnet test --filter "Category!=Integration"` 全 green

## テスト方針

3 点セット（境界値・プロパティ・否定形）を FR-10 のテスト仕様書へ追記する（T-10-181〜）。
主なクラス: `MaintenanceMarginAutoReduceTests`（Domain）・`MaintenanceMarginReductionServiceTests`
（Application）・`AuditEntryFactoryTests` / `NotificationFormatterTests` / `ReportRendererTests`（記録先）。

## 計画書との差異

**無し**（値・規則はすべて計画原文から採った）。計画が定めていない事項は発明せず、
実装判断として [IADR-0133](../adr/IADR-0133_maintenance-margin-auto-reduce.md) に明記した（維持率の算式・
複数建玉の適用閾値・発動条件の等号・到達不能時の振る舞い）。うち**維持率の算式と適用閾値**は
計画側の追認が望ましいため環流する（[feedback](../../feedback/20260804_uc06-maintenance-ratio-formula.md)）。

## 未決事項

1. **維持率・純資産・必要証拠金の供給元**（#342 の PoC 結果に依存）。moomoo が返す維持率の定義が
   本実装の算式と異なる場合、算式を供給値へ寄せる（IADR-0133 決定1 を新 IADR で改める）
2. **信用買い（`MarginLong`）の規制維持率**を計画が定めていない。空売り側の式（`max($5.00÷株価, 30%)`）と
   自前 40% の厳しい方をそのまま適用している（安全側だが過大な可能性）。環流済み
3. **定期評価の周期**（何秒ごとに維持率を見るか）は計画に無い。供給元と同時に決める（#331）
4. 日報・月報への**データ供給経路**（監査台帳を読むか、リスク管理に照会 API を置くか）は #331 で決める。
   本 issue は描画とポートまで

## 検証結果（2026-08-04）

| 検査 | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx` | **0 warning / 0 error** |
| `dotnet test --filter "Category!=Integration"` | **2,522 passed / 0 failed**（着手前 2,477 ＋ 本 issue 45 件） |
| `scripts/check-coverage.js` | 行カバレッジ **66.31%**（13,324/20,094）／ floor 62% |
| `scripts/check-test-traceability.js` | OK（テスト 325 ファイル・起点 ID 25 種） |
| `scripts/check-banned-libraries.js` | OK（不採用ライブラリの混入なし） |
| `scripts/check-consumer-endpoint-names.js` | OK（11 サービス・キュー名の一意性） |
| `node --test scripts/scripts.test.js` | OK |
| `dotnet format --verify-no-changes` | 差分なし |
| `scripts/check-doc-links.js` | 本 issue で追加した文書の破損リンク **0 件**（既存 20 件は他文書の先行課題） |
| `PlanConformance.Tests` | green。**既知逸脱レジストリは 6 行のまま**（#333 / #334 / #358 担当。#330 担当の行は無い） |
| `scripts/check-commit-messages.js` | OK（5 件） |

追加したテスト 45 件の内訳: Domain 27（`MaintenanceMarginAutoReduceTests`）／ Application 9
（`MaintenanceMarginReductionServiceTests`）／ 監査 2 ／ 通知 1 ／ 報告書 6。
うち 9 件は**壊れた入力の扱い**（IADR-0133 決定8・T-10-206〜209）の追加分である
（AI レビュー 🟡 の是正。初版のコード注釈が「弾く」と「例外で中断する」を取り違えていた）。

## レビュー指摘の是正（2026-08-04・AI レビュー 🟡）

`MaintenanceMarginReducer` の注釈が「株価 0 以下は閾値算出が先に**弾く**」と書いていたが、実際は
`MaintenanceMarginThresholdFor` が**例外を投げる**（＝`Plan` 全体が中断する）。意味が違い、次の実装者が
「処理済み」と誤読する。供給元が実ブローカー照会へ繋がる（#331 / #342）と、低ティアのデータソースでは
0 や欠損値が現実に混ざるため、この経路は生きる。

是正: 入口で検査して**スナップショット全体を信頼しない**（IADR-0133 決定8）／注釈を実挙動に合わせた／
境界テスト（T-10-206〜209）で (a) 決済が起きないこと (b) 拒否が観測可能であること
(c) 健全な建玉が同居しても部分処理して分母を歪めないこと、を固定した。
