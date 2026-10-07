---
title: IADR-0511 空売り実弾解禁の verdict は取引ガードの商品種別設定の改訂番号（設定ストアが進める単調増加カウンタ）を写し取り、発行後に番号が変われば無効、番号が無ければ無効へ倒す
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-19, UC-06, ADR-0034, ADR-0016, ADR-0011, IADR-0281, IADR-0132, IADR-0161, IADR-0012]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0034_short-sell-inclusion-observed-and-strategy-change.md (決定 5 の表の契機 2)
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定 14 の 2026-08-07 確定・verdict の形式)
related_specs:
  - ../specs/20261008_1220_shortsell-verdict-product-types-revision.md
---

# IADR-0511: 空売り実弾解禁の verdict は商品種別設定の改訂番号を写し取り、変われば無効・無ければ無効へ倒す（#1220）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: claude（[#1220](https://github.com/endazon/ai-stock-trading/issues/1220) の受け入れ基準に沿って起案）

## 起点・関連

- 関連する計画書 ID: FR-20（段階ゲート）、FR-19（取引ガードの商品種別）、UC-06、
  ADR-0034 決定 5（verdict を無効化する「② 戦略の変更」の 2 契機。本 IADR は**契機 2** を実装する）、
  ADR-0016 決定 14（2026-08-07 確定・verdict の形式）、ADR-0011（モデルのピン留め＝契機 1 の根拠）
- 関連する実装仕様書: `.ai-context/specs/20261008_1220_shortsell-verdict-product-types-revision.md`
- 実装 issue: #1220（起票元 #1204・第 4 回全体監査 B-18）。関連 #388
- 先行 IADR: IADR-0281（verdict を段階ゲートの承認台帳へ相乗り・3 契機の判定。本 IADR は判定に 1 契機を足す）、
  IADR-0132（空売りの有効・無効の単一情報源は `EnabledProductTypes`）、IADR-0012（設定ストアの単一行 JSON と楽観排他）、
  IADR-0161（設定行へキーを足すときは既存行を書き換えず、読み取り時に既定を与える）

## コンテキスト

ADR-0034 決定 5 は、ADR-0016 決定 14 の無効化契機「② 戦略の変更」を次の 2 契機と定めた。

| # | 契機 | 実装の状況（`origin/develop` facba5c1） |
| --- | --- | --- |
| 1 | 取引判断のピン留めモデルの変更 | 成立している。Stage 0 の戦略 ID（`Stage0StrategyIdentity.StrategyIdFor(modelId, contentHash)`）がモデルを含み、IADR-0281 決定 3 の戦略 ID の一致で捉える |
| 2 | 取引ガードの商品種別設定（現物 / 信用買い / 空売り の有効・無効）の変更 | **判定に入っていない。** `ShortSellReleasePolicy.Evaluate` は ① 未承認 ② 期限切れ ③ 情報源 ④ 戦略 ID の 4 つだけを見る。空売りを無効化して再度有効化しても verdict は有効のまま |

**プロンプト・方針の変更は契機に含めない**（同決定が明示的に除外。変更の単位が計画側で定義されていない）。

決めるべきは、**verdict の発行後に商品種別設定が変わったこと**を機械が判定する材料の取り方である。
受け入れ基準 1 は「**無効化 → 再有効化を含む**」と明示しており、往復で元の集合に戻る場合も捉えなければならない。

## 検討した選択肢

| 案 | 内容 | 往復（無効化 → 再有効化） | 評価 |
| --- | --- | --- | --- |
| A | 発行時の `EnabledProductTypes` を verdict に写し、評価時の集合と等価比較する | **捉えない** | 往復後の集合は発行時と等しい。**受け入れ基準 1 を満たさない**。変更回数か時刻を併せ持たない限り不十分 |
| B | 設定の最終変更時刻を持ち、verdict の発行時刻より後なら無効 | 捉える | 発行時刻は `IClock`、設定の更新時刻はストア（`DateTimeOffset.UtcNow`）と**時計が別**。同時刻・時計の巻き戻り・試験時計で前後が決まらない。さらに「商品種別の変更時刻」を別に持たないと禁止銘柄の追加でも失効する |
| **C（採用）** | **商品種別の集合が変わる保存のたびに 1 進む改訂番号**を設定ストアが持ち、verdict は発行時の番号を写す。評価時に一致を見る | **捉える**（往復で 2 進む） | 時計に依らず等価比較だけで決まる。商品種別以外の保存では進まない |
| D | 設定の変更履歴（`settings_change_log`）を発行時刻以降で走査し、`Guard` の変更で商品種別が変わったものを探す | 捉える | 履歴の前後値は表示用の `ToString()` 文字列であり、集合を機械的に復元できない。時計の問題も B と同じ |

### 番号をどこに置き、誰が進めるか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| C-1 | `RiskManagementSettings` のプロパティにし、`RiskSettingsService.UpdateGuard` が `with` で +1 する | 番号が HTTP の設定応答に載り、`with` で運ばれる。**呼び出し側が番号を作れる**（古い設定値を保存すれば巻き戻る）。`UpdateGuard` 以外の保存経路が増えると数え漏れる |
| **C-2（採用）** | **設定行の JSON（`SettingsDto.ProductTypesRevision`）に置き、ストアの `Save` が保存の直前の行と比べて進める**。読み出しは `IRiskSettingsStore.GetProductTypesRevision()` | ドメインの設定・API の形は変わらない。**呼び出し側は番号を書けない**。保存がストアを通る限り経路が増えても数え漏れない（関門を全経路の下流に置く。`UpdateStage` の allow-list と同じ論法） |
| C-3 | 設定テーブルに列を足す | 単一行 JSON の設定にキー以外の列を足す前例が無く、マイグレーションが増えるだけで C-2 に対する利点が無い |

## 決定

**決定 1: 「商品種別設定の変更」は改訂番号で判定する（案 C-2）。**
純関数 `ProductTypeSettingsRevision.Next(before, after, current)` は集合として比べ（`SetEquals`。順序・インスタンスに依らない）、
違えば `current + 1`、同じなら `current` を返す。`InMemoryRiskSettingsStore` と `EfRiskSettingsStore` の `Save` がこれを呼んで番号を進める
（EF は保存の直前に読んだ行と比べる。行が無ければ既定値と 0 から）。`SimulatorProfileRiskSettingsStore` は素通し。
番号は設定行の JSON の `productTypesRevision` に同居し、**ドメインの `RiskManagementSettings` には載せない**。
楽観排他（IADR-0012）が保存ごと止めるため、競合時に番号の加算だけが失われることもない。

**決定 2: 番号を持たない旧い設定行は 0 と読む。**
`RiskSettingsSerialization.ReadProductTypesRevision` はキーの無い行に 0 を与える。マイグレーションで既存の設定行を書き換えない（IADR-0161 決定 2 と同じ規律）。
**これで旧い verdict が有効に見えることは無い** —— 本変更より前に発行された verdict は番号を持たず（決定 3）、判定は番号の一致を見る前に無効へ倒す。
本変更の後に発行した verdict と、本変更の後の商品種別の変更の数え方は 0 起点で一貫する。

**決定 3: verdict は発行時の番号を写し取り、台帳に列を足す。**
`ShortSellReleaseAttestation` / `ShortSellReleaseVerdict` に `long? ProductTypesRevision` を足し、
`StageGateService.RecordShortSellReleaseVerdict` が**サーバ側で**現在の番号を写し取る（情報源・戦略 ID と同じ。自己申告にしない）。
台帳 `stage_transitions` に列 `ShortSellReleaseProductTypesRevision`（`bigint` nullable）を足す（マイグレーション `20261007234025_AddShortSellReleaseProductTypesRevision`）。
段階遷移の行は null。**`EfStageGateStore` は番号の列だけが null の行を添付ごと落とさない**（落とすと `Missing` と読まれ、理由が読めなくなる）。

**決定 4: 判定は ⑤ として足し、状態値を 2 つ末尾へ足す。**
`ShortSellReleasePolicy.Evaluate(verdict, currentSourceFingerprint, currentStrategyId, currentProductTypesRevision, now)`。
判定順は ① 未承認 ② 期限切れ ③ 情報源 ④ 戦略 ID ⑤ 商品種別設定。
⑤ は verdict 側か現在側の番号が無ければ `ProductTypesUnknown = 6`（**fail-closed。④ の空の戦略 ID と同じ規律**）、
番号が違えば `ProductTypesChanged = 5`。序数は HTTP（`GET /risk-controls/stage-gate`）で往来するため末尾へ足し、既存の 0〜4 は動かさない。
`StageReleaseContext` に `CurrentProductTypesRevision` を**既定値なし**で足す（IADR-0281 決定 4。渡し忘れをコンパイルで止める）。
段階ゲート現況（`ShortSellReleaseState`）に `CurrentProductTypesRevision` を足し、発行時の番号（`Verdict.ProductTypesRevision`）と並べて読めるようにする。

**決定 5: プロンプト・方針は判定の入力に入れない。** `Evaluate` の引数はちょうど 5 つで、構造テスト（T-20-9）が固定する。
商品種別以外の設定（禁止銘柄・市場・同日再エントリー・上限・段階・最小取引件数・発注先）の変更は番号を進めない。

## 結果

- 良い影響:
  - ADR-0034 決定 5 の 2 契機がどちらも機械判定になった。**空売りの無効化 → 再有効化の往復も失効させる**。
  - 番号はストアだけが進めるため、設定変更の経路が増えても数え漏れない。呼び出し側が番号を巻き戻す経路が無い。
  - 失効の理由（`ProductTypesChanged` / `ProductTypesUnknown`）が段階ゲート現況から読める。
- 悪い影響・トレードオフ:
  - 信用買いの有効・無効の変更でも空売りの verdict は失効する（ADR-0034 決定 5 の表が 3 種すべてを契機に挙げているため。意図どおり）。
  - 同じ集合を再送しただけでは失効しないが、**一度でも集合を変えれば元に戻しても再発行が要る**（受け入れ基準 1 の要求そのもの）。

### デプロイ時の影響（ライブ PoC）

- 🔴 **本変更より前に発行された verdict は、デプロイ直後に `ProductTypesUnknown`（無効）になる。** 台帳の新しい列は null のまま
  （マイグレーションは列を足すだけでデータを書き換えない）であり、判定は「変わっていない」と読まない。
  解禁を続けるには利用者が verdict を再発行する（`POST /risk-controls/stage-gate/transition` に `approval=1`）。
- **ただし発注の挙動は変わらない。** 発注審査は今日まだ `StageReleaseContext` を受け取っておらず（IADR-0281 決定 6）、
  Stage 3 の空売りは verdict の状態に関わらず常に拒否である。変わるのは段階ゲート現況（SC-03・Discord の現況表示）の verdict の状態だけ。
- ライブのデータは書き換えない。

## 残余リスク

- **本変更を含まない版へ切り戻し、その版で設定を保存してから再び本変更の版へ戻すと、設定行の番号のキーが落ちて 0 に戻る。**
  切り戻しの前に発行した verdict の番号と偶然一致すると、切り戻し中の商品種別の変更を見落とし得る。
  切り戻し中に発行された verdict は番号を持たず無効へ倒れるため、見落としは「切り戻しの前に番号つきで発行した verdict」に限られ、
  30 日の有効期限が上限になる。切り戻しは運用上の例外であり、ここで機械の対策は置かない（切り戻した場合は verdict を再発行する）。
- 設定ストアの外（DB の手編集）で商品種別を変えると番号は進まない。設定の変更は利用者の API に限る（ADR-0007）という前提に依る。

## 関連

- Supersedes: なし
- Superseded by: なし
- IADR-0281 へ本 IADR を指す追記を置いた（［2026-10-08 追記 / #1220］）。
