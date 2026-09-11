// @platform/ui の cn() のスタブ。実物は clsx ＋ tailwind-merge（競合ユーティリティの後勝ち解決）だが、
// 単独リポにはどちらも無く、テストはクラス名で引かない。真の値だけを空白で連結する。
type ClassValue = string | number | boolean | null | undefined | ClassValue[] | Record<string, unknown>;

export function cn(...inputs: ClassValue[]): string {
  const out: string[] = [];
  const push = (v: ClassValue) => {
    if (!v) return;
    if (typeof v === 'string' || typeof v === 'number') out.push(String(v));
    else if (Array.isArray(v)) v.forEach(push);
    else if (typeof v === 'object') {
      for (const [k, on] of Object.entries(v)) if (on) out.push(k);
    }
  };
  inputs.forEach(push);
  return out.join(' ');
}
