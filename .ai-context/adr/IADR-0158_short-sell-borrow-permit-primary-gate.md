---
title: IADR-0158 空売りの一次ゲートは借株可否（IsShortPermit）とし、借株料 20% の閾値は「発火しない既知の統制」として残置する。料率は単位が確定するまで費用計算へ接続しない
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-15, FR-17, UC-06, ADR-0016, ADR-0019, IADR-0131, IADR-0134, IADR-0144]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0158: 空売りの一次ゲートは借株可否（`IsShortPermit`）とし、20% の借株料閾値は「発火しない既知の統制」として残置する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-07
- 決定者: endazon（利用者裁定 2026-08-06 = ADR-0016 決定3 の改訂）/ Claude Code（実装への写像）

## 起点・関連

- 計画書 ID: **ADR-0016（計画リポ） 決定3（2026-08-06 改訂）**（本 IADR の直接の起点。環流 endazon/project-planning#204）／
  同 決定10（拒否理由 9 種・すべてクラス A）／同 決定14（Stage 1 で検証できない統制）／
  ADR-0019（計画リポ） 決定1 項目3（実測の出所）／**FR-10**・UC-06・FR-15 / FR-17（費用計算＝接続しない側）
- 対象 Issue: [#417](https://github.com/endazon/ai-stock-trading/issues/417)（由来: [#329](https://github.com/endazon/ai-stock-trading/issues/329) の統制が実測で発火しないと判明したための追随）
- 作業仕様書: [20260807_417_short-sell-borrow-permit-gate](../specs/20260807_417_short-sell-borrow-permit-gate.md)
- 機能 / テスト仕様書: [FR-10 機能仕様書](../../docs/functional/FR-10_risk-controls.md)・[FR-10 テスト仕様書](../../docs/tests/FR-10_risk-controls-tests.md)

### 既存 IADR との関係（改訂か後継か）

| IADR | 関係 | 内容 |
| --- | --- | --- |
| **[IADR-0131](IADR-0131_short-selling-controls-fail-closed.md)** | **部分改訂**（本 IADR が改める範囲は 1 点のみ） | 決定2 の「借株料が `null`・`BorrowAvailable == false`・文脈が `null` はいずれも `BorrowUnavailable`」という**フェイルクローズの構造は有効なまま**である。改めるのは、**どれが一次ゲートか**という位置づけと、その入力の**名前と供給元**（`BorrowAvailable` という一般名 → `ShortPermit`＝moomoo `IsShortPermit`）である。決定1（`Side` × `PositionEffect` での識別）・決定4（判定できないものは通さない）・決定5（比率 50% の文字どおりの実装）・決定6（クラス分類の単一情報源）は**すべて不変**である |
| **[IADR-0144](IADR-0144_moomoo-short-selling-poc-outcomes.md) 決定4** | **後継**（同決定を実装へ落とす。内容は覆さない） | 同決定は PoC 実測から「`IsShortPermit=False` を拒否の一次条件とする／20% 閾値は実装するが発火しない見込みであり、落とさず記録して**計画へ環流する**／`1.5` の単位が確定するまで費用計算へ流さない」と述べた。**その環流の裁定が下りた**（ADR-0016 決定3 改訂）ため、本 IADR が**計画の裏付けを得た版**として実装を確定する。IADR-0144 の他の決定（1・2・3・5・6）は不変である |
| [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) | 前提 | 拒否理由 9 種と序数の安定性。**本 IADR は拒否理由を 1 つも増やさない**ため、同 IADR の写経（`RejectionReason.ShortSellReasons`）は変更しない |

## コンテキストと課題

ADR-0016 決定3 は当初「借りにくい銘柄ほど借株料が高く、踏み上げリスクも高いため、**コストと危険度を
同じ閾値（年率 20%）で弾ける**」という前提に立っていた。**この前提は実測で成り立たなかった。**

| 実測（ADR-0019 PoC 項目3・実弾の信用口座・8 銘柄・1 時点） | 値 |
| --- | --- |
| `ShortFeeRate`（AAPL / GME / RIOT） | **いずれも 1.5**（借株在庫 `ShortPoolRemain` は 26,452,338 / 1,898,200 / 1,201,180 と 20 倍以上開く） |
| `IsShortPermit=False` の銘柄（AMC・SPCE） | `ShortPoolRemain=0` / `ImShortRatio=100` |

**一律料率であれば 20% の閾値は永久に超えず、その統制は何も弾かない。** 一方で API は
`IsShortPermit` によって銘柄を明確に区別している。これは本リポジトリが繰り返し潰してきた
**「実装したが効いていない」**の一例であり、`docs/blocked-tasks.md` の「実装済みだが発動しない機能」に
登録されていた。2026-08-06 の利用者裁定が是正を定めたため、実装を追随させる。

決めるべきことは 3 つある。

1. 一次ゲートを何にし、**どの拒否理由へ写像するか**（新しいコードを足すのか、既存へ畳むのか）。
2. 発火しない 20% 閾値を**落とすのか残すのか**。残すなら「残っていること」をどう担保するのか。
3. 単位が未確定の `ShortFeeRate` を**どこまで実装へ入れるか**。

## 検討した選択肢

### 論点 A: `IsShortPermit=False` の拒否理由

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | 新しい拒否理由（例 `ShortNotPermitted`）を新設する | **計画が明示的に禁じた**（決定3 改訂「新しいコードは追加しない」）。9 種の写経（IADR-0134）と計画適合検査も同時に動かすことになり、増やす利得が無い |
| **A-2** | **既存の `BorrowUnavailable`（クラス A）へ写像する** | `BorrowUnavailable` は「都度の借株需給による locate 失敗」を表し、`IsShortPermit=False`（借株在庫 0・Reg SHO 由来の制限）は**まさにその事象**である。監査ログ（FR-11）の理由が実態と食い違わない |
| A-3 | `BannedSymbol`（クラス C）で表現する | **禁止**。市況由来の事象を「AI が禁止事項を犯そうとした件数」へ混入させると段階昇格ゲート（FR-20）が機能しなくなる（決定10 が $5 未満の除外について明示的に禁じた誤りと同型） |

### 論点 B: 20% 閾値の扱い

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 発火しないので削除する（`BorrowCostExceeded` も削除） | **料率が銘柄別になった日に無防備になる。** 計画は残置を明示的に指示している |
| B-2 | 残すが、何も書かない | 「どうせ発火しないから」と将来削除される。**「発火しない」ことと「無い」ことの区別が失われる** |
| **B-3** | **残置し、「発火しない既知の統制」であることをコード・テスト・文書の 3 か所へ書く** | 削除は**テストが赤くする**（本 IADR 決定2）。読んだ人が意図を復元できる |

### 論点 C: 単位未確定の `ShortFeeRate`

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | 実測値 `1.5` をそのまま年率（`BorrowRateAnnual`）へ写像する | **上限 0.20 を 7.5 倍超過して全銘柄が拒否される。** 「一律だから発火しない」の正反対 |
| C-2 | `1.5` を「1.5%」と解釈して 0.015 へ変換する | 解釈は当て推量である。**外れれば統制が 100 倍緩む側**（費用計算へ入れば採算判定も 100 倍ずれる） |
| **C-3** | **写像そのものを行わない**（供給を書かない）。単位が確定するまで `BorrowRateAnnual` は `null` のまま | フェイルクローズにより安全側。**反転の危険をテストで実行可能な形に残す** |

## 決定

### 決定 1: 一次ゲートは借株可否（`ShortPermit`）とし、`BorrowUnavailable`（クラス A）へ写像する（案 A-2）

`ShortSellOrderContext.BorrowAvailable`（一般名「locate が成立したか」）を **`ShortPermit`** へ改め、
**供給元を moomoo `TrdGetMarginRatio.IsShortPermit` 一本に名指しする**。既定は `false`（借株不可・安全側）。

```csharp
if (!context.ShortPermit)                                  // 一次ゲート（ADR-0016 決定3 改訂）
    reasons.Add(RejectionReason.BorrowUnavailable);
else if (context.BorrowRateAnnual is null)                 // 決定3 の未改訂部分（照会不可なら空売りしない）
    reasons.Add(RejectionReason.BorrowUnavailable);
else if (context.BorrowRateAnnual > limits.BorrowRateCapAnnual)  // 二次（残置・発火しない既知の統制）
    reasons.Add(RejectionReason.BorrowCostExceeded);
```

- **名前を変えることが本決定の実質である。** 一般名のままでは、供給を書く者が**どの実測フィールドを
  当てるべきか**をコードから読めない。決定3 の改訂は「一次ゲートは `IsShortPermit` である」と
  **名指し**したのだから、実装もその名前で受ける。
- `ShortPermit == false` は「**借株不可**」と「**照会できていない**」の両方を含み、いずれも
  `BorrowUnavailable` である。**両者を区別する新しい理由コードは作らない**（決定3 改訂の禁止事項であり、
  かつ拒否側の帰結は同一である）。
- **一次ゲートが立ったとき二次は評価しない。** 借株できない銘柄に「料率が高い」も併記すると、
  監査ログ（FR-11）の理由が実態より多弁になり、原因（可否 or 料率）の切り分けが濁る。

### 決定 2: 20% 閾値は残置し、「発火しない既知の統制」であることを 3 か所へ書く（案 B-3）

**「発火しない」ことと「無い」ことは別である。** 残置は次の 3 経路で担保する。

| 経路 | 何が守るか |
| --- | --- |
| コード | `ShortSellEvaluator` の二次分岐のコメント（発火しない見込み・**落とすと料率が銘柄別になったとき無防備**） |
| テスト | **T-10-212**（上限超の料率で `BorrowCostExceeded` が立つ＝残置の証明）・既存の境界テーブル・**T-10-213**（実測値をそのまま年率にすると全件拒否＝単位の反転） |
| 計画適合検査 | `PlanRiskDefaults` が `ShortSellingLimits` のメンバ列（`BorrowRateCapAnnual` を含む）を写経しており、**削除すると別方向からも赤くなる** |

**閾値判定を削除する変異は、上記により 3 件のテストが落ちる**（実施済み・作業仕様書と PR 本文に内訳）。

### 決定 3: `ShortFeeRate` は単位が確定するまで写像も費用接続もしない（案 C-3）

- **`BorrowRateAnnual` は「単位が確定した年率（比率）」だけを受ける契約**とし、XML doc に明記する。
- **費用計算（FR-17 の `CostCalculator` / `TradingAssumptions`、FR-15 の `BacktestCostModel`）へ接続しない。**
  接続を**構造で検知する否定形テスト**（公開面に `Borrow` / `ShortFee` を含む名前が生えたら赤くなる）を置く
  ——値の検証では「まだ接続していないこと」を守れないためである。
- **反転の危険をテストに残す**（T-10-213）。`1.5` を年率と読めば**全銘柄が拒否**され、`1.5%`（0.015）と
  読めば**何も弾かない**。同じ値が単位の読み方ひとつで正反対に振れることを実行可能な形で記録する。

### 決定 4: 本 PR では借株照会の**供給元**を実装しない（範囲の明示）

`TrdGetMarginRatio` の実弾ヘッダ照会・キャッシュ・レート制限（**30 秒あたり 10 回。失敗した照会も枠を
消費する**）の実装は #331 / #342 系の範囲であり、本 IADR は**判定側の規則**だけを移す。

**帰結として、現状は空売りが 1 件も通らない状態が続く**（`ShortSellOrderContext` の供給元が無いため
文脈は `null`＝`BorrowUnavailable`）。これは IADR-0131 決定2 の時点から変わらない既知の状態であり、
`docs/blocked-tasks.md` の「実装済みだが発動しない機能」に登録して可視化する。

## 理由

- **決定 1**: 統制は「危険な銘柄を弾く」ために置く。実測が「弾いているのは可否である」と示した以上、
  一次ゲートを可否へ移さなければ**統制は名目だけになる**。拒否理由を増やさないのは、計画の明示的な
  指示であることに加え、`BorrowUnavailable` の定義（都度の借株需給による locate 失敗）が
  `IsShortPermit=False` の実態と**そのまま一致する**からである。
- **決定 2**: 統制の削除は、削除した瞬間には何も壊れない（発火していないため緑のまま）。だからこそ
  **テストで残置を固定する**必要がある。IADR-0156 決定4（「未採用であることをテストで固定する」）と
  同じ形であり、本リポジトリの既定の作法である。
- **決定 3**: 単位の取り違えは**片方向に危険ではない**——大きく読めば全件拒否（機会損失）、小さく読めば
  費用の過小評価（採算判定の破綻）。どちらへ倒れても計画の意図から外れるため、**当て推量をしない**。

## 影響

- 肯定的:
  - **「実装したが効いていない」統制が、実測に基づく一次ゲートへ置き換わる**。借株不可の銘柄は
    料率がいくつであっても空売りできない。
  - 一次ゲートの入力が**実測フィールド名で名指し**され、供給を書く者が別のフィールドを当てられない。
  - 20% 閾値の残置が**テストと計画適合検査の 2 系統**で守られ、「どうせ発火しないから」と消せない。
  - 借株料の費用接続が**構造で塞がれた**（単位の裁定前に黙って繋がる経路が無い）。
- 制約 / 残余リスク:
  - **一次ゲートを実地で観測できない。** 供給元が無いうえ、`BorrowRateAnnual` も単位未確定で供給できず、
    フェイルクローズにより空売りは全件拒否のままである。**本 PR のテストは規則を固定するが、
    ブローカー照会との疎通は一度も検証していない。**
  - **実測は 8 銘柄・1 時点である。** 料率の一律性が再測で覆れば、一次ゲートの構成を見直す
    （ADR-0016 決定3 改訂が明記）。
  - **`ShortFeeRate` の単位が確定しない限り、決定3 改訂の「20% 閾値は永久に超えない」という記述自体が
    検証できない。** `1.5` が比率なら閾値は**全銘柄で発火**し、記述と正反対になる（計画へ環流した）。
  - `ShortPermit` は `bool` であり、「借株不可」と「照会できていない」を**区別しない**。拒否側の帰結が
    同一であるため区別を実装しなかったが、監査ログで両者を切り分けたい要求が出れば足りなくなる。
  - **ADR-0016 決定4 の改訂（強制買戻しの事後推定）には追随していない**（別 issue の範囲）。
    `BuyInBanned` の供給元は依然として無い。

## 関連

- Supersedes: なし（[IADR-0131](IADR-0131_short-selling-controls-fail-closed.md) 決定2 の**一次ゲートの位置づけと入力名**のみを改める部分改訂。
  同 IADR の他の決定は有効。[IADR-0144](IADR-0144_moomoo-short-selling-poc-outcomes.md) 決定4 の**後継**＝計画の裁定を得て実装を確定した版）
- Superseded by: なし
- 環流: feedback/20260807_adr0016-shortfeerate-unit-and-borrow-supply.md（環流記録）
  （`ShortFeeRate` の単位の確定要請・「発火しない」という記述の前提・供給が無い間は一次ゲートを観測できないこと）
