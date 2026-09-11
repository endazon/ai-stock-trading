import {
  cloneElement,
  createContext,
  isValidElement,
  useContext,
  useEffect,
  useId,
  useRef,
  useState,
  type ButtonHTMLAttributes,
  type HTMLAttributes,
  type MouseEvent,
  type ReactElement,
  type ReactNode,
  type RefObject,
} from 'react';

// @platform/ui の Dialog 群（実物は @base-ui/react/dialog）のスタブ。
// 写すのは role / aria の同期（`role="dialog"` ＋ `aria-modal` ＋ Title / Description の自動結線）、
// **`initialFocus`**（取消側へ初期フォーカスを当てる既存の規律を単独リポのテストでも検証できるようにする）、
// Esc で閉じる、Base UI の **`render` prop 方式**（Radix の `asChild` ではない）だけ。
// フォーカストラップ・ポータル・背景は写さない。
interface DialogContextValue {
  open: boolean;
  setOpen: (open: boolean) => void;
  titleId: string;
  descriptionId: string;
}
const DialogContext = createContext<DialogContextValue | null>(null);

function useDialog(): DialogContextValue {
  const ctx = useContext(DialogContext);
  if (!ctx) throw new Error('Dialog のスタブ: Dialog* は Dialog（Root）の中で使う');
  return ctx;
}

export interface DialogProps {
  open?: boolean;
  defaultOpen?: boolean;
  onOpenChange?: (open: boolean) => void;
  children?: ReactNode;
}

/** ダイアログの開閉と状態を束ねる根（HTML 要素を描かない）。 */
export function Dialog({ open, defaultOpen = false, onOpenChange, children }: DialogProps) {
  const [inner, setInner] = useState(defaultOpen);
  const id = useId();
  const isOpen = open ?? inner;
  const setOpen = (next: boolean) => {
    if (open === undefined) setInner(next);
    onOpenChange?.(next);
  };
  return (
    <DialogContext.Provider
      value={{ open: isOpen, setOpen, titleId: `${id}-title`, descriptionId: `${id}-description` }}
    >
      {children}
    </DialogContext.Provider>
  );
}

type RenderProp = ReactElement<{ onClick?: (e: MouseEvent<HTMLElement>) => void }>;

/** Base UI の `render` prop（要素を渡すと、それに onClick を合成して描く）。 */
function renderOrButton(
  render: RenderProp | undefined,
  onClick: (e: MouseEvent<HTMLElement>) => void,
  props: ButtonHTMLAttributes<HTMLButtonElement>,
) {
  if (render && isValidElement(render)) {
    return cloneElement(render, {
      onClick: (e: MouseEvent<HTMLElement>) => {
        render.props.onClick?.(e);
        onClick(e);
      },
    });
  }
  return <button type="button" onClick={onClick} {...props} />;
}

export interface DialogButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  render?: RenderProp;
}

/** ダイアログを開くボタン。 */
export function DialogTrigger({ render, onClick, ...props }: DialogButtonProps) {
  const { setOpen } = useDialog();
  return renderOrButton(
    render,
    (e) => {
      onClick?.(e as MouseEvent<HTMLButtonElement>);
      setOpen(true);
    },
    props,
  );
}

/** ダイアログを閉じるボタン。見た目を付けるときは `render={<Button …>取消</Button>}`。 */
export function DialogClose({ render, onClick, ...props }: DialogButtonProps) {
  const { setOpen } = useDialog();
  return renderOrButton(
    render,
    (e) => {
      onClick?.(e as MouseEvent<HTMLButtonElement>);
      setOpen(false);
    },
    props,
  );
}

type InitialFocus =
  | boolean
  | RefObject<HTMLElement | null>
  | ((interactionType: 'mouse' | 'touch' | 'pen' | 'keyboard' | '') => HTMLElement | null | boolean | void);

export interface DialogContentProps extends Omit<HTMLAttributes<HTMLDivElement>, 'children'> {
  className?: string;
  children?: ReactNode;
  /** 開いたときにフォーカスを当てる要素（Base UI と同じ型）。省略時は最初のタブ可能要素。 */
  initialFocus?: InitialFocus;
}

const FOCUSABLE = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';

/** 画面の上へ重なる本体。開いているときだけ描く。 */
export function DialogContent({ initialFocus, children, ...props }: DialogContentProps) {
  const { open, setOpen, titleId, descriptionId } = useDialog();
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const popup = ref.current;
    if (!popup) return;
    let target: HTMLElement | null | undefined;
    if (typeof initialFocus === 'function') {
      const r = initialFocus('');
      if (r === false) return;
      target = r instanceof HTMLElement ? r : undefined;
    } else if (initialFocus === false) {
      return;
    } else if (initialFocus && typeof initialFocus === 'object') {
      target = initialFocus.current;
    }
    (target ?? popup.querySelector<HTMLElement>(FOCUSABLE) ?? popup).focus();
  }, [open, initialFocus]);

  if (!open) return null;
  return (
    <div
      ref={ref}
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
      tabIndex={-1}
      onKeyDown={(e) => {
        if (e.key === 'Escape') setOpen(false);
      }}
      {...props}
    >
      {children}
    </div>
  );
}

export interface DialogTitleProps extends HTMLAttributes<HTMLHeadingElement> {
  className?: string;
}

/** ダイアログの題名（<h2>）。`aria-labelledby` はスタブが id で結ぶ。 */
export function DialogTitle(props: DialogTitleProps) {
  const { titleId } = useDialog();
  return <h2 id={titleId} {...props} />;
}

export interface DialogDescriptionProps extends HTMLAttributes<HTMLParagraphElement> {
  className?: string;
}

/** ダイアログの本文（<p>）。`aria-describedby` はスタブが id で結ぶ。 */
export function DialogDescription(props: DialogDescriptionProps) {
  const { descriptionId } = useDialog();
  return <p id={descriptionId} {...props} />;
}

export interface DialogActionsProps extends HTMLAttributes<HTMLDivElement> {
  children: ReactNode;
}

/** 操作ボタンの帯。 */
export function DialogActions(props: DialogActionsProps) {
  return <div {...props} />;
}
