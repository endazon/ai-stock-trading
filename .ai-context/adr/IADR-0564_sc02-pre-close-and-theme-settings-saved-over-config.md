---
title: IADR-0564 閉場前の統制（締め切り N・手仕舞いの有効と M）とテーマの定義を SC-02 から保存する。置き場はリスク管理の設定行、効く値は「保存値があれば保存値・無ければ構成の値」（項目ごと）、保存値と構成の値の食い違いは GET の応答と画面に示す。手仕舞いの構成はリスク管理へ移し、取引判断は効いている値をサイジング文脈で受け取る
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-19, FR-11, SC-02, UC-06, ADR-0070, ADR-0071, ADR-0066, ADR-0009, ADR-0042, IADR-0561, IADR-0562, IADR-0527, IADR-0530, IADR-0161, IADR-0163, IADR-0134, IADR-0532, IADR-0071]
author: claude (Claude Code)
created: 2026-10-21
updated: 2026-10-21
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0070_pre-close-entry-cutoff-and-flatten.md（決定 5・6）
  - planning:projects/ai-stock-trading/07_adr/ADR-0071_theme-concentration-limit.md（決定 2・5・6）
  - planning:projects/ai-stock-trading/05_screens/01_screens.md（SC-02「設定変更の一般則」）
  - planning#808 / planning#809（裁定 2026-10-21）
related_specs:
  - ../specs/20261021_1407_sc02-pre-close-and-theme-settings.md
---

# IADR-0564: SC-02 の閉場前の統制とテーマの上限の置き場・経路・構成との関係（#1407）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-21
- 決定者: 計画 ADR-0070 決定 5・ADR-0071 決定 5（利用者裁定 2026-10-21）が「保存の置き場と経路、構成と保存値のどちらが効くかの関係は、実装が決めて IADR に記録する」と任せた事項の判断。実装は Claude Code。

## 起点・関連

- 起票: [#1407](https://github.com/endazon/ai-stock-trading/issues/1407)。段 A（本 IADR の決定 1〜5。サーバ）と段 B（決定 6。BFF と画面）に分ける。
- 前提（覆さない）: [IADR-0561](IADR-0561_pre-close-entry-cutoff-and-flatten.md)（締め切りと手仕舞いの統制。決定 3 の構成の置き場だけを本 IADR 決定 1 が改める）、
  [IADR-0562](IADR-0562_theme-concentration-limit.md)（テーマの上限。「SC-02 は今回は不採用」を計画 ADR-0071 決定 5 が改めた）、
  [IADR-0527](IADR-0527_high-volatility-order-cap-single-function-three-sites.md) 決定 2・3（統制値はリスク管理の設定に持ち、取引判断へはサイジング文脈で渡す）、
  [IADR-0530](IADR-0530_sc02-high-volatility-settings-watchlist-first-typo-guard.md)（SC-02 の誤記の防ぎ方）、IADR-0161 決定 2（旧行を書き換えない）、IADR-0163 決定 2（必須依存）、
  IADR-0134 決定 2（履歴の種別は末尾へ足す）、IADR-0532 決定 1（設定の保存と履歴の記録は 1 つのトランザクション）。
- 仕様書: `.ai-context/specs/20261021_1407_sc02-pre-close-and-theme-settings.md`

## コンテキスト

- 締め切り N とテーマの定義はリスク管理の構成（`PreCloseEntryCutoff:Minutes:<Market>`・`ThemeConcentration:Themes`）に、手仕舞いの有効と M は取引判断の構成（`PreCloseFlatten:*`）にあった。
  いずれも起動時に読む値で、変えるには構成の変更と再起動が要る。
- 計画 ADR-0070 / ADR-0071 決定 5: SC-02 から表示・設定する。手続きは §5 の他の統制値と同じ（OwnerOnly・理由必須・値域外は拒否・履歴。拒否の実効はサーバ）。
  画面はその時点で効いている値を表示する。保存の置き場と経路・構成と保存値の関係は実装が決める。
- 利用者の依頼（#1407）: 画面に保存した値と構成の値が食い違うときは、それを検知して画面に示す。

## 検討した選択肢

### 構成と保存値の関係

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A. 保存値があれば保存値、無ければ構成の値（項目ごと）（採用）** | 構成は「画面で保存するまでの値」。保存した項目は再起動なしで効く。保存値 ≠ 構成の値なら食い違いとして示す | 画面の「効いている値」が 1 つに決まる。配備までの暫定手段（構成）の値をそのまま初期値に使える。保存後に構成を変えても効かないが、食い違いの表示で読める |
| B. 構成の値が常に優先し、画面は構成が無いときだけ効く | 構成を書いた環境では画面の変更が効かない | 画面で設定する裁定（決定 5）を実質的に無効にする |
| C. 厳しい側が効く（N は大きい方・テーマは和集合 等） | 統制が緩む向きの変更を構成と画面の両方で止める | 項目ごとに「厳しい側」の定義が要り、テーマの定義では一意に決まらない（名前の衝突・上限の違い）。画面で緩める変更（利用者の裁定でできる）が構成に負けて効かない理由を説明しづらい |
| D. 最後に変えた方が効く | 構成の変更時刻を知る手段が無い（起動時刻で代用すると再起動のたびに入れ替わる） | 決定的でない |

### 手仕舞いの値の置き場

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **a. リスク管理の設定に置き、構成もリスク管理へ移す。取引判断はサイジング文脈で効いている値を受け取る（採用）** | 3 つの統制が 1 つの設定行・1 つの変更手続き・1 つの構成の置き場に揃う。食い違いの検知も 1 か所で計算できる | 取引判断の構成のキーが使えなくなる（残っていれば起動を止めて知らせる）。既存の照会（サイジング文脈）を使い、新しい依存は作らない |
| b. 保存値はリスク管理、構成は取引判断のまま | 取引判断が保存値 ?? 構成を解決する | 食い違いを画面に示すには、取引判断の構成をリスク管理か BFF へ渡す新しい経路が要る |
| c. 取引判断に保存の口を作る | 取引判断に設定の永続化と履歴を新設する | 変更の手続き（理由・履歴・OwnerOnly）を 2 か所に持つ |

## 決定

### 決定 1: 置き場はリスク管理の設定行。手仕舞いの構成もリスク管理へ移す

- `RiskManagementSettings` に `PreCloseEntryCutoff`（`PreCloseEntryCutoffSettings?`）・`PreCloseFlatten`（新設 `PreCloseFlattenSettings?`＝有効・無効と市場ごとの M）・
  `ThemeConcentration`（`ThemeConcentrationSettings?`）を足す。**null＝保存していない**。
- リスク管理が構成の値をまとめて `ConfiguredSessionAndThemeControls` として持つ（締め切り・テーマは従来のローダー、手仕舞いは新設の `PreCloseFlattenLoader`＝
  キー `PreCloseFlatten:Enabled` / `PreCloseFlatten:Minutes:<Market>`。値域の外・読めない値・未知の市場は起動を止める）。
- 🔴 **IADR-0561 決定 3 の「構成 `PreCloseFlatten:Enabled`・`:Minutes:<Market>`（取引判断）」を改める。** 取引判断に残るのは巡回の間隔（`PreCloseFlatten:IntervalSeconds`）だけである。
  取引判断の構成に `Enabled` / `Minutes` が残っていれば**起動を止める**（黙って読まないと「構成で有効にした手仕舞い」が効かないまま気付けない＝統制が緩む向き）。
  helm の values には両キーとも注記の例があるだけで、実際に書いた構成は無かった（確認: `deploy/` と `docker-compose.yml` を全文検索）。
- 取引判断の常駐（`PreCloseFlattener`）は、効いている値を**サイジング文脈**（REST `SizingContextView.PreCloseFlatten`・gRPC `PreCloseFlattenControls`。IADR-0527 決定 3 と同じ経路）で受け取る。
  閉場の 60 分前（M の上限）より前は照会しない。窓の候補に入ったら 1 巡回に 1 回だけ照会する。
  未供給（旧応答・照会失敗の安全既定・値域の外）は、**直近に受け取った値**（`PreCloseFlattenLastKnown`。singleton）で判定する。起動から一度も受け取っていなければ既定（無効・15 分）。
  いずれも警告をログに出す。方針の書式の行による有効化は別に効く（IADR-0561 決定 4）。
  ［2026-10-21 PR #1408 のレビュー］当初は「未供給は既定で判定する。照会に失敗したときは保有の照会（同じ相手）もどのみち失敗する」と書いた。
  実際の経路を確かめると、サイジング文脈と保有の照会は同じ相手（リスク管理・同じ HTTP クライアント／gRPC の輸送）だが**別々の呼び出し**であり、
  一方だけの一過性の失敗（5 秒のタイムアウト・非 2xx・契約の食い違いの安全既定）はあり得る。そのとき既定へ倒すと、有効にした手仕舞いが出ない・延ばした M が 15 分へ縮む
  （統制が緩む向き）。そこで直近の値を保つ形にした（レビューの案 (a)。費用は singleton 1 つ）。

### 決定 2: 効く値は「保存値があれば保存値・無ければ構成の値」（項目ごと）

- `SessionAndThemeControlResolution.Effective*`（締め切り・手仕舞い・テーマの 3 つを別々に解決）。審査（`OrderScreeningService`。予約の組み直しの締め切りの判定を含む）と
  サイジング文脈は、**呼ばれるたびに設定行を読んで**解決する（再起動は要らない）。
- 審査は構成の値を `ConfiguredSessionAndThemeControls` の**必須の引数**で受ける（IADR-0163 決定 2。以前の `PreCloseEntryCutoffSettings` と `ThemeConcentrationSettings` の 2 引数を 1 つにした）。
- 保存値を消して構成の値へ戻す口は持たない（構成と同じ値を保存すれば、保存値が効いたまま食い違いは消える）。必要になれば足す（残る制約）。

### 決定 3: 食い違いは GET の応答で返し、画面が示す

- `GET /risk-controls/settings/pre-close` → `PreCloseControlsView`（`effective*`・`configured*`・`saved*`〔無ければ null〕・`*Source`〔0=構成・1=保存値〕・`*Drift`・
  市場ごとの N の上限〔場中の長さ〕・M の上限〔60〕）。
- `GET /risk-controls/settings/theme-concentration` → `ThemeConcentrationControlView`（`effective`・`configured`・`saved`・`source`・`drift`）。
- **食い違い＝保存値があり、かつ構成の値と値の上で違う**（市場ごとの分・有効・無効で比べる。テーマは名前・市場・上限・銘柄の集合〔大文字小文字・空白を区別しない〕で比べ、並び順は比べない）。
  保存値が無ければ食い違いは無い（構成の値が効いている）。
- 応答は他の SC-02 の設定と同じく OwnerOnly。市場は数値の enum（画面の規約）で返す。

### 決定 4: 変更の手続きは §5 の他の統制値と同じ

- `PUT /risk-controls/settings/pre-close`（締め切りの市場ごとの N・手仕舞いの有効と市場ごとの M を一度に置き換える）と `PUT /risk-controls/settings/theme-concentration`（定義の全体を置き換える）。
- **OwnerOnly**（サービスロール・未認証は 403 / 401。生成 AI と AI の監視銘柄の入れ替え案の経路にこの口を呼ぶコードは無い。IADR-0527 決定 2 の 2026-10-11 追記と同じ注意が当てはまる）・
  **理由必須**・**値域外は 400 で設定も履歴も変えない**（値域の実効はサーバ。`RiskSettingsService` とエンドポイントの両方で検査する）。
- 値域: N は 1〜場中の長さ（米国 390・日本 330）、M は 1〜60、**米国・日本の両方の値を求める**（欠けた市場を既定で黙って埋めない・重複を後勝ちにしない）。
  テーマは名前が空でなく重複しない・銘柄 1 つ以上で空のコードが無い・上限 1 以上・既知の市場（ADR-0071 決定 2）。外す値（0）は持たない。
  空の一覧（テーマなし）は値域の上では受理する（テーマを外すのは利用者の裁定でできる。画面は緩める変更として確認を求める＝段 B）。
- 正規化: テーマの名前の前後の空白を落とし、銘柄コードは前後の空白を落として大文字へ揃え、同じテーマの中の重複を除く。
- 履歴の種別は末尾へ足す: `PreCloseControlsChanged`（11）・`ThemeConcentrationChanged`（12）。前後値は中身を文字列で残す（保存前は「未保存（構成の値）」）。

### 決定 5: 永続化は設定行の JSON のキーを足すだけ（旧行を書き換えない）

- キー `preCloseEntryCutoff`（市場名 → 分）・`preCloseFlatten`（`enabled`・`minutes`）・`themeConcentration`（テーマの一覧）。保存していなければキーを書かない。
- キーの無い行（旧行）は null＝構成の値が効く。**値域の外の永続値（手での編集など）も null として読む＝構成の値へ倒す**（構成は起動時に値域を確かめている）。
  黙って丸めない・外す値を効かせない（IADR-0527 の高ボラティリティ銘柄の丸めとは違う扱い。こちらは倒す先〔構成の値〕が値域の中にあるため）。

### 決定 6（段 B）: BFF は素通し、画面は効いている値と食い違いを示す

- BFF に `GET`/`PUT /bff/risk-controls/settings/pre-close`・`/theme-concentration` の素通しを足す（IADR-0071・IADR-0530 決定 3 と同じ形）。
- 画面は効いている値・出どころ（構成／保存値）・食い違い（構成の値を並べる）を示し、手仕舞いの行に「画面で無効でも方針の『閉場前の手仕舞い: 有効』の行で有効になり得る」を示す。
- テーマの銘柄コードの誤記は IADR-0530 決定 1 と同じ形（監視銘柄からの選択を主の経路・手入力は書式と照合と確認の印）で防ぐ。統制を緩める変更（N・M を縮める・手仕舞いを無効にする・
  上限を上げる・銘柄やテーマを外す）は列挙して確認を求める（IADR-0086 決定 3）。
- 段 B の詳細は段 B の PR で本 IADR へ日付つきで追記する。

## 結果

- 画面（段 B の後）から 3 つの統制を再起動なしで変えられる。配備までは構成（リスク管理）で変える。
- 受け入れ基準は `SessionAndThemeControlsTests`（T-10-2945〜T-10-2952）・`GrpcRiskReadTests` / `HttpSizingContextProviderTests`（T-10-2953）・`PreCloseFlattenerTests`（T-10-2933 の更新）と、
  契約フィクスチャ（`risk-controls.settings-pre-close.json`・`risk-controls.settings-theme-concentration.json`）で固定した。

## 残る制約

- 保存値を消して構成の値へ戻す口は無い。
- 取引判断が**起動から一度も**サイジング文脈の手仕舞いの値を受け取れていない間は、既定（無効・15 分）で判定する。構成や画面で有効にしていても手仕舞わず、延ばした M も 15 分になる（警告をログに出す）。
  その後に受け取れなくなった巡回は直近の値で判定するため、画面で値を変えた直後に照会が失敗し続けると、変える前の値で判定し続ける（変更が遅れて効く）。
- 取引判断とリスク管理の版がずれる間（旧いリスク管理）は、サイジング文脈に手仕舞いの値が無く既定の無効で判定する（方針の行による有効化は効く）。配備は両サービスを同じ版で揃える。
- 臨時の半日取引日の締め切りは引き続き通常の閉場で判定する（IADR-0561 の残余。本 IADR は変えない）。
