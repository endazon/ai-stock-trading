---
title: 配備の前後で helm のリリースとチャートの差（設定の未反映）を検出する（#1022）
type: spec
status: accepted
related_ids: [NFR-09, IADR-0439, IADR-0362, IADR-0283, IADR-0100, IADR-0341]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (非機能要件表。本件に当たる番号は無い＝無採番 NFR)
---

# 仕様書: helm のリリースとチャートの差を検出する（#1022）

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **NFR（無採番）**。運用の検査器・手順書の整備であり、計画の非機能要件表（NFR-01〜18）に当たる番号が無い
  （`traceability.md` の無採番の場合 2）。届いていなかった設定（予約の自動リコンサイル）が支えるのは NFR-09 だが、本件はその実装ではない。
- 実装判断: IADR-0439（新規）。関連: IADR-0362（`Reconciliation__*` を values へ）・IADR-0283（前回リリースの値の引き継ぎ）・
  IADR-0100（values-local.yaml）・IADR-0341（Reloader は OpenD を再起動しない）
- 起点 Issue: #1022（Refs #856 / #882）

## 事象

2026-09-25、リリース `ast` が 9/17 の 6 版のまま。以降の配備は `kubectl rollout restart` だけで、values の `Reconciliation__*` 6 項目が
8 日間稼働に届いていなかった。確認は `helm get manifest` と、`helm get values` を与えた `helm template` の差を手で比べて行った。

## やること（issue の 3 項目の扱い）

| issue の項目 | 本 PR |
| --- | --- |
| 稼働中のリリースと今のチャート（values-local を含む）の描画の差を出すスクリプト（読み取りのみ・OpenD の Deployment が変わるかを必ず示す）。手順書で配備の前に走らせる | `scripts/helm-release-drift.js`・chart README の手順（前と後） |
| 手順書に、Pod の入れ替えだけでは values・テンプレートの変更は入らないと明記 | chart README の新しい節・運用仕様書のデプロイ表と予約の自動リコンサイルの節 |
| 可能なら、チャートの版とリリースの差を監視する | **採らない**（IADR-0439「採らなかった案」。全サービスへ版を運ぶ経路が要り範囲を超える。残余リスクに残す） |

## 母集合（規則 1〜6・9）

- 「Pod の入れ替えで設定が入る」と読める記述が残っていないか: `git grep -n "rollout restart" -- docs deploy scripts` → chart README（#673 の注記。
  イメージを届けるための restart であり、values が入るとは書いていない）・`deploy/opend/README.md:348`（RSA 鍵の再生成後の OpenD の再起動）・
  `docs/blocked-tasks.md:601`（スキーマの再生成）・`docs/operations/wolverine-queue-cleanup-runbook.md:162`（キューの再作成）・
  `scripts/README.md`・`scripts/k8s-local-deploy.sh`（helm upgrade の後の restart）。**いずれも values の反映を主張していない**ので変えない。
- 「再デプロイ」の語: `git grep -n "再デプロイ" -- docs/operations/operations.md` → 300・384・448・450 行。いずれも「値を戻して配備する」意で、
  配備＝`k8s-local-deploy.sh`（helm upgrade を含む）を指す。変えない。予約の自動リコンサイルの節（「構成が Pod に届いていない」）だけに、
  Pod の入れ替えでは入らないことと手順への導線を足す。
- 配備の手順書の所在: 運用仕様書のデプロイ表が「詳細は chart README」とし、chart README の「デプロイ」節が手順の本体。両方へ書く。
- scripts の一覧: `scripts/README.md` の「本リポ固有」の表へ 1 行足す。
- 🔴 **テスト仕様書の trace ブロックは本 PR では足さない**: 同じ文書（`docs/tests/FR-10_risk-controls-tests.md`）の trace ブロックの
  `specs:` / `issues:` 行を並行の PR（#1039 の #1040）が変えており、隣接行の変更は衝突する。本文の節は文書の途中へ置き（末尾の追記と衝突しない）、
  trace は運用仕様書の trace ブロック（IADR-0439・本仕様書・#1022）と本仕様書が持つ。

## 決定する挙動

- 比べるもの: 稼働側＝`helm get manifest <release> -n <ns>`、チャート側＝`helm template <release> <chart> -n <ns> --is-upgrade --no-hooks
  --skip-tests -f <helm get values の結果> [-f <--values>...]`。`--values` はリリースの値の**後ろ**に重ねる。
- 出すもの: 資源の追加（チャートにだけ在る＝配備で作られる）・削除（リリースにだけ在る＝配備で消える）・内容の違い。ワークロードはコンテナごとの
  env のキーの追加・変更・削除と image の変更、それ以外の差の行数。Secret は変わったことだけ。
- OpenD の Deployment（既定の名前 `opend`・`--opend-name`）の判定を必ず 1 行目に出す。
- 終了コード: 0 差なし／1 差あり（OpenD は不変または無し）／3 差あり（OpenD の Deployment が変わる・作られる・消える）／2 使い方の誤り・helm の失敗。
- 読み取り専用: helm は `get manifest` / `get values` / `template` / `version` のみ。kubectl は呼ばない。
- 秘密: manifest の行を出さない。secretKeyRef は参照先ごと伏せる。平文でも機密らしい名前・資格情報入りの URL・`Password=` は伏せる。
  リリースの values は 0600 の一時ファイルにだけ書き、表示せず（失敗時の stderr も出さない）、消す。
- `--self-test`: 同梱の fixture だけで走る（helm・クラスタを使わない）。`--live` / `--rendered` で 2 つのファイルを比べる。
- CI: `scripts/scripts.repo.test.js` から自己試験と差の計算を試験し、CI の `scripts-tests`（`node scripts/scripts.test.js`）に載る。ワークフローは変えない。

## 受け入れ基準 → テスト（T-10-1526〜T-10-1534。T-10-1520〜1525 は #1039 の PR（#1040）が使う）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | env の追加 | `scripts.repo.test.js`（T-10-1526） |
| 2 | env の値の変更（と削除） | 同（T-10-1527） |
| 3 | 資源の追加・削除 | 同（T-10-1528） |
| 4 | 秘密の伏せ方（Secret・secretKeyRef・機密らしい平文・資格情報入りの URL） | 同（T-10-1529） |
| 5 | OpenD の Deployment の判定と終了コード | 同（T-10-1530） |
| 6 | 書き方だけの違いは差にしない | 同（T-10-1531） |
| 7 | 読み取り専用の閂・values を出さない・一時ファイルを残さない | 同（T-10-1532） |
| 8 | 自己試験は helm・クラスタ無しで通り CI に載る | 同（T-10-1533） |
| 9 | 部分集合の YAML の読み | 同（T-10-1534） |

## 検証の範囲

- 🔴 **実クラスタに対しては走らせていない**（作業の制約）。検証は fixture と、オフラインの `helm template` 同士（既定の values と
  values-local.yaml＋OpenD 有効）の比較だけ。

## 監査の是正（2026-09-26・PR #1043 の監査 NO-GO）

| 指摘 | 是正 | 試験 |
| --- | --- | --- |
| F1（中）資格情報の伏せ方が狭い（プローブ 10 件中 9 件が平文で出た） | キー名を語に分けて機密らしい語で判定（`__` の区切り・camelCase）。値は URL の userinfo（`://` の後の最初の空白までの `@`。`/` を含むパスワードも）・`/webhooks/<id>/<token>`・クエリの鍵・トークンらしいパス要素・接続文字列の `Pass=` / `Pwd=` / `Password=` / `AccountKey=` 等・トークンらしい長い値で判定。例外は `…TokenEndpoint` / `…TokenUrl` / `…Per1kTokens` だけ。プローブ 10 件＋追加 10 件を `credential-cases.js`（連結で組む見張り値）に置き、機密でない 9 件を陰性対照にした | T-10-1529（件ごとの Theory・追加／変更／削除）・自己試験 2 件（T-10-1533 が走らせる） |
| F2（低）読み取り専用の閂が禁止の列挙 | 引数列を許可の列挙で検める（サブコマンド・フラグ・位置引数の数・`-` 始まりの値）。`--release` / `--namespace` の書式、`--chart` / `--values` の `-` 始まり・URL・実在を helm の前に検める。`--helm` / `HELM_RELEASE_DRIFT_HELM` は運用者の選択として手順書に書く | T-10-1532・T-10-1533 |
| F3（低）Windows で `mode 0600` が効かない・シグナルで一時ファイルが残る | SIGINT / SIGTERM / SIGHUP のハンドラで消して終了（130 / 143 / 129）。置き場所（OS の一時ディレクトリ・実行中だけ）と強制終了の後始末を手順書に書いた | T-10-1532（シグナルごとの Theory） |

- 変異注入（11 種＋secretKeyRef の 2 種）はいずれも赤になる（結果は `docs/tests/FR-10_risk-controls-tests.md` の本節）。
- テスト仕様書の trace ブロック: #1040 が develop に入ったため衝突の懸念が無くなり、本 PR で IADR-0439・本仕様書・#1022 を足した
  （上の「母集合」の除外は解消）。

## 完了の定義

- `node scripts/scripts.test.js` と `node scripts/helm-release-drift.js --self-test` が通る。
- 静的検査（trace ブロック・ADR 索引・リンク・コミット規約）が通る。
