import { redirect } from "next/navigation";

import styles from "@/components/AppShell.module.css";
import { LogoutButton } from "@/components/LogoutButton";
import { Badge, Card } from "@/components/ui/Field";
import { EmptyState, ErrorState } from "@/components/ui/States";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { currentUser } from "@/lib/auth/currentUser";

export const dynamic = "force-dynamic";

/**
 * "Giriş yaptım" sayfası — T13'ün tek ekranı.
 *
 * <p>
 * Ekranların kendisi T15+'ta. Buradaki tablo, kimlik akışının uçtan uca
 * çalıştığının kanıtı: kullanıcı adı, roller ve <b>kapsam</b> API'den
 * (<c>/auth/me</c>) geliyor, yani token gerçekten sunucudan sunucuya
 * kullanılıyor.
 * </p>
 */
export default async function HomePage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    // Korunan yolda oturum yoksa giriş akışına. `returnTo` ile kullanıcı
    // girişten sonra geldiği yere dönüyor.
    //
    // YALNIZCA bu dal yönlendiriyor. "API cevap vermiyor" da buraya bağlansaydı
    // giriş → sayfa → giriş döngüsü oluşurdu.
    redirect("/api/auth/login?returnTo=%2F");
  }

  if (identity.status === "error") {
    return (
      <div className={styles.shell}>
        <header className={styles.header}>
          <span className={styles.brand}>Bizigo Log Analyzer</span>
          <span className={styles.spacer} />
          <ThemeToggle />
          <LogoutButton />
        </header>
        <main className={styles.main} id="icerik">
          <ErrorState title={identity.message} hint={identity.hint} />
        </main>
      </div>
    );
  }

  const user = identity.user;

  return (
    <div className={styles.shell}>
      <a className="skip-link" href="#icerik">
        İçeriğe atla
      </a>

      <header className={styles.header}>
        <span className={styles.brand}>Bizigo Log Analyzer</span>
        <span className={styles.spacer} />
        <span className={styles.identity} title={user.username}>
          {user.username || user.subject}
        </span>
        <ThemeToggle />
        <LogoutButton />
      </header>

      <main className={styles.main} id="icerik">
        <h1>Giriş yapıldı</h1>

        {user.sees_nothing ? (
          // Kapsam boşsa kullanıcı hiçbir veri göremez ve sebebi bir yetki
          // sorunu DEĞİL, eksik grup eşlemesi. Sessiz bırakmak "sistem bozuk"
          // ile "yetkiniz yok"u ayırt edilemez kılar (K17).
          <p className={styles.notice}>
            Hiçbir gruba eşlenmediğiniz için veri göremiyorsunuz. Kontrol düzlemindeki
            grup → <code>owner_group</code> eşlemesi eksik olabilir; yöneticinize başvurun.
          </p>
        ) : null}

        <Card>
          <div className={styles.grid}>
            <div className={styles.definition}>
              <span className={styles.definitionTerm}>Kullanıcı</span>
              <span className={styles.definitionValue}>{user.username || "—"}</span>
            </div>

            <div className={styles.definition}>
              <span className={styles.definitionTerm}>Denetim kimliği (sub)</span>
              <span className={styles.definitionValue}>{user.subject || "—"}</span>
            </div>

            <div className={styles.definition}>
              <span className={styles.definitionTerm}>Roller</span>
              <span className={styles.tagRow}>
                {user.roles.length > 0 ? (
                  user.roles.map((role) => (
                    <Badge key={role} tone="accent">
                      {role}
                    </Badge>
                  ))
                ) : (
                  <span className={styles.definitionValue}>—</span>
                )}
              </span>
            </div>

            <div className={styles.definition}>
              <span className={styles.definitionTerm}>IdP grupları</span>
              <span className={styles.tagRow}>
                {user.idp_groups.length > 0 ? (
                  user.idp_groups.map((group) => <Badge key={group}>{group}</Badge>)
                ) : (
                  <span className={styles.definitionValue}>—</span>
                )}
              </span>
            </div>

            <div className={styles.definition}>
              <span className={styles.definitionTerm}>Görülebilen kapsam</span>
              <span className={styles.tagRow}>
                {user.unrestricted ? (
                  <Badge tone="warning">kısıtsız</Badge>
                ) : user.owner_groups.length > 0 ? (
                  user.owner_groups.map((group) => (
                    <Badge key={group} tone="success">
                      {group}
                    </Badge>
                  ))
                ) : (
                  <Badge tone="danger">yok</Badge>
                )}
              </span>
            </div>
          </div>
        </Card>

        <Card padded={false}>
          <EmptyState
            title="Ekranlar henüz yok"
            description="Bu iskelet yalnızca kimlik akışını ve tasarım temelini taşıyor. Olay arama, parser editörü ve alarmlar sonraki ticket'larda geliyor."
          />
        </Card>
      </main>
    </div>
  );
}
