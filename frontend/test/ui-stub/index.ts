// @platform/ui のテスト/型検査用スタブ（test-only）。**実体は合成時に解決する**
// （基盤の pnpm workspace パッケージ `packages/ui`。`tsconfig.json` の paths が
// `../../packages/ui/src/index.ts` を指す）。単独リポでは `tsconfig.standalone.json` の paths と
// `vitest.config.ts` / `e2e/vite.harness.config.ts` の alias がここへ差し替える（`@foundation` スタブと同じ二重性。
// IADR-0080 決定 2）。
//
// 写し方の規律:
//   - **各部品を素の HTML へ写し、`role` / `aria-*` / テキストの出方を実物と一致させる**
//     （テストは役割とテキストで引くため。見た目のクラス名・アイコン・cva は写さない）。
//   - `role` を**実物が付けない部品にはここでも付けない**（例: EmptyState・Note）。
//     スタブが余計な role を持つと、単独リポのテストだけ通って合成時に落ちる。
//   - 文言を持たない（実物と同じ。IADR-0125 決定 1）。
//
// 🔴 **網羅の安全弁は基盤側の `pnpm -r run typecheck` である。** 実物の公開面（`packages/ui/src/index.ts`）に
//    在って本スタブに無い export を本ユニットが使うと、単独リポの typecheck は落ちるので気付ける。
//    逆（スタブに在って実物に無い）は単独リポでは気付けず、**合成時の typecheck / build が落とす**。
//    実物の公開面が変わったら本ファイルの export 一覧を揃える（2026-09-12 時点の実物と一致）。
export { cn } from './cn';
export { Button, buttonVariants, type ButtonProps } from './Button';
export { StatusBadge, type StatusBadgeProps } from './StatusBadge';
export { Tag, tagVariants, type TagProps } from './Tag';
export {
  Input,
  inputVariants,
  type InputProps,
  Textarea,
  textareaVariants,
  type TextareaProps,
  Select,
  selectVariants,
  type SelectProps,
  Label,
  type LabelProps,
} from './formControls';
export { Alert, type AlertProps } from './Alert';
export { Card, CardHeader, CardTitle, CardContent } from './Card';
export {
  Table,
  TableCaption,
  TableHead,
  TableBody,
  TableRow,
  TableHeaderCell,
  TableCell,
} from './Table';
export { Tabs, TabsList, TabsTrigger, TabsContent } from './Tabs';
// 待ち・空・エラーの三部品（UI/UX 改善 2026-09-12 で基盤へ追加）。
export { Spinner, type SpinnerProps } from './Spinner';
export { Skeleton, type SkeletonProps } from './Skeleton';
export { LoadingState, type LoadingStateProps } from './LoadingState';
export { EmptyState, type EmptyStateProps } from './EmptyState';
export { ErrorState, type ErrorStateProps } from './ErrorState';
// hi-fi モック由来の区画部品（同上）。
export { Panel, type PanelProps } from './Panel';
export { Stat, type StatProps } from './Stat';
export { ProgressBar, type ProgressBarProps } from './ProgressBar';
export { Kv, type KvProps, KvItem, type KvItemProps } from './Kv';
export { Note, type NoteProps } from './Note';
export { Rule } from './Rule';
// Base UI（@base-ui/react）土台の重ね物（同上）。スタブは role / aria の同期だけを写す。
export {
  Dialog,
  DialogTrigger,
  DialogClose,
  DialogContent,
  type DialogContentProps,
  DialogTitle,
  DialogDescription,
  DialogActions,
} from './Dialog';
export {
  TooltipProvider,
  Tooltip,
  TooltipTrigger,
  TooltipContent,
  type TooltipContentProps,
} from './Tooltip';
