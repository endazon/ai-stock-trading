---
title: 経路B（values-local）の取引判断で定時サイクルの監視銘柄数の前提（TradeCycle__MaxWatchedSymbols）を 12 へ上げる。監視銘柄 11 件に合わせ、ブローカの consumer_timeout に収まる最大値とする（#1169）
type: spec
status: accepted
related_ids: [FR-04, FR-02, NFR-02, NFR-13, IADR-0490, IADR-0100, IADR-0129, IADR-0434, IADR-0494, IADR-0489]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02, FR-04, NFR-02, NFR-13)
---

# 仕様書: 経路B の監視銘柄数の前提を 12 へ上げる（#1169）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-04**（AI による売買判断）・**FR-02**（定時の取引サイクル）。非機能: NFR-02（定時サイクル 1 回の所要 10 分以内）・NFR-13（LLM 費用の統制）
- 関連 IADR: IADR-0490（定時サイクルの実行時間の上限を構成から導く。本件の設定点の出所）・IADR-0100（values-local）・IADR-0129（Wolverine の共通配線）・
  IADR-0434 / IADR-0494（Finnhub の巡回の予算。監視銘柄数で決まる）・IADR-0489（env 配列の置き換え）
- 起票: [#1169](https://github.com/endazon/ai-stock-trading/issues/1169)（設定点 `TradeCycle:MaxWatchedSymbols` を導入した issue）。
  契機は PoC の監視銘柄が 11 件になったこと（#1189 で現在値の重複照会を止めた後、所有者が 5 銘柄を追加した）。
  **新しい IADR は起こさない**（新しい設計判断は無く、既存の設定点へ値を入れる構成変更である。IADR-0490 へ日付つき追記を足す）。
- ブランチ: `chore/FR-04-1169-max-watched-symbols-local`
- 基点コミット: `origin/develop` `017cbb56`

## 目的・背景

IADR-0490 決定 1 は、定時サイクルのハンドラの実行時間の上限を「1 銘柄の締め切り × 監視銘柄数の前提 ＋ 60 秒」で導く。
前提（既定 10）を監視銘柄が超えると、`InformationCollectedHandler` は毎サイクル警告を 1 行出すだけで判断は続ける。
このとき「監視銘柄が前提以内なら上限に届かない」という上界が成り立たず、最悪（全銘柄が締め切りまで遅い）でサイクルが打ち切られ、
共通の再試行でサイクル全体がやり直しになる（LLM の二重呼び出し。発注は決定 2 で重複しない）。PoC は 11 件で前提を超えている。

## 調査（基点コミット）

### 1. 前提を超えたときの挙動

| 事実 | 出典 |
| --- | --- |
| 1 銘柄の締め切り ＝ LLM の timeout × 呼び出し回数 ＋ 30 秒。サイクル ＝ 1 銘柄 × 前提 ＋ 60 秒（秒は切り上げ） | `backend/Services/TradeDecisionService/Features/TradeDecision/ScheduledCycleBudget.cs:64-76`（式は `:73-74`） |
| 前提は `TradeCycle:MaxWatchedSymbols`。未設定・不正・非正値は 10 | 同 `:112-115`・`Program.cs:390-399` |
| 前提超過は警告のみ（判断は止めない。打ち切りはハンドラの上限で起きる） | `Infrastructure/Steps/InformationCollectedHandler.cs:75-83` |
| 古い起点（`now − CollectedAt` が鮮度の上限を超えた）は判断せず捨てる。鮮度の上限は `NewsStatusValidFor`（巡回間隔の 2 倍・下限 5 分）をクランプ | 同 `:49-60`・`ScheduledCycleBudget.cs:98-106` |

### 2. 経路B の値（基点コミットの `values-local.yaml`）

| 値 | 経路B | 出典 |
| --- | --- | --- |
| LLM の timeout | `LlmGateway__TimeoutSeconds=""` → 30 秒 | `values-local.yaml:368` |
| 呼び出し回数 | スクリーニング 1（`Decision__EnableScreening=true`）＋ 票数 1（既定）＝ 2 | `values-local.yaml:376`・`Program.cs:386` |
| 1 銘柄の締め切り | 30 × 2 ＋ 30 ＝ **90 秒** | 導出 |
| 情報収集の巡回間隔 | 300 秒（chart は全プロファイルで `ASPNETCORE_ENVIRONMENT=Development` を描く → `appsettings.Development.json`）→ 鮮度の上限 600 秒 | `templates/deployment.yaml:127-128`・`InformationCollectionService/appsettings.Development.json:12`・`values-local.yaml:176` |
| ブローカの `consumer_timeout` | 既定 1,800 秒（RabbitMQ 3.13。chart も MSP の `platform-infra` も変えていない＝`git grep consumer_timeout` に設定が無い） | IADR-0490 決定 3 |
| 受信 | Inline・1 本ずつ・PreFetch 100（先読みした未 ack の配信にも `consumer_timeout` が掛かる） | IADR-0490 決定 3 |

### 3. 前提の上限を決める制約（上界の計算）

サイクルが巡回間隔（300 秒）より長いと、次の `InformationCollected` は先読みされたまま待つ。待っている配信にも `consumer_timeout` が掛かる。

- **判断される起点**: 開始時に鮮度の上限以内（待ち ≤ 600 秒。配信時刻 ≥ 収集時刻なので、配信からの待ちも 600 秒以下）で、その後サイクルの上限 T まで走る。
  配信から ack まで ≤ **600 ＋ T**。
- **捨てられる起点**: 待ちは前のサイクルの残り（≤ T）で、捨てるのは即時。≤ T。

したがって **600 ＋ T < 1,800**、すなわち **T < 1,200 秒**が要る（超えると、前提以内の監視銘柄でもブローカがチャネルを閉じて処理中のサイクルが再配送され得る）。

| 前提 N | T ＝ 90N ＋ 60 | 600 ＋ T | 1,800 秒に収まるか |
| --- | --- | --- | --- |
| 10（既定） | 960 | 1,560 | 収まる |
| 11（現在の監視銘柄数） | 1,050 | 1,650 | 収まる |
| **12** | **1,140** | **1,740** | **収まる（余裕 60 秒）** |
| 13 | 1,230 | 1,830 | 超える |
| 15（依頼の例） | 1,410 | 2,010 | 超える |

→ **N ＝ 12**（最大の安全値。現在の 11 件に 1 件の余裕）。15 は採らない。

その他の制約（N を変えても変わらないもの）:

- **LLM 費用（NFR-13）・Finnhub のレート（IADR-0434 / IADR-0494）**は**実際の監視銘柄数**で決まり、前提 N では変わらない（N は上限の導出にだけ使われる）。
  費用の統制は既存の月次上限（台帳）が担う。
- **NFR-02（10 分）**: 最悪の T は 10 分を超える（既定 960 秒でも超える。IADR-0490 の残余リスクに記載済み）。実測の所要（1 銘柄 7〜10 秒の LLM × 2 回）では 11 件で 3〜4 分程度。
- k8s のプローブ・Pod の停止猶予はハンドラの実行時間と無関係（ハンドラは HTTP の応答を塞がない）。

- **T が巡回間隔以下のとき**は起点が溜まらないので、制約は T ＜ 1,800 だけである（上の式は T ＞ 巡回間隔のときに限る。巡回間隔が長い構成に当てると前提 1 でも誤って弾く。独立監査 🟡2）。
  経路B の巡回 300 秒・鮮度の上限 600 秒は情報収集の `appsettings.Development.json` 由来（#1192 で Production へ切り替えるときは `Collection__PollIntervalSeconds=300` を明示する予定）。
- 🔴 **上の式は 1 回の試行についてである（既知の穴。独立監査 🟡1）。** 共通のエラー方針 `OnAnyException().RetryWithCooldown(2s, 10s, 30s)`
  （`backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/WolverineExtensions.cs:38-39`・`:216-217`）は同じ配信の中で ack せずに再試行するので、
  ハンドラの上限による打ち切りや銘柄の catch の外へ漏れた例外では、再試行 1 回ごとに最大 T が足される。**再試行の連鎖全体の検査は #1194**
  （MSP の IADR-0478 決定 7 `EnsureRetryChainFits` と同型の起動時の検査）。本件では値を変えない（前提以内なら 1 銘柄の締め切りが打ち切りを防ぐ）。

### 4. 本番既定（values.yaml）を変えない理由

- 前提を上げる根拠は**経路B の監視銘柄の実数（11 件）**であり、本番既定のプロファイルで 10 件を超えた実績は無い（既定 10 のまま前提が成り立つ）。
  根拠の無い既定の引き上げは上限（T）を全プロファイルで延ばし、NFR-02 からの乖離と、滞留時の待ちを広げるだけである。
- 本番既定も同じ chart で描かれ、巡回間隔は同じ 300 秒である（`ASPNETCORE_ENVIRONMENT=Development` が全プロファイル共通）。したがって
  本番既定で前提を上げるときも §3 と同じ制約（600 ＋ T < 1,800 ⇒ N ≤ 12）が掛かる。上げる必要が生じたら、そのプロファイルの監視銘柄の実数を根拠に同じ計算を引き直す。

## 母集合（規則 9・10。`origin/develop` `017cbb56`）

`git grep -n 'MaxWatchedSymbols\|監視銘柄数の前提\|10 銘柄\|960 秒'`（`backend/` と `.ai-context/specs/` を除く）で引いた。

| 箇所 | 扱い |
| --- | --- |
| `deploy/helm/ai-stock-trading/values-local.yaml`（trade-decision の extraEnv） | 1 行足す（コメントに導出と上限の根拠） |
| `deploy/helm/ai-stock-trading/values.yaml` | 変えない（上記 §4） |
| `deploy/helm/ai-stock-trading/README.md`（経路B の有効化の一覧） | 1 項目を足す |
| `docs/operations/operations.md:259-283`（定時サイクルの実行時間の上限） | 表の「既定（稼働）」に経路B の値を併記し、前提を上げるときの上限（鮮度の上限 ＋ T < consumer_timeout）を足す。`:538` の対処「前提を上げて再配備」にも上限を添える |
| `.ai-context/adr/IADR-0490_*.md` と索引の行 | 凍結。日付つき追記 |
| `.github/workflows/helm.yml`（values-local の有効化の描画検査） | 経路B で 12 が描画され、本番既定に混入しないことを検査に足す |
| `docs/tests/FR-10_risk-controls-tests.md:5337-5367` | 導出の試験の記述で、値に依存しない。変えない |
| `.ai-context/adr/IADR-0434_*.md:93`（「10 銘柄以上の余裕」） | 市場監視の Finnhub 予算の話で別物。変えない |

## 受け入れ基準

1. `helm template -f values-local.yaml` の trade-decision に `TradeCycle__MaxWatchedSymbols=12` が描画され、`values.yaml` 単独の描画には現れない。
2. `helm lint` が両プロファイルで通り、描画の env の差は本行だけである。
3. CI の文書系検査（trace ブロック・知識グラフ・ADR 索引・追記の欠落・クロスリポ参照・計画 ID の修飾・必読予算）と gitleaks が緑。
4. バックエンドのコードは変えない。

## 配備（PoC）

trade-decision の env が変わるので、`helm upgrade -f values-local.yaml` で trade-decision の Pod が作り直される（イメージの作り直しは不要）。
起動時の 1 行 `定時サイクルの実行時間の上限: 00:19:00（… × 監視銘柄数の前提 12 ＋ …）` を確かめ、以後のサイクルで
`上限の前提` の警告が出ないことを確かめる。監視銘柄を 13 件以上にするときは、前提を上げる前に §3 の制約を引き直す（LLM の timeout を縮める・票数を見直す等）。
