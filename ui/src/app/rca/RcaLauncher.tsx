"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";

import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import type { RcaBundleSummary, RcaReport } from "@/lib/rca/report";
import { screenState } from "@/lib/ui/screen-state";

import styles from "./rca.module.css";

/**
 * Taban penceresi seçenekleri — <b>önseçili değer yok</b>.
 *
 * <p>
 * T35 taban uzunluğunu ölçmeyi bilerek açık bıraktı ve ölçüm aracını yazdı;
 * koordinatörün süpürmesi de bunu doğruladı: dirsek fixture'ın kuyruk
 * biçimine göre <b>yedi kat</b> kayıyor, yani <i>seçilebilir bir taban yok</i>.
 * Bir varsayılan koymak, ölçülmemiş bir sayıyı ekranda <b>ölçülmüş gibi</b>
 * göstermek olurdu.
 * </p>
 *
 * <p>
 * Bedeli kullanıcıya düşüyor ve bu bilinçli: taban <b>açıkça</b> seçiliyor ve
 * ekran bunun ölçülmemiş olduğunu söylüyor.
 * </p>
 */
const BASELINE_CHOICES = [
  { value: "1", label: "1 gün" },
  { value: "7", label: "7 gün" },
  { value: "30", label: "30 gün" },
] as const;

/** Olay penceresi uzunlukları — RCA'nın baktığı aralık. */
const WINDOW_CHOICES = [
  { value: "15", label: "15 dakika" },
  { value: "60", label: "1 saat" },
  { value: "240", label: "4 saat" },
] as const;

export interface RcaLauncherProps {
  readonly initialBundles: readonly RcaBundleSummary[];
  readonly initialError: string | null;
}

export function RcaLauncher({ initialBundles, initialError }: RcaLauncherProps) {
  const router = useRouter();

  const [bundles, setBundles] = useState<readonly RcaBundleSummary[] | null>(
    initialError ? null : initialBundles,
  );
  const [loadError, setLoadError] = useState<string | null>(initialError);
  const [baselineDays, setBaselineDays] = useState<string>("");
  const [windowMinutes, setWindowMinutes] = useState<string>("60");
  const [running, setRunning] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);

  const state = screenState(bundles, loadError);

  async function gather() {
    if (!baselineDays) {
      setRunError("Taban penceresi seçilmeli — ölçülmüş bir varsayılanı yok.");
      return;
    }

    setRunning(true);
    setRunError(null);

    const to = new Date();
    const from = new Date(to.getTime() - Number(windowMinutes) * 60_000);
    // Taban olay penceresiyle **örtüşmüyor**: örtüşen bir taban "ilk-görülen"i
    // tanım gereği boşaltır ve sinyal sessizce hiçbir şey döndürür. Sunucu da
    // aynı sebeple reddediyor.
    const baselineTo = from;
    const baselineFrom = new Date(baselineTo.getTime() - Number(baselineDays) * 86_400_000);

    try {
      const report = (await api.post("/v1/rca", {
        body: {
          from: from.toISOString(),
          to: to.toISOString(),
          baseline_from: baselineFrom.toISOString(),
          baseline_to: baselineTo.toISOString(),
          owner_groups: [],
          source_ids: [],
        },
      })) as RcaReport;

      router.push(`/rca/${report.bundle_id}`);
    } catch (cause) {
      setRunError(describeError(cause));
      setRunning(false);
    }
  }

  async function reload() {
    setLoadError(null);
    setBundles(null);

    try {
      const body = (await api.get("/v1/rca")) as { bundles?: readonly RcaBundleSummary[] };
      setBundles(body.bundles ?? []);
    } catch (cause) {
      setLoadError(describeError(cause));
    }
  }

  return (
    <>
      <section className={styles.launcher} aria-labelledby="yeni-rca">
        <h2 id="yeni-rca">Yeni analiz</h2>

        <div className={styles.launcherRow}>
          <label>
            Olay penceresi
            <select value={windowMinutes} onChange={(e) => setWindowMinutes(e.target.value)}>
              {WINDOW_CHOICES.map((choice) => (
                <option key={choice.value} value={choice.value}>
                  {choice.label}
                </option>
              ))}
            </select>
          </label>

          <label>
            Taban penceresi
            <select
              value={baselineDays}
              onChange={(e) => setBaselineDays(e.target.value)}
              aria-describedby="taban-notu"
            >
              <option value="">— seçin —</option>
              {BASELINE_CHOICES.map((choice) => (
                <option key={choice.value} value={choice.value}>
                  {choice.label}
                </option>
              ))}
            </select>
          </label>

          <Button type="button" disabled={running} onClick={() => void gather()}>
            {running ? "Kanıt toplanıyor…" : "Kanıt topla"}
          </Button>
        </div>

        <p className={styles.quiet} id="taban-notu">
          Taban uzunluğunun <strong>ölçülmüş bir varsayılanı yok</strong>: kısa seçilirse her
          yeni şey &quot;ilk kez görüldü&quot; olur, uzun seçilirse gerçek yenilik gürültüde
          kaybolur. Doğru değer verinin karakterine bağlı, o yüzden burada size soruluyor.
        </p>

        {runError ? <p className={styles.reviewError}>{runError}</p> : null}
      </section>

      <section aria-labelledby="paketler">
        <h2 id="paketler">Toplanmış kanıt paketleri</h2>

        {state === "loading" ? <LoadingState label="Paketler yükleniyor" /> : null}

        {state === "error" ? (
          <ErrorState
            title={loadError ?? "Paketler okunamadı."}
            action={
              <Button type="button" onClick={() => void reload()}>
                Yeniden dene
              </Button>
            }
          />
        ) : null}

        {state === "empty" ? (
          <EmptyState
            title="Henüz kanıt paketi yok."
            description="Yukarıdan bir pencere seçip ilk analizi başlatabilirsiniz."
          />
        ) : null}

        {state === "ready" && bundles ? (
          <DataTable
            caption="Toplanmış kanıt paketleri"
            rowKey={(bundle) => bundle.bundle_id}
            rows={bundles}
            columns={[
              {
                key: "gathered",
                header: "Toplandı",
                width: "14rem",
                render: (bundle) => (
                  <a href={`/rca/${bundle.bundle_id}`}>
                    <time dateTime={bundle.gathered_at}>{bundle.gathered_at}</time>
                  </a>
                ),
              },
              {
                key: "window",
                header: "Olay penceresi",
                render: (bundle) => (
                  <>
                    <time dateTime={bundle.window_from}>{bundle.window_from}</time> →{" "}
                    <time dateTime={bundle.window_to}>{bundle.window_to}</time>
                  </>
                ),
              },
              {
                key: "out_of_scope",
                header: "Kapsam dışı",
                width: "8rem",
                numeric: true,
                // 0 ise satır sessiz: her rapordaki bir uyarının değeri sıfır.
                render: (bundle) =>
                  Number(bundle.out_of_scope_count) > 0 ? bundle.out_of_scope_count : "—",
              },
              {
                key: "status",
                header: "Durum",
                width: "9rem",
                render: (bundle) =>
                  bundle.is_partial ? (
                    <span className={styles.badge} data-tone="warn">
                      kanıt eksik
                    </span>
                  ) : (
                    <span className={styles.quiet}>tam</span>
                  ),
              },
            ]}
          />
        ) : null}
      </section>
    </>
  );
}
