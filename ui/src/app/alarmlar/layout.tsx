import Link from "next/link";
import { redirect } from "next/navigation";
import type { ReactNode } from "react";

import styles from "@/components/AppShell.module.css";
import { LogoutButton } from "@/components/LogoutButton";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { ErrorState } from "@/components/ui/States";
import { currentUser } from "@/lib/auth/currentUser";

import alerts from "./alerts.module.css";

export const dynamic = "force-dynamic";

/**
 * Alarm ekranlarının ortak kabuğu (T23).
 *
 * <p>
 * Kimlik kapısı <b>burada</b>, her sayfada değil: dört ekranın dördünde ayrı
 * ayrı yazılsaydı biri unutulduğunda o ekran oturumsuz açılırdı ve bunu ancak
 * biri fark ettiğinde öğrenirdik. Sunucu bileşeni olması da bilinçli — kimlik
 * kontrolü istemciye inen bir koşula bağlanamaz.
 * </p>
 *
 * <p>
 * Kapsamı boş olan kullanıcı ekranlara giriyor ama <b>uyarıyla</b>: hiçbir
 * kural yazamayacak ve sebebi bir yetki sorunu değil, eksik grup eşlemesi
 * (K17). Sessizce boş liste göstermek "sistem bozuk" ile "verin yok"u ayırt
 * edilemez kılardı.
 * </p>
 */
export default async function AlertsLayout({ children }: { children: ReactNode }) {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Falarmlar");
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
            <nav className={alerts.tabs} aria-label="Alarm bölümleri">
              <Link href="/alarmlar">Kurallar ve geçmiş</Link>
              <Link href="/alarmlar/kanallar">Kanallar</Link>
              <Link href="/alarmlar/bakim">Bakım pencereleri</Link>
            </nav>

            {identity.user.sees_nothing ? (
              <p className={alerts.notice} role="status">
                Hiçbir gruba eşlenmediğiniz için kural yazamazsınız. Kontrol düzlemindeki
                grup → <code>owner_group</code> eşlemesi eksik olabilir; yöneticinize başvurun.
              </p>
            ) : null}

            {children}
          </>
        )}
      </main>
    </div>
  );
}
