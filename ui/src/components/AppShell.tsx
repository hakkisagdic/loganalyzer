import Link from "next/link";
import type { ReactNode } from "react";

import { LogoutButton } from "@/components/LogoutButton";
import { ThemeToggle } from "@/components/ui/ThemeToggle";

import styles from "./AppShell.module.css";

/**
 * Ortak çerçeve: marka, gezinme, kimlik, tema ve çıkış.
 *
 * <p>
 * T13'te tek ekran vardı ve başlık sayfanın içindeydi. İkinci ekran gelir
 * gelmez kopyalanacaktı; iki kopya arasındaki ilk fark da kullanıcıya
 * "başka bir uygulamaya geçtim" hissi verirdi. T28'in tutarlılık denetimi
 * bunun üstüne bakacak.
 * </p>
 */

export interface AppShellProps {
  readonly username?: string;
  readonly children: ReactNode;
}

const NAV = [
  { href: "/", label: "Genel bakış" },
  { href: "/olaylar", label: "Log arama" },
  { href: "/kaynaklar", label: "Kaynaklar" },
  { href: "/alarmlar", label: "Alarmlar" },
  // Katalog T20 ile indi ama gezinmeye girmemişti: gezinmede olmayan bir ekran
  // yalnızca adresini bilenin ekranıdır. T19 parser editörünü eklerken yanına
  // koydu — ikisi aynı işin iki yarısı (yaz/dene → yayınla/geri al).
  { href: "/katalog", label: "Parser kataloğu" },
  // Editör `author` rolü istiyor ve kapı sunucuda (`parserlar/layout.tsx`).
  // Bağlantının herkese görünmesi bilinçli: gizlemek, yetkisi olmayan birine
  // ekranın VAR OLDUĞUNU saklardı ve "neden ben göremiyorum" sorusu hiç
  // sorulamazdı. Girildiğinde sebebi açıkça yazan bir ekran çıkıyor.
  { href: "/parserlar", label: "Parser editörü" },
] as const;

export function AppShell({ username, children }: AppShellProps) {
  return (
    <div className={styles.shell}>
      {/* Klavye kullanıcısı her sayfada gezinmeyi baştan geçmek zorunda kalmasın. */}
      <a className="skip-link" href="#icerik">
        İçeriğe atla
      </a>

      <header className={styles.header}>
        <Link className={styles.brand} href="/">
          Bizigo Log Analyzer
        </Link>

        <nav className={styles.nav} aria-label="Ana gezinme">
          {NAV.map((item) => (
            <Link key={item.href} className={styles.navLink} href={item.href}>
              {item.label}
            </Link>
          ))}
        </nav>

        <span className={styles.spacer} />

        {username ? (
          <span className={styles.identity} title={username}>
            {username}
          </span>
        ) : null}

        <ThemeToggle />
        <LogoutButton />
      </header>

      <main className={styles.main} id="icerik">
        {children}
      </main>
    </div>
  );
}
