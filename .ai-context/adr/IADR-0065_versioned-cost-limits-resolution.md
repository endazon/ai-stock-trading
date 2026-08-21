---
title: IADR-0065 費用統制の月次上限をバージョン付き前提条件から解決する（ICostLimitsProvider の非同期化）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - FR-17
  - IADR-0027
  - IADR-0051
  - IADR-0063
author: claude
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR: 費用統制 / FR-17: 全体前提条件の一元管理)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§6 月次費用上限)
---

# IADR-0065: 費用統制の月次上限をバージョン付き前提条件から解決する

- 状態: Accepted
- 日付: 2026-07-17
- 決定者: claude（実装セッション・Issue #139）

## 起点・関連

- 関連する計画書 ID: **NFR（費用統制）** / **FR-17**（全体前提条件の一元管理・バージョン管理）
- 関連する実装仕様書: [20260717_139_versioned-cost-limits.md](../specs/20260717_139_versioned-cost-limits.md)
- 関連する IADR: [IADR-0027](IADR-0027_cost-control.md)（費用統制）/ [IADR-0063](IADR-0063_assumptions-versioned-resolution.md)（前提条件の解決基盤）/ [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s 認証）
- Issue: [#139](https://github.com/endazon/ai-stock-trading/issues/139)（[#19](https://github.com/endazon/ai-stock-trading/issues/19) の明示的後続）

## コンテキストと課題

`CostControlService` の月次費用上限は IADR-0027 以来、暫定で前提条件の**既定値ハードコード**から供給している。

```csharp
public sealed class DefaultCostLimitsProvider : ICostLimitsProvider
{
    public MonthlyCostLimits GetLimits() => TradingAssumptionsDefaults.Create().CostLimits;
}
```

このため**利用者が設定サービスで上限を変更しても費用統制のしきい値判定（80%/100%）に反映されない**。

IADR-0063（#19 Slice B）が、この解決に必要な基盤を既に整備済みである:

- `IAssumptionsProvider.GetCurrentAsync()` → `VersionedAssumptions`（前提条件＋Version）
- `CachedAssumptionsProvider`: HTTP 取得（s2s トークン）＋ キャッシュ ＋ `AssumptionsChanged` 購読による無効化 ＋ 二段 fail-safe
- `AddAiStockTradingAssumptions(config)` による 1 行配線

**費用統制はこの基盤の最初の消費者になる**（本 IADR 時点で他に消費側は存在しない）。したがって本決定の主題は
「機構をどう作るか」ではなく「**既存の機構へどう繋ぐか**」であり、争点は次の 1 点に集約される:

> `ICostLimitsProvider.GetLimits()` は**同期**、`IAssumptionsProvider.GetCurrentAsync()` は**非同期**である。
> この不整合をどちらへ寄せるか。

（この論点は IADR-0063 の仕様書「リスク・留意」で #139 への申し送りとして予告されていた。）

## 検討した選択肢

### 論点 1: 同期ポートと非同期プロバイダの不整合

| 選択肢 | 内容 | 評価 |
| --- | --- | --- |
| **A. ポートを非同期化する（採用）** | `ICostLimitsProvider.GetLimitsAsync()` にし、`CostControlService.Record`/`GetLlmState` へ波及させる | 呼び出し元（エンドポイント・Consumer）は既に async のため波及は表層で止まる。ブロッキングなし |
| B. `.GetAwaiter().GetResult()` で同期的に待つ | ポートを変えずアダプタ内でブロックする | ASP.NET Core のスレッドプールを占有し、キャッシュミス時（＝HTTP 往復 5 秒タイムアウト）にデッドロック・枯渇のリスク。費用計上は全 LLM 呼び出しの後段にあり流量がある |
| C. 起動時に一度読んで固定する | `IHostedService` で読み込みシングルトンへ | 版が上がっても追随できず、受け入れ基準 3 を満たさない |
| D. バックグラウンドで定期更新しキャッシュを同期読みする | 別スレッドで poll し `volatile` フィールドへ | `CachedAssumptionsProvider` の TTL・イベント無効化・fail-safe を**二重実装**することになる。IADR-0063 の単一情報源を壊す |

### 論点 2: `DefaultCostLimitsProvider` の扱い

`Program.cs` の登録先を `Configuration:BaseUrl` の有無で振り分けるか（＝費用統制側にも「設定サービスへ到達できるか」の
判断を持つか）が争点。共有クライアントは既に BaseUrl 未設定なら `DefaultAssumptionsProvider`（＝既定値）へ倒れる。

| 選択肢 | 内容 | 評価 |
| --- | --- | --- |
| A. Program で BaseUrl を見て登録を振り分ける | 未設定なら `DefaultCostLimitsProvider`、設定済みなら `AssumptionsCostLimitsProvider` | **BaseUrl の判定が 2 箇所に重複**し、「設定サービスへ到達できるか」の決定点が分裂する。共有クライアント側の判定と食い違えば挙動が読めなくなる |
| **B. 常に `AssumptionsCostLimitsProvider` を登録する（採用）** | 未設定時の既定値への縮退は共有クライアントに委ねる | 判断が 1 箇所に留まる。挙動は A と完全に等価（`DefaultAssumptionsProvider` が既定値を返すため） |

`DefaultCostLimitsProvider` そのものは **Application 層の「外部依存を持たない既定アダプタ」として残す**（非同期化のみ）。
同層の `InMemoryCostLedger` が Program に登録されないまま Application の既定実装として存在している前例に倣う。
既定の上限値（利用者決定 20,000/15,000）が無自覚にずれないよう、テストで固定する対象としての価値もある。

## 決定

1. **`ICostLimitsProvider` を非同期化する**（論点 1・選択肢 A）。
   `MonthlyCostLimits GetLimits()` → `ValueTask<MonthlyCostLimits> GetLimitsAsync(CancellationToken)`。
   波及して `CostControlService.Record`/`GetLlmState` も非同期化する。**`Review` は上限を参照しない**ため同期のまま据え置く
   （非同期化は上限を読む経路にのみ及ぼす）。

2. **`AssumptionsCostLimitsProvider`（新規）を `Worker/Composable/Adapters/` に置く**。
   `IAssumptionsProvider` へ委譲し `.Assumptions.CostLimits` を返すだけの薄いアダプタとする。
   配置は既存慣行（`InformationCollectionService.Worker/Composable/Adapters/HttpCostControlGate`＝他サービスへの
   同期照会アダプタ）に揃える。Application 層に置かないのは、Application が外部サービスクライアントへ依存しないため。

3. **fail-safe の向きは IADR-0063 決定 5 をそのまま継承する**（費用統制側で再実装・上書きしない）:
   ① 取得成功 → 最新値 ② 取得失敗だが過去に成功 → **last known good** ③ 一度も取得できていない → 既定値（20,000/15,000）。
   ②が③に優先する理由は費用統制において特に重い: 利用者が LLM 上限を 15,000 → 5,000 へ**絞っていた**場合、
   障害時に既定へ戻すと上限が**緩む＝浪費側**へ倒れる。陳腐化していても利用者の意図に最も近い値を持ち続ける。

4. **版の追随は `AssumptionsChanged` 購読による無効化に委ねる**。費用統制の `Program.cs` で
   `x.AddConsumer<AssumptionsChangedConsumer>()` を登録する。**費用統制側にキャッシュを持たない**
   （持つと二重キャッシュになり、無効化の届かない層が生まれて版の取りこぼしが再発する）。

5. **`Program.cs` は常に `AssumptionsCostLimitsProvider` を登録する**（論点 2・選択肢 B）。
   `Configuration:BaseUrl` 未設定時に既定値へ倒す判断は共有クライアントに一本化し、費用統制側では二重に判定しない。
   `DefaultCostLimitsProvider` は Application 層の外部依存なし既定アダプタとして残す（非同期化のみ）。
   結果として **BaseUrl 未設定時の挙動は従来と同一**（既定値 20,000/15,000 で判定・外部接続なし）。

6. **新しいイベントは追加しない**。`AssumptionsChanged` は既存イベントであり、監査サービス（#17）が既に購読・記録している
   （`AuditConsumerCoverageTests` の対象で、追加の Consumer は不要）。

## 理由

- **決定 1**: 費用計上は全 LLM 呼び出しの後段にあり流量がある経路で、そこで同期ブロックすると
  キャッシュミス時（HTTP 5 秒タイムアウト）にスレッドプールを占有する。呼び出し元は既に非同期なので、
  非同期化のコストは呼び出し表記だけで済み、実質的なトレードオフがない。
- **決定 3/4**: 「版が上がったら追随」「一度でも取得できたら last known good を保持」は IADR-0063 が
  チケット方式（取得**前**に単調増加チケットを読む）で既に解いている。消費側で解き直すと、
  #19 が修正した「取得中に届いた変更を取得成功時に消してしまう」レースを再導入する危険がある。**繋ぐだけにする**。
- **決定 5**: 「設定サービスへ到達できるか」の判断は 1 箇所にあるべきで、共有クライアントが既にそれを持っている。
  費用統制側で同じ条件を再判定しても挙動は変わらず、食い違いうる分岐が増えるだけになる。

## 結果

- 良い影響:
  - 利用者による上限変更が費用統制のしきい値判定に反映される（受け入れ基準 1）。
  - 版が上がれば `AssumptionsChanged` で無効化され、次回参照で追随する（受け入れ基準 3）。
  - `ConfigurationService.Client` に**最初の実消費者**ができ、基盤が机上でなく実配線で検証される。
- 悪い影響・トレードオフ:
  - `ICostLimitsProvider` と `CostControlService` の公開シグネチャが変わる（内部利用のみ・破壊的影響は本リポ内に閉じる）。
  - 費用計上の経路にキャッシュミス時のみ HTTP 往復が入る（TTL 5 分・イベント無効化時のみ）。
  - `AssumptionsChanged` の購読先が増える（費用統制）。ブローカ不達時は TTL 5 分で追随する。
- フォローアップ:
  - **実 ConfigurationService・実 Keycloak を跨いだ往復の検証は #82 の E2E に委ねる**（IADR-0063 決定 6 と同方針）。
    本 PR の単体テストは `IAssumptionsProvider` の偽物で閉じており、実基盤には依存しない。
  - 前提条件の他の消費側（損益集計・AI 判断の採算評価・リスク統制の費用込み上限判定）の配線は各 issue で後続。
  - `MonthlyCostLimits.Total`/`Infrastructure`/`Data` は現状どの判定にも使われていない（LLM のみ）。総額上限の統制は
    計画側の要否確認が要る（IADR-0027 の範囲どおり本 PR では触れない）。

## 関連

- Supersedes: なし（IADR-0027 の「上限供給は暫定」という留保を解消する）
- Superseded by: なし
