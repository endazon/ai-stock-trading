import {
  createContext,
  useContext,
  useId,
  useState,
  type ButtonHTMLAttributes,
  type HTMLAttributes,
} from 'react';

// @platform/ui の Tabs 群（実物は @radix-ui/react-tabs）のスタブ。
// 写すのは WAI-ARIA の tabs パターン（tablist / tab + aria-selected + aria-controls / tabpanel）と
// 「非活性の内容は描かない」既定だけ。ロービングタブインデックス（矢印キー移動）は写さない。
interface TabsContextValue {
  value: string | undefined;
  setValue: (v: string) => void;
  baseId: string;
}
const TabsContext = createContext<TabsContextValue | null>(null);

function useTabs(): TabsContextValue {
  const ctx = useContext(TabsContext);
  if (!ctx) throw new Error('Tabs のスタブ: TabsList / TabsTrigger / TabsContent は Tabs の中で使う');
  return ctx;
}

export interface TabsProps extends Omit<HTMLAttributes<HTMLDivElement>, 'defaultValue' | 'dir'> {
  value?: string;
  defaultValue?: string;
  onValueChange?: (value: string) => void;
  orientation?: 'horizontal' | 'vertical';
}

export function Tabs({ value, defaultValue, onValueChange, orientation: _o, children, ...props }: TabsProps) {
  const [inner, setInner] = useState(defaultValue);
  const baseId = useId();
  const current = value ?? inner;
  const setValue = (v: string) => {
    if (value === undefined) setInner(v);
    onValueChange?.(v);
  };
  return (
    <TabsContext.Provider value={{ value: current, setValue, baseId }}>
      <div {...props}>{children}</div>
    </TabsContext.Provider>
  );
}

export function TabsList(props: HTMLAttributes<HTMLDivElement>) {
  return <div role="tablist" {...props} />;
}

export function TabsTrigger({
  value,
  onClick,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement> & { value: string }) {
  const { value: current, setValue, baseId } = useTabs();
  const active = current === value;
  return (
    <button
      type="button"
      role="tab"
      id={`${baseId}-trigger-${value}`}
      aria-selected={active}
      aria-controls={`${baseId}-content-${value}`}
      data-state={active ? 'active' : 'inactive'}
      tabIndex={active ? 0 : -1}
      onClick={(e) => {
        onClick?.(e);
        if (!e.defaultPrevented) setValue(value);
      }}
      {...props}
    />
  );
}

export function TabsContent({
  value,
  forceMount,
  ...props
}: HTMLAttributes<HTMLDivElement> & { value: string; forceMount?: true }) {
  const { value: current, baseId } = useTabs();
  const active = current === value;
  if (!active && !forceMount) return null;
  return (
    <div
      role="tabpanel"
      id={`${baseId}-content-${value}`}
      aria-labelledby={`${baseId}-trigger-${value}`}
      data-state={active ? 'active' : 'inactive'}
      hidden={!active}
      tabIndex={0}
      {...props}
    />
  );
}
