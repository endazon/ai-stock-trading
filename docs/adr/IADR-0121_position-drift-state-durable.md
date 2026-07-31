---
title: IADR-0121 建玉乖離トラッカーの状態は DB 単一行＋並行トークンで持ち、競合に負けた観測は捨てる
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, FR-10, FR-11, NFR, UC-02, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-31
updated: 2026-07-31
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# IADR-0121: 建玉乖離トラッカーの状態を durable 化し、単一レプリカ前提を明示的な保証へ置き換える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-31
- 決定者: endazon（利用者・#305 起票と設計承認）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-05（発注・注文状態の追跡。[IADR-0118](IADR-0118_broker-position-reconciliation.md) と同じ
  拡張解釈）、FR-10（リスク統制）、FR-11（監査）、FR-09（通知）、NFR（可用性・水平スケール）、
  [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)（証券会社連携）
- 対象 Issue: [#305](https://github.com/endazon/ai-stock-trading/issues/305)（親
  [#292](https://github.com/endazon/ai-stock-trading/issues/292)）
- 関連する実装仕様書: [20260731_305_drift-tracker-replica-safety](../specs/20260731_305_drift-tracker-replica-safety.md)
- 関連 IADR: [IADR-0118](IADR-0118_broker-position-reconciliation.md)（建玉突合。**本 ADR がその「状態を
  インメモリに置く理由と、その前提」節を置き換える**）、
  [IADR-0085](IADR-0085_paper-withdrawal-notification-dedup.md)（durable な通知重複排除＝本 ADR が倣う型）、
  [IADR-0106](IADR-0106_consumer-endpoint-name-uniqueness.md)（MassTransit のキュー名と競合コンシューマ）、
  [IADR-0012](IADR-0012_risk-settings-persistence.md)（単一行＋`Version` の楽観的排他）

## 背景・課題

IADR-0118 の `PositionDriftTracker` は `AddSingleton` でプロセス内に 3 つの状態
（観測中シグネチャ・連続観測回数・報告済みシグネチャ）を持ち、「同一シグネチャを**連続 N 回**（既定 2）
観測したら報告する」を判定していた。

`BrokerPositionsObserved` の購読は MassTransit 既定の `DefaultEndpointNameFormatter` により **consumer クラス名
から導かれる単一キュー**で受ける（IADR-0106 が別件で実害を確認した構造）。したがって `replicas` を 2 以上に
すると複数 Pod が同一キューを consume する**競合コンシューマ**になり、観測が Pod へラウンドロビンで分散する。
各 Pod のカウンタは 1 のまま `required = 2` に到達せず、**乖離が恒久的に未報告のまま**になる。

この縮退は**静か**である。例外は出ず、ログにも異常が残らない。単に「乖離が報告されなくなる」だけで、
IADR-0118 の受け入れ基準①「乖離が定期的に検知され、監査・通知へ届く」を無言で損なう。統制系で最も避けたい
失敗の形である。

IADR-0118 はこの前提を文章として明記していた（「本 ADR は単一レプリカ前提のもとでの決定である」）。
しかしそれは**インフラ側の暗黙の前提に依存し続ける**ことであって、コードが保証しているものではない。
同種の重複排除である `IWithdrawalNotificationStore`（IADR-0085）が DB 単一行で解決している以上、
本トラッカーだけがインメモリという非対称も残る。

## 検討した選択肢

1. **状態を DB 単一行へ durable 化する**（採用）。`IWithdrawalNotificationStore` と同型。
2. **IADR-0118 に単一レプリカ前提を明記するだけ**（＝現状維持）。
3. **起動時に `replicas > 1` を検出して警告／拒否する**（fail-loud 化）。
4. **リーダー選出で単一処理を保証する**。

## 決定

**選択肢 1 を採る。** 具体的には次の 4 点を決める。

### 決定 1: 状態は専有 DB の単一行（`position_drift_state`）に置く

`(ObservedSignature, ConsecutiveCount, ReportedSignature, Version)` を `Id = 1` の単一行に持ち、
`Version` を EF の `IsConcurrencyToken` にする（IADR-0012 と同型）。

層の分割は既存の Ports/Adapters に揃える。

| 型 | 役割 |
| --- | --- |
| `IPositionDriftStateStore` / `PositionDriftState` | Application のポート。`Version` は**不透明な並行トークン**で判定には使わない |
| `PositionDriftDecision.Decide` | 純関数。連続条件・dedup・解消の判定（IADR-0118 の意味論をそのまま移設） |
| `PositionDriftTracker` | singleton → **scoped**。`Get → Decide → TrySave` を束ねる |
| `EfPositionDriftStateStore` | 単一行 upsert。`DbUpdateConcurrencyException` を `false` へ写像する |
| `InMemoryPositionDriftStateStore` | ユニット試験・非 relational 用。**版意味論も実装する**（競合を再現できる） |

並行制御の検証は **InMemory provider に閉じない**。本決定の安全性は最終的に Npgsql が発行する
`UPDATE ... WHERE Id=1 AND Version=@original` の実挙動に依存するため、実 PostgreSQL の統合テスト
（`PositionDriftStateConcurrencyE2ETests`・`Category=Integration`）を 1 本置く。とくに**初回行の同時 INSERT
（主キー衝突 23505）は InMemory では到達できない**（共有ストアのため後発の `Find` が必ず行を見つける）ため、
実 DB でのみ固定できる経路である。他の単一行ストア（IADR-0085 等）は実 DB テストを持たないが、本 ADR は
「レプリカ間の read-modify-write を守る」ことが主題であるため一段厚くする。

### 決定 2: 競合に負けた観測は捨てる（リトライしない）

`TrySave` が `false`（別レプリカが先に状態を進めた）を返したら、その観測は報告せずに終える。

**これが安全である根拠は「必ずどれか 1 つは勝つ」こと。** 状態は単調に前進するため、報告が止まることはない。
捨てた側の観測内容は、乖離が解消するまで**毎巡回で再観測される**（既定 600 秒）。失うのは最大 1 巡回分の
時間であって、報告そのものではない。`ReportedSignature` の dedup も DB 側にあるため、二重報告は起きない。

リトライしない理由: 同一 `DbContext` で再読込するには `ChangeTracker.Clear()` が要り、同じスコープで取引台帳を
読んでいる consumer の追跡エンティティまで巻き込む。得られるのは「最大 1 巡回の短縮」だけで割に合わない。
競合は `LogDebug` に残して**観測可能**にする（無言にしない＝本 ADR が消そうとしている失敗の形を再生産しない）。

あわせて、乖離が解消したときは**連続回数も 0 へ戻す**（「何の」連続かが無いため 0 が正しい）。これで乖離ゼロが
続く間は状態が完全に不変になり、トラッカーは「変化なし＝書かない」で巡回ごとの無駄な永続化を避けられる
（`EfWithdrawalNotificationStore.ClearNotifiedSignature` が同じ理由で無駄な巡回書き込みを避けているのと同型）。
判定結果は変わらない——解消は元から報告対象ではなく、再発時は連続 2 回で再び報告へ到達する。

### 決定 3: 判定意味論は 1 つも変えない

連続 N 回（既定 2・構成キーにしない）、順序非依存の正準シグネチャ、前回報告と同一なら再報告しない、
解消したら報告済みを忘れる、**是正しない**（検知・記録・通知のみ・#304 の領分）——すべて IADR-0118 のまま。

`PositionDriftDecision` は現行 `ShouldReport` の中身をそのまま移したものであり、単一トラッカー＝単一レプリカでの
振る舞いは**同値**である。その証拠として IADR-0118 で書いた既存 9 テストを store 注入へ置換するだけで維持する。

**変わる 1 点**: 再起動をまたいで連続カウントと報告済みが保持される。IADR-0118 は「再起動後に 1 度だけ
再報告され得るが許容」としていたが、durable 化でその再報告も消える（改善方向）。

### 決定 4: `replicas: 1` は変えない（本 ADR は水平スケールを認定しない）

`deploy/helm/ai-stock-trading/templates/deployment.yaml` の `replicas: 1` は据え置く。本 ADR が消すのは
**このコンポーネントの無言縮退**であって、リスク管理サービス全体の水平スケール認定ではない。

ただし調査の結果、リスク管理サービスに残る他の跨ぎ状態は既に durable か冪等であり、本トラッカーが
**判定を担う唯一のインメモリ跨ぎ状態**であった。

| 常駐・シングルトン | レプリカ跨ぎの扱い |
| --- | --- |
| `WithdrawalEvaluationService` | `IWithdrawalNotificationStore`（DB 単一行・IADR-0085） |
| `ObservedDrawdownRefreshService` | DB の単調 latch（IADR-0103） |
| `QuoteCache` | 純キャッシュ（Pod ごとに持っても判定は変わらない・API 呼び出しが増えるだけ） |
| `PositionDriftTracker` | **本 ADR の対象** |

## 根拠

### なぜ「明記するだけ」では足りないのか（選択肢 2 を採らない理由）

#305 の受け入れ基準①（明記）は IADR-0118 の該当節で**既に満たされている**。それでも問題が残るのは、
明記が守るのは「読んだ人」だけだからである。`kubectl scale` の 1 コマンド、HPA の導入、あるいは将来の
values 化のいずれもが、ADR を読まずに前提を破れる。そして破れたことは**どのシグナルにも現れない**。

durable 化は同じ保証をコードとスキーマ（`IsConcurrencyToken`）に移す。暗黙の運用前提が、破ろうとすると
DB が拒否する明示的な制約になる。

### なぜ fail-loud（選択肢 3）ではないのか

Pod から自分のレプリカ数を知るには Kubernetes API への参照が要る（ServiceAccount・RBAC・クライアント）。
統制の検知器 1 つのために k8s API 依存を持ち込む対価が、得られる「起動時の警告」に見合わない。
構成でレプリカ数を宣言させる方式は、宣言と実体がずれた瞬間に**また静かに壊れる**（同じ失敗の再生産）。

そして何より、fail-loud は**スケールできない状態を固定する**だけで、#305 の受け入れ基準②が挙げる
「レプリカを増やした場合に乖離報告が無言で止まらない」を満たす方向ではない。

### なぜリーダー選出（選択肢 4）ではないのか

リース管理（k8s Lease か DB ベース）の常駐と、リーダー交代時の状態引き継ぎが要る。引き継ぎに失敗すれば
結局カウンタが飛ぶため、**どのみち durable な状態が要る**。単一行 1 つで済むものに調停機構を足す理由がない。

### 通知重複排除（IADR-0096）を durable にしなかった判断との違い

IADR-0096（日報未確定スキップ通知）は**営業日単位の in-memory dedup** を意図的に選んだ。あちらは
「通知が 1 日に複数回出る」ことが最悪の結果で、縮退の向きが**過剰通知（うるさくなる）**だった。

本 ADR の縮退の向きは**未通知（黙る）**である。統制の検知器が黙る方向の縮退は、うるさくなる方向とは
安全上の意味が違う。この非対称が、同じ「in-memory で足りるか」の問いに逆の答えを与える。

## 影響・追随

- **実弾ゲート（閂 0〜4）に差分ゼロ。** ブローカ呼び出しを 1 つも増やさない。SIMULATE 限定・実弾 OFF は不変。
- **DB スキーマ変更あり**: `position_drift_state` テーブル 1 つを追加する Migration（`AddPositionDriftState`）。
  IADR-0118 の「Migration 無し」はここで更新される。
- `Shared.Contracts` 不変・新規イベント無し（契約ガード 3 点＝baseline / URN 固定 / 監査 Consumer に差分なし）。
- Helm / values / compose / `.env.example` / 構成キーは**不変**（設定点を 1 つも足さない）。
- IADR-0118 の「状態をインメモリに置く理由と、その前提」節に、本 ADR による置き換えの注記を入れる。
- 発注執行側 `BrokerPositionSnapshotService` を `replicas > 1` にすると 1 巡回に複数の観測が出て連続条件が
  早く満たされる＝一過性フィルタが弱まる。**無言の停止とは逆向き**（報告が増える側）のため本 ADR では扱わない。
  多重化するなら観測の重複排除（`ObservedAt` 窓など）を別途決めること。
- 乖離の**自動是正**は引き続き行わない（#304）。

## 代替案を採らなかった理由

- 選択肢 2（明記のみ）: 明記は読んだ人しか守らない。破っても無言なので、統制の検知器としては不十分。
- 選択肢 3（fail-loud）: k8s API 依存の対価に見合わず、スケール不能を固定するだけで受け入れ基準②を満たさない。
- 選択肢 4（リーダー選出）: 交代時の引き継ぎに結局 durable な状態が要る。調停機構は過剰。
