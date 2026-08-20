import Link from "next/link";
import { redirect } from "next/navigation";
import type { ReactNode } from "react";

import styles from "@/components/AppShell.module.css";
import { LogoutButton } from "@/components/LogoutButton";
import { ErrorState } from "@/components/ui/States";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { currentUser } from "@/lib/auth/currentUser";

import parsers from "./parsers.module.css";

export const dynamic = "force-dynamic";

/**
 * Parser editörünün kabuğu ve **rol kapısı** (T19).
 *
 * <p>
 * <b>Kapı burada, sayfanın içinde değil.</b> Kimlik kontrolü her sayfada ayrı
 * yazılsaydı biri unutulduğunda o sayfa oturumsuz açılırdı ve bunu ancak biri
 * fark ettiğinde öğrenirdik. Sunucu bileşeni olması da bilinçli: rol kontrolü
 * istemciye inen bir koşula bağlanamaz.
 * </p>
 *
 * <p>
 * <b>Neden `author`:</b> <c>POST /v1/parsers/try</c> keyfi bir satırı motora
 * koşturuyor ve keyfi YAML derliyor — bedeli sınırsız bir hesaplama ucu. F1'de
 * bilerek <c>author</c> istenmiş; ekranın bunu tekrarlaması, okuyucunun her
 * düğmeye basıp 403 toplamasını engelliyor. Sunucu kararı yine de tek
 * doğrulama noktası: burası onu <b>görünür</b> kılıyor, yerine geçmiyor.
 * </p>
 */
const AUTHOR_ROLES = ["author", "admin"];

export default async function ParsersLayout({ children }: { children: ReactNode }) {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Fparserlar");
  }

  const canAuthor =
    identity.status === "ok" && identity.user.roles.some((role) => AUTHOR_ROLES.includes(role));

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
        ) : canAuthor ? (
          children
        ) : (
          // Boş bir editör göstermek "yazdım ama kaydedilmedi"ye yol açardı:
          // kullanıcı işini kaybettiğini sanır. Sebep en başta söyleniyor.
          <ErrorState
            title="Parser yazma yetkiniz yok."
            hint={
              "Bu ekran `author` rolü istiyor: parser denemesi keyfi bir satırı motorda " +
              "koşturuyor ve keyfi YAML derliyor. Rol atanması için yöneticinize başvurun."
            }
            action={
              <p className={parsers.muted}>
                Yayınlanmış parser'ları görüntülemek için katalog ekranını kullanabilirsiniz.
              </p>
            }
          />
        )}
      </main>
    </div>
  );
}
