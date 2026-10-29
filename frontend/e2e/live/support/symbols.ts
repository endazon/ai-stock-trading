// NFR / SC-02 / FR-13 / IADR-0601（#1419 / E-61・A-047）: live の取引影響層で監視銘柄に足す**試験銘柄のコード**（純関数）。
// 常に `E2E` ＋ 数字 6 桁にする。runId の数字（下 6 桁）を使い、6 桁に満たなければ時刻の数字で補う
// （runId に数字が無いと `E2E` だけになり、実行ごとに一意でなくなるため）。
export function testSymbolOf(runId: string, now: Date = new Date()): string {
  const fromRun = runId.replace(/\D/g, '').slice(-6);
  const fill = String(now.getTime()).slice(-6);
  return `E2E${(fill + fromRun).slice(-6)}`;
}
