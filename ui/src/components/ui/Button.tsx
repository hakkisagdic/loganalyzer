import type { ButtonHTMLAttributes } from "react";

import styles from "./ui.module.css";

export type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";

const variantClass: Record<ButtonVariant, string> = {
  primary: styles.buttonPrimary!,
  secondary: styles.buttonSecondary!,
  ghost: styles.buttonGhost!,
  danger: styles.buttonDanger!,
};

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  readonly variant?: ButtonVariant;
}

/**
 * Tek düğme bileşeni.
 *
 * <p>Her ekranın kendi düğmesini çizmesi, F2 sonunda toparlanamayan tutarsızlık
 * demek — bu yüzden düğme ekranlardan önce burada.</p>
 */
export function Button({ variant = "secondary", className, type = "button", ...rest }: ButtonProps) {
  const classes = [styles.button, variantClass[variant], className].filter(Boolean).join(" ");

  return <button {...rest} type={type} className={classes} />;
}
