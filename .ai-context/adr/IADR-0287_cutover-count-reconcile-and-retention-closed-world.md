---
title: IADR-0287 切替の件数突合は RetentionScope の閉世界の補集合を manifest に明示列挙し、bash+psql の単一実装（指紋つき・未確定予約の減少を FAIL）でテスト固定する
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - FR-11
  - NFR-08
  - NFR-09
  - NFR-10
  - NFR-11
  - ADR-0009
  - IADR-0057
  - IADR-0059
  - IADR-0074
  - IADR-0109
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/INDEX.md
---

# IADR-0287: 切替の件数突合は RetentionScope の閉世界の補集合を manifest に明示列挙し、bash+psql の単一実装（指紋つき・未確定予約の減少を FAIL）でテスト固定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: Claude Code（起案・実測）／ endazon（切替本体・破棄の判断は別途承認）

## 起点・関連

- 関連する計画書 ID: FR-11（監査証跡）、FR-05（発注執行）、NFR-08 / **NFR-09** / **NFR-10** / NFR-11（データ保持。計画 INDEX 決定 22・planning#28）、ADR-0009（停止系の状態）
- 関連する実装仕様書: [`.ai-context/specs/20260903_346_cutover-preparation.md`](../specs/20260903_346_cutover-preparation.md)
- 関連 issue: #346（本件）、#344（親）、#204（前段ゲート）、#137 / #141（Reserved の扱い）、#339（`RetentionScope`）
- 関連 IADR: IADR-0057（発注前予約・3 相）、IADR-0059（終端行のみパージ・Reserved 対象外）、IADR-0074（滞留 Reserved の自動リコンサイル）、IADR-0109（`AST_*_LIB=1` で source する Bash テストの作法）

## コンテキストと課題

#346 は再実装版への切替に際し「7 年保持対象の欠損ゼロ・未確定予約の引き継ぎ完全性の自動突合」を退行防止として要求する。
現状は `RetentionScope`（NFR-10 の閉世界。パージ「してよい」2 ストアだけを列挙し、それ以外は自動パージ不可）で**消さない**ことは担保しているが、
**切替で運ばれたか**を確かめる手段は無い。決めるべきことは 4 つ——(1) 何を数えるか（母集合）、(2) 何を「同じ」とみなすか（件数だけか）、
(3) 突合ロジックをどこに置くか（bash / C# / Node）、(4) 母集合の腐りをどう止めるか。

## 決定

### 決定 1: 母集合は 7 サービス DB の**全ユーザテーブル**とし、manifest に明示列挙する。DB 側の実在集合と双方向に照合し、片方にしか無ければ部分出力せず exit 2

- `RetentionScope` は「パージしてよい側」だけを列挙する閉世界であり、**保全側は暗黙（補集合）**である。切替の突合ではその補集合を**数える必要がある**ため、
  manifest（`scripts/cutover-count-reconcile.sh` の `AST_CUTOVER_MANIFEST`・35 テーブル）に明示列挙する。列挙は `ledger`（21）/ `state`（12）/ `reserved`（1）/ `dedup`（1）の 4 区分。
- `dedup`（`processed_messages`）も**切替では保全する**。パージは opt-in の常駐（NFR-11）の仕事であり、切替という一回性の作業に混ぜない。
- 実行時は `pg_tables` で実在テーブルを発見し、**manifest に無い／DB に無い**のどちらも exit 2 で止め、**部分的なスナップショットを書かない**
  （部分出力は「全数」と読み違えられる。`check-cross-repo-refs.js` 等の「0 件検査で緑にしない」作法と同じ向き）。
- `__EFMigrationsHistory` は同数検査から外し、減少のみ FAIL・増加は NOTE（新スキーマ適用の証跡）。

### 決定 2: 「同じ」は件数だけでなく **時刻列の min/max と内容指紋**で判定し、未確定予約（Reserved）は**件数が同じでも減っていれば FAIL**

- 指紋は `md5(string_agg(md5(t::text), '' order by md5(t::text)))`——行順に依存せず、1 行の改変・入れ替えで変わる。件数一致だけでは「1 行消して 1 行足した」を通してしまう。
- NFR-09（未確定データの無期限保持）は**テーブル件数**では守れない（Reserved が Completed へ動いても件数は同じ）。`order_dispatch_reservations` は
  `count(*) filter (where "State" = 0)` を別に採り、減少は「無期限保持・自動削除禁止」を名指しで FAIL、増減いずれも凍結中の状態変化として FAIL にする。
- after にだけあるテーブルは NOTE（新スキーマの新規テーブルは正常）。before にあって after に無いテーブルは FAIL。

### 決定 3: 突合ロジックは **bash+psql の単一実装**に置き、C# / Node へ複製しない。純関数性は `compare` が 2 つの TSV しか読まないことで担保する

- 候補は 3 つあった。(a) bash（awk）に置く／(b) Node の純関数（`scripts/lib/`）を bash から呼ぶ／(c) C# の Domain に置く。
- (c) は所有サービスが無い横断関心であり、切替当日に走らせる場所（テスト以外）も無い。(b) は言語が 2 つになり、psql が動く場所（クラスタ内・運用ホスト）に Node が要る。
  (a) は 1 か所で完結し、`AST_CUTOVER_LIB=1` で source する既存の Bash テスト作法（IADR-0109 / #274）にそのまま乗る。**採るのは (a)**。
- 純関数性の担保は「`compare` は引数の 2 ファイル以外を読まない」を構造で守ること（DB へ触らない・環境変数を見ない）で置き換える。
  47 検査の `scripts/cutover-count-reconcile.test.sh` が psql スタブで固定し、CI の `shell-scripts` step で走る。
- `snapshot` / `controls` は **SELECT しか発行しない**。テストはスタブが記録した全 SQL に書き込み語（insert/update/delete/drop/alter/truncate/create）が無いことを検査する。
- 🔴 `run_sql` は stdin を `/dev/null` へ落とす。`kubectl exec -i` 越しの psql は呼び出し元 while ループの stdin を飲み込み、**母集合の残りを黙って読み飛ばす**。

### 決定 4: manifest の腐り（DbSet の増減）は **CI の Node テストで ModelSnapshot と突き合わせて**止める（切替当日の実行時検査は最後の網）

- `scripts/scripts.repo.test.js` が manifest をスクリプトから読み、EF `*ModelSnapshot.cs` の `ToTable` 集合との一致・時刻列のプロパティ実在・
  `dedup`/`reserved` と `RetentionScope.PurgeableStores` の一致（4 検査）を見る。DbSet を足した PR は manifest も直さない限り赤くなる。
- 決定 1 の実行時照合は**切替当日**にしか発火せず遅い。両方持つのは、腐りが「切替当日に数え落とす」側へ倒れるからである。

### 決定 5: リハーサルは**別 DB へのコピー**（`createdb -O ai` ＋ `pg_dump | psql`）で行い、既存 DB へは 1 行も書かない。接続先の切替は `AST_DB_PREFIX`

- `ai` ロールは `CREATEDB` を持たない（実測）ため、作成だけ superuser（`postgres`・ローカルの unix socket は trust）で行い、所有者は `ai` にする。
- 一時スキーマ（同一 DB 内）は採らない——`snapshot` の発見が `schemaname='public'` に固定されており、スキーマを可変にすると本番でも誤ったスキーマを数える余地ができる。
- TSV には**論理名**（manifest の db）を書く。本番とコピーの TSV をそのまま `compare` できる。

## 実測（作業仕様書 §リハーサル記録の要約）

- 本番（非凍結）→ コピー: **FAIL 3 件**（`audit_events` 338 → 340。稼働中の監査サービスが 2 行書いた）。**凍結を before スナップショットより前に置く根拠**。
- 凍結相当（コピー → コピー）: **FAIL 0 件／42 行**。指紋まで一致。
- 陰性（Reserved 1 行＋監査 1 行を削除）: **FAIL 7 件**を検出（うち 1 件が「未確定予約が減っている」の名指し）。
- `controls` 29 項目は本番とコピーで一致し、`TradingDefaults.CreateSettings()` の直列化と一致（`settings_change_log` 0 件）。
- 14 の一時 DB を DROP し、本番の再スナップショットは `audit_events` の稼働中増分（338 → 342）以外すべて一致（既存データ未変更）。

## 影響・代償

- 指紋は各行を文字列化して md5 を取るため、行数に比例する（O(n log n)）。現状は最大 342 行で問題にならないが、7 年後の `audit_events` では
  数分かかり得る。切替は市場閉場中の一回性の作業であり、許容する。必要なら `ledger` 以外の指紋を省く分岐を足す。
- manifest はテーブル単位であり、**列の増減（同名テーブルの新スキーマ）は指紋の不一致として FAIL に出る**。列を足すマイグレーションを含む切替では
  「before は旧スキーマ・after は新スキーマ」になるため、**件数・min/max のみで OK とし指紋差は説明つきで受容する**手順を移行仕様書に置いた。
- 本 IADR はデータの**運搬**の検証を決めるものであり、7 年保持を**担保する保管**（バックアップ先・リストア試験）は決めていない（利用者裁定・移行仕様書の未決事項）。

## 却下した代替案

- **C# Domain の純関数＋xUnit**（候補 (c)）: 所有サービスが無く、切替当日の実行主体も無い。テストのための型を本番コードへ足すことになる。
- **Node の純関数を bash から呼ぶ**（候補 (b)）: psql が動く場所に Node を要求し、2 言語で同じ責務を持つ。
- **件数のみの突合**: 「1 行消して 1 行足した」「Reserved → Completed」を通す。NFR-09 の不変条件を守れない。
- **manifest を持たず実在テーブルをすべて数える**: 時刻列・未確定条件の指定先が無く、保持区分（ledger / state）も表現できない。実行時の双方向照合は残す。

## 残余リスク

- **同名テーブルの列追加を伴う切替**では指紋差の受容判断が人手になる（上記代償）。受容は移行仕様書の手順で「差分の説明」を書くことを要求する。
- `controls` の意味づけ（期待値）は移行仕様書の表にあり、`TradingDefaults` が変わると表が腐る。`TradingDefaultsTests` が計画値を固定しているので、
  表は「そのテストの値を転記した」ものとして読む（正本はコード）。
