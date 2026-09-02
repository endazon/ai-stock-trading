---
title: watchlist の初期投入経路を用意してから MarketMonitor__BaseUrl を結線する（構成からの初回シード）
type: work
status: done
related_ids: [FR-02, FR-13, UC-06, SC-02, IADR-0088, IADR-0095, IADR-0114]
author: claude (Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 作業仕様書: watchlist の初期投入経路を用意してから MarketMonitor__BaseUrl を結線する（#286）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（取引サイクル・定時判断）, FR-13（監視銘柄の変更は利用者のみ・変更履歴を記録）
- ユースケース（UC）: UC-06（監視設定の変更）
- 画面（SC）: SC-02（監視銘柄変更・#196 で実装済み）
- 関連 ADR: 計画 ADR は無し（実装レベルの決定）。実装ADR: IADR-0088（監視銘柄 API の認可設計）・
  IADR-0095（HttpWatchlistProvider のフォールバック設計）・IADR-0114（#279 決定5・本 issue の切り出し元）
- 計画書リンク: `project-planning/projects/ai-stock-trading/02_requirements/01_requirements.md`（FR-02/FR-13）

## 目的・背景

`../project-planning` は本 worktree に隣接クローンされていない（本セッションでは #286 の issue 本文・関連 IADR・
実コードで裏取りする）。

#279（IADR-0114 決定5）の調査で、`TradeDecisionService` の `MarketMonitor__BaseUrl` を market-monitor へ結線する
だけでは取引サイクルが沈黙することが判明した。理由:

1. market-monitor の watchlist は `MonitorDefaults.CreateSettings()`（`MonitoredSymbols = []`）で**空にシードされる**
   （`EfMonitoredSymbolStore.GetSettings()` の「未設定時シード」経路）。
2. `HttpWatchlistProvider`（TradeDecisionService）のフォールバックは非 2xx・timeout・例外・不正応答（null）に限られ、
   **200 ＋ 空配列は正常応答**として `[]` をそのまま返す（IADR-0095 の設計意図＝「空の watchlist は監視しない利用者の
   正当な選択」を尊重するため、フォールバックさせない）。
3. したがって結線すると権威源が空を返し判断対象ゼロになり、現状の構成ベース watchlist
   （`TradeCycle__Watchlist__0__Symbol=AAPL`）が使われなくなる。

**フォールバック側を変えるのではなく、権威源（market-monitor）に初期値を入れるのが筋**という #286 の分析は
現在も有効。利用者裁定（2026-09-02）は 3 候補（(a) デプロイスクリプトから POST／(b) 構成からの初回シード／
(c) SC-02 からの手動投入を運用手順として文書化）のうち **(b) 構成からの初回シード**を採る。

## 対象範囲

- 対象:
  - `MarketMonitorService`: `MonitorDefaults.CreateSettings` を構成（`Monitor:SeedSymbols`）から供給する経路。
  - 単一行 JSON ストア（`EfMonitoredSymbolStore`）に「未設定」と「利用者が明示的に全削除した」を区別する
    永続フラグ（`SeededAt`／`ClearedByUserAt`）を追加。
  - `values-local.yaml`: `trade-decision.MarketMonitor__BaseUrl` を結線し、`market-monitor.Monitor__SeedSymbols__0`
    へ現行の構成ベース watchlist と同じ銘柄（AAPL/UnitedStates）を投入。
  - ドキュメント（chart README）に初回シード・全削除の意思の尊重・再シード条件を記す。
- 対象外:
  - (a) デプロイスクリプトからの `POST /monitor/watchlist` 自動投入（(b) を採用したため不要）。
  - `HttpWatchlistProvider` のフォールバック設計変更（IADR-0095 を変えない。#286 分析どおり）。
  - 本番 `values.yaml` への `SeedSymbols` 投入（既定は引き続き空＝現行挙動のバイト等価を保つ）。
  - 収集間隔（`Monitor:PollIntervalSeconds`）の設計変更（IADR-0164 決定1 と無関係）。

## 設計

### データモデル: `MonitorSettingsRow` へ 2 列追加

`monitor_settings`（単一行 JSON＋Version・IADR-0012 踏襲）へ、**ドメイン型 `MarketMonitorSettings` には含めない**
永続層専用のフラグを追加する。ドメイン型・`GET /monitor/settings` の応答・全置換 PUT の入力型に混ぜないのは、
`CollectionIntervalNotConfigurableTests`（IADR-0164 決定1）と同型の理由——**設定として往来する型に持たせると、
API 経由で直接動かせる操作に化ける**（今回のフラグは「利用者が全削除した」という事実の記録であり、API 契約で
直接セットしてよい値ではない）。

```csharp
public sealed class MonitorSettingsRow
{
    public int Id { get; set; }
    public string Json { get; set; }
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SeededAt { get; set; }         // 追加: 構成シードを適用した最終時刻（null=シード未実施）
    public DateTimeOffset? ClearedByUserAt { get; set; }  // 追加: 利用者が明示的に全削除した時刻（null=未削除）
}
```

既存行（本機能導入前に作られた行）は列追加時に両方とも `NULL` になる（後方互換）。

### 構成: `Monitor:SeedSymbols`

`ConfigurationWatchlistProvider`（TradeDecisionService）の `WatchlistEntry` と対称の構成形式にする
（`TradeCycle:Watchlist` と同型・列挙は列挙名でバインド）。

```csharp
public sealed class MonitorSeedOptions
{
    public const string SectionName = "Monitor"; // MonitorOptions（PollIntervalSeconds）と同じ節。keyは別なので衝突しない。
    public IReadOnlyList<SeedSymbolEntry> SeedSymbols { get; init; } = [];
    public IReadOnlyCollection<MonitoredSymbol> ToMonitoredSymbols() => ...;

    public sealed class SeedSymbolEntry
    {
        public string? Symbol { get; set; }
        public Market Market { get; set; }
    }
}
```

helm 環境変数は `Monitor__SeedSymbols__0__Symbol` / `Monitor__SeedSymbols__0__Market`。**既定は空リスト**＝
`MonitorDefaults.CreateSettings()` が従来どおり空でシードする（構成未投入の環境・本番 `values.yaml` は現行挙動の
バイト等価）。

### 判定ロジック: `EfMonitoredSymbolStore`

`GetSettings()`:

1. 行が無い（真の未設定）→ 構成シード（`Monitor:SeedSymbols`。未設定なら空）を適用して挿入。
   `SeededAt=now`, `ClearedByUserAt=null`。
2. 行はあるが `MonitoredSymbols` が空 **かつ** `ClearedByUserAt is null` → 「未設定と同視」して構成シードを
   （再）適用する。**構成シードが空なら書き込みをしない**（ホットパスでの無意味な Version 増加を避ける・
   既存挙動のまま空を返す）。これにより本機能導入前に空で作られた既存行も、`SeedSymbols` を設定した瞬間に
   後方互換で拾われる。
3. 行があり `MonitoredSymbols` が空でない、または `ClearedByUserAt` が設定済み → 触らずそのまま返す
   （利用者の意思を尊重・IADR-0095 と同じ設計思想）。

`Save(settings)`（`MonitorWatchlistService.Add/Remove`・`MonitorSettingsService.UpdateMovementThreshold/
UpdateCooldown/Replace` の共通下請け）:

- 保存後に `MonitoredSymbols` が**空でなくなる** → `ClearedByUserAt = null`（削除状態の解除。再追加で復活）。
- 保存前は非空・保存後に**空になる** → `ClearedByUserAt = now`（今まさに利用者が全削除した）。
- 保存前後とも空のまま（変動閾値・クールダウンだけの部分更新等） → 何も触らない（タイムスタンプを保存の
  都度上書きしない）。

このロジックを `MonitorWatchlistService`（ドメイン層）ではなく `EfMonitoredSymbolStore`（永続層）に置くことで、
DELETE 経路だけでなく全置換 PUT（`MonitorSettingsService.Replace`）で空にした場合も同じ規律で捕捉する
（Save が単一のチョークポイント）。

### `values-local.yaml`

- `trade-decision.MarketMonitor__BaseUrl`: `""` → `http://market-monitor-service:8080`（結線）。
- `market-monitor`: `Monitor__SeedSymbols__0__Symbol=AAPL` / `Monitor__SeedSymbols__0__Market=UnitedStates`
  （`trade-decision.TradeCycle__Watchlist__0__*` と同じ銘柄。結線しても判断対象が減らないことを保証する）。
- `trade-decision.TradeCycle__Watchlist__0__*` は**変更しない**（照会不達時のフォールバックとして残す。
  #286 スコープの明示要件）。
- 本番 `values.yaml` は無変更（`MarketMonitor__BaseUrl` は引き続き空。`Monitor:SeedSymbols` の設定点も追加しない
  ＝描画バイト等価）。

## 受け入れ基準

- [x] watchlist が空でない状態で `MarketMonitor__BaseUrl` を結線しても、定時サイクルが従来どおり銘柄を判断対象にする
      （`values-local.yaml` で AAPL/UnitedStates を両側に投入。実環境確認手順は下記）。
- [x] SC-02 で銘柄を追加／削除すると、次の定時サイクルの判断対象が追随する（既存の GET /monitor/watchlist
      同期照会は無改修。実環境確認手順は下記）。
- [x] 照会不達（BaseUrl 不正・market-monitor 停止）では構成ベース watchlist へフォールバックする
      （`HttpWatchlistProvider`／`ConfigurationWatchlistProvider` は無改修。既存テスト不変で確認）。
- [x] 「全削除した状態」が再シードで巻き戻らない（`ClearedByUserAt` フラグ。ユニットテストで固定）。

## テスト方針

- `EfStoreTests`（純粋な永続層のユニットテスト・InMemory EF Core provider）に追加:
  - 未設定（行なし）→ 構成シードが適用される。
  - 未設定・構成シードも空 → 従来どおり空でシードされる（既存テスト「設定は未設定時に既定値をシードして返す」
    が退行しないことの確認を兼ねる）。
  - 空だが `ClearedByUserAt` あり（模擬的に事前挿入）→ 構成シードがあっても再シードされない。
  - 既存行（非空）→ 構成シードの有無に関わらず触られない。
  - 後方互換: `SeededAt`/`ClearedByUserAt` 列を持たない旧行相当（両方 null で挿入）を「未設定」として拾う。
- `MonitorWatchlistService` 経由（`EfMonitoredSymbolStore` を実体で使う統合寄りのテスト）:
  - 最後の 1 件を `Remove` すると `ClearedByUserAt` が立つ（直後の `GetSettings` が構成シードを再適用しない）。
  - その後 `Add` すると解除され、以後は通常どおり空になれば再度 `ClearedByUserAt` が立つ。
- `TradeDecisionService` 側 `HttpWatchlistProvider`／`ConfigurationWatchlistProvider` の既存テストは無改修のまま
  green であることを確認する（本変更が影響しないことの回帰確認）。

## 計画書との差異

- 差異: なし。#286 は実装リポジトリ内で発見された運用ギャップであり、計画書の要求（FR-02/FR-13）自体に
  誤り・不足は無い。

## 未決事項

- なし。(a)/(c) は利用者裁定により不採用と確定済み。

## 実環境での確認手順（受け入れ基準 4 件・オーケストレータが再デプロイ後に実施）

1. **結線後も判断対象が減らない**: 再デプロイ後、`ast.trade_cycle.decisions` メトリクス（#287 で追加された
   業務メトリクス）が定時サイクルごとに増加することを確認する。増加しない場合は
   `ast.information.items_collected` も併せて見る——**動かない場合は watchlist が空**（本 issue の症状）、
   **`items_collected` は動くが `decisions` の `action` が見送り側に寄る場合は情報源縮退**（#337・別原因。
   issue #286 コメント 2026-08-28 の切り分け表を参照）。
2. **SC-02 の変更が次サイクルに追随**: owner トークンを取得し（`ast-secrets` の owner クライアント資格情報。
   #226/IADR-0098）、`POST /monitor/watchlist`（例: 一時的にもう 1 銘柄を追加）→ 次の定時サイクル後に
   `GET /monitor/watchlist` で反映を確認 → `ast.trade_cycle.decisions` の対象銘柄数（ログ or メトリクスラベル）
   が増えていることを確認する。確認後は追加した銘柄を `DELETE /monitor/watchlist` で戻す。
3. **照会不達時のフォールバック**: 一時的に `MarketMonitor__BaseUrl` を不正な URL に切り替えて再デプロイ
   （または market-monitor-service を一時停止）し、trade-decision のログに
   「監視銘柄（watchlist）の照会に失敗」または「照会がタイムアウト」の警告が出て `TradeCycle:Watchlist`
   （AAPL/UnitedStates）へフォールバックすることを確認する。確認後は `MarketMonitor__BaseUrl` を元に戻す。
4. **全削除が再シードで巻き戻らない**: owner トークンで `DELETE /monitor/watchlist`（AAPL/UnitedStates）を
   全件実行し watchlist を空にする → market-monitor サービスを再起動（Pod 再作成）→
   `GET /monitor/watchlist` が空のままであること（`Monitor__SeedSymbols__0` が再投入されないこと）を確認する。
   確認後は `POST /monitor/watchlist` で元の状態（AAPL/UnitedStates）へ戻す。
