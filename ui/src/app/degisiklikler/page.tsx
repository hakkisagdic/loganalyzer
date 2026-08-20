import { redirect } from "next/navigation";

import styles from "@/components/AppShell.module.css";
import { LogoutButton } from "@/components/LogoutButton";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { ErrorState } from "@/components/ui/States";
import { currentUser } from "@/lib/auth/currentUser";

import { ChangeFeed } from "./ChangeFeed";

export const dynamic = "force-dynamic";

/**
 * Değişiklik akışı ekranı — liste ve **elle giriş** (T24'ün UI parçası).
 *
 * <p>
 * Bu formun değeri bugün değil F3'te görünüyor: RCA "ne değişti" verisi
 * olmadan "ne oldu"nun ötesine geçemiyor ve o veri <b>geçmişe dönük
 * üretilemiyor</b>. Webhook'lar CI'dan geleni topluyor; bu form, CI'dan
 * geçmeyen değişiklikler için — elle yapılan bir config push'u, bir bakım
 * penceresi, bir donanım değişikliği.
 * </p>
 *
 * <p>
 * <b>Kapsam listesi sunucudan geliyor.</b> Kullanıcının yazabileceği gruplar
 * <c>/auth/me</c>'nin döndüğü kapsam; istemcinin serbest metin girmesine izin
 * vermek, API'nin zaten reddedeceği bir isteği yazdırmak olurdu (K17).
 * </p>
 */
export default async function ChangesPage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Fdegisiklikler");
  }

  return (
    <div className={styles.shell}>
      <a className="skip-link" href="#icerik">
        İçeriğe atla
      </a>

      <header className={styles.header}>
        <a className={styles.brand} href="/">
          Bizigo Log Analyzer
        </a>
        <span className={styles.spacer} />
        <ThemeToggle />
        <LogoutButton />
      </header>

      <main className={styles.main} id="icerik">
        <h1>Değişiklikler</h1>

        {identity.status === "error" ? (
          <ErrorState title={identity.message} hint={identity.hint} />
        ) : (
          <ChangeFeed
            // Kısıtsız kapsamda liste boş geliyor; o durumda grup serbest
            // yazılıyor çünkü admin her gruba yazabiliyor.
            ownerGroups={identity.user.owner_groups}
            unrestricted={identity.user.unrestricted}
          />
        )}
      </main>
    </div>
  );
}
