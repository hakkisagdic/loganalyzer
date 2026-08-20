import { redirect } from "next/navigation";

import styles from "@/components/AppShell.module.css";
import { LogoutButton } from "@/components/LogoutButton";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { ErrorState } from "@/components/ui/States";
import { currentUser } from "@/lib/auth/currentUser";

import { ConnectorManager } from "./ConnectorManager";

export const dynamic = "force-dynamic";

/**
 * Connector yönetim ekranı (T25, K34: "ekrandan yapılandırılabilmeli").
 *
 * <p>
 * <b>Kimlik bilgisi bu ekrana hiç inmiyor.</b> API yanıtı yalnızca
 * <c>credential_set</c> boolean'ını ve sabit bir maske taşıyor; şifreli metin
 * bile dönmüyor. Dolayısıyla "mevcut parolayı göster" diye bir düğme yok — ve
 * olmaması bir eksiklik değil, ticket'ın kabul kriteri.
 * </p>
 */
export default async function ConnectorsPage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Fdegisiklikler%2Fconnectorler");
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
        <h1>Değişiklik connector'ları</h1>

        {identity.status === "error" ? (
          <ErrorState title={identity.message} hint={identity.hint} />
        ) : (
          <ConnectorManager
            ownerGroups={identity.user.owner_groups}
            unrestricted={identity.user.unrestricted}
            canManage={
              // Yetki uçta zaten zorlanıyor; ekran onu YANSITIYOR. Düğmeyi
              // gösterip 403 aldırmak, kullanıcıya sebebini söylemeyen bir
              // arıza gibi görünürdü.
              identity.user.roles.includes("author") || identity.user.roles.includes("admin")
            }
          />
        )}
      </main>
    </div>
  );
}
