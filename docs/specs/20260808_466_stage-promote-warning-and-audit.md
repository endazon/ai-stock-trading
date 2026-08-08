---
title: /stage promote に最小取引件数の引き下げ警告を出し、昇格時点の設定値と警告有無を監査ログへ残す
type: spec
status: approved
related_ids: [FR-20, FR-11, SC-02, UC-06, ADR-0008, IADR-0164, IADR-0180]
author: endazon (with Claude Code)
created: 2026-08-08
updated: 2026-08-08
---

# 仕様書: `/stage promote` の引き下げ警告と昇格記録

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#466](https://github.com/endazon/ai-stock-trading/issues/466)（由来は [#459](https://github.com/endazon/ai-stock-trading/issues/459) の棚卸し）
- 起点 ID: **FR-20**（段階ゲート）／**FR-11**（監査ログ）／**SC-02**（リスク設定画面）／**UC-06**
- 起点の裁定: `planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md` **§4.1 の 2026-08-07 追補3**（planning pin `c2998a6`。利用者裁定 質問票 第15回 Q13-a / Q13-b。環流 project-planning#252）
- 先行実装: [IADR-0164](../adr/IADR-0164_stage1-trade-count-setting-and-monitor-parameter-relocation.md)（最小取引件数を設定値化し、SC-02 と `/stage status` に警告を出した）

### 裁定の本文（§4.1 追補3 より）

> - **「昇格承認」が指すのは承認操作そのものである**（Q13-a）。**Discord Bot の `/stage promote`（承認操作）に警告を出す。** `/stage status`（現況照会）だけでは足りない —— 「承認前に status を読む」は**人の運用に依存する前提**であり、読まなければ警告が届かない。（…）**現在の `/stage promote` の応答（`StageTransitionResult`）は合格条件を運ばないため、遷移応答へ合格条件を載せる契約変更が要る**（実装側の残件。`IADR-0164`）。
> - **警告を無視して昇格した事実を記録に残す**（Q13-b）。**昇格時点の最小取引件数の設定値と、警告が出ていたか否か**を監査ログ（FR-11）へ記録する。

## 実装の現状（実測 2026-08-08・`develop` = `f25edda`）

| 経路 | 警告 | 実測した根拠 |
| --- | --- | --- |
| SC-02（画面・入力中／保存済み） | **出る** | `frontend/src/features/sc02-risk-settings/Stage1TradeCountForm.tsx:152`（`STAGE1_TRADE_COUNT_BELOW_BASIS_WARNING`） |
| `/stage status`（Discord・現況照会） | **出る** | `HttpStageGateController.FormatStatus`（`v.Stage1Criteria is { BelowStatisticalBasis: true }`） |
| **`/stage promote`（Discord・承認操作）** | **出ない** | `StageGateCommandHandler` の `StagePromote` は `controller.RequestTransitionAsync` を呼ぶだけ。応答 DTO `StageTransitionResultView` に合格条件の項目が無い |
| **監査ログ（`StageTransitioned`）** | **残らない** | `Shared/AiStockTrading.Shared.Contracts/Events/StageTransitioned.cs` の 7 項目に設定値も警告有無も無い（`event-schemas.baseline.json:162-170` が実測を固定している） |

**したがって「60 営業日・5 件で Stage 2 へ上がった」事実は、どこにも残らない。**

## 決定

### 決定1: 遷移応答（`StageTransitionResult`）へ実効の合格条件を載せる

裁定が名指しした契約変更である。`StageGate.RequestTransition` は既に `StageGatePolicy` を引数で受け取っており、
`StageGateService.EffectivePolicy()` が設定値（`Stage1MinimumTradeCount`）を重ねた**実効値**を渡している。
したがって純ドメインの結果へそのまま載せられる（新しい供給経路を作らない）。

**受理・拒否の両方に載せる。** 拒否時も承認操作は行われており、設定が下がっている事実は変わらない。
「拒否されたときだけ警告が消える」経路を作らない。

### 決定2: 警告は `/stage promote` に出す。`/stage demote` には出さない

裁定は「**昇格承認**」を名指ししている。差し戻しは安全側の操作であり、そこへ同じ警告を出すと
「読まれない警告」化を招く（裁定が `/stage status` だけでは足りないとした理由と同じ論理の裏返し）。

**実装上の含意**: `HttpStageGateController.RequestTransitionAsync(int targetStage)` は現段階を持たないため、
昇格か差し戻しかを判定できない。よって**アダプタは警告文言を本文へ混ぜず別項目（`Stage1Warning`）で返し、
`StageGateCommandHandler` が `BotCommandKind.StagePromote` のときだけ本文へ足す**。
整形（数値 enum → 表示テキスト）をアダプタ 1 か所に閉じる IADR-0081 決定1 の規律は保たれる
（Application 層は整形済み文字列だけを扱う）。

### 決定3: 文言は SC-02 と同一にし、Discord 側で 1 か所に集約する

issue が「画面側（SC-02）の警告と**文言・条件を揃える**」ことを求めている。現状 `/stage status` の文言は
SC-02 と別に書き下ろされており、既に割れている。`/stage promote` を足すと**3 か所**になる。

- Discord 側（`/stage status` と `/stage promote`）の文言を**定数 1 個へ集約**する。
- その文言を SC-02 の `STAGE1_TRADE_COUNT_BELOW_BASIS_WARNING` と一致させる。

**残余リスク**: C#（バックエンド）と TypeScript（フロントエンド）に跨るため、
**文言の一致を機械的に強制する手段が無い**。定数のコメントで相互参照し、テストで C# 側の文言を固定する。

### 決定4: 監査へは「設定値」と「警告有無」を**両方**載せる（片方から導出しない）

`StageTransitioned` へ 2 項目を追加する。

| 追加項目 | 型 | 意味 |
| --- | --- | --- |
| `Stage1MinimumTradeCount` | `int` | 遷移時点の最小取引件数の設定値 |
| `Stage1BelowStatisticalBasis` | `bool` | **その時点で警告が出ていたか否か** |

**警告有無を設定値から後で導出しない。** 導出にすると、将来 `Stage1TradeCountBounds` の統計的根拠（100）が
改訂されたときに**過去の記録の解釈が黙って書き換わる**。「当時警告が出ていたか」は当時の事実であり、
当時の値で凍結して記録する。

**受理された遷移すべてに載せる（昇格に絞らない）。** 絞ると降格の記録が「設定不明」になり、
`int?` / `bool?` の null が「昇格ではなかった」と「供給されなかった」の両方を意味してしまう。

あわせて、監査エントリの**人が読む要約**（`AuditEntryFactory.From(StageTransitioned)`）にも
警告が出ていた場合にその旨を足す。payload（`AuditSerialization.Serialize`）には自動で載るが、
要約を走査する監査では「なぜ 5 件で上がったのか」が目に入らない。

## 🔴 やらないこと（issue の明示）

- **警告を理由に昇格を拒否すること。** 裁定は「警告を伴う利用者の明示的な選択として認める」としている。
  `Stage1Gate.Evaluate` は `BelowStatisticalBasis` を参照しない（現状のまま。テストで固定する）。
- **`/stage status` にだけ警告を出して済ませること。** 裁定が名指しで否定している。
- **閾値 100 を Discord 側へ写経すること。** 判定はサーバ（Risk）が `BelowStatisticalBasis` で宣言する
  （IADR-0164 決定6 の規律。旧版サーバ耐性のため項目が無ければ警告を出さない）。

## 影響範囲

| 層 | ファイル | 変更 |
| --- | --- | --- |
| Risk / Domain | `StageTransition.cs` | `StageTransitionResult` へ `Stage1Criteria` を追加 |
| Risk / Domain | `StageGate.cs` | `RequestTransition` の全 return 経路へ実効条件を載せる |
| Shared / Contracts | `Events/StageTransitioned.cs` | 2 項目を追加（＋ baseline 再生成） |
| Risk / Api | `RiskControlEndpoints.cs` | `StageTransitioned` 発行時に 2 項目を渡す |
| Notification / Infrastructure | `HttpStageGateController.cs` | 応答 DTO へ `Stage1Criteria` を追加・警告文言を定数化・`Stage1Warning` を返す |
| Notification / Application | `Ports`（`StageTransitionCommandResult`）・`StageGateCommandHandler.cs` | 警告項目の伝搬と promote 限定の付加 |
| Audit / Application | `AuditEntryFactory.cs` | 要約へ警告の旨を足す |

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 引き下げ状態の `/stage promote` の応答に警告が含まれる | `StageGateCommandHandlerTests`（promote・below basis） |
| 2 | **否定形**: 既定値のままなら警告は出ない | 同上（100 件・警告なし） |
| 3 | **否定形**: 警告が出ても昇格は拒否されない | `StageGateServiceTests` / `Stage1GateTests`（`Evaluate` が `BelowStatisticalBasis` を参照しない） |
| 4 | 監査ログに設定値と警告有無が残る | `RiskControlEndpoints`（発行）・`AuditEntryFactoryTests`（要約・payload） |
| 5 | **否定形**: `/stage demote` には警告が出ない | `StageGateCommandHandlerTests`（demote） |
| 6 | `/stage status` にも引き続き出る（回帰） | `HttpStageGateControllerTests` 既存 |
| 7 | 文言が SC-02 と一致する | C# 側の文言を定数テストで固定（クロス言語の強制は不可・残余リスク） |

## 対照実験（緑 → 赤 → 緑）

実装前に「赤くなるはずのもの」を実走して確認する（本 repo の型・IADR-0166/0172/0179 の系譜）。

1. 新テストを先に書き、**実装前に赤**であることを確認する。
2. 実装後に**緑**。
3. `BelowStatisticalBasis` の判定を反転させて、**狙ったテストだけが赤**になることを確認する（写経の不在の実測）。

## 検証

- `dotnet build backend/backend.slnx`（0 Warning / 0 Error）
- `dotnet test backend/backend.slnx`
- `node scripts/check-doc-links.js` ほか CI ゲート
