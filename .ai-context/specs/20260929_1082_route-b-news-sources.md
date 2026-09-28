---
title: 経路 B で情報収集のニュース源（finnhub-news・google-news）を有効にする（#1082）
type: spec
status: accepted
related_ids: [FR-01, FR-04, FR-08, ADR-0020, ADR-0043, ADR-0031, ADR-0004, IADR-0435, IADR-0434, IADR-0437, IADR-0220, IADR-0064, IADR-0453]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md (決定 2・3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 1・2 (a)(b))
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md
---

# 仕様書: 経路 B で情報収集のニュース源（finnhub-news・google-news）を有効にする

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（情報収集）。関連 FR-04（AI 判断の材料）・FR-08（KB 保存）
- ユースケース（UC）: なし（配備設定の変更）
- 画面（SC）: なし
- 関連 ADR: ADR-0020 決定 2（ニュース系は Finnhub 企業ニュースと Google News RSS の 2 系統を必須に置き、いずれか 1 つ以上が生きていること）・
  決定 3（ニュース系の全滅で新規建てを止める）、ADR-0043 決定 1（日次上限は未実測）・決定 2 (a)(b)（同一鍵の合計 ≤ 60 回/分・1 巡回を巡回間隔に収める）。
  実装: IADR-0435（Finnhub の対象銘柄は監視銘柄に追随・現在値と企業ニュースで 1 つのバケット）、IADR-0434（同一鍵の予算表）、IADR-0437（分次で説明できない 429＝4301）、
  IADR-0220（未構成は欠測に数えない）
- 計画書の確認: 隣接クローン `../project-planning`（`aa068ac`）で `02_datasource-candidates.md`「ニュース系は … いずれか 1 つ以上が生きていること」・ADR-0020 決定 2 を読んだ
- 起点 issue: #1082（#1078 から切り出し。#1035 のやること 3 を引き継ぐ）

## 目的・背景

経路 B（`deploy/helm/ai-stock-trading/values-local.yaml`）の `Collection__Source__Provider` は `"finnhub,sec-edgar,fred"` で、ニュース源を含まない。
稼働 PoC（#1078）で判断の rationale はどの銘柄も「好材料ニュース等の情報が提供されていない」と書いた。原因の 1 つがこの未構成である
（ほかの原因〔判断側の KB 検索が NoOp・Scope 未送出・本文未取込〕は #1078 の別の子 issue で扱う）。

## 対象範囲

- 対象:
  - `values-local.yaml`: Provider に `finnhub-news`・`google-news` を加え、`Collection__Source__GoogleNews__Queries__0` に固定のクエリを 1 つ入れる。注記を更新する。
  - `.github/workflows/helm.yml`: 経路 B の有効化の検査（`Assert values-local activates route-B features`）に、2 つのニュース源の列挙とクエリが空でないことを足す
    （既に sec-edgar・fred を同じ形で検査している。新しい検査器ではなく既存の検査の項目追加）。
  - `deploy/helm/ai-stock-trading/README.md`: 1 巡回の上限銘柄数（150→75）・日次の要求量・429 の見張り方・google-news の固定クエリ・ニュース系の全滅の縮退を書く。
  - `FinnhubCycleFitTests.cs` のコメント（「経路 B の既定＝現在値だけ」が本変更で誤りになる）。
  - IADR-0453（新規）・IADR 索引。
- 対象外:
  - 本番の `values.yaml`（経路 B のみ。本番既定の Provider は空のまま）。
  - C# のコード（アダプタは既に構成を読む。変更不要）。
  - google-news のクエリを監視銘柄に追随させる仕組み（残余・下記）。
  - KB の重複保存（#1084）。判断側の KB 検索の有効化（#1078 の別の子）。
  - 並行する #1081（C# の InformationCollected・TradeDecision）・#1083（KB 検索）とはファイルを重ねない。

## 設計

### finnhub-news

- 鍵・対象銘柄（監視銘柄に追随。IADR-0435 決定 1）・自制レート（30 回/分。IADR-0435 決定 4 で現在値と共有）をそのまま使う。追加の設定は要らない。
- 予算（ADR-0043 決定 2）:
  - (a) 同一鍵の合計: 情報収集 30 ＋ 市場監視 12 ＋ 市況 5 × 3 ＝ 57 ≤ 60 回/分。**変わらない**（バケットを共有するため）。helm.yml の予算の検査も同じ値で通る。
  - (b) 1 巡回の上限銘柄数: `floor(30 × 300 ÷ (60 × 2))` ＝ 75（以前は 150）。監視銘柄 6 件は収まる。
- 日次: 巡回 300 秒 ＝ 288 巡回/日（収集は市場の開場を問わず回る）。6 銘柄で 6 × 1 × 288 ＝ 1,728 → 6 × 2 × 288 ＝ **3,456 回/日**（約 2 倍）。
  費用統制の間隔延長中はこれより少ない。**日次上限は未実測**（ADR-0043 決定 1）なので `Finnhub__ProvisionalDailyLimit` は空のまま（推測値を入れない）。

### google-news のクエリ

- コードで確認した事実: `GoogleNewsRssSource` は構成の `GoogleNews:Queries`（固定の配列）をそのまま引く。監視銘柄に追随するのは Finnhub 系の 2 ソースだけ
  （`IFinnhubSymbolSet`。`WatchlistFollowingSourceFetcher` は `FinnhubSymbolSelector` だけを更新する）。記事は `Symbol: null`（クエリを銘柄として詐称しない）。
- したがって**市場全般の固定クエリ 1 つ**（`米国株`）にとどめる。理由:
  - 銘柄ごとの固定クエリ（例: `AAPL 株価`）は監視銘柄を変えると古くなり、IADR-0435 が塞いだ「固定値のまま追随しない」形を別のソースで作り直す。
  - 要求は既定の 1 回/分の自制に従う。1 クエリなら 1 巡回 1 要求で、公式保証の無い RSS への負荷を最小にする。
  - 取得は `hl=ja&gl=JP`（コード固定）なので日本語のクエリにする。監視銘柄は米国株である。
- 監視銘柄への追随が無いことは残余として IADR-0453 に記録する。

### ニュース系の全滅による縮退（挙動の変化）

- 以前は 2 ソースとも未構成で、欠測に数えなかった（IADR-0220）。本変更後は、同じ巡回で 2 ソースとも取得できないと ADR-0020 決定 3 の
  「ニュース系の全滅」で新規建てを止める（手仕舞い・損切りは止めない）。計画どおりの挙動であり、README と IADR に書く。

## 母集合（規則 9・10: 誤りの側で走査する）

`git grep` を `:!.ai-context/specs` で次の語に掛けた（2026-09-29、`73a79e33`）。

| 語 | ヒット | 扱い |
| --- | --- | --- |
| `finnhub,sec-edgar,fred` | helm README 188 行 | 更新 |
| `150 銘柄` | `FinnhubCycleFitTests.cs` のコメント、values-local（本変更で書いた「以前は」） | テストのコメントを更新（算術は正しいまま） |
| `未構成` × ニュース | values-local 187-191 | 更新 |
| `finnhub だけ` | helm README 289 行 | 更新 |
| CI の説明「公式情報源が ON」 | helm README | 「ニュース源」を足す |

除外: `.ai-context/specs/`・既存 IADR（凍結記録。IADR-0435 の残余「経路 B は finnhub-news を有効にしていない」は当時の事実として残し、IADR-0453 で解消を記録する）。
`docs/` にヒットは無かった。

## 受け入れ基準

1. `values-local` の描画で `Collection__Source__Provider` が `finnhub-news`・`google-news` を含み、`Collection__Source__GoogleNews__Queries__0` が空でない。
2. 本番既定（`values.yaml`）の描画は変わらない（`helm.yml` の「既定描画は経路 B 有効化を含まない」が緑）。
3. Finnhub の予算 (a) の検査が緑（57 ≤ 60）。
4. helm.yml の全ステップがローカルで緑。変異（2 ソースの片方を外す・クエリを空にする・変更前へ戻す・部分一致の偽物）で赤になる。
5. `InformationCollectionService.Tests` が緑（構成のキー `Collection:Source:GoogleNews:Queries:0` と Provider の束縛は既存の `InformationSourceSelectionTests` が固定している）。
6. `node scripts/scripts.test.js` と文書系の検査が緑。

## テスト方針

- C# のテストは追加しない（コード変更なし。束縛は既存テストで固定済み）。
- 配備の検査は helm.yml の既存ステップへの項目追加で担保する（sec-edgar・fred と同じ形）。

## 計画書との差異

なし（ADR-0020 決定 2 の 2 系統を経路 B でも有効にする。ADR-0043 決定 2 の予算内）。

## 残余

- google-news は監視銘柄に追随しない（IADR-0453 残余）。
- KB の重複保存がニュースで悪化する（#1084）。
- 判断へ届くには #1078 の他の前提（KB 検索の有効化）が要る。本変更だけでは rationale は変わらない。
- 日次上限は未実測のまま。429 は運用ログで見張る（README）。

## 採番

IADR-0453（develop `73a79e33` の最大 IADR-0452 ＋ 1）。並行する #1081 と衝突したら、後からマージする側が改番する。

## 未決事項

なし。
