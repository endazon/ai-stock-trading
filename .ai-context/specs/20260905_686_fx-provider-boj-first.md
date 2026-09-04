---
title: 為替レート源を第一情報源（日銀）へ切り替える（Fx__Provider の 6 箇所・3 サービス分）
type: spec
status: review
related_ids: [FR-01, FR-06, FR-07, FR-10, FR-16, FR-17, ADR-0022]
author: endazon (with Claude Code)
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md
---

# 仕様書: 為替レート源を第一情報源（日銀）へ切り替える

> Issue [#686](https://github.com/endazon/ai-stock-trading/issues/686)（#643 の分割）。
> **実装（アダプタ・ファクトリ・順位つきフォールバック・状態の可視化）は #381 の 2 層で完了している。
> 残っているのは構成（Helm values）だけである** —— 構成が `fred` / `""` を指しているために、
> 計画（ADR-0022 決定1）が第一と定めた日銀が**どの環境でも一度も使われていない**。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（情報収集）／FR-06・FR-07・FR-16（報告書の為替差損益・期末レート）／
  FR-10・FR-17（統制上限判定の換算レート・採算評価）
- 関連 ADR: **ADR-0022 決定1**（為替の第一の情報源は日銀「外国為替市況（日次）」・系列 `FM08'FXERD04`）／
  **決定2**（FRED はフォールバックとして残し、切り替わった事実を記録・通知する）／
  **決定4・決定5**（鮮度の警告しきい値 5 日・絶対上限 30 日）
- 計画書リンク: `project-planning/projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md`
  （隣接クローン・読み取り専用）
- 対象 Issue: [#686](https://github.com/endazon/ai-stock-trading/issues/686)（分割元 #643・一次確認の残置元 #279）
- 関連する実装ADR: IADR-0194（日銀アダプタと順位つきフォールバック）／IADR-0199（切替状態の供給結線）／
  IADR-0286（認識時レート・期末レートの供給）／IADR-0107（provider 選択と no-op 既定）／
  IADR-0174（鮮度 30 日・警告 5 日）／IADR-0152（基準通貨 USD・逆数）

## 目的・背景

`FxRateSourceFactory` は `Fx:Provider: "boj"` を受け付け、FRED のキーがあれば順位つきフォールバックを
後段に積む（IADR-0194 決定4）。日銀アダプタ `BojFxRateSource` は系列コードの落とし穴（データコードとの
取り違え・中心相場 `FXERD05` との取り違え）まで否定形テストで固定済みである。フォールバック切替の
可視化（`FxRateSourceFellBack` の監査・通知・報告書への供給）も結線済みである。

にもかかわらず、**構成は 6 箇所すべてが日銀を指していない**:

| サービス | 用途 | `values.yaml`（本番） | `values-local.yaml`（経路B） |
| --- | --- | --- | --- |
| `report` | 為替差損益の**期末レート** | `Fx__Provider: ""`（no-op） | `Fx__Provider: "fred"` |
| `risk-management` | 承認記録時の**認識時レート** | `Fx__Provider: ""`（no-op） | `Fx__Provider: "fred"` |
| `trade-decision` | 統制上限判定の**換算レート** | `Fx__Provider: ""`（no-op） | `Fx__Provider: "fred"` |

結果として、

- **経路B はフォールバック側だけで動いている。** FRED `DEXJPUS` は系列こそ営業日次だが公表は
  H.10 週次リリースであり、最新観測の齢は最大 12.84 日まで積み上がる（#271 / IADR-0112 の実測）。
  鮮度**警告**しきい値は 5 日であるため、**平常運用でも警告域に常駐しうる**
  （chart README も「日銀アダプタが入るまでの既知の状態」と明記していた）。
- **FRED は API キーを要する。** 現状 `FRED_API_KEY` 未設定は「**日本株だけが全件見送りになる**」という
  分かりにくい形で効く（#262 / IADR-0109。基準通貨は #364 で USD へ移ったため必須となる市場が入れ替わった）。
  **日銀は認証不要**であり、第一へ切り替えればこの前提そのものが消える。
- **本番は 3 箇所とも no-op** ＝外貨建ての判断・記録・報告のすべてが供給されない。

## 一次確認（日銀 API 実測。2026-09-05）

**#279 で残置されていた「統計分類 `Boj:Db` ・系列コード `Boj:SeriesCodes` の一次確認」は済んでいる**
（#381 / IADR-0194 で実測のうえアダプタの既定値として焼き込み済み: `db=fm08` / `code=FXERD04`）。
本作業では**現在も取得できること・鮮度**を資格情報なしで叩いて再確認した。

```bash
curl -s "https://www.stat-search.boj.or.jp/api/v1/getDataCode?format=json&lang=en&db=fm08&code=FXERD04"
```

| 項目 | 実測値（2026-09-05 00:15 JST 時点） |
| --- | --- |
| HTTP | `200`・`STATUS:200`・`MESSAGE: Successfully completed`・認証ヘッダ不要 |
| 系列名 | `US.Dollar/Yen Spot Rate at 17:00 in JST, Tokyo Market`（＝17 時時点の**仲値**） |
| `LAST_UPDATE` | `20260904` |
| 最新の非 null 観測 | **`20260902` = `159.7`**（1 USD あたりの円） |
| 直近の並び | `20260902=159.7` `20260901=159.99` `20260831=159.57` `20260830=null` `20260829=null` `20260828=159.51` |
| 観測点数 | 10,468 |

- **最新観測の齢は約 2〜3 日**（`20260902` 17:00 JST → 実測時点）。ADR-0022 決定1 追補の「収録は翌々営業日
  8:50 頃」と一致し、**警告しきい値 5 日の内側**である。FRED 単独の最大 12.84 日と比べて構造的に新しい。
- `20260829`（土）・`20260830`（日）が `null` である。**欠測・休場・未収録は同じ `null`** で返るため
  区別しない（計画も区別しないと明記）。アダプタは「値のある直近」を採る。
- **取り違えの罠が現在も生きていることを対照実測した**: 同じ `20260902` で
  `FXERD04`（仲値）= **159.7** に対し `FXERD05`（中心相場）= **160.25**。
  **どちらも「もっともらしい」値であり、取り違えても動作は正常に見える。** 既存の否定形テストを維持する。

## 対象範囲

### 対象

- `deploy/helm/ai-stock-trading/values.yaml`: `Fx__Provider` の **3 箇所**を `""` → `"boj"`。
- `deploy/helm/ai-stock-trading/values-local.yaml`: `Fx__Provider` の **3 箇所**を `"fred"` → `"boj"`。
  `Fx__Fred__ApiKey`（`ast-secrets/fred-api-key`）はそのまま残す＝**フォールバックとして後段に積まれる**。
- `.github/workflows/helm.yml`: 描画ゲートの期待値を追随（`fred` → `boj`）し、**3 箇所すべて**を数える
  形へ強める（1 箇所だけ直しても緑にならないようにする）。
- `deploy/helm/ai-stock-trading/README.md` / `docs/operations/operations.md` /
  `scripts/k8s-local-deploy.sh` の記述: 「`Fx__Provider=fred`」「`FRED_API_KEY` は日本株取引の必須前提」を実体へ合わせる。
- `FxOptions.Provider` の XML ドキュメント: 「現在の実装は "fred"」という**古い記述**を是正する。

### 対象外（やらないこと・理由）

- **鮮度の受容窓（`MaxRateAgeDays` / `StaleRateWarningDays`）は変えない。** 既定は既に
  **30 日 / 5 日**であり、これは #381 / IADR-0174 で**計画 ADR-0022 決定4・決定5 の値へ揃えたもの**である
  （#271 / IADR-0112 の 14 日は FRED の週次公表からの逆算であり、根拠は計画側へ移った）。
  日銀は日次なので 30 日は緩いが、**狭めるのは計画値からの逸脱**であり、休場明け・取得失敗・
  フォールバック中（FRED は最大 12.84 日）で新規建てが止まりやすくなる。**計画を変えずに実装だけ
  厳しくしない。**
- **C# の実装コード**（アダプタ・ファクトリ・フォールバック・可視化）。すべて実装・テスト済みであり、
  本作業は構成に閉じる。XML ドキュメントの是正のみ行う。
- **`ReportService` のコード**（並行作業中のため触らない。本作業は同サービスの helm 構成のみに触れる）。
- **実 API を叩くテストの追加**（IADR-0194 決定「外部依存で CI を落とさない」を踏襲）。

## 判断（本作業で決めたこと）

### 本番 `values.yaml` を `""` → `"boj"` にする

**する。** 理由と反論は IADR-0308 に記す。要点:

- 空既定の根拠は「**実接続には鍵が要る／鍵が無ければどのみち no-op**」であった。
  **日銀は認証不要**であり、この根拠が消える。空のまま残すと、本番を起動した瞬間に
  「外貨建ての判断・記録・報告が黙って供給されない」という**既知の沈黙**を計画に反して温存する。
- CI の描画ゲート（経路B 限定の設定が既定描画へ漏れていないことの検査）に**掛からない**ことを実測で確認する
  （禁止語の列挙に `value: "boj"` は無く、そもそも本値は `values.yaml` 自身が持つ＝「経路B からの漏出」ではない）。
  **ゲートを緩めない。**

### 経路B `values-local.yaml` を `"fred"` → `"boj"` にする

**する**（計画準拠。迷いは無い）。FRED の鍵は残すため、**日銀第一・FRED フォールバック**の
2 段構成になる（ADR-0022 決定2 の冗長化を初めて実際に満たす）。

### FRED のフォールバックを残す

**残す。** `FxRateSourceFactory.Create` の `case Boj` は、`options.Fred.ApiKey` が空でなければ
`FallbackFxRateSource` を組み、空なら**日銀単独＋「フォールバックがありません」警告**へ倒す（実装で確認済み）。
`ResolveProvider` は `boj` 指定なら鍵の有無に関わらず `boj` を申告する（日銀は認証不要のため）。
したがって:

- 本番（鍵なし）: `boj` 単独。自己申告は `boj`。冗長化が無い旨の警告が起動時に出る。
- 経路B（鍵あり）: `boj` → `fred` の順位つきフォールバック。自己申告は `boj`。

## 受け入れ基準（Issue #686 との対応）

- [ ] 3 サービス（`report` / `risk-management` / `trade-decision`）すべてで `Fx__Provider` が日銀を指す
      （`values.yaml`・`values-local.yaml` の**両方**・計 6 箇所）
- [ ] 日銀が落ちたときに FRED へ切り替わり `FxRateSourceFellBack` が発行されること
      （`FallbackFxRateSourceTests` で担保済み。本作業は**構成が実際にその 2 段を組む**ことを描画で確認する）
- [ ] 否定形: 第一が落ちても外貨建ての判断・記録・報告が**通ってしまわない**こと
      （レート未解決は従来どおり見送り・未供給。`FxOptions` の 3 段縮退は不変）
- [ ] 起点 ID コメント付き

## 検証（母集合の引き方）

**誤りの側から引く**（規則 1）。`fred` を含む行ではなく、**`Fx__Provider` を持つ行すべて**と、
**`FRED_API_KEY` / `fred-api-key` を「必須前提」と述べている行すべて**をパスから引く（規則 3・4）。

```bash
git grep -n 'Fx__Provider' -- deploy/ .github/          # 設定点そのもの（6 + ゲート）
git grep -n 'FRED_API_KEY' -- . | grep -v '^\.ai-context/'   # 「必須前提」の記述面
git grep -rn 'Fx__Provider=fred\|Fx:Provider' -- docs/ deploy/ scripts/
```

除外とその理由:

| 除外 | 理由 |
| --- | --- |
| `.ai-context/`（作業仕様書・実装ADR） | **point-in-time の凍結記録**。当時の記述を後から書き換えない |
| `CHANGELOG.md` | 生成物。コミット件名は書き換えない |
| `COLLECTION_FRED_API_KEY` / `Collection__Source__Fred__*` | **情報収集の FRED であり為替とは別枠**（`FxOptions` のコメントが明記）。本作業の対象ではない |
| `docker-compose.yml` | 収集側の FRED 鍵のみで `Fx__Provider` を持たない（実測で確認） |
| `scripts/k8s-local-deploy.test.sh` | `FRED_API_KEY` を**秘匿値の保存テストの題材**として使うだけで、為替の必須性を述べていない |

コマンド:

```bash
dotnet build backend/backend.slnx
dotnet test backend/Shared/AiStockTrading.Shared.Infrastructure.Tests/AiStockTrading.Shared.Infrastructure.Tests.csproj
dotnet format backend/backend.slnx --verify-no-changes
helm lint --strict deploy/helm/ai-stock-trading
helm template ast deploy/helm/ai-stock-trading                                    # 既定（本番）
helm template ast deploy/helm/ai-stock-trading -f deploy/helm/ai-stock-trading/values-local.yaml
node scripts/check-trace-blocks.js
node scripts/check-doc-links.js
node scripts/check-cross-repo-refs.js
node scripts/gen-knowledge-graph.js --check
COMMIT_RANGE=origin/develop..HEAD node scripts/check-adr-index-sync.js
node scripts/check-commit-messages.js
```

**Helm はリストを置換する**ため、`extraEnv` を触る変更では**env が消える事故**が起きる（#279 / IADR-0114 決定4）。
変更前後で**描画された env 名の件数**を数え、PR 本文へ載せる。

## リスク・留意点

- **本番の既定挙動が変わる。** 空（外部へ 1 リクエストも出さない）から、日銀 API への実接続（認証不要・
  1 回/分の自制・6 時間キャッシュ）へ変わる。本番は他の外部連携がすべて空のため、レートを要求する
  経路自体がほぼ動かないが、**「本番既定は外部へ触らない」という chart の姿勢は 1 箇所崩れる**。
  切り戻しは `values.yaml` の 3 行を `""` へ戻すだけである。
- **日銀が落ちたときの縮退は本番では 1 段しかない**（鍵が無いため FRED が積まれない）。
  起動時の警告で可視化されるが、冗長化を求める ADR-0022 決定2 を本番で満たすには
  `ast-secrets/fred-api-key` の投入が要る（運用手順として chart README へ書く）。
