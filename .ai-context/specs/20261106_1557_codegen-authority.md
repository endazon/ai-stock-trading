---
title: イメージのビルド時の Wolverine の codegen に、無害な仮の Auth__Authority を与える（#1529 の守りは弱めない）
type: spec
status: accepted
related_ids: [NFR, IADR-0612, IADR-0129, IADR-0609]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
---

# 仕様書: ビルド時の codegen と Production の認証の既定の検査（#1557）

## 起点

- #1557。基盤の submodule の更新（MSP#2463）でイメージのビルドが赤になった（risk-management-service・configuration-service）。
- `backend/Dockerfile` の build 段の `codegen write` が exit 134 で止まる。#1529（IADR-0612）の `StandaloneRealmGuard.Validate` が、Production（sdk イメージの既定の環境名）で `Auth:Authority` が空のときに、ホストの組み立ての時点で例外を投げるためである。AST の CI はイメージを作らないので見つからなかった。
- 手元で再現した。RiskManagementService の `dotnet run … -- codegen write`（環境名なし＝Production）は exit 134 になった。

## 決定

1. **codegen の段だけに、仮の `Auth__Authority=https://codegen.invalid/realms/codegen` を与える**（Dockerfile の `dotnet run … -- codegen write` の行の環境変数）。
   - `.invalid` は名前解決しない予約の TLD。codegen はホストを組み立てるだけで Start しないので、どこへも接続しない。
   - 単体実行向けのレルム（`/realms/ai-stock-trading`）を指さないので、守りの 2 つ目の条件にも掛からない。
   - 生成物は Wolverine のハンドラの配線で、Authority の値に依存しない。
   - 値は `RUN` の行の中だけで効く。稼働のイメージ（runtime 段）には残らない（`ENV` にしない）。
2. **守り（`StandaloneRealmGuard`）は変えない。** 稼働の起動では、従来どおり配備の値（`global.authAuthority` など）が要る。
3. **環境名は変えない**（Development にすると Program.cs の配線が変わり、生成物がずれ得る）。
4. Wolverine の codegen の実行を検知して守りを飛ばす形は、採らない。起動の引数で守りが外れる経路を作らないためである。Dockerfile の 1 行で閉じる今の形のほうが、守りの射程が単純になる。

## 母集合（規則 9）

- `codegen write` を呼ぶ箇所: `backend/Dockerfile` の build 段の 1 行だけ（`git grep "codegen write"` で、他は注記と試験）。
- Production で起動時に構成を要求する守り: `StandaloneRealmGuard` だけが codegen を止めた。Wolverine を配線する全 11 サービスで、仮の Authority を与えた codegen が rc=0 になった（下の手元の実走）。

## 受け入れ基準

- [x] Wolverine を配線する全サービス（11）で、Dockerfile と同じ形の codegen が通る（手元の実走。環境名なし・仮の Authority だけを与えた。値なしでは RiskManagementService で exit 134 を再現した）。
- [x] Dockerfile の codegen の段が仮の Authority を渡し、その値が `.invalid` で、単体実行向けのレルムを指さず、Production の守りを通ることを試験で固定する（`StandaloneRealmGuardTests`）。
- [x] Production で Authority が無い・単体実行向けのレルムを指すなら、従来どおり止まる（既存の試験）。値は稼働の段（`ENV`）に置かない（同上）。

## 範囲外

- AST の CI でイメージを作ること（ビルドの時間がかかる。基盤の Images の段が作る）。
