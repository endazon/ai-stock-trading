---
title: IADR-0307 端点間レイテンシは起点をイベントに載せて運び、終点 2 点で計上する（射影も join もしない）
type: impl-adr
status: Accepted
related_ids: [NFR-01, NFR-02, NFR-07, FR-02, FR-03, FR-04, FR-05, FR-11]
author: endazon (with Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0307: 端点間レイテンシは起点をイベントに載せて運び、終点 2 点で計上する（射影も join もしない）

- 状態: Accepted
- 日付: 2026-09-04
- 決定者: endazon（実装は Claude Code）

## 起点・関連

- 関連する計画書 ID: NFR-01（価格変動検知 → 発注完了・5 分以内）／NFR-02（定時サイクル 1 周・10 分以内・収集→判断→発注→記録）／NFR-07（可観測性）
- 関連する issue: [#689](https://github.com/endazon/ai-stock-trading/issues/689)（本 IADR の起点）／分割元 [#637](https://github.com/endazon/ai-stock-trading/issues/637)／実測は [#690](https://github.com/endazon/ai-stock-trading/issues/690)（実 LLM・開場中・本 IADR の範囲外）
- 関連する実装仕様書: `.ai-context/specs/20260904_689_nfr-01-02-end-to-end-latency-metrics.md`
- 下地: [IADR-0255](./IADR-0255_business-metrics-and-dashboards.md)（業務メトリクスと `BusinessMetricNames` の単一情報源）／[IADR-0089](./IADR-0089_backtest-verdict-supply.md)（イベント射影）／[IADR-0026](./IADR-0026_audit-deterministic-correlation.md)（決定的相関 v5 UUID）

## コンテキストと課題

NFR-01/02 は**サービスを跨ぐ区間**を目標値で縛る。ところが本リポジトリには、その区間を測る計器が
1 本も無い（着手時の実測: `grep -rn "NFR-01\|NFR-02" backend --include=*.cs` は 0 件）。
既存の `ast.trade_cycle.decision_duration_ms` は **`TradeDecisionService` 内の判断 1 回**しか測らず、
[#287](https://github.com/endazon/ai-stock-trading/issues/287) の作業仕様書自身が「NFR-01/02 の**下地**」と
自認していた。下地は検証ではない。

測れない理由は計器の不足ではなく、**相関が切れていること**である。

| 系統 | 相関の鍵 | 起点時刻 |
| --- | --- | --- |
| 市場・情報系 | `PriceMovementDetected.EventId` / `InformationCollected.EventId` | `DetectedAt` / `CollectedAt` |
| 注文系 | `DecisionId`（`TradeDecisionMade` → `OrderApproved` → `OrderExecuted`） | — |

`TradeDecisionAppService` は `DecisionId` を `Guid.NewGuid()` で**新規採番**しており、起点イベントの
素性を下流へ 1 バイトも渡していない。**両端を見ている唯一のサービス（`AuditService`）ですら、
台帳の `CorrelationId` が別物であるため結べない。**

決めるべきことは 2 つある。**(a) どこで両端を突き合わせるか**、**(b) 突き合わせられなかったときに何を出すか**。

## 検討した選択肢

### (a) 突き合わせの置き方

| 案 | 方式 | 評価 |
| --- | --- | --- |
| A. 監査台帳への**イベント射影**（[IADR-0089](./IADR-0089_backtest-verdict-supply.md) 型） | `AuditService` が起点イベントと `OrderExecuted` を突き合わせる | ❌ **成立しない。**射影を置いても、起点と注文チェーンを結ぶ鍵が存在しない。結ぶには結局 (B) と同じ結線が要り、**そのうえで状態（DecisionId → 起点時刻の表）を持つことになる** |
| B. **起点をイベントに載せて運ぶ**（採用） | 契約 3 本の末尾へ provenance 2 フィールドを既定値つきで足し、終点で引き算する | ✅ 状態なし・join なし。レプリカ・再起動・順序に影響されない。契約の追加は後方互換（末尾・既定 null） |
| C. メッセージヘッダ（Wolverine）で運ぶ | 契約を変えない | ❌ 監査・下流サービスからは見えず、契約テスト（`EventBackwardCompatibilityTests`）の保護も効かない。**明示の契約でないものは黙って消える** |
| D. 分散トレース（Tempo）の span で測る | 既存の OTel トレース | ❌ トレースはサンプリングされ、目標達成の**件数**を数える用途に耐えない。ダッシュボード（Prometheus）とも別系統になる |

**A を最初に検討した**のは、本リポジトリの既存の型がイベント射影だからである。**足りなかったのは
射影の置き場ではなく、結ぶ鍵そのものであった。**

### (b) 終点をどこに置くか

計画本文の語がそのまま終点である。

- NFR-01「価格変動検知から**発注完了**まで」→ `OrderExecutionService` が `OrderExecuted` を発行する点
- NFR-02「収集→判断→発注→**記録**」→ `AuditService` が監査台帳へ 1 行書いた点

**1 点にまとめて「発注完了で代表する」案は採らなかった。** 記録の区間を落として測ると、NFR-02 は
構造的に**過少報告**になる（速く見える方向へ倒れる）。目標値の達成判定に使う計器を、達成側へ倒れる
近似で作らない。

### (c) ヒストグラムのバケット

OTel の既定境界は上限 10,000 ms である。5 分（300,000 ms）・10 分（600,000 ms）は**すべて `+Inf` へ
落ちる**ため、既定のままでは分位点も超過件数も読めない。**計器はあるが読めない**という、最も
気付きにくい失敗の形になる。

## 決定

1. **取引サイクルの起点（cycle provenance）を契約イベントで運ぶ。** `TradeDecisionMade` /
   `OrderApproved` / `OrderExecuted` の**末尾へ既定値つきで 2 フィールド**を足す（後方互換の追加）。
   - `string? CycleTrigger` —— 既存のメトリクスタグ語彙（`scheduled` / `price-movement`）をそのまま使う。
     **enum にしない**——未知の値が既定 0 へ黙って落ちるのを避ける（`LlmStopReasons` と同じ方針）。
   - `DateTimeOffset? CycleStartedAt` —— 起点イベント自身の時刻。
     **判断サービスの現在時刻で代用しない**（検知・配送の区間が計測から消える）。
2. **終点は 2 点。突き合わせ（join）も状態も持たない。**
   - NFR-01: `OrderExecutionService` の `OrderApprovedHandler`（`OrderExecuted` を発行する点）
   - NFR-02: `AuditService` の `OrderExecutedAuditHandler`（**台帳へ書いた後**に計上する）
3. 🔴 **「測れなかった」と「0 だった」を区別する。** 起点が無い（`CycleTrigger` か `CycleStartedAt` が
   `null`）／経過が負（サービス間の時計ずれ）のときは、**ヒストグラムへ 1 件も入れず**、専用の
   Counter `ast.trade_cycle.latency_unobserved` へ `stage`・`reason` タグつきで 1 件数える。
   **0 を入れると「5 分以内・10 分以内を満たしている」と読めてしまう。**
   分岐は `BusinessMetrics.RecordCycleLatency` の 1 か所に持つ（2 つの計上点で判断が割れないため）。
4. **起点を持たない経路には起点を作らない。** 利用者の手仕舞い（`PositionCloseService`）・維持証拠金の
   自動縮小（`MaintenanceMarginReductionService`）・約定追跡の後追い（`OrderFillPoller`）は
   **取引サイクルではない**ため `null` のままにし、未観測として数える。
5. **休場の早期 return はサイクル完了として数えない。** 休場では判断が走らず `TradeDecisionMade` が
   出ないため、provenance を持つ注文が 1 件も生まれず、**ヒストグラムにも未観測カウンタにも入らない**
   （#637 が指摘した「0〜18 ms を実績にしない」の構造的な担保）。単体テストで否定形を固定する。
6. **バケット境界を View で明示し、目標値そのもの（300,000 / 600,000 ms）を境界に置く。**
   超過件数が隣り合うバケットの引き算で読め、分位点の補間に頼らない。境界配列は 2 本の
   ヒストグラムで共有する（片方だけ動かすと比較できなくなる）。
7. **計器は挙動を変えない。** 計上は発行・記録の**後**に行い、統制・発注の判断には一切使わない。
   provenance は統制の入力にしない（`OrderScreeningService` は中継するだけで読まない）。

## 理由

- **鍵が無いところに射影を置いても結べない。** 選択肢 A が成立しないことが本 IADR の中心である。
  「既存の型に寄せる」ことよりも「結線を作る」ことが先である。
- **状態を持たない計器は壊れにくい。** [IADR-0124](./IADR-0124_position-drift-state-durable.md) が
  実測したとおり、プロセス内の状態はレプリカと再起動で黙って失われる。起点を運べば、終点は
  引き算するだけで済み、そもそも失われる状態が無い。
- **過少報告は達成側へ倒れる。** 記録の区間を落とした近似で NFR-02 を測ると、目標未達を見逃す方向に
  だけ誤差が出る。統制の計器と同じく、**誤差の向きは安全側へ倒す**。

## 影響・残余リスク

- **契約 3 本にフィールドが増える。** 末尾・既定 `null` の追加であり、既存の生成箇所・購読側は
  変更なしでコンパイルが通る。`event-schemas.baseline.json` を再生成して新フィールドを固定した
  （固定しないと、次の PR で消しても検査が緑のまま通る。[IADR-0198](./IADR-0198_fx-expired-visibility.md) の
  「追加を許容することと、追加を記録しないことは違う」）。
- **再配送では同じ所要値が重複計上され得る**（AI レビュー 🟢 指摘・[#700](https://github.com/endazon/ai-stock-trading/pull/700)）。
  `OrderApproved` が再配送されると、発注そのものは冪等（相1 で既存結果を再発行）でも
  `OrderApprovedHandler` は計上を毎回行う。**同一値の重複であるため分位点は大きく歪まない**が、
  件数（レート）で読むと水増しになる。これは既存の `RecordOrderExecuted` / `RecordOrderScreening` と
  同じ性質であり、本 IADR で新たに作った歪みではない。**計器のために発注経路へ重複排除を足さない**
  （挙動を変えないという決定7 に反する）。件数で読む必要が生じたら、その時点で計上側に鍵を持たせる。
- **監査の payload（`AuditSerialization`）に 2 フィールドが増える。** 7 年保持の記録に起点が残る
  という意味では利得だが、既存の payload と形が変わる点は残余リスクとして記録する。
- 🔴 **時計は同期していない前提である。** 起点と終点は別プロセスであり、区間には NTP のずれが
  そのまま乗る。負になった区間は捨てて件数だけ残すが、**正の側の小さなずれは検出できない**。
  目標値（5 分・10 分）に対して秒未満のずれは実用上無視できるという判断であり、
  **ミリ秒単位の SLO を本計器で名乗らない**。
- **本 IADR は計器までである。** 実 LLM を含む開場中の実測と、目標未達だった場合の計画への環流は
  [#690](https://github.com/endazon/ai-stock-trading/issues/690) の持ち場である。**本 PR は目標値を
  一切触らない。**
- **実クラスタでの系列の疎通は未確認**（`check-observability-assets.js` が守るのは名前の一致まで）。
  実バックエンドでの確認は可観測性スタックの opt-in stand-up 後に行う。
