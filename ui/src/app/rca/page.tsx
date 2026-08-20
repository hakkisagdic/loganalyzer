import { redirect } from "next/navigation";

import styles from "@/components/AppShell.module.css";
import { LogoutButton } from "@/components/LogoutButton";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { describeError } from "@/lib/api/errors";
import { serverApi } from "@/lib/api/server";
import { currentUser } from "@/lib/auth/currentUser";
import type { RcaBundleSummary } from "@/lib/rca/report";

import { RcaLauncher } from "./RcaLauncher";

export const dynamic = "force-dynamic";

/**
 * RCA raporları — liste ve <b>elle tetikleme</b> (T37).
 *
 * <p>
 * Elle tetikleme dar tutuldu: yalnızca "kullanıcı" tetikleyicisi, kuyruk/kota/
 * debounce yok — onlar dört tetikleyiciyle birlikte F4'te. Ama <b>bir</b>
 * tetikleyici olmak zorundaydı: T36'nın kabul kriteri <i>"model kapalıyken
 * rapor okunabiliyor ve işe yarıyor"</i> ve bu, ekranda hiç rapor
 * üretilemiyorsa <b>gösterilemezdi</b> — yalnızca birim testinde iddia
 * edilirdi. Bu depoda doğrulanmamış her katman kırık çıktı.
 * </p>
 */
export default async function RcaPage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Frca");
  }

  let bundles: readonly RcaBundleSummary[] = [];
  let error: string | null = null;

  try {
    const body = (await serverApi.get("/v1/rca")) as { bundles?: readonly RcaBundleSummary[] };
    bundles = body.bundles ?? [];
  } catch (cause) {
    error = describeError(cause);
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
        <h1>RCA raporları</h1>
        <RcaLauncher initialBundles={bundles} initialError={error} />
      </main>
    </div>
  );
}
