---
title: 本番の既定の配備で取引判断・情報収集・通知を市場監視へ結線する（#1050）
type: spec
status: accepted
related_ids: [FR-02, FR-13, FR-01, SC-02, ADR-0044, ADR-0042, IADR-0095, IADR-0282, IADR-0433, IADR-0435, IADR-0324, IADR-0098]
author: claude (Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0044_watchlist-in-decision-prompt-and-stage0-asof.md（実測 4・フォローアップ 3）
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 本番の既定の配備で監視銘柄の消費側を市場監視へ結線する（#1050）

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-02**（定時取引サイクルの判断対象）・**FR-13**（監視銘柄の変更）。関連: FR-01（情報収集）・SC-02（監視銘柄の画面）。
- 計画 ADR: **ADR-0044**「実測」表の 4 と「結果 / フォローアップ」3（本番の配備で取引判断に市場監視を結線する）。ADR-0042（Discord での監視銘柄の変更の経路）。
- 実装判断: **IADR-0095 への追記**（新しい IADR は起こさない。結線を導入したのが IADR-0095 であり、本件はその「実環境では BaseUrl を設定する」を
  チャートの本番既定で果たすだけで、新しい判断は資格情報を足さないこと・固定リストの意味の書き分けに限られる）。
- 起点 Issue: #1050（planning#673 の実測 4）

## 事実（着手時に確かめたもの）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | 本番既定の `MarketMonitor__BaseUrl` は 3 か所とも空。issue の行番号 :338 は**情報収集**、:423 は**通知**、:619 は**取引判断**（issue 本文の「:338 trade-decision」は取り違え） | `values.yaml` を読む |
| 2 | Service は `templates/service.yaml` が `services` の各キーから `<キー>-service`・`port: 8080` で描く。Deployment と同じ namespace（`namespace.name`）。よって宛先は短名 `http://market-monitor-service:8080` で届く（`values-local` の値と同じだが、これは values からではなく template から導いた） | `service.yaml` を読む・`helm template -s templates/service.yaml` |
| 3 | 取引判断・情報収集は `AddAiStockTradingServiceToken`（`ServiceAuth__ClientId` / `__ClientSecret` ＋ template が導出する `ServiceAuth__TokenEndpoint`）で照会する。本番既定に `ast-secrets` の `service-auth-client-id` / `-secret` が `optional: true` で既に在る | 両 `Program.cs`・`values.yaml`・`deployment.yaml` の `$hasServiceAuth` |
| 4 | 通知は `AddDiscordOwnerToken`（`Notifications__Discord__OwnerAuth__*` ＋ template が notification にだけ注入する `OwnerAuth__TokenEndpoint`）。本番既定に `discord-owner-auth-client-id` / `-secret` が既に在る | `NotificationService/Program.cs`・`deployment.yaml` |
| 5 | 市場監視の GET `/monitor/watchlist` は `OwnerOrService`（IADR-0095 決定 2）。サービス主体・owner 主体のどちらでも読める。適用（proposal-apply）は OwnerOnly | `MonitorSettingsEndpoints.cs` |
| 6 | 取引判断の固定リスト（`TradeCycle:Watchlist`）は**照会に失敗した巡回ごと**のフォールバック（`HttpWatchlistProvider`。200 ＋空は倒さない）。情報収集の固定リストは**一度も読めていないときだけ**（`FinnhubSymbolSelector`・IADR-0435）。issue の「一度も読めていないときだけ」は後者の意味 | 各アダプタのコード |
| 7 | 本番既定（`ASPNETCORE_ENVIRONMENT=Development` で `appsettings.Development.json` も効く）には `TradeCycle:Watchlist` も `Monitor:SeedSymbols` も無い。結線前も後も、利用者が登録するまで判断対象はゼロ（悪化しない） | appsettings・values を grep |
| 8 | 情報収集は `Collection__Source__Provider` が空（本番既定）だと `NoSourcesFetcher` になり、監視銘柄を照会しない。通知は Bot 無効（本番既定）の間は `/policy` が走らない。起動時の見積り（`finnhub-daily-request-estimate`）も Finnhub 系が無効なら 0 のまま | `InformationCollectionService/Program.cs`・`InformationSourceFactory.EstimateDailyVolume` |
| 9 | `values-local.yaml` は 3 サービスとも `extraEnv` を全要素で持ち、Helm はリストを置換する。よって values.yaml の変更は values-local の描画に届かない | `values-local.yaml` 冒頭の注記・描画のバイト比較 |
| 10 | チャートの試験の枠組みは `.github/workflows/helm.yml` の `Assert …` ステップ（`helm template` の描画を awk で検査）。既存に `MarketMonitor` を見る検査は無い | `helm.yml` を読む |

## やること

| issue の項目 | 本 PR |
| --- | --- |
| 本番の既定で trade-decision（と同じ設定を持つ消費側）を市場監視へ結線 | `values.yaml` の 3 か所を `http://market-monitor-service:8080` に |
| 固定リストの意味を README と values のコメントに合わせる | values の 3 か所のコメント・chart README（新節「監視銘柄の権威源への結線」・経路B の「サイクル配線」・情報収集の節・初回シードの節の最後の項） |
| s2s の資格情報が本番既定で揃うか確かめ、足りなければ足す | **足さない**（事実 3・4。全部既に在る）。README の表と IADR-0095 追記に参照先を書く |
| chart の render の試験で本番の既定が空でないことを固定 | `helm.yml` に 1 ステップ（T-10-1650〜T-10-1652） |

## 母集合（規則 1〜6・9・10）

- 誤りの側の文字列で走査: `git grep -n "MarketMonitor__BaseUrl\|MarketMonitor:BaseUrl" -- ':!backend' ':!.ai-context/specs' ':!.ai-context/superpowers' ':!CHANGELOG.md'`。
  - `values.yaml` 3 か所 → 値とコメントを直す。
  - chart README :172（経路B の有効化の列挙に `MarketMonitor` BaseUrl）・:277（「本番既定は空」）・:479（「結線しない限り関与しない・バイト等価」） → 直す。:272・:462 は結線の効果の説明で、本番既定の値を主張しないので変えない。
  - `values-local.yaml` 139・165・168・328・411・492 → 値は同じ。コメントは経路B での事実として正しい（「結線するため」「フォールバックのみ」）。**変えない**（変えると values-local の描画はバイト等価のままだが、差分を増やすだけ）。
  - IADR-0095 :71〜94 → 追記で本番既定の結線を記録（「既定挙動: 未設定なら不変」はアプリの構成既定の話として有効）。
  - IADR-0282 :85（決定 5「本番 values.yaml は無変更（引き続き空）」）・IADR-0435 :85（決定 5「本番既定は空」）→ **各 PR の時点の記録**。本文へ後付け注記はせず、IADR-0095 追記がこれらを改めると名指しする（索引行を 3 本変えると並行 PR との衝突面が増える。追記の単一の置き場を IADR-0095 に寄せる）。
  - IADR-0114 :130（「結線すると取引サイクルが沈黙する」）→ IADR-0282 の初回シードで解消済みの経緯。本番既定では固定リストも空なので沈黙は結線前と同じ（事実 7）。変えない。
  - IADR-0433 :88（配備の行）→ 本番既定の値を主張していない。変えない。
- `git grep -n "監視銘柄" -- docs deploy README.md | grep "本番\|既定\|未結線\|固定リスト"` → chart README :261・:281（情報収集の節。本番既定の値を主張しない）・observability の説明（「結線したとき」）・SC-02 画面仕様書（「本画面での変更は定時サイクルに反映される」＝結線後の事実。本件で本番既定でも真になる）。変えない。
- 規則 10（本件で新たに誤りになる自分の記述）: README の新節の「CI が検査する」はステップ名で引いた。T-ID は 1650〜1652 で、1600〜1639 は他の作業の予約（指示）。
- 🔴 **テスト仕様書の trace ブロックは足さない**: `docs/tests/FR-10_risk-controls-tests.md` の trace ブロックの `specs:` / `issues:` 行は並行 PR が頻繁に変え、隣接行は衝突する（#1022 の仕様書と同じ判断）。本文の節は文書の途中（helm のリリースとの差の節の直後）へ置く。trace は本仕様書と IADR-0095 追記が持つ。
- chart README の「現状の既知の誤り」: :272〜273「起動時の見積りはフォールバック用の固定リストの数で数えており…（是正は別作業）」は #1030 / IADR-0437 で是正済み（結線時は 1 巡回の上限で数える）で古い。本件の変更で新たに誤りになったものではないので**触らない**（報告に残す）。

## 決定する挙動

- 本番既定の描画で変わるのは 3 行だけ（`MarketMonitor__BaseUrl` の値 `""` → `"http://market-monitor-service:8080"`）。
- `values-local.yaml` を重ねた描画はバイト等価。
- 資格情報・template・Service は変えない。

## 受け入れ基準 → テスト（T-10-1650〜T-10-1652）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | `helm template` の本番の既定で 3 か所とも宛先が入り、描画された Service と一致する | `helm.yml`（T-10-1650） |
| 2 | 各消費側に照会の資格情報が揃う | 同（T-10-1651） |
| 3 | values-local との差分に意図しない変化が無い（同じ検査が通り宛先が一致・描画はバイト等価） | 同（T-10-1652）＋描画のバイト比較（PR 本文） |

## 検証の範囲

- `helm lint --strict`・追加ステップを手元で実行（既定・values-local とも緑）。変異 6 件で赤を確認（テスト仕様書の表）。
- `helm template` の前後の差（本番既定 3 行・values-local バイト等価）を PR 本文に載せる。
- `node scripts/scripts.test.js`（scripts の試験）。
- クラスタには触れない（helm upgrade / install・変更系の kubectl はしない）。
