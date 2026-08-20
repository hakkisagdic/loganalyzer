import { redirect } from "next/navigation";

import styles from "@/components/AppShell.module.css";
import { LogoutButton } from "@/components/LogoutButton";
import { ErrorState } from "@/components/ui/States";
import { ThemeToggle } from "@/components/ui/ThemeToggle";
import { describeError } from "@/lib/api/errors";
import { serverApi } from "@/lib/api/server";
import { currentUser } from "@/lib/auth/currentUser";
import type { RcaReport } from "@/lib/rca/report";

import { ReportView } from "./ReportView";

export const dynamic = "force-dynamic";

interface PageProps {
  readonly params: Promise<{ id: string }>;
}

/**
 * Tek bir RCA raporu.
 *
 * <p>
 * Veri <b>sunucuda</b> çekiliyor: token tarayıcıya hiç ulaşmıyor ve rapor HTML
 * olarak iniyor. Kanıt özetleri ham log gövdesi taşıdığı için bu ekstra bir
 * önlem değil, K17'nin devamı.
 * </p>
 *
 * <p>
 * Kapsam dışı bir paket sunucudan <b>404</b> dönüyor (403 değil — 403 paketin
 * varlığını doğrular ve "şu pencerede RCA koşulmuş" tek başına sızıntı).
 * Ekranın onu "bulunamadı" diye göstermesi bu yüzden doğru cümle.
 * </p>
 */
export default async function RcaReportPage({ params }: PageProps) {
  const { id } = await params;
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect(`/api/auth/login?returnTo=${encodeURIComponent(`/rca/${id}`)}`);
  }

  let report: RcaReport | null = null;
  let failure: string | null = null;

  try {
    report = (await serverApi.get("/v1/rca/{id}", { path: { id } })) as RcaReport;
  } catch (cause) {
    failure = describeError(cause);
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
        <p>
          <a href="/rca">← RCA raporları</a>
        </p>

        {failure ? (
          <ErrorState title={failure} />
        ) : report ? (
          <ReportView report={report} />
        ) : (
          <ErrorState title="Rapor okunamadı." />
        )}
      </main>
    </div>
  );
}
