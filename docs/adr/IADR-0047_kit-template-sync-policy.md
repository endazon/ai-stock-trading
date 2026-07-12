---
title: IADR-0047 kit テンプレート更新には追随し、restore 系 CI/スクリプトは slnx 自動発見形を採る（IADR-0046 決定 4 の部分変更）
type: impl-adr
status: Accepted
related_ids:
  - NFR
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "https://github.com/endazon/project-planning/pull/21（impl-handoff-kit repo-template の更新）"
---

# IADR-0047: kit テンプレート更新には追随し、restore 系 CI/スクリプトは slnx 自動発見形を採る

- 状態: Accepted
- 日付: 2026-07-12
- 決定者: endazon（kit 更新のマージと本リポへの反映指示）・claude（実装詳細）

## 起点・関連

- 起点: NFR（CI ゲート・環境セットアップ）・issue #104・PR #105
- 関連: [IADR-0046](IADR-0046_unit-repo-layout.md) 決定 4（本 ADR で部分変更）・
  kit 雛形（planning PR #21、platform IADR-0058 / #256 の教訓を反映済み）

## コンテキストと課題

IADR-0046 決定 4 は再編時の CI 追随を「パスを `backend/backend.slnx` へ更新する。CI の構造（単一ユニット）は
維持する（platform 側のような自動発見は不要）」と定めた。その直後、本リポジトリの生成元である
impl-handoff-kit のテンプレートが更新され（planning PR #21・マージ済み）、restore 系
（security / copilot-setup / setup.sh）は slnx/sln **自動発見ループ**が標準形になった。
オーナーより「kit 更新内容を本リポジトリへ反映する」指示があり、IADR-0046 決定 4 の
「自動発見は不要」との関係を整理する必要がある。

## 検討した選択肢

1. **restore 系のみ kit の自動発見形へ追随し、ci.yml（lint/build/test の主ゲート）は明示パスを維持（採用）**
2. 全ファイルで明示パス（`backend/backend.slnx`）を維持 — kit との恒常的 drift が発生し、
   テンプレート更新のたびに手動差分管理が必要。copilot-setup の旧判定バグ相当の取りこぼしを再発しうる
3. ci.yml も含め全て自動発見化 — 主ゲートの対象が暗黙化し、単一ユニットのリポでは可読性を損なう

## 決定

1. **kit テンプレート更新への追随を原則とする**（生成元との drift 最小化。反映は仕様書を伴う PR で行う）。
2. **restore 系**（`security.yml`・`pr-title.yml` の vulnerable-scan、`copilot-setup-steps.yml`、
   `scripts/setup.sh`）は kit 標準の **slnx/sln 自動発見ループ**を採る。
   IADR-0046 決定 4 の「自動発見は不要」は restore 系について本 ADR で変更する。
3. **ci.yml（lint / build-and-test の主ゲート）は明示パス `backend/backend.slnx` を維持**する
   （検査対象を明示し可読性を保つ。IADR-0046 決定 4 のこの部分は存続）。

## 理由

- 実害の是正: 旧 copilot-setup の判定（`ls *.sln **/*.csproj`）は `backend/backend.slnx` レイアウトで
  復元対象を発見できず silent skip していた。自動発見はレイアウト変更に対して頑健。
- kit は platform 実装の教訓（IADR-0058・#256 等）を継続的に取り込む単一情報源であり、
  drift を放置すると教訓の伝播が止まる。restore 系は「全ソリューションを対象にする」ことが意味論であり、
  自動発見が意図をそのまま表す。主ゲート（build/test）は対象の明示に価値があるため区別する。

## 結果

- 良い影響: kit との整合が保たれ、将来のテンプレート更新の適用が容易になる。restore の取りこぼしが構造的に消える。
- 悪い影響 / トレードオフ: restore 系と ci.yml で書式が二形式併存する（役割の違いとして本 ADR に根拠を記録）。
- フォローアップ: kit テンプレートの更新を検知して追随する運用（platform 側 issue #230 の submodule 運用
  整備と同様の仕組み化）は将来課題。

## 関連

- Supersedes: [IADR-0046](IADR-0046_unit-repo-layout.md) 決定 4 のうち「platform 側のような自動発見は不要」の
  部分（restore 系に限り自動発見へ変更。パス更新・ci.yml の明示維持は存続）
- Superseded by: なし
