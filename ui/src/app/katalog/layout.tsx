import Link from "next/link";
import { redirect } from "next/navigation";
import type { ReactNode } from "react";

import styles from "@/components/AppShell.module.css";
import { LogoutButton } from "@/components/LogoutButton";
import { ErrorState } from "@/components/ui/States";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { currentUser } from "@/lib/auth/currentUser";

import catalog from "./catalog.module.css";

export const dynamic = "force-dynamic";

/**
 * Katalog ekranlarının ortak kabuğu (T20).
 *
 * <p>Kimlik kapısı burada, her sayfada değil: ikisinde ayrı ayrı yazılsaydı
 * biri unutulduğunda o ekran oturumsuz açılırdı ve bunu ancak biri fark
 * ettiğinde öğrenirdik.</p>
 */
export default async function CatalogLayout({ children }: { children: ReactNode }) {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Fkatalog");
  }

  return (
    <div className={styles.shell}>
      <a className="skip-link" href="#icerik">
        İçeriğe atla
      </a>

      <header className={styles.header}>
        <Link className={styles.brand} href="/">
          Bizigo Log Analyzer
        </Link>
        <span className={styles.spacer} />
        {identity.status === "ok" ? (
          <span className={styles.identity} title={identity.user.username}>
            {identity.user.username || identity.user.subject}
          </span>
        ) : null}
        <ThemeToggle />
        <LogoutButton />
      </header>

      <main className={styles.main} id="icerik">
        {identity.status === "error" ? (
          <ErrorState title={identity.message} hint={identity.hint} />
        ) : (
          <>
            <nav className={catalog.tabs} aria-label="Katalog bölümleri">
              <Link href="/katalog">Parser kataloğu</Link>
              <Link href="/katalog/inceleme">İnceleme kuyruğu</Link>
            </nav>
            {children}
          </>
        )}
      </main>
    </div>
  );
}
