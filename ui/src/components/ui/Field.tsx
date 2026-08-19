"use client";

import { useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes } from "react";

import styles from "./ui.module.css";

export interface FieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "id"> {
  readonly label: string;
  readonly hint?: string;
  readonly error?: string;
}

/**
 * Etiketli form alanı.
 *
 * <p>
 * Kimlikler <c>useId</c> ile üretiliyor: etiket ile alanın bağı elle yazılan
 * bir <c>id</c>'ye bırakılırsa aynı ekranda iki alan aynı kimliği alır ve
 * ekran okuyucu yanlış etiketi okur. İpucu ve hata metinleri de
 * <c>aria-describedby</c> ile bağlanıyor, yoksa yalnızca gören kullanıcıya
 * ulaşırlar.
 * </p>
 */
export function Field({ label, hint, error, className, ...rest }: FieldProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;

  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(" ");

  return (
    <div className={styles.field}>
      <label className={styles.fieldLabel} htmlFor={id}>
        {label}
      </label>
      <input
        {...rest}
        id={id}
        className={[styles.fieldControl, error ? styles.fieldInvalid : null, className]
          .filter(Boolean)
          .join(" ")}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy || undefined}
      />
      {hint ? (
        <p className={styles.fieldHint} id={hintId}>
          {hint}
        </p>
      ) : null}
      {error ? (
        <p className={styles.fieldError} id={errorId}>
          {error}
        </p>
      ) : null}
    </div>
  );
}

export interface SelectOption {
  readonly value: string;
  readonly label: string;
}

export interface SelectFieldProps
  extends Omit<SelectHTMLAttributes<HTMLSelectElement>, "id" | "children"> {
  readonly label: string;
  readonly hint?: string;
  readonly error?: string;
  readonly options: readonly SelectOption[];
}

/**
 * Etiketli açılır liste.
 *
 * <p>
 * Ortak kitte, ekranın içinde değil: alarm formu kural tipi, karşılaştırma ve
 * kapsam için üç ayrı liste kullanıyor ve her birini ekranın kendi çizmesi,
 * F2 sonunda toparlanamayan tutarsızlığın başladığı yer olurdu (T13 → T28).
 * <c>Field</c> ile aynı erişilebilirlik kurallarını paylaşıyor — kimlik
 * <c>useId</c>'den, ipucu ve hata <c>aria-describedby</c> ile bağlı.
 * </p>
 */
export function SelectField({ label, hint, error, options, className, ...rest }: SelectFieldProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;

  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(" ");

  return (
    <div className={styles.field}>
      <label className={styles.fieldLabel} htmlFor={id}>
        {label}
      </label>
      <select
        {...rest}
        id={id}
        className={[styles.fieldControl, error ? styles.fieldInvalid : null, className]
          .filter(Boolean)
          .join(" ")}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy || undefined}
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
      {hint ? (
        <p className={styles.fieldHint} id={hintId}>
          {hint}
        </p>
      ) : null}
      {error ? (
        <p className={styles.fieldError} id={errorId}>
          {error}
        </p>
      ) : null}
    </div>
  );
}

export type BadgeTone = "neutral" | "accent" | "success" | "warning" | "danger";

const badgeClass: Record<BadgeTone, string> = {
  neutral: styles.badgeNeutral!,
  accent: styles.badgeAccent!,
  success: styles.badgeSuccess!,
  warning: styles.badgeWarning!,
  danger: styles.badgeDanger!,
};

/** Rozet — durumu renkle **ve** metinle anlatıyor (WCAG 1.4.1). */
export function Badge({ tone = "neutral", children }: { tone?: BadgeTone; children: ReactNode }) {
  return <span className={`${styles.badge} ${badgeClass[tone]}`}>{children}</span>;
}

export function Card({ children, padded = true }: { children: ReactNode; padded?: boolean }) {
  return <div className={`${styles.card} ${padded ? styles.cardPadded : ""}`}>{children}</div>;
}
