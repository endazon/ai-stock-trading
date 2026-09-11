import type {
  HTMLAttributes,
  TableHTMLAttributes,
  TdHTMLAttributes,
  ThHTMLAttributes,
} from 'react';

// @platform/ui の Table 群のスタブ。表構造の a11y（caption は sr-only、th の scope 既定 col）だけを写す。
export function Table(props: TableHTMLAttributes<HTMLTableElement>) {
  return (
    <div>
      <table {...props} />
    </div>
  );
}
export function TableCaption({ className, ...props }: HTMLAttributes<HTMLTableCaptionElement>) {
  return <caption className={className ?? 'sr-only'} {...props} />;
}
export function TableHead(props: HTMLAttributes<HTMLTableSectionElement>) {
  return <thead {...props} />;
}
export function TableBody(props: HTMLAttributes<HTMLTableSectionElement>) {
  return <tbody {...props} />;
}
export function TableRow(props: HTMLAttributes<HTMLTableRowElement>) {
  return <tr {...props} />;
}
export function TableHeaderCell({ scope, ...props }: ThHTMLAttributes<HTMLTableCellElement>) {
  return <th scope={scope ?? 'col'} {...props} />;
}
export function TableCell(props: TdHTMLAttributes<HTMLTableCellElement>) {
  return <td {...props} />;
}
