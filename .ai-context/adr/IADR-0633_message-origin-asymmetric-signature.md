---
title: IADR-0633 統制の入力になるメッセージの発行元の印を、発行元ごとの共有の鍵の HMAC から非対称の署名（ECDSA P-256）へ置き換え、受け手は確かめる鍵（公開の鍵）だけを持つ（旧い印は段 1 でだけ受ける。監査は記録だけの受け手にする）
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-05, FR-10, FR-11, ADR-0009, ADR-0080, IADR-0627, IADR-0610, IADR-0623]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md (決定 1)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・FR-11・NFR-05)
---

# IADR-0633: 発行元の印を非対称の署名にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**（[IADR-0627](IADR-0627_message-origin-signing-for-control-inputs.md) の決定 2・6 と §残余 1 を置き換える）
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」）／ Claude Code（起案・実装）

## 起点・関連

- 非機能 `NFR`（クラウド移行の前の堅牢化）。鍵の置き場は NFR-05・ADR-0080 決定 1 に従う。
- 置き換える決定: IADR-0627（発行元ごとの共有の鍵の HMAC）。表（24 型）・段・時刻の窓・再送の排除・配線の呼び出しは引き継ぐ。
- 実装仕様書: [`.ai-context/specs/20261107_message-origin-asymmetric-signature.md`](../specs/20261107_message-origin-asymmetric-signature.md)

## コンテキストと課題

- IADR-0627 の印は HMAC で、受け手は確かめる型の発行元の鍵（共有の鍵）を持つ。鍵は発行元ごとで型ごとではないので、受け手の Pod の中身が漏れると、
  その Pod は持っている鍵の発行元として、その発行元が出せる**どの型**も作れる（IADR-0627 §残余 1 が想定した「確かめる型だけ」より広い）。
- 発行元が秘密を持ち、受け手は確かめるだけの鍵を持つ形にすれば、受け手の Pod の中身が漏れても発行元を名乗れない。

## 検討した選択肢

1. **非対称の署名。発行元は秘密の鍵、受け手は公開の鍵だけ（採用）。**
2. HMAC の鍵を「発行元 × 型」に分ける（親の鍵から型ごとの鍵を導き、受け手には確かめる型の分だけを渡す）— 受け手は確かめる型の発行元を引き続き名乗れる（例: リスク管理は発注執行の型を名乗れる）。採らない。
3. 現状のまま受容する — 所有者の裁定（中程度もすべて直す）に合わない。採らない。

### 署名の方式の選び方

| 候補 | .NET 10 の標準 | 判断 |
| --- | --- | --- |
| Ed25519 | **無い**（`System.Security.Cryptography` に型が無いことを .NET 10.0.14 の公開型の列挙で確かめた。合成の ML-DSA の名前の中にだけ現れる） | 外部の実装（BouncyCastle・NSec など）が要る。採らない |
| ML-DSA（.NET 10 の新設） | 在るが OpenSSL 3.5 以上が要る。この環境の OpenSSL 3.0 では `MLDsa.IsSupported=False` | 稼働イメージの OpenSSL に依存して動かないことがある。採らない |
| **ECDSA P-256 / SHA-256** | **在る**（`ECDsa`。OpenSSL・CNG のどちらでも動く。鍵の形 PKCS#8・SubjectPublicKeyInfo を標準で読み書きできる） | **採る**。新しい依存を足さない（Central Package Management への追加なし）。手元の `openssl` で鍵の組を作れる |

- 署名は IEEE P1363 の固定長（64 バイト）。ECDSA の署名は乱数を含むので同じ本文でも毎回違うが、確かめには影響しない。
- 速さ: 受け手 1 件あたり確かめ 1 回（公開の鍵の数だけ。入れ替えの間は 2 回まで）。統制の入力のメッセージの量（判断・約定・観測）に対して十分に小さい。

## 決定

1. **署名**: 発行元は自分の秘密の鍵（P-256・PKCS#8 DER の base64）で、IADR-0627 と同じ正準形（型の完全名・メッセージ ID・発行元・時刻・本文の正規形）を、
   先頭の版を `v2` にして署名し、見出し `ast-origin-sig2` に `v2.<base64>` で載せる（`MessageOriginSignature`）。発行元・ID・時刻の見出しは IADR-0627 と同じ。
2. **受け手**は、確かめる型の発行元の公開の鍵（SubjectPublicKeyInfo DER の base64）だけを持つ。公開の鍵からは署名できない（`Sign` は null。試験で固定）。
   - `ast-origin-sig2` が在れば、新しい署名だけで判定する（合わなければ `Mismatch`。旧い印へ倒さない）。
   - 入れ替えの間だけ `PreviousVerifyKeys` の公開の鍵でも確かめる。
3. **鍵の配り方**:
   - OpenBao `cloud/ast/ast-message-origin-signing-keys`（秘密の鍵）・`cloud/ast/ast-message-origin-verify-keys`（公開の鍵）。property ＝ 発行元の識別子。
     ESO が Secret `ast-message-origin-signing-keys`・`ast-message-origin-verify-keys` へ写す（`externalSecrets.messageOriginSigningKeys`・`messageOriginVerifyKeys`）。
   - chart の `global.messageOrigin.signers`（7 つ）のサービスにだけ自分の秘密の鍵を env `MessageOrigin__SigningKey` で渡す。
     `verifiers`（受け手 → 発行元）の受け手にだけ、確かめる発行元の公開の鍵を `MessageOrigin__VerifyKeys__<発行元>` で渡す。コードの要る鍵と試験で突き合わせる。
   - 鍵の組は、オーナーが `scripts/ast-message-origin-keygen.sh --live` で手元で作る（基盤の `seed-secrets.sh` の `gen` は乱数のバイトしか作れない）。
     鍵はシェルの変数にだけ置き、画面・ディスク・引数に出さず、基盤の `put-external-secret.sh` の標準入力へ秘密の鍵 → 公開の鍵の順に流す。
     既に在れば作り直さず、`--rotate` で組ごと作り直す。秘密の鍵の性質は ADR-0080 決定 1 の区分 1（外から持ち込む値）として扱う。
   - 基盤の OpenBao の policy `ast-eso` は `cloud/data/ast/*` を読めるので、基盤の変更は要らない。
4. **段と移行**:
   - 段 1（`Report`・既定）: 旧い HMAC の印（IADR-0627。見出し `ast-origin-sig`）も、旧い鍵が構成されていれば確かめる。新しい署名が無く旧い印だけが合うものは
     結果 `Legacy`（EventId 16261・メトリクスの `reason=legacy`）として記録して通す。発行元は旧い鍵があれば新旧の両方を付ける（旧い受け手が読めるように）。
   - 段 2（`Enforce`・values-cloud）: 新しい署名で `Valid` のものだけを処理する（`Legacy` は処理しない）。
   - 段 2 の起動の止め（fail-closed）: 発行元なのに秘密の鍵が無い／確かめる発行元の公開の鍵が 1 つでも無い／旧い共有の鍵が構成されている。
   - 段に依らず止める: 読めない鍵・P-256 でない鍵・秘密と公開の取り違え・発行しないサービスに秘密の鍵・記録だけの受け手に旧い鍵。
   - chart の `legacyHmac.enabled`（既定 false）は段 1 でだけ描ける（段 2 と併用すると描画で止める）。values-cloud は使わない。
5. **監査は記録だけの受け手にする**（`MessageOriginCatalog.RecordOnlyServices`）。監査の受ける表の型すべてのハンドラの前で確かめ、`Valid` 以外を
   構造化した Warning（EventId 16261）とメトリクス（`outcome=admitted`）に残して、**段 2 でも処理を続ける**（台帳から事象を捨てると証跡が欠ける）。
   監査に要るのは 7 つの公開の鍵だけで、秘密は増えない。段 2 で公開の鍵が欠ければ起動で止める（構成の誤りを黙って通さない）。
6. 通知は対象外のまま（`ExcludedServices`）。理由: 通知は利用者へ知らせるだけで、操作は認可つきの REST / gRPC を通る。統制の型だけを確かめても、
   通知の種類の大半（統制の型の外）は守れず、拒めば知らせるべき事象の取りこぼしになる。偽の事象の検出は監査の記録（決定 5）で行う。
7. 注文の承認の印（IADR-0610。リスク管理と発注執行の共有の鍵の HMAC）は本 IADR では変えない。発注執行は本 IADR の署名でも注文の承認を確かめる
   （IADR-0627 決定 7 を引き継ぐ）ので、段 2 では共有の鍵だけで承認を偽ることはできない。

## 結果

- 段 2 では、受け手の Pod の中身が漏れても、その Pod は他のサービスを発行元として名乗れない（持つのは公開の鍵だけ）。
  名乗れるのは、漏れた Pod 自身が発行元として許された型だけである（自分の秘密の鍵）。
- 鍵は 7 つの組（秘密 7・公開 7）。Secret は 2 つになり、旧い Secret `ast-message-origin-keys` はクラウドでは読まない。
- 段 1 の既定（鍵なし）では処理は変わらない。

## 残余・フォローアップ

1. **発行元の Pod の中身が漏れると、その発行元が出せる型は名乗れる**（秘密の鍵を持つため）。発行元の権限は表で最小にしている。
2. 秘密の鍵 7 つは 1 つの Secret の別のキーに置く。各 Pod は自分のキーだけを env で受けるが、名前空間の Secret を読める主体（ESO・管理者）は全部を読める。
   Pod の ServiceAccount のトークンは置かない（IADR-0609）。Secret を発行元ごとに分けるのは、名前空間の Secret の読みを Pod に許す構成が現れたときに行う。
3. 鍵の入れ替えは手順（`--rotate` と作り直し）だけで、`PreviousVerifyKeys` を chart から渡す口はまだ無い。段 2 での入れ替えは、キューを空にしてから行う。
4. 監査の台帳の列に確かめの結果を残すのは本 IADR では入れない（台帳の形の変更と移行を伴う）。いまの検出は Warning とメトリクスで行う。
5. 再送の記録がプロセスの中にだけあること・印が受け手に束縛されないこと・同時に届いた同じ ID の扱いは、IADR-0627 §残余 2・7・8 のまま。
6. 段 2 へ上げる前に、段 1 のまま受け手のログに EventId 16261（`legacy` を含む）が出ないことを確かめる（IADR-0627 §残余 3 と同じ）。
