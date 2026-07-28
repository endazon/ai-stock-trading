---
title: IADR-0037 非同期イベント契約は当面 AsyncAPI を採用せず、共有 C# 契約＋Markdown を継続し、軽量な URN 回帰ガードで補強する
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-05, ADR-0001, ADR-0002, IADR-0009, IADR-0079]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-28
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0037: 非同期イベント契約は当面 AsyncAPI を採用せず、共有 C# 契約＋Markdown を継続し、軽量な URN 回帰ガードで補強する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-04（取引判断）、FR-05（発注執行）、ADR-0001（platform 再利用・イベント連携）、ADR-0002（証券会社アダプタ）
- 関連する実装 ADR: [IADR-0009](IADR-0009_async-contract-format.md)（本評価の起点。「契約が増え形式化の便益がコストを上回った時点で AsyncAPI 移行を再検討する」フォローアップの再評価）、[IADR-0001](IADR-0001_repo-structure-and-stack.md)（platform 整合の制約）
- 関連する通信仕様書: [events-and-ports](../api/events-and-ports.md)
- 作業仕様書: [20260711_asyncapi-adoption-evaluation](../specs/20260711_asyncapi-adoption-evaluation.md)
- 対象 Issue: #51（派生元 #34 / PR #45）。関連 platform イベント規約 #22

## コンテキストと課題

IADR-0009（2026-07-09）は、非同期イベント契約を Markdown 通信仕様（`docs/api/events-and-ports.md`）で管理し、
**AsyncAPI は現段階で採用しない**と決定した。その根拠は「契約が少数（当時 4 件）かつ流動的で、AsyncAPI の
ツールチェーン導入コストが便益を上回る」ことであり、明示的なフォローアップとして「**契約が増え形式化の便益が
コストを上回った時点で AsyncAPI 移行を再検討する**」を残していた。Issue #51 はこの再検討トリガの追跡票である。

本 IADR は、その再検討を実施し、現時点での採用可否を確定する。決めるべきことは 2 点:

1. 非同期イベント契約の記述形式に **AsyncAPI を採用するか否か**（採用する場合は移行方針）。
2. IADR-0009 が残した曖昧な再検討トリガを、**観測可能で再現性のある条件**へ具体化するか。

### 現状把握（IADR-0009 以降の変化と不変点）

**変化した点**

- 非同期イベント契約数が **4 → 10** に増加した（`AiStockTrading.Shared.Contracts/Events`）:
  `TradeDecisionMade` / `OrderApproved` / `OrderRejected` / `OrderExecuted` / `PriceMovementDetected` /
  `StopLossTriggered` / `InformationCollected` / `CostThresholdReached` / `AssumptionsChanged` / `ReportConfirmed`。

**変化していない点（これが評価の核心）**

- **契約の権威は共有 C# `record` 型**である。全イベントは `AiStockTrading.Shared.Contracts` の不変 record として定義され、
  MassTransit がその **型そのもの**から wire 上の識別子（`urn:message:Namespace:Type`）とスキーマを導出する。
  発行側・購読側は**同一アセンブリ（同一 DLL）を参照**するため、サービス間のスキーマドリフトは**コンパイラによって
  構造的に防止**されている（各サービスがスキーマを個別に再宣言する多言語系とは前提が異なる）。
- **エコシステムは単一言語（C#/.NET）・単一ソリューション**である。非 .NET の購読者、外部組織の購読者、
  イベントストリームの公開先はいずれも存在しない。すなわち AsyncAPI の主便益（言語中立の機械可読契約・
  多言語コード生成・組織横断での契約公開・ドキュメントポータル）を**享受する消費者が現時点で不在**である。
- **基盤 microservices-platform（IADR-0001 の整合先）は AsyncAPI を採用していない**。platform は MassTransit の
  `MessageUrn`（名前空間から導出される正準 URN）＋ **URN 回帰テスト**（`EventMessageUrnTests`）で契約を固定している。
  本リポは repo 構成・規約を platform に揃える制約（IADR-0001）を負う。

## 検討した選択肢

評価軸: ①機械可読性・自動化（契約テスト/コード生成/ドキュメント）、②導入・維持コスト（ツールチェーン・CI・二重管理）、
③platform 整合（IADR-0001）、④契約権威との整合（C# 型が単一情報源）、⑤現時点の便益の実在性（享受する消費者の有無）。

### 案 A（採用）: 共有 C# 契約＋Markdown を継続し、`MessageUrn` 回帰ガードで補強

IADR-0009 の方針（Markdown 通信仕様）を継続する。加えて、契約数増加で顕在化した唯一の具体的リスク＝
「wire 契約の意図しない破壊的変更（型リネーム・名前空間移動で URN が変わる）」に対し、platform と同型の
軽量な `MessageUrn` 回帰テストを CI に追加して固定する（実装は後続タスク）。

- ①: 契約テストは URN 回帰＋共有アセンブリのコンパイル整合で担保（破壊的変更を CI で検知）。コード生成は不要（型が権威）。
- ②: 追加コストは xUnit テスト 1 本のみ。ツールチェーン増設なし。
- ③: platform（`EventMessageUrnTests`）と同一手法で**整合が最良**。
- ④: C# 型を単一情報源のまま維持。二重管理ゼロ。
- ⑤: 実在するリスク（URN 破壊）に実在する手当を最小コストで行う。

### 案 B（不採用）: AsyncAPI を即採用（`asyncapi.yaml` を権威文書化＋生成/検証パイプライン）

`asyncapi.yaml` を著述し、生成器・CI 検証・ドキュメント公開を導入する。

- ①: 機械可読・コード生成・ドキュメントポータルなど表現力は最大。
- ②: **コスト大**。AsyncAPI CLI/バリデータ・CI ジョブ・スキーマ保守を新設。C# 型が実行時の権威のまま残るため、
  `asyncapi.yaml` は**並行して手保守**され**ドリフト源**になる（形式化が防ぐはずの不整合を新たに生む逆説）。
- ③: platform が採用しない手法を単独導入し、**IADR-0001 の整合制約に逆行**する。
- ④: 契約の権威が C# 型と YAML の**二重**になり、単一情報源原則を崩す。
- ⑤: 多言語コード生成・組織横断公開の便益を**享受する消費者が不在**。便益は将来仮説にとどまる。

### 案 C（不採用・時期尚早）: C# 契約型から AsyncAPI を**生成**（code-first、`openapi.yaml` と同様の生成物扱い）

C# 型を権威に保ったまま、生成器で `asyncapi.yaml` を派生物として出力する（手保守を避ける）。

- ①: 生成物ゆえドリフトなし。将来外部消費者が現れれば機械可読契約を即供給できる。
- ②: 生成器 + CI コストは残る。かつ **MassTransit のエンベロープ意味論（URN・ヘッダ・多重メッセージ）を正しく写す
  C#→AsyncAPI 生成器はエコシステムに成熟した既製品が乏しく**、独自実装は保守負債になりやすい。
- ③: platform が行わない生成を単独導入し、③はやはり整合に反する。
- ⑤: 便益はやはり潜在（消費者不在）。生成器を作る労力に見合う需要が今は無く**時期尚早**。

## 決定

**案 A を採用する。現時点では AsyncAPI を採用しない**（IADR-0009 の不採用判断を再確認・維持する）。

- 非同期イベント契約は引き続き **共有 C# `record`（`AiStockTrading.Shared.Contracts`）を権威**とし、人間可読の契約は
  **Markdown 通信仕様（`docs/api/events-and-ports.md`）** で管理する（IADR-0009 継続）。
- 契約数増加で顕在化した唯一の具体的リスク（wire URN の破壊的変更）に対し、platform と同型の軽量な **`MessageUrn`
  回帰テスト**を CI に追加して契約を固定する。**本 IADR はこの実装を後続タスクとして指示する**（設計文書タスクの
  スコープ外。別 issue/PR 化）。
- IADR-0009 が残した曖昧なトリガを、以下の**観測可能な再採用トリガ**へ具体化する。**いずれか 1 つ**を満たした時点で
  AsyncAPI（案 B/C）採用を再評価する。
  1. **非 .NET / 外部の購読者**がイベントストリームに現れる（多言語コード生成の便益が実在化する）。
  2. イベントが**共有アセンブリを参照できない配置境界（別デプロイ単位・別組織）を越えて**公開される。
  3. **platform が AsyncAPI を規約として採用**する（IADR-0001 に従い整合させる）。
  4. 契約数/流動性で Markdown 表の保守が破綻し、**かつ** C#→AsyncAPI の成熟した生成器が利用可能になる
     （案 C の二重管理・生成器負債の反論が解消される）。

## 理由

- **契約の権威が共有 C# 型である**という不変の前提の下では、契約数が 4→10 に増えても AsyncAPI の主便益は生じない。
  スキーマの単一情報源は既にコンパイラが保証しており、AsyncAPI が提供する「言語中立の機械可読契約」は
  この構成では**冗長**である。増えた契約数はトリガの代理指標に過ぎず、真の判定軸は「**享受する消費者の有無**」である。
- 契約数増加で実在化したリスクは「wire 契約の破壊的変更検知」ただ 1 点であり、これは AsyncAPI 一式ではなく
  **URN 回帰テスト（案 A）で最小コストかつ platform 整合的に手当**できる。目的（破壊的変更の防止）に対して
  AsyncAPI は過剰（over-engineering）であり、CLAUDE.md の「過剰な抽象化を行わない」方針にも合致する。
- **IADR-0001 の platform 整合制約**は硬い前提であり、platform が採用しない AsyncAPI の単独導入はコスト側を大きく
  押し上げる。整合を崩す判断は、便益が実在化した時（上記トリガ）に限る。
- 案 C（生成）はドリフトを避ける点で案 B より優れるが、成熟した C#→AsyncAPI 生成器の不在と消費者不在により
  **時期尚早**。トリガ 4 で条件が揃った時に再評価する余地として残す。

## 結果

- 良い影響:
  - IADR-0009 のフォローアップ（Issue #51）に決着がつき、再検討トリガが**観測可能な条件**へ具体化された
    （将来「契約が増えた」だけで蒸し返されない）。
  - 契約権威（共有 C# 型）と単一情報源原則を維持し、二重管理・ドリフトを持ち込まない。
  - platform 整合（IADR-0001）を保つ。
  - 契約数増加で生じた唯一の実リスク（URN 破壊的変更）に対する軽量ガードの導入方針が定まった。
  - 決定の前提（Markdown で人間可読な契約を管理する）を実体化するため、本 PR で `events-and-ports.md` を現状 10 件へ
    同期した（未掲載だった `InformationCollected` / `CostThresholdReached` / `AssumptionsChanged` / `ReportConfirmed` を追記）。
- 悪い影響・トレードオフ:
  - 非同期契約は引き続き機械可読（AsyncAPI）でなく、外部購読者が現れた場合のコード生成・契約公開は即応できない
    （トリガ発火時に案 B/C を再評価して対応）。
  - Markdown 通信仕様は人手保守のままで、契約数の一層の増加時は保守負荷が漸増する（トリガ 4 で見直す）。
- フォローアップ:
  - ~~**`MessageUrn` 回帰テストの実装**（platform `EventMessageUrnTests` と同型）を別 issue/PR で行う。10 イベントの
    正準 URN（`urn:message:AiStockTrading.Shared.Contracts.Events:<Type>`）を固定し、破壊的変更を CI で検知する。~~
    → **完了**（[#253](https://github.com/endazon/ai-stock-trading/issues/253)）。
    `AiStockTrading.Shared.Contracts.Tests/EventMessageUrnTests.cs` が全イベント（本テスト追加時点で 17 件）の
    正準 URN を固定する。検出範囲の [IADR-0079](IADR-0079_event-backward-compat-contract-test.md) との分担は
    同 ADR「既知の限界」を参照。
  - 上記「再採用トリガ」のいずれかが観測された時点で本 IADR を見直し、案 B/C を再評価する（必要なら新 IADR で Supersede）。
  - platform のイベント規約（#22）の進展を監視し、platform が AsyncAPI を採る場合は整合のため追随を検討する。

## 関連

- Supersedes: なし（IADR-0009 の不採用判断を**再確認・継続**するものであり、置換ではない。IADR-0009 は有効なまま）。
- Superseded by: なし
