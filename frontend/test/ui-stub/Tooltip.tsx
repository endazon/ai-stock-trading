import {
  cloneElement,
  createContext,
  isValidElement,
  useContext,
  useId,
  useState,
  type ButtonHTMLAttributes,
  type HTMLAttributes,
  type ReactElement,
  type ReactNode,
} from 'react';

// @platform/ui の Tooltip 群（実物は @base-ui/react/tooltip）のスタブ。
// 写すのは `role="tooltip"` と、ホバー／フォーカスで開閉する振る舞い、Base UI の `render` prop だけ。
// ポータル・配置計算・遅延の共有は写さない。
interface TooltipContextValue {
  open: boolean;
  setOpen: (open: boolean) => void;
  id: string;
}
const TooltipContext = createContext<TooltipContextValue | null>(null);

function useTooltip(): TooltipContextValue {
  const ctx = useContext(TooltipContext);
  if (!ctx) throw new Error('Tooltip のスタブ: Tooltip* は Tooltip（Root）の中で使う');
  return ctx;
}

/** 実物は遅延を共有する Provider。スタブでは素通し。 */
export function TooltipProvider({ children }: { children?: ReactNode; delay?: number; closeDelay?: number }) {
  return <>{children}</>;
}

export interface TooltipProps {
  open?: boolean;
  defaultOpen?: boolean;
  onOpenChange?: (open: boolean) => void;
  children?: ReactNode;
}

export function Tooltip({ open, defaultOpen = false, onOpenChange, children }: TooltipProps) {
  const [inner, setInner] = useState(defaultOpen);
  const id = useId();
  const isOpen = open ?? inner;
  const setOpen = (next: boolean) => {
    if (open === undefined) setInner(next);
    onOpenChange?.(next);
  };
  return <TooltipContext.Provider value={{ open: isOpen, setOpen, id }}>{children}</TooltipContext.Provider>;
}

type TriggerElement = ReactElement<Record<string, unknown>>;

export interface TooltipTriggerProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  render?: TriggerElement;
}

export function TooltipTrigger({ render, ...props }: TooltipTriggerProps) {
  const { open, setOpen, id } = useTooltip();
  const handlers = {
    onMouseEnter: () => setOpen(true),
    onMouseLeave: () => setOpen(false),
    onFocus: () => setOpen(true),
    onBlur: () => setOpen(false),
    'aria-describedby': open ? id : undefined,
  };
  if (render && isValidElement(render)) {
    return cloneElement(render, handlers);
  }
  return <button type="button" {...handlers} {...props} />;
}

export interface TooltipContentProps extends HTMLAttributes<HTMLDivElement> {
  className?: string;
  sideOffset?: number;
}

export function TooltipContent({ sideOffset: _sideOffset, ...props }: TooltipContentProps) {
  const { open, id } = useTooltip();
  if (!open) return null;
  return <div role="tooltip" id={id} {...props} />;
}
