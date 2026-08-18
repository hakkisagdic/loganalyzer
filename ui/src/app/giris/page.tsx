import Link from "next/link";

import styles from "@/components/AppShell.module.css";
import { Card } from "@/components/ui/Field";
import { ErrorState } from "@/components/ui/States";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { safeReturnTo } from "@/lib/auth/redirects";

export const dynamic = "force-dynamic";

/**
 * Giriş sayfası.
 *
 * <p>
 * Parola alanı <b>yok</b> ve olmayacak: `bizigo-ui` istemcisinde
 * `directAccessGrantsEnabled=false`, yani parola akışı Keycloak tarafında da
 * kapalı. Kimlik doğrulaması Keycloak'ın kendi sayfasında yapılıyor;
 * uygulamanın parolayı hiç görmemesi bilinçli.
 * </p>
 *
 * <p>OIDC akışı yarıda kırılırsa hata buraya `?hata=` ile düşüyor.</p>
 */
export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ hata?: string; ipucu?: string; returnTo?: string }>;
}) {
  const params = await searchParams;
  const returnTo = safeReturnTo(params.returnTo);
  const loginHref = `/api/auth/login?returnTo=${encodeURIComponent(returnTo)}`;

  return (
    <div className={styles.loginPage}>
      <ThemeToggle />

      <Card>
        <div className={styles.loginCard}>
          <h1>Bizigo Log Analyzer</h1>
          <p>Devam etmek için kurumsal hesabınızla giriş yapın.</p>

          {params.hata ? <ErrorState title={params.hata} hint={params.ipucu} /> : null}

          {/*
            Düğme değil BAĞLANTI: giriş bir yönlendirme, JavaScript gerektiren
            bir eylem değil. Betik yüklenmese de çalışıyor.
          */}
          <Link href={loginHref} prefetch={false}>
            Keycloak ile giriş yap
          </Link>
        </div>
      </Card>
    </div>
  );
}
