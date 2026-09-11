import type {
  InputHTMLAttributes,
  LabelHTMLAttributes,
  ReactNode,
  SelectHTMLAttributes,
  TextareaHTMLAttributes,
} from 'react';

// @platform/ui の Input / Textarea / Select / Label のスタブ。
// 写すのは a11y に効く振る舞いだけ:
//   - `invalid` バリアントと `aria-invalid` を必ず揃える（実物と同じ）。
//   - Label の `requiredHint` は sr-only テキストとして出る（記号 * は aria-hidden）。

export type InputProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'size'> & {
  inputSize?: 'sm' | 'md' | 'lg' | null;
  invalid?: boolean | null;
};
export function inputVariants(opts?: { inputSize?: InputProps['inputSize']; invalid?: InputProps['invalid'] }): string {
  return `input ${opts?.inputSize ?? 'md'}${opts?.invalid ? ' invalid' : ''}`;
}
export function Input({ inputSize: _inputSize, invalid, ...props }: InputProps) {
  return <input aria-invalid={invalid ? true : props['aria-invalid']} {...props} />;
}

export type TextareaProps = TextareaHTMLAttributes<HTMLTextAreaElement> & { invalid?: boolean | null };
export function textareaVariants(opts?: { invalid?: TextareaProps['invalid'] }): string {
  return `textarea${opts?.invalid ? ' invalid' : ''}`;
}
export function Textarea({ invalid, rows, ...props }: TextareaProps) {
  return <textarea rows={rows ?? 4} aria-invalid={invalid ? true : props['aria-invalid']} {...props} />;
}

export type SelectProps = Omit<SelectHTMLAttributes<HTMLSelectElement>, 'size'> & {
  selectSize?: 'sm' | 'md' | 'lg' | null;
  invalid?: boolean | null;
};
export function selectVariants(opts?: { selectSize?: SelectProps['selectSize']; invalid?: SelectProps['invalid'] }): string {
  return `select ${opts?.selectSize ?? 'md'}${opts?.invalid ? ' invalid' : ''}`;
}
export function Select({ selectSize: _selectSize, invalid, children, ...props }: SelectProps) {
  return (
    <select aria-invalid={invalid ? true : props['aria-invalid']} {...props}>
      {children}
    </select>
  );
}

export interface LabelProps extends LabelHTMLAttributes<HTMLLabelElement> {
  requiredHint?: ReactNode;
  children: ReactNode;
}
export function Label({ requiredHint, children, ...props }: LabelProps) {
  return (
    <label {...props}>
      {children}
      {requiredHint === undefined ? null : (
        <>
          <span aria-hidden>*</span>
          <span className="sr-only">{requiredHint}</span>
        </>
      )}
    </label>
  );
}
