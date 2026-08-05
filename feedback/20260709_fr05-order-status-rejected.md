---
title: FR-05 の注文状態に「証券会社拒否（Rejected）」を追記
type: plan-feedback
status: open
category: 要求の不足
related_ids: [FR-05]
source_repo: ai-stock-trading
source_ref: PR #43 / fix/FR-05-paper-broker-validation / docs/adr/IADR-0007_broker-rejection-vs-risk-rejection.md
author: endazon (with Claude Code)
created: 2026-07-09
---

# フィードバック: FR-05 の注文状態に「証券会社拒否（Rejected）」を追記

> **送付済み（2026-08-06 JST）。** 計画リポジトリへ `plan-feedback` ラベル付き Issue として起票した:
> [endazon/project-planning#211](https://github.com/endazon/project-planning/issues/211)。
> あわせて計画リポジトリの `draft/feedback/20260709_fr05-order-status-rejected.md` へ記録本体を配置した。
> 本記録は 2026-07-09 に作成されながら計画リポジトリへ到達しておらず、2026-08-06 の未到達棚卸しで送付した。
> 以降のトリアージ・裁定は当該 Issue で行う。本書は実装リポジトリ側の控えである。

## 種別

要求の不足（注文状態モデルの列挙に「拒否」が欠けている）。

## 起点となる計画書

- 機能要求（FR）: FR-05（発注執行・注文状態の追跡）
- ユースケース（UC）: UC-01, UC-02
- 関連 ADR: ADR-0002（証券会社選定）
- 計画書リンク: `02_requirements/01_requirements.md`（FR-05）

## 現状（計画書の記述 / As-Is）

FR-05 は「注文状態（**受付・約定・失注・取消**）を追跡できる」と記載しており、証券会社による**拒否**が
列挙に含まれていない。

## 問題点 / あるべき姿（To-Be）

moomoo 等での実発注では、資金不足・値幅制限・不正な注文内容により**証券会社側で拒否**される注文が通常発生する。
これを表現・追跡・通知する状態が計画の注文状態モデルに無いと、実装（`OrderStatus`）と計画の記述が乖離する。
注文状態の列挙に「拒否（証券会社拒否）」を加え、リスク管理サービスによる発注前拒否（`OrderRejected` イベント。
注文はブローカーへ到達しない）とは別事象として区別することが望ましい。

## 実装で判明した経緯

PR #43（Issue #30）で `PaperBrokerAdapter` の入力検証を実装する際、実ブローカーが拒否する不正注文を表現する
`OrderStatus` の値が無いことが判明。`OrderStatus.Rejected`（証券会社拒否）を追加し、区別の根拠を
[IADR-0007](../docs/adr/IADR-0007_broker-rejection-vs-risk-rejection.md) に記録した。

## 提案（計画への反映案）

- 反映先候補: 要求更新（FR-05 の状態列挙）
- 提案内容: FR-05 の注文状態を「受付・約定・失注・取消・**拒否（証券会社拒否）**」へ更新し、備考に
  「リスク管理の発注前拒否（`OrderRejected`）とは区別する」旨を追記する。

## 影響範囲

- 実装は既に `OrderStatus.Rejected` を持つため、計画側の文言更新のみで整合する。
- 通知（FR-09）・監査（FR-11）で拒否を区別して扱う下流仕様に波及する（既に IADR-0007 で区別を明記済み）。
