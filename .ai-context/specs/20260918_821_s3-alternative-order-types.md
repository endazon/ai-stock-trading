---
title: S3 他のブローカー側注文種別（StopLimit / TrailingStop）を SIMULATE で試し、注文種別と拒否理由（retType / retMsg）を監査へ残す
type: spec
status: accepted
related_ids: [FR-10, FR-12, FR-11, UC-02, ADR-0040, ADR-0016, ADR-0003, IADR-0016, IADR-0060, IADR-0111, IADR-0210, IADR-0211, IADR-0342, IADR-0347]
author: endazon (with Claude Code)
created: 2026-09-18
updated: 2026-09-18
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (決定1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 の 3 文〔口座種別の軸〕)
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md (§逆指値が成立しない場合の扱い)
---

# 仕様書: S3 他のブローカー側注文種別を SIMULATE で試し、拒否理由を監査へ残す（#821）

## 起点

- #821（enhancement）。計画 ADR-0040（planning#638 の裁定・2026-09-17）決定 1 の **S3**。
- 前提（先行）: #819 / [IADR-0342](../adr/IADR-0342_simulate-stop-loss-method-selection.md)（選択機構・実弾での拒否・S2）。
  S3 は同 IADR 決定 4-5 の「未実装のため S0 と同じ扱い＋警告ログ」に置かれており、本作業がその置き場を実装で置き換える。
- 症状の一次情報: #809（moomoo のペーパー口座は `OrderType_Stop` を受け付けない＝ `Paper trading does not support Stop order`）。
- 新規 IADR: **IADR-0347**（本作業の設計）。

## 射程

| 含む | 含まない（別 issue / 別作業） |
| --- | --- |
| S3 の解決結果を専用の扱いにする（`StopLossMethodPolicy`） | S1 ソフトウェア逆指値（#820） |
| moomoo アダプタの `OrderType_StopLimit` / `OrderType_TrailingStop` 発注 | SC-02 の入力・SC-03 の表示・日報（#823） |
| 注文種別の選択（`Broker:Moomoo:AlternativeStopOrderType`）と StopLimit の指値幅 | 実弾（`TrdEnv=real`）での S3 —— **選べない**（IADR-0342 決定 2・決定 4 のまま） |
| 拒否理由（retType / retMsg）の捕捉と監査への記録（新イベント） | 空売りの扱いの変更 —— **常に S0**（ADR-0016 決定 2(b)。変えない） |
| 拒否時の扱いは S0 と同じ（建玉を持たない） | 既定の手法の変更 —— **変えない**（コードの既定は S0） |
| 受理された場合は S0 と同じ保護レグ（記録・ガードの巡回対象） | Discord 通知の追加（拒否時は既存の `ProtectiveStopCoverageLost` が Critical で鳴る） |

## 設計（詳細は [IADR-0347](../adr/IADR-0347_alternative-broker-order-types-for-simulate.md)）

1. **扱いの追加**: `StopLossMethodDisposition.AlternativeBrokerOrderType` を足し、`StopLossMethodPolicy.Resolve` の
   最後の分岐を「S2 → 免除／**S3 → 代替注文種別**／S1・未知 → S0（未実装）」に分ける。**判定の順序（S0 → 発注先 →
   空売り → 手法）は変えない** —— S3 も moomoo SIMULATE の買い（現物・信用買い）にしか届かない。
2. **能力ポート**: `IAlternativeProtectiveOrderBroker`（`AlternativeProtectiveOrderType` と
   `PlaceAlternativeStopOrderAsync`）。moomoo アダプタだけが実装する。paper は実装しない（S3 が届かないため）。
   戻り値 `AlternativeProtectiveOrderPlacement` が **注文種別・ブローカー注文・拒否理由（retType / retMsg）**を運ぶ。
3. **注文種別の写像**: `MoomooOrderKind` に `StopLimit` / `TrailingStop` を足す。
   - StopLimit: `AuxPrice` = 発火価格、`Price` = **指値**（発火価格から不利側へ `StopLimitOffsetRatio`〔既定 1%〕）。
   - TrailingStop: `TrailType_Amount` ＋ `TrailValue` = |エントリーの判断価格 − 発火価格|、`TrailSpread` = 0。
   選択は `Broker:Moomoo:AlternativeStopOrderType`（`stoplimit` 既定 / `trailingstop`）。未知の値は**起動時に停止**する
   （`MoomooBrokerOptions` の既存の作法）。
4. **拒否理由の捕捉**: `MMApiMoomooTradeClient.EnsureSucceeded` が投げる例外を
   `MoomooTradeRequestException`（`InvalidOperationException` 派生・`RetType` / `RetMsg` / `Operation` を保持）に変える。
   アダプタは従来どおり終端 `Rejected` へ倒しつつ、**理由を戻り値へ載せる**（ログだけに残さない）。
5. **監査**: 新イベント `AlternativeProtectiveStopAttempted`（EntryDecisionId 相関・注文種別・注文状態・
   ブローカー注文 ID・`RejectReasonCode`（retType）・`RejectReasonMessage`（retMsg）・手法・発注先）。
   `AlternativeProtectiveStopAttemptedAuditHandler` が中央監査台帳へ記録する。**受理・拒否のどちらでも 1 件出す**
   ——「何の種別で試したか」は受理時にも一次証跡が要る（受理を実測したときに読めなければ意味がない）。
6. **拒否時の扱い**: S0 とまったく同じ（`ProtectiveStopCoverageLost`・未約定なら取消／約定済みなら成行手仕舞い）。
   受理された場合も S0 と同じ（`ProtectiveStopPlaced`・`protective_stop_orders` へ記録し `ProtectiveStopGuard` の巡回対象）。
7. **能力が無い場合**: S3 なのに `IAlternativeProtectiveOrderBroker` が無い発注先へ届いたら **発注せず見送る**
   （`StopOrderUnsupported`。既存の「逆指値能力が無い Open は見送る」と同じ fail-closed）。

## 母集合（着手前の走査・2026-09-18・`git grep -l -F` ＋ `-- ':!CHANGELOG.md'`）

| 検索語 | ファイル数 | 扱い |
| --- | --- | --- |
| `AlternativeBrokerOrderType` | 10 | 本番コード: `StopLossMethodPolicy`（分岐を分ける）。`AuditEntryFactory` / `NotificationFormatter` の `S3` ラベルは**変えない**（表示名は同じ）。`StopLossMethodChange`（選べる値の allow-list）は**変えない**（S3 は既に選べる）。契約 `StopLossExecutionMethod` は XML コメントの「未実装（#821）」だけを直す（**序数は動かさない**）。テスト 3 件は S3 の期待値を更新（`StopLossMethodPolicyTests` / `OrderExecutionServiceStopLossMethodTests` / `StopLossMethodContractTests` は序数のみで変更不要）。`.ai-context/` 2 件は凍結記録のため変更しない（IADR-0342 は「S3 は #821」と書いており、本 PR の後も正しい） |
| `IProtectiveOrderBroker` | 19 | ポートの契約は**変えない**（S3 は姉妹ポートを足すだけ）。`PaperBrokerAdapter` は新ポートを実装しない。`ProtectiveStopGuard` は**変えない**（受理された S3 のレグは S0 と同じ記録になるため、再発注は `PlaceStopOrderAsync`＝S0 で行う。失効時の再発注まで S3 で行うと、ガードが手法を知る必要が生じる——IADR-0347 に残余リスクとして記録） |
| `MoomooOrderKind` | 5 | `IMoomooTradeClient`（種別と要求の項目を追加）・`MMApiMoomooTradeClient`（写像）・`MoomooBrokerAdapter`（発注）・`MoomooBrokerAdapterTests`。`.ai-context/specs/` 1 件は凍結記録 |
| `NotImplementedFallbackToBrokerStop` | 3 | `StopLossMethodPolicy` / `OrderExecutionAppService`（警告文から S3 を外す）／`StopLossMethodPolicyTests` |
| `ProtectiveStopWaived` | 27 | **同型の先行実装**（新イベント → 監査ハンドラ → 基準 JSON → docs）。本 PR はこの型どおりに `AlternativeProtectiveStopAttempted` を足す。**通知は足さない**（拒否は既存の `ProtectiveStopCoverageLost` が Critical で鳴り、受理は S0 と同じ。通知ハンドラの母集合はハンドラ型であり、全イベントに通知を求めていない＝`NotificationConsumerCoverageTests`） |
| `MoomooBrokerOptions` | 24 | 本番コード 5 件のうち `MoomooBrokerOptions`（新しい設定）・`Program.cs`（アダプタへ渡す）・`BrokerFactory`（受け渡し）を変更。`BrokerSelection` / `LiveTradingGate` / `MMApiMoomooTradeClient` は**変えない**。`.ai-context/specs/` 13 件は凍結記録。`docs/operations/*` 2 件は運用手順（新しい設定は chart README へ書く） |
| `Broker:Moomoo` | 26 | 設定キーの追加に追随するのは `deploy/helm/.../values.yaml` ＋ `templates/deployment.yaml` ＋ chart `README.md`（`replyTimeoutSeconds` と同型の「空なら env を注入しない」形）。`deploy/opend/*` は OpenD 側の設定であり無関係 |
| `EnsureSucceeded`（`MMApiMoomooTradeClient`） | 1 | 例外型だけを差し替える（メッセージ文字列は不変。`InvalidOperationException` 派生のため既存の捕捉・表明は壊れない） |

**除外とその理由**

- `.ai-context/specs/` の既存仕様書（凍結記録。`traceability.repo.md`）と `.ai-context/adr/` の確定済み IADR
  （IADR-0342 は「S3 は #821」と書いており本 PR 後も正しい。IADR-0210 は S0 の規律で不変）。
- `docs/api/openapi.yaml`: 8 行の雛形で本作業の経路を持たない（生成物が無い）。
- `deploy/helm/.../files/pipeline.json`: 主経路のイベントだけを列挙しており、保護逆指値系は 1 件も載っていない
  （`ProtectiveStopWaived` も無い）。新イベントも載せない。
- フロントエンド: 契約フィクスチャ（`risk-controls.{settings,status}.json`）は **`stopLossMethod` の値域を変えない**
  （S3 は #819 の時点で既に選べる）。画面は #823。
- 実弾（`TrdEnv=real`）の経路: IADR-0342 決定 2・決定 4 の 2 方向の拒否がそのまま効く（本 PR は触らない）。

## 受け入れ基準 → テスト

| # | 受け入れ基準（#821） | テスト |
| --- | --- | --- |
| 1 | SIMULATE ＋ S3 の発注で、**注文種別と拒否理由が監査に残る** | `OrderExecutionServiceAlternativeStopTests.SIMULATEでS3は代替注文種別で発注し拒否理由が試行の記録に残る`（T-10-360）／`MoomooBrokerAdapterAlternativeStopTests.StopLimitの拒否はretTypeとretMsgを戻り値へ載せる`（T-10-363）／`AuditEntryFactoryTests` ＋ `AuditEventConsumersTests`（T-10-366） |
| 2 | 受理された場合は **S0 と同じ保護レグ**として扱う | `OrderExecutionServiceAlternativeStopTests.S3が受理されたらS0と同じ保護レグとして記録されガードの巡回対象になる`（T-10-361） |
| 3 | 拒否時の扱いは S0 と同じ（建玉を持たない） | `OrderExecutionServiceAlternativeStopTests.S3の拒否は未約定なら取消し約定済みなら成行手仕舞いする`（T-10-362） |
| 4 | 注文種別は設定で選べる（StopLimit 既定 / TrailingStop） | `MoomooBrokerOptionsTests.代替の保護注文種別は設定で選べ未知の値は起動時に停止する`（T-10-364）／`MoomooBrokerAdapterAlternativeStopTests.TrailingStopはトレール幅を発火価格とエントリー価格の差で送る`（T-10-365） |
| 5 | 実弾では S3 を選べない・空売りは常に S0・既定は動かない（**回帰**） | `StopLossMethodPolicyTests`（表を S3 の新しい扱いへ更新。実弾は `Refused`・空売りは `BrokerStopOrder` のまま）／`OrderExecutionServiceStopLossMethodTests`（S1 と未知だけが S0 扱いへ縮む）／既存の `StopLossMethodSettingsTests` / `StopLossMethodContractTests` は**無変更で緑**（T-10-367） |

## 実装中に追加で判明したこと

- `MMApiMoomooTradeClient.EnsureSucceeded` の例外は **retType / retMsg を文字列へ畳んでいた**ため、そのままでは
  監査へ構造化して載せられない。専用例外（`MoomooTradeRequestException`）へ変え、メッセージ文字列は 1 バイトも変えなかった
  （既存の表明・ログの読みを壊さないため）。
- StopLimit は**指値価格が必須**である。発火価格と同値にすると急落時に約定しない（＝保護にならない）ため、
  不利側へずらす比率を設定（既定 1%）に持たせた。これは実装判断であり IADR-0347 決定 3 に記録した。
- TrailingStop のトレール幅は発火価格だけからは決まらない（「エントリーからどれだけ離すか」が要る）。
  能力ポートへ**エントリーの判断価格**を渡す形にした（`OrderIntent.Price`）。

## ［2026-09-25 追記 / #842］受け入れ基準表のテスト名 3 件は実在しない —— 実体への対応

上の「受け入れ基準 → テスト」表の 3 件は、実装中にテストを分割・改名したため**リポジトリに存在しない名前**を引いている
（#842 論点 4。`git grep` で 3 語とも本仕様書 1 ファイルだけに現れることを確認）。カバレッジ自体は存在する。
本文（表）は凍結記録として書き換えず、実体への対応をここに置く。

| 表の行 | 表が引く（不在） | 実体（`OrderExecutionService.Tests`） |
| --- | --- | --- |
| 1（T-10-363） | `MoomooBrokerAdapterAlternativeStopTests.StopLimitの拒否はretTypeとretMsgを戻り値へ載せる` | `MoomooBrokerAdapterAlternativeStopTests.代替注文種別の拒否はretTypeとretMsgを戻り値へ載せる` |
| 3（T-10-362） | `OrderExecutionServiceAlternativeStopTests.S3の拒否は未約定なら取消し約定済みなら成行手仕舞いする` | `OrderExecutionServiceAlternativeStopTests.S3の拒否は未約定ならエントリーを取り消す` ＋ `…S3の拒否は約定済みなら成行で手仕舞う` ＋ `…エントリーが生きていなければS3の試行は行われない` |
| 4（T-10-364） | `MoomooBrokerOptionsTests.代替の保護注文種別は設定で選べ未知の値は起動時に停止する` | `MoomooBrokerOptionsTests.代替の保護注文種別は未設定なら_StopLimit_で指値幅は_1_パーセント` ＋ `…代替の保護注文種別を構成から読む` ＋ `…未知の代替注文種別は起動時に停止する` ＋ `…StopLimit_の指値幅を構成から読む` ＋ `…範囲外の指値幅は起動時に停止する` |

