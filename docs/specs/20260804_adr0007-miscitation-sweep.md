---
title: 作業仕様書 — ADR-0007 の誤帰属を是正する（#299）。ガード設定の変更権限は正当なので残し、それ以外を各要求の根拠へ振り分ける
type: work
status: review
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md (取引ガードのソフト設定・禁止銘柄・変更権限)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (統制はリスク管理サービスが強制しAIは上書きできない)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md (段階ゲート)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md (pause/lockout/kill switch の 3 統制)"
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (トレーサビリティ表: FR ごとの関連 ADR)"
related_specs:
  - ./20260804_planning-plan-refs-repair.md
---

# 作業仕様書: ADR-0007 の誤帰属を是正する

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（計画書引用の正確性。**NFR** 相当）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（**新規 IADR も作らない**。引用の是正であり設計判断を伴わない）
- 対象 Issue: [#299](https://github.com/endazon/ai-stock-trading/issues/299)（ADR-0007 の誤引用が全体化）
- 先行 PR: [#371](https://github.com/endazon/ai-stock-trading/pull/371)（実在しないファイル名 20 件の是正。同 issue と重なる 2 ファイルを先行して巻き取り済み）

## 目的・背景

`ADR-0007` は本リポジトリの **154 ファイル・319 箇所**で参照されている（2026-08-04 実測。`develop` の
`1b76ef1` 時点）。issue #299 は、このうち多数が ADR-0007 を「**統制の権限（変更操作は利用者のみ）**」の
根拠として引用しており、実物の ADR-0007 とは無関係であると報告している。

### 重要: #299 の前提は不正確であり、そのまま機械適用してはならない

#299 は「実物の ADR-0007 は取引ガード／信用取引に関する決定であり、**統制の権限ではない**」と書き、
是正方針として「誤引用を `ADR-0003` ＋ FR-10 本文へ、pause/lockout 文脈は `ADR-0009` へ」を挙げている。

**ADR-0007 の §決定 を全文で読むと、次の 1 行が実際に含まれている。**

> - ガード設定の変更は利用者のみが行え、変更履歴を記録する

すなわち「変更は利用者のみ・変更履歴を記録」は **ADR-0007 の決定そのもの**である。ただし対象は
**ガード設定**（商品種別の可否・市場別の有効/無効・取引禁止銘柄・差金決済防止・発注パターンの禁止）に
限定される。#299 の指示どおり一律に ADR-0003 へ倒すと、**正当な引用まで壊し、「壊れた引用」を
「別の誤った引用」に置き換えるだけ**になる。

したがって本作業は #299 の**意図**（誤帰属の解消）に従い、**手段**は下記の対応表へ差し替える。
この差異は #299 へも報告する。

### 是正先の対応表（計画のトレーサビリティ表が根拠）

`02_requirements/01_requirements.md` の要求トレーサビリティ表（実測）を単一の権威とする。

| 引用の対象 | FR | 計画が定める関連 ADR | 本作業の扱い |
| --- | --- | --- | --- |
| ガード設定の内容・**その変更権限・変更履歴** | FR-19 | **ADR-0007**, ADR-0016 | **変更しない（正当）** |
| リスク統制値・kill switch | FR-10 | ADR-0003, ADR-0009, ADR-0016, ADR-0018 | **ADR-0003** |
| pause・日次損失ロックアウト | FR-10 | 同上 | **ADR-0009** |
| 報告書の確定（human-in-the-loop） | FR-06 / FR-07 | **ADR-0003** | **ADR-0003** |
| 段階ゲートの昇格・差し戻し承認 | FR-15 / FR-20 | **ADR-0008** ほか | **ADR-0008** |
| 全体前提条件の変更 | FR-17 | **—（ADR 無し）** | **ADR 参照を外す**（FR-17 本文が根拠） |
| 監視設定・監視銘柄 | FR-12 / FR-13 | **—（ADR 無し）** | **ADR 参照を外す** |
| 通知・Discord Bot | FR-09 / FR-14 | **—（ADR 無し）** | **ADR 参照を外す** |
| 監査台帳 | FR-11 | ADR-0016 決定15 | **ADR 参照を外す**（FR-11 が根拠） |
| Keycloak 認証・認可の基盤 | — | platform ADR-0004 | **ADR-0007 を外す** |

**無い ADR を当てはめない。** FR-13 / FR-17 / FR-09 / FR-14 は計画側に関連 ADR が無いため、
「別の ADR へ張り替える」のではなく **ADR 参照そのものを外して FR 本文へ寄せる**。ここを埋めようとすると
新しい誤帰属を作る。これは #299 の方針（「ADR-0003 ＋ FR-10 本文へ」）が想定していない類型である。

## 対象範囲

`ADR-0007` の参照は **319 箇所・154 ファイル**（2026-08-04 実測。`develop` の `1b76ef1` 時点）ある。
**2 段階に分ける。** 1 つの PR にすると差分が 150 ファイルを超えてレビュー不能になり、かつ本作業は
1 行ずつ文脈を判定する性質のため、レビュアーが追試できる単位に保つ必要があるからである。

| 段階 | 範囲 | 件数 | 本書での扱い |
| --- | --- | --- | --- |
| **第 1 段階（本 PR）** | `backend/` `frontend/` のコード内コメント・テストコメント | 106 → 31 | 完了 |
| 第 2 段階（後続 PR） | `docs/` の `related_ids` / `plan_refs` / 本文 | 210 | 未着手（分類は本書に記載） |

コード側を先にした理由は、**コメントは実装者が編集中に読む一次情報**であり、誤帰属が新しい実装へ
伝播する経路がもっとも短いためである。文書側は参照時に読むもので、伝播は間接的である。

- **第 2 段階の分類**（本書の対応表に基づき、文書の主題で群分けした結果）:

  | 群 | 文書の例 | 是正 |
  | --- | --- | --- |
  | ガード（FR-19） | IADR-0004 / 0006 / 0038 / 0040 / 0132、`functional/FR-19_trading-guard.md`、相場操縦・注文分解の仕様書 | **維持** |
  | 設定ストア混在 | IADR-0010 / 0012、`data/risk-management-aggregates.md`、risk-management-application / worker | **併記** |
  | 前提条件（FR-17） | IADR-0021 / 0063 / 0080、`data/trading-assumptions.md`、SC-01、configuration-assumptions | **ADR 参照を外す** |
  | 報告書（FR-06/07） | IADR-0024 / 0042 / 0071、`data/reports.md`、report-confirmation | **ADR-0003** |
  | 監視銘柄（FR-13） | IADR-0088 / 0090 / 0095、market-monitor-worker、watchlist 系 | **ADR 参照を外す** |
  | 段階ゲート（FR-20） | IADR-0070 / 0081、stage-gate-transitions、stage-gate-discord-bot | **ADR-0008** |
  | kill switch / pause | IADR-0062 / 0075、152_pause-resume、15_discord-bot | **ADR-0003 / ADR-0009** |
  | 認証・s2s | IADR-0051、76_s2s-service-token、foundation-min-port | **platform ADR-0004** |

- **対象外**:
  - **`IADR-0007`**（実装 ADR `IADR-0007_broker-rejection-vs-risk-rejection.md`）。**別名前空間であり
    本作業とは無関係**。17 箇所あり、`ADR-0007` の grep に巻き込まれるため明示的に保護する。
  - **計画リポジトリ（`planning/`）の内容**。参照する側だけを直す。
  - `docs/specs/20260801_impl-handoff-kit-sync.md` など **PR 単位の point-in-time 記録**の本文。
    当時の状態を正しく記録しているため後から書き換えない（[#371](https://github.com/endazon/ai-stock-trading/pull/371) と同じ扱い）。
  - `check-doc-links.js` の改修と、検査から漏れている `plan_refs` 21 件の書式統一
    （[20260804_planning-plan-refs-repair.md](./20260804_planning-plan-refs-repair.md) に記録済み。検査器の設計判断を伴う）。

## 設計

### 判定は「その行が何の権限・何の内容を述べているか」で行う

ファイル単位・サービス単位では決まらない。同一ファイル内で正当な引用と誤引用が混在する。

### 併記が正しい類型がある（置換でも削除でもない）

RiskManagementService は **ガード設定（FR-19）・統制上限（FR-10）・段階設定（FR-20）・kill switch を
1 つの設定ストアと 1 本の変更履歴で扱う**。したがって「変更は利用者のみ・変更履歴を記録する」という
規律が 4 系統に同時に掛かっており、ADR-0007 が決めているのはそのうち**ガード設定の分だけ**である。

該当箇所（`RiskSettingsService` / `SettingsChangeEntry` / `ISettingsChangeLog` / `EfSettingsChangeLog` /
`PersistenceRows` / `RiskControlEndpoints` の設定節）は、**ADR-0007 を残したうえで ADR-0003・ADR-0008 を
併記する**。ADR-0007 だけを残すと上限・段階の根拠が欠け、ADR-0007 を消すとガード設定の根拠が欠ける。

### 「監査性（ADR-0007）」は対象で分かれる

`KillSwitchService` / `PauseService` の「監査性（ADR-0007）: アクターと理由のない変更は受け付けない」は、
**kill switch と pause の監査**であってガード設定の監査ではない。前者は ADR-0003、後者は ADR-0009 とし、
記録先が共通の設定変更履歴であることは FR-11 で説明する。

### Keycloak 認証基盤の引用は落とす

`Program.cs` の「`ADR-0004（platform）, ADR-0007`: Keycloak 認証」は、**認証・認可の基盤**についての記述で
ある。ADR-0007 は認証方式を何も決めていない。platform ADR-0004 のみを残す。

## 受け入れ基準（第 1 段階＝コード側）

- [x] コード側の `ADR-0007` 残存参照が、**すべてガード設定（FR-19）の文脈か正しい併記**である（106 → 31）。
- [x] ガード設定を扱う箇所から `ADR-0007` が失われていない（過剰削除をしていない）。
      `BannedSymbol` / `TradingGuardSettings` / `ProductType` / `PositionEffect` /
      `ManipulationPatternAnalyzer` / `TradingDefaults` はいずれも維持した。
- [x] 計画に関連 ADR が無い要求（FR-13 / FR-17）の箇所に、**ADR を当てはめていない**（FR 参照へ寄せた）。
- [x] 併記が必要な箇所（設定ストア・変更履歴・認可ポリシー）で ADR-0003 / ADR-0007 / ADR-0008 が揃っている。
- [x] `IADR-0007` を 1 箇所も変更していない。
- [x] **変更がコメント行のみ**である（コメント以外の追加行が 0 件であることを機械的に確認）。
- [x] `dotnet build backend/backend.slnx` が警告 0・エラー 0。
- [ ] `dotnet test` が緑（CI の `build-and-test` ジョブで確認する）。

### 第 2 段階（docs 側）の受け入れ基準

- [ ] `docs/` の `ADR-0007` 残存参照が、すべてガード設定の文脈か正しい併記である。
- [ ] `node scripts/check-doc-links.js` が破損 0 件。
- [ ] point-in-time 記録（`20260801_impl-handoff-kit-sync.md` 等）を書き換えていない。

## テスト方針

コメント・文書の変更であり、振る舞いは変わらない。検証は次で行う。

| 検証 | 期待 |
| --- | --- |
| `ADR-0007` の残存箇所を全件目視し、ガード設定文脈かを確認 | 全件がガード設定 |
| `dotnet build backend/backend.slnx` | 警告ゼロ |
| `dotnet test backend/backend.slnx` | 緑（コメント変更のため挙動不変） |
| `node scripts/check-doc-links.js` | 破損 0 件 |
| `git diff` にコード行（コメント以外）の変更が無いこと | 変更なし |

## 計画書との差異

- 差異: なし（計画は読むだけ）。ただし **issue #299 の記載内容とは方針が異なる**（上記）。#299 へ報告する。

## 未決事項

なし。
