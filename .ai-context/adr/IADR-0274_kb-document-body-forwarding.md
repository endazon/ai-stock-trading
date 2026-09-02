---
title: IADR-0274 KB 文書の本文は POST /documents の Body として送り、1 MB 超は切り詰めずメタデータのみで登録する
type: impl-adr
status: Accepted
related_ids: [FR-08, ADR-0001, ADR-0010, IADR-0069, IADR-0093]
author: claude (Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0274: KB 文書の本文は POST /documents の Body として送り、1 MB 超は切り詰めずメタデータのみで登録する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: claude（[#565](https://github.com/endazon/ai-stock-trading/issues/565)）

## 起点・関連

- 関連する計画書 ID: **FR-08**（確定報告書・収集情報・判断根拠を platform ナレッジベースへ保存し
  RAG 検索に利用する）
- 関連する実装仕様書: [20260902_565_kb-document-body](../specs/20260902_565_kb-document-body.md)
- 関連 IADR: [IADR-0069](IADR-0069_knowledge-base-rag-foundation.md)（KB 保存・RAG 取得の基盤結線。
  「本文は受け取らない」というスコープ境界を書いた側。本 ADR がそのスコープ境界を解く）、
  [IADR-0093](IADR-0093_kb-writer-cross-realm-s2s.md)（s2s 認証。本 ADR は認証経路を変更しない）

## コンテキストと課題

issue #565 の指摘: `HttpKnowledgeBaseWriter` が platform `POST /documents` へ送る DTO
（`CreateDocumentBody`）に本文（コンテンツ）のフィールドが無く、確定報告書・収集情報の本文が
1 バイトも渡っていないため RAG 検索が本文をヒットしようがない。IADR-0069 はこれを既知のスコープ境界
として記録していた。

調査の結果、**MSP `DocumentService` には FR-21（本文の直接受け入れ）が既に実装済み**であることを
確認した（`CreateDocumentRequest.Body`・`DocumentBodyIntake`）。したがって「基盤へ起票する」という
issue #565 の対処順序①は不要であり、②（AST 側アダプタの追随）へ直接進める。

決めるべきは 2 点。

1. `CreateDocumentBody` へ `Body` をどう足すか。
2. platform 側が 1 MB 超を 413 で拒否する（`DocumentBodyIntake.MaxBytes`）ため、AST 側でも上限判定が
   要る。**超過時にどう振る舞うか**（送信全体を諦めるか、本文だけ落として登録は続けるか、切り詰めて
   送るか）。

## 検討した選択肢（超過時の振る舞い）

| 案 | 内容 | 判定 |
| --- | --- | --- |
| A | 本文を切り詰めて送る | 却下 |
| B | 上限超過を保存失敗（`NotSaved`）として扱う | 却下 |
| C | 本文なしで登録し、メタデータの保存は維持する | **採用** |

### 案 A を退けた理由

platform 側 `DocumentBodyIntake` は明示的に「切り詰めて成功を返さない」（FR-21 受け入れ基準⑥）。
送信側で先に切り詰めて 1 MB 以内に収めて送ると、**platform 側の 413 チェックをすり抜けて「一部だけ
索引された文書」が黙って作られる**。これは platform 側が構造的に禁じている状態を AST 側の判断で
作ってしまうことになり、FR-21 の意図（全文が索引される・切り詰めた成功を返さない）に反する。

### 案 B を退けた理由

1 MB 超の本文（長大な確定報告書・月報など）を理由に**メタデータの保存ごと**失敗させると、
「収集情報・確定報告書を KB へ保存している」という記録そのものが欠落する。現状（本文が 1 バイトも
渡っていない）と比べても後退であり、「本文がヒットしない」だけの縮退（案 C）のほうが安全側である。
既存の fail-safe（非 2xx・例外・タイムアウトは `NotSaved`）は「送信そのものが失敗した」場合の縮退で
あり、「送ろうとした内容が大きすぎる」は性質が異なる——後者は送信前に判定できるため、判定できるなら
より情報を残す側（案 C）を選べる。

## 決定

### 決定1: `CreateDocumentBody` に `Body`（末尾・既定 null）を追加し `document.Content` を渡す

platform `CreateDocumentRequest.Body` と同じ理由（既存クライアントの位置引数呼び出しを壊さない）で
**末尾へ既定値つきで追加する**。

### 決定2: 上限判定は UTF-8 バイト数の純関数（`KnowledgeBodyLimits.Exceeds`）とし、上限値は platform 側と同値（1 MB）にする

文字数で判定すると日本語本文が実サイズの 3 分の 1 で通過し、上限が事実上 3 MB へ化ける
（platform 側 `DocumentBodyIntake.ExceedsLimit` のコメントと同じ理由）。**上限値を platform 側より
緩くすると、送るたびに 413 を引いて無駄な往復になる**ため同値にする。

### 決定3: 1 MB 超は Body を外して送り、メタデータの保存は維持する（案 C）

`KnowledgeBodyLimits.Exceeds(document.Content)` が真なら `CreateDocumentBody.Body = null` にして送る
（送信自体は行う）。`LogWarning` で縮退を残す。呼び出し側（収集サイクル・報告確定）へは例外を伝播
しない——既存の fail-safe と同じ向き。

### 決定4: owner 属性の POST 時上書きは AST 側で対処しない（現状追認）

platform `DocumentBodyIntake.WithOwner`（ADR-0060 決定3・#1057）は `POST /documents` 作成時、要求由来の
`owner` を常に認証済み主体へ上書きする。AST が補完する `owner=system`（#520・予約値）は作成時点では
実効を持たないが、これは **#520 が対処すべき既存の挙動であり本 ADR のスコープ外**——本 ADR は Body の
追加のみを扱う。実環境確認（仕様書参照）で s2s クライアントの role（`platform-operator`）が
`POST /documents` の書き込みロール要件を満たすことは確認済みであり、Body 付き POST 自体は通る。

## 理由

- 切り詰めない・保存自体は諦めない、という 2 つの安全側判断を組み合わせることで、**「索引の正確性」**
  （切り詰めた本文を索引しない）と**「記録の完全性」**（メタデータだけでも残す）の両方を守る。
- 上限値を platform 側と同値にすることで、**送信前に無駄な往復を避けつつ、境界のズレによる想定外の
  413 を防ぐ**（送信側が緩ければ 413 を引くだけ、送信側が厳しければ実は送れる本文を諦めるだけで、
  いずれも実害は小さいが、同値にしておけば境界のテストがそのまま両リポジトリの契約になる）。

## 結果

- 良い影響: 確定報告書・収集情報の本文が RAG 検索の対象になる経路が開通した（ローカル環境での
  実ヒット確認は別途インフラ整備が要る。仕様書「実環境確認」参照）。IADR-0069 のスコープ境界が解消された。
- 悪い影響・トレードオフ: 1 MB 超の本文は引き続き検索対象にならない（メタデータのみ）。将来的に
  分割送信・要約後保存などで対処する余地があるが、現時点の FR-08 は「本文が検索できること」を
  求めており、上限超過時の分割は計画に明記が無いため実装しない。
- フォローアップ: 実環境での RAG ヒット確認（結合テスト）は #565 の受け入れ基準③として実環境残件の
  まま残す。ローカルクラスタの Istio mTLS ドリフト・`KnowledgeBase:Auth:Authority` の realm 名誤り・
  Voyage AI API キー未設定の 3 点が確認できており、いずれも本 PR の範囲外（仕様書「実環境確認」参照）。

## 関連

- Supersedes: なし（[IADR-0069](IADR-0069_knowledge-base-rag-foundation.md) の決定は生きている。
  本 ADR はその「本文は受け取らない」というスコープ境界の記述のみを更新する）
- Superseded by: なし
