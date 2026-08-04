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

`ADR-0007` を含む行は本リポジトリに **154 ファイル・319 行**ある（`develop` の `1b76ef1` を
`git grep -n "ADR-0007" 1b76ef1 -- docs backend frontend` で実測。`bin/` `obj/` は除外）。
このうち **17 行は `IADR-0007`**（実装 ADR・別名前空間）にマッチしただけで対象外なので、
**是正対象は 302 行**である（docs 186 行・backend/frontend 116 行）。issue #299 は、このうち多数が ADR-0007 を「**統制の権限（変更操作は利用者のみ）**」の
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

> **計数の訂正（AI レビュー 🟡 指摘）**: 当初この表と PR 本文に第 1 段階の是正前を「106」と書いていたが、
> これは実測値ではなく概算であった。上記の実測手順で数え直すと **116** である。第 2 段階の「172」は
> `HEAD` の `docs/` から `IADR-0007` 行と本作業自身の仕様書 2 本を除いた実数で、`1b76ef1` の 186 行との
> 差 14 行は本作業で追加した 2 本の仕様書が `ADR-0007` に言及している分である。

| 段階 | 範囲 | 件数 | 本書での扱い |
| --- | --- | --- | --- |
| 第 1 段階 | `backend/` `frontend/` のコード内コメント・テストコメント | 116 → 31 | 完了（[PR #376](https://github.com/endazon/ai-stock-trading/pull/376)・マージ済み） |
| 第 2 段階の前半 | `docs/` のうち曖昧さのない 5 群 | 172 → 106 | 完了（[PR #377](https://github.com/endazon/ai-stock-trading/pull/377)） |
| **第 2 段階の後半（本 PR）** | `docs/` `feedback/` `infra/` の残り全件 | 118 → 110（全件が正当） | 完了 |

> **計数の再訂正（第 2 段階の後半で実測し直した）**: 前半 PR は残数を「107」と書いたが、
> `93f4b64` 時点の実測は **`docs/` 106 行**（`IADR-0007` 行と本作業自身の仕様書 2 本を除く）である。
> これに **`feedback/` 7 行**と、下記の**圧縮表記 5 行**を加えた **118 行**が後半の実際の対象だった。
> 前半 PR の内訳表（維持 45 ＋ 併記 21 ＋ 個別判定 35 ＋ 対象外 4 ＝ 105）も、この 118 と
> 突き合わせて下表へ置き換える。**目安ではなく実測値のみを載せる。**

第 2 段階を前後半に分けたのは、後半に残るのが**1 行ずつ文脈判定を要する群**だからである。前半の 5 群
（FR-17 / FR-13 / FR-06・07 / FR-20 / pause・kill switch）は**文書の主題で一意に決まる**ため機械的に
処理でき、レビューも群単位で追試できる。両者を混ぜると、判定の難易度が違うものが同じ差分に並ぶ。

**第 2 段階の後半＝対象 122 行の内訳（`93f4b64` 時点の実測）**:

| 範囲 | 実測 |
| --- | --- |
| `docs/` の裸の `ADR-0007`（`IADR-0007` 行と本作業自身の仕様書 2 本を除く） | 106 |
| `feedback/` の裸の `ADR-0007` | 7 |
| **`infra/`**（前半までの探索範囲に入っていなかった。後述） | 2 |
| **圧縮表記 `ADR-000X/0007`**（`ADR-0007` の grep に掛からない。後述） | 7 |
| **合計** | **122** |

**処理結果**:

| 区分 | 対象 | 実測 |
| --- | --- | --- |
| **維持**（ガード・信用文脈 FR-19。全行を ADR-0007 実物と 1 行ずつ突き合わせ済み） | IADR-0004 / 0006 / 0038 / 0040 / 0132、`functional/FR-19_trading-guard.md`、`tests/FR-19_*`、332_trading-guards、risk-eval-core-fixes、risk-guard-core、order-decomposition、manipulation-detector、portfolio-projection、IADR-0035 / 0018、154_order-lifecycle-telemetry / IADR-0067、188_frontend-guard-edit-ui / IADR-0086、IADR-0042（先行是正の記録）、foundation-min-port / IADR-0051（併記済み）、`feedback/20260804_fr19-guard-scope.md` | **85** |
| **是正**（内訳は下表） | — | **32** |
| **対象外**（point-in-time 記録） | `20260801_impl-handoff-kit-sync.md`（2）・`20260804_plan-feedback-sent-issue-links.md`（2）・`20260802_344_reimplementation-preparation.md`（1・改定された ADR 番号の列挙であり引用ではない） | **5** |

| 是正の種別 | 対象 | 実測 |
| --- | --- | --- |
| **併記**（設定ストア混在・1 画面に複数系統） | IADR-0010 / 0012、`data/risk-management-aggregates.md`、risk-management-application、risk-management-worker、SC-02、106_frontend-risk-settings、IADR-0084、`infra/README.md`、`infra/keycloak/realm-export.json` | 23 |
| **張り替え → ADR-0003** | market-valuation-wiring / IADR-0066（時価評価はガードでなく FR-10 の判定入力）、`data/reports.md` / IADR-0024 / IADR-0071 / 20260710_report-confirmation（報告書の確定）、`ReportEndpoints.cs`（第 1 段階の取りこぼし） | 8 |
| **参照削除**（FR へ寄せる） | 209_watchlist-authoritative-wiring → FR-13・IADR-0088 | 1 |

> **「維持」が 85 行と多いことこそが本作業の結論である。** #299 の前提（ADR-0007 は統制の権限を
> 決めていない）は不正確であり、ガード設定・その変更権限・信用取引に関する引用はいずれも
> ADR-0007 §決定そのものだった。後半で実際に是正を要したのは **32 行**である。
> 残存 113 行（維持 85 ＋ 併記で ADR-0007 を残した 23 ＋ 対象外 5）はすべて正当な引用である。

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

### 探索そのものが 2 つの穴を持っていた（再発防止の知見）

第 2 段階の後半で判明した。**どちらも「件数を数えた手段が対象を取りこぼしていた」**という同型の失敗である。

1. **圧縮表記は `ADR-0007` の grep に掛からない。** `ADR-0003/0007`・`ADR-0001/0003/0007` のように
   ハイフン以降だけを `/` で連ねる表記が 7 箇所あり、**第 1 段階（コード側・マージ済み）の
   `ReportEndpoints.cs` すら取りこぼしていた**（PR #377 の AI レビューが検出）。
   探索は次の 2 本を併用しなければならない。

   ```sh
   grep -rn  "ADR-0007" <path>                                  # 裸の参照
   grep -rnE "ADR-[0-9]{4}(\s*/\s*[0-9]{4})+" <path> | grep -E "/\s*0007"   # 圧縮表記
   ```

   本作業では、見つけた圧縮表記を**すべて展開**した（`ADR-0003 / ADR-0007` の形）。以後この表記を
   新たに作らないこと。**同じ穴は `IADR-0006/0040` 等の実装 ADR 側にも残っている**が、
   別名前空間であり本 issue の対象外である。

2. **探索範囲に `infra/` が入っていなかった。** 第 1・2 段階とも `docs/` `backend/` `frontend/` しか
   数えておらず、`infra/README.md` と `infra/keycloak/realm-export.json` の 2 箇所が未検査のまま
   残っていた。いずれも `trading-owner`（OwnerOnly）の根拠を ADR-0007 単独に帰属させる誤りだった。
   **範囲は「リポジトリ全体から生成物と `planning/` を除く」で取るのが正しい**（`CHANGELOG.md` は
   コミット履歴からの生成物であり、過去の件名を記録しているため対象外）。

### 併記は「対象明示形」で書く

`ADR-0003 / ADR-0007 / ADR-0008` と羅列するだけでは、**どの ADR が何を決めたかが復元できず**、
読んだ人が同じ誤帰属を再生産する。本作業では次の形に統一した。

```
（ガード設定: ADR-0007 / 統制上限・kill switch: ADR-0003 / 段階設定: ADR-0008）
```

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

### 置換は `related_ids` に重複を作る（機械的に検査する）

`ADR-0007` を別の ID へ**置換**すると、置換先が同じリストに既にある場合に**重複**が生まれる。
第 2 段階の前半で実際に 5 ファイルで起きた（AI レビューが 1 件を指摘し、同型を機械検査して残り 4 件を発見）。

| ファイル | 重複した ID |
| --- | --- |
| `adr/IADR-0063_assumptions-versioned-resolution.md` | `FR-17` |
| `adr/IADR-0062_discord-bot-gateway-and-authorization.md` | `ADR-0003` |
| `adr/IADR-0070_stage-gate-persistence-and-approval.md` | `ADR-0008` |
| `specs/20260717_15_discord-bot-authorization-killswitch.md` | `ADR-0003` |
| `specs/20260717_19_assumptions-versioned-read.md` | `FR-17` |

**置換のたびに `related_ids` / `plan_refs` の重複を全件検査する。** 目視では見つからない
（リストが長く、置換先は他の行に離れて存在するため）。同様に、`plan_refs` から ADR-0007 の
エントリを削除するときは、**そのファイルが代替の計画書参照を保持しているか**を削除前に確認する。

### Keycloak 認証基盤の引用は落とす

`Program.cs` の「`ADR-0004（platform）, ADR-0007`: Keycloak 認証」は、**認証・認可の基盤**についての記述で
ある。ADR-0007 は認証方式を何も決めていない。platform ADR-0004 のみを残す。

## 受け入れ基準（第 1 段階＝コード側）

- [x] コード側の `ADR-0007` 残存参照が、**すべてガード設定（FR-19）の文脈か正しい併記**である（116 → 31）。
- [x] ガード設定を扱う箇所から `ADR-0007` が失われていない（過剰削除をしていない）。
      `BannedSymbol` / `TradingGuardSettings` / `ProductType` / `PositionEffect` /
      `ManipulationPatternAnalyzer` / `TradingDefaults` はいずれも維持した。
- [x] 計画に関連 ADR が無い要求（FR-13 / FR-17）の箇所に、**ADR を当てはめていない**（FR 参照へ寄せた）。
- [x] 併記が必要な箇所（設定ストア・変更履歴・認可ポリシー）で ADR-0003 / ADR-0007 / ADR-0008 が揃っている。
- [x] `IADR-0007` を 1 箇所も変更していない。
- [x] **変更がコメント行のみ**である（コメント以外の追加行が 0 件であることを機械的に確認）。
- [x] `dotnet build backend/backend.slnx` が警告 0・エラー 0。
- [x] `dotnet test` が緑（CI の `build-and-test` ジョブが pass・3m10s。ローカルでは未実行）。

### 第 2 段階の前半（本 PR）の受け入れ基準

- [x] FR-17 / FR-13 / FR-06・07 / FR-20 / pause・kill switch の 5 群を是正した（172 → 107）。
- [x] 関連 ADR が無い FR-13 / FR-17 に**別の ADR を当てはめていない**（FR 本文へ寄せた）。
- [x] ガード設定でない対象を指す `plan_refs` の ADR-0007 エントリを削除した（**8 本**）。削除した
      ファイルはいずれも `01_requirements.md` か代替 ADR を既に持ち、参照が失われていない。
- [x] `node scripts/check-doc-links.js` が破損 0 件。
- [x] point-in-time 記録（`20260801_impl-handoff-kit-sync.md` 等）を書き換えていない。
- [x] [IADR-0042](../adr/IADR-0042_report-review-state-machine-and-detail-rendering.md) の
      **先行是正の記録は残した**（「IADR-0024 は ADR-0007 と誤引用していた」の記述）。末尾の
      「別タスクへ切り分け」だけを訂正済みへ更新した。

### 第 2 段階の後半（本 PR）の受け入れ基準

- [x] **残存参照 113 行を全件、行単位で ADR-0007 実物と突き合わせた**。すべてガード設定・信用取引の
      文脈か、正しい併記か、point-in-time 記録である（維持 85・併記で残した 23・対象外 5）。
- [x] 維持すべきガード文脈から `ADR-0007` が**失われていない**（過剰削除ゼロ）。`plan_refs` からの
      ADR-0007 削除は後半では **0 件**（対象 5 ファイルはいずれもガード設定を扱う正当な参照）。
- [x] 関連 ADR が無い FR-13 に**別の ADR を当てはめていない**（209_watchlist は FR-13・IADR-0088 へ寄せた）。
- [x] **圧縮表記 `ADR-000X/0007` を全件（7）検出し処理した**。第 1 段階の取りこぼし
      （`ReportEndpoints.cs`）を含む。残る 1 件は改定 ADR 番号の列挙であり引用ではない。
- [x] **`infra/` を新たに探索範囲へ入れ**、`trading-owner` の OwnerOnly 根拠 2 箇所を併記へ是正した。
- [x] `IADR-0007` を 1 箇所も変更していない。
- [x] `node scripts/check-doc-links.js` が破損 0 件。
- [ ] `dotnet build backend/backend.slnx` が警告 0・エラー 0。**ローカルでは未実行**（本セッションの
      実行環境に .NET SDK が無い）。本 PR の backend への変更は `ReportEndpoints.cs` の**コメント 1 行**
      のみであり、CI の `build-and-test` ジョブの結果をもって確認する。
- [x] point-in-time 記録を書き換えていない。

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

いずれも**本作業（引用の是正）の範囲外**であり、無編集のまま記録する。埋めると別種の変更が
同じ差分に混ざる。

1. **`feedback/20260804_fr19-guard-scope.md` の鮮度**（誤帰属ではなく状態の未反映）。
   同書 106 行は「ADR-0007 に手仕舞いへの適用の記述が無い」として論点 2 の裁定を仰いでいるが、
   計画側 ADR-0007 には **2026-08-04 追補**が入り「取引禁止銘柄リスト＝**全注文（手仕舞いにも適用）**」
   と**選択肢 A で裁定済み**である。frontmatter は `status: open` のまま。同じ「裁定待ち」表記が
   [20260804_332_trading-guards](./20260804_332_trading-guards.md) 未決事項 1・
   [FR-19_trading-guard](../functional/FR-19_trading-guard.md) 未決事項・
   [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md) にも残る。
   **裁定の実装への反映状況の確認を含むため、別 issue で追随すべきである。**
2. **圧縮表記 `ADR-000X/000Y` を禁じる機械検査**。本作業で検出手順は確立したが（上記「再発防止の
   知見」）、CI ゲートの追加は検査器の設計判断を伴うため、`check-doc-links.js` の改修と同じく
   [20260804_planning-plan-refs-repair.md](./20260804_planning-plan-refs-repair.md) 側の判断に委ねる。
