"use client";

import { useEffect, useState } from "react";

import { Button } from "./Button";

export type Theme = "light" | "dark";

export const THEME_STORAGE_KEY = "bizigo.theme";

/**
 * Tema, sayfa boyanmadan ÖNCE uygulanmak zorunda.
 *
 * <p>React'in ilk çiziminde ayarlamak yetmiyor: kullanıcı koyu tema seçmişse
 * bir kare boyunca beyaz ekran görüyor. Bu betik <c>&lt;head&gt;</c> içinde,
 * senkron çalışıyor ve gövde boyanmadan <c>data-theme</c>'i yazıyor.</p>
 *
 * <p>Seçim yoksa hiçbir şey yazılmıyor — o durumda `prefers-color-scheme`
 * geçerli kalıyor (bkz. `tokens.css`).</p>
 */
export const themeBootstrapScript = `(function(){try{var t=localStorage.getItem(${JSON.stringify(
  THEME_STORAGE_KEY,
)});if(t==="light"||t==="dark"){document.documentElement.dataset.theme=t;}}catch(e){}})();`;

function systemTheme(): Theme {
  return globalThis.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export function ThemeToggle() {
  // Sunucu tarafında tema bilinmiyor; ilk çizimde `undefined` bırakıp
  // bağlandıktan sonra okuyoruz, yoksa hidrasyon uyuşmazlığı çıkıyor.
  const [theme, setTheme] = useState<Theme | undefined>(undefined);

  useEffect(() => {
    const stored = localStorage.getItem(THEME_STORAGE_KEY);
    setTheme(stored === "light" || stored === "dark" ? stored : systemTheme());
  }, []);

  function toggle() {
    const next: Theme = theme === "dark" ? "light" : "dark";
    setTheme(next);
    document.documentElement.dataset.theme = next;

    try {
      localStorage.setItem(THEME_STORAGE_KEY, next);
    } catch {
      // Gizli sekmede depolama kapalı olabilir; tema yine de bu sekmede geçerli.
    }
  }

  const isDark = theme === "dark";

  return (
    <Button
      variant="ghost"
      onClick={toggle}
      // Durumu düğmenin metninde değil erişilebilir adında taşıyoruz: simge tek
      // başına ekran okuyucuya bir şey söylemiyor.
      aria-label={isDark ? "Açık temaya geç" : "Koyu temaya geç"}
      aria-pressed={isDark}
    >
      <span aria-hidden="true">{isDark ? "☀" : "☾"}</span>
      <span>{isDark ? "Açık" : "Koyu"}</span>
    </Button>
  );
}
