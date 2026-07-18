// IADR-0080: @foundation/ui/NotFound のテスト/型検査用スタブ。実体は platform 合成時に解決。存在秘匿の 404 画面。
export function NotFound() {
  return (
    <main style={{ padding: '2rem', textAlign: 'center' }}>
      <h1>見つかりませんでした</h1>
      <p>お探しのページは存在しないか、アクセスできません。</p>
    </main>
  );
}
