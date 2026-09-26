---
title: IADR-0439 配備の前後で、稼働中の helm リリースとチャートの描画の差（設定の未反映）を読み取り専用の検査器で出し、OpenD の Deployment が変わらないことを確かめる
type: impl-adr
status: Accepted
related_ids: [NFR-09, IADR-0362, IADR-0283, IADR-0100, IADR-0341]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (非機能要件表。NFR-09 は届いていなかった設定が支える要件)
---

# IADR-0439: helm リリースとチャートの差を読み取り専用で検出する

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（起票 [#1022](https://github.com/endazon/ai-stock-trading/issues/1022)。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #1022（Refs #856 / #882）
- 関連する実装仕様書: [20260926_1022_helm-release-drift](../specs/20260926_1022_helm-release-drift.md)
- 関連 IADR: IADR-0362（`Reconciliation__*` を values へ入れた変更。本件で 8 日間届いていなかったもの）・IADR-0283（前回リリースの値の引き継ぎ）・
  IADR-0100（values-local.yaml の常設）・IADR-0341（ESO 所有と Reloader。Reloader は OpenD を再起動しない）
- 起点 ID: NFR（無採番。運用の検査器であり、非機能要件表に当たる番号が無い）。`related_ids` の NFR-09 は、届いていなかった設定
  （未確定の予約の照合）が支える要件として挙げる（本 IADR がその要件を実装するのではない）。

## コンテキスト

- 2026-09-25、helm リリース `ast` が 9/17 の版のまま更新されていなかった。それ以降の配備はすべて `kubectl rollout restart` による
  イメージの入れ替えだけだった。
- **Pod の入れ替えは、リリースが保存している Pod テンプレートのまま Pod を作り直すだけで、values・テンプレートの変更は入らない。**
  そのため #882（IADR-0362）で values に入れた `Reconciliation__*` の 6 項目が 8 日間、稼働中の発注執行に届かず、#856 のトリアージは
  「配備で有効化済み」と誤って記録していた。
- 20:29 JST の helm upgrade（7 版）で反映し、`helm get manifest` と、`helm get values` を与えた `helm template` の差を手で比べて確かめた。
  この手順が手作業で、配備の前後に走らせる決まりも無かった。

## 決定

1. **差を出す検査器 `scripts/helm-release-drift.js` を置く。** 稼働側＝`helm get manifest <release>`、チャート側＝
   `helm template <release> <chart> --is-upgrade --no-hooks --skip-tests -f <helm get values の結果> [-f <追加の values>...]`。
   追加の values（values-local.yaml）は**リリースの値の後ろ**に重ねる（リポジトリの今のプロファイルが勝つ＝次の配備が入れるものに近い）。
2. **出すもの**: 追加・削除・内容の違う資源、ワークロード（Deployment・StatefulSet・DaemonSet・Job・CronJob 等）のコンテナごとの
   env のキーの追加・削除・変更と image の変更、それ以外の差の行数。env の並び・コメント・空行・フロー形式とブロック形式の書き方だけの
   違いは差にしない。
3. **OpenD の Deployment は必ず 1 行で判定を出す**（変化なし／変化あり／チャートにだけ在る／リリースにだけ在る／どちらにも無い）。
   変わるなら終了コード 3、変わらない差は 1、差なし 0、使い方の誤り・helm の失敗は 2。配備の手順書（chart README）は
   「OpenD の Deployment は変わってはならない・3 なら配備しない」と明記する（Pod が作り直されると有人認証のセッションが切れる）。
4. **読み取り専用**: helm は `get manifest` / `get values` / `template` / `version` だけを呼ぶ。`assertReadOnly` がそれ以外の
   サブコマンドと `--dry-run` / `--validate` / `--post-renderer` 等を例外で拒む。kubectl は呼ばない。
5. **秘密を出さない**: manifest の行をそのまま出さない。Secret は変わったことだけを示す。env は secretKeyRef を参照先ごと伏せ、
   平文の value でも名前が機密らしいもの（Password・Secret・Token〔`…TokenEndpoint` を除く〕・ApiKey・ConnectionString・Webhook 等）と
   資格情報入りの URL・`Password=` を含む値は伏せる。`helm get values` の結果は 0600 の一時ファイルにだけ書いて描画に使い、表示せず
   （失敗時の stderr も出さない）、終わったら消す。
6. **自己試験は fixture だけで走る**（`--self-test`・`scripts/fixtures/helm-release-drift/`）。helm もクラスタも使わない。
   `scripts.repo.test.js` から走らせ、CI の `scripts-tests` に載せる（ワークフローは変えない）。
7. **YAML は外部依存ゼロの部分集合で読む**（helm が描くブロック形式と単純なフロー形式）。スクリプト群の「依存ゼロ」の方針に従う。

## 採らなかった案

- **`helm diff` プラグインを使う**: クラスタ側の差を詳しく出せるが、プラグインの導入が要り、差の本文（Secret を含む）をそのまま出す。
  秘密を出さない保証を自前で持てない。
- **`helm upgrade --dry-run=server` で比べる**: 検査がクラスタへ要求を出し、読み取り専用の境界がサブコマンドの名前で閉じない。
- **`k8s-local-deploy.sh` に組み込んで配備を止める**: 本件の事故は「`helm upgrade` を通らない配備」で起きた。配備スクリプトの中に置いても
  `rollout restart` だけの配備では走らない。まず手順書の前後の確認として置き、同型の事故が再び起きたら自動化を検討する（検査器の追加は同型 2 回から）。
- **起動時に自分の設定の版を記録し、チャートと食い違えば警告する（issue の「可能なら」）**: チャートの版をアプリへ運ぶ経路（注釈・env）を
  全サービスに足す変更になり、本件の範囲を超える。残余リスクに残す。

## 結果・残余リスク

- 配備の前に「何が入るか」、後に「差が無いこと」を確かめられる。OpenD の Deployment が変わる配備を事前に止められる。
- **検査は手順書に従って人が走らせたときだけ効く**（`rollout restart` だけの配備を機械で止めるものではない）。
- `--set` で渡した値（`k8s-local-deploy.sh` が前回リリースから引き継ぐもの）と values-local.yaml が同じ項目を持つと、描画では
  values-local.yaml が勝ち、実際の配備と食い違う差が出得る（手順書に注意を書いた）。
- env・image 以外の差は行数だけを出す（中身は出さない）。ブロックスカラーの中のコメント行だけの変更は拾わない。
- 実クラスタでの走行は本 PR では行っていない（検証は fixture と、オフラインの `helm template` 同士の比較だけ）。
