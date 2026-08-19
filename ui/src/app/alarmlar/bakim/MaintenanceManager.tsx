"use client";

import { useCallback, useEffect, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Badge, Card, Field, SelectField } from "@/components/ui/Field";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/alerts/errors";
import { formatInstant, type MaintenanceWindow, type MaintenanceWindowList } from "@/lib/alerts/types";

import styles from "../alerts.module.css";

/** `datetime-local` alanının beklediği biçim; saniye ve zaman dilimi taşımıyor. */
function toLocalInput(value: Date): string {
  const pad = (part: number) => String(part).padStart(2, "0");

  return (
    `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}` +
    `T${pad(value.getHours())}:${pad(value.getMinutes())}`
  );
}

/**
 * Bakım penceresi yönetimi (T23 kapsamı: "susturma/bakım penceresi yönetimi").
 *
 * <p>
 * Kural tipindeki <b>sessizlik</b> ile karıştırılmamalı: orası "veri susmuş",
 * burası "alarmı sustur". İkisinin ayrı isimleri var ve ekranda da ayrı
 * yerdeler — aynı sayfada olsalardı bakım penceresi açan biri sessizlik
 * kuralını kapattığını sanabilirdi.
 * </p>
 *
 * <p>
 * Zaman alanları <b>yerel saatte</b> giriliyor, sunucuya UTC gidiyor. Bakım
 * penceresini açan kişi saat 02:00'de sahada; ona UTC yazdırmak hata üretir ve
 * o hatanın bedeli, alarmların yanlış saatte susturulması.
 * </p>
 */
export function MaintenanceManager({ ownerGroups }: { ownerGroups: readonly string[] }) {
  const [windows, setWindows] = useState<readonly MaintenanceWindow[] | null>(null);
  const [ownerGroup, setOwnerGroup] = useState(ownerGroups[0] ?? "");
  const [startsAt, setStartsAt] = useState(() => toLocalInput(new Date()));
  const [endsAt, setEndsAt] = useState(() => toLocalInput(new Date(Date.now() + 3600_000)));
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = (await api.get("/v1/alerts/maintenance", { signal })) as MaintenanceWindowList;
      setWindows(list.windows);
    } catch (cause) {
      if (!signal?.aborted) {
        setError(describeError(cause));
        setWindows([]);
      }
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  async function create() {
    setBusy(true);
    setError(null);

    try {
      await api.post("/v1/alerts/maintenance", {
        body: {
          ownerGroup,
          // `new Date(...)` yerel saati okuyup ISO'ya çevirirken UTC'ye
          // dönüştürüyor; sunucu da UTC bekliyor.
          startsAt: new Date(startsAt).toISOString(),
          endsAt: new Date(endsAt).toISOString(),
          ruleId: null,
          reason,
        },
      });

      setReason("");
      await load();
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  async function remove(window: MaintenanceWindow) {
    setBusy(true);

    try {
      await api.delete("/v1/alerts/maintenance/{id}", { path: { id: window.id } });
      await load();
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  const now = Date.now();

  return (
    <div className={styles.stack}>
      <div className={styles.toolbar}>
        <h1>Bakım pencereleri</h1>
      </div>

      {error ? <ErrorState title="Pencere işlemi başarısız" hint={error} /> : null}

      <Card>
        <div className={styles.stack}>
          <h2>Yeni pencere</h2>

          <div className={styles.formGrid}>
            <SelectField
              label="Kapsam"
              value={ownerGroup}
              onChange={(event) => setOwnerGroup(event.target.value)}
              options={ownerGroups.map((group) => ({ value: group, label: group }))}
              hint="Bu gruptaki tüm kurallar pencere boyunca bastırılır."
            />

            <Field
              label="Başlangıç (yerel saat)"
              type="datetime-local"
              value={startsAt}
              onChange={(event) => setStartsAt(event.target.value)}
            />

            <Field
              label="Bitiş (yerel saat)"
              type="datetime-local"
              value={endsAt}
              onChange={(event) => setEndsAt(event.target.value)}
              hint="Pencere bittiği anda kurallar yeniden koşar."
            />
          </div>

          <Field
            label="Sebep"
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            hint="Altı ay sonra bu satırı okuyan kişi neden susturulduğunu bilmeli."
          />

          <div className={styles.inlineActions}>
            <Button variant="primary" onClick={create} disabled={busy || !ownerGroup}>
              Pencere aç
            </Button>
          </div>
        </div>
      </Card>

      {windows === null ? (
        <LoadingState label="Pencereler yükleniyor…" />
      ) : windows.length === 0 ? (
        <Card>
          <EmptyState
            title="Tanımlı bakım penceresi yok"
            description="Planlı bir bakım sırasında alarmların susması için pencere açın."
          />
        </Card>
      ) : (
        <Card padded={false}>
          <DataTable
            caption="Bakım pencereleri"
            rowKey={(row) => row.id}
            rows={windows}
            columns={[
              { key: "group", header: "Kapsam", width: "16%", render: (row) => row.owner_group },
              { key: "start", header: "Başlangıç", width: "18%", render: (row) => formatInstant(row.starts_at) },
              { key: "end", header: "Bitiş", width: "18%", render: (row) => formatInstant(row.ends_at) },
              {
                key: "state",
                header: "Durum",
                width: "12%",
                render: (row) => {
                  const start = new Date(row.starts_at).getTime();
                  const end = new Date(row.ends_at).getTime();

                  if (now >= start && now < end) {
                    return <Badge tone="warning">açık</Badge>;
                  }

                  return now < start ? <Badge>planlı</Badge> : <Badge tone="neutral">bitti</Badge>;
                },
              },
              { key: "reason", header: "Sebep", width: "26%", freeText: true, render: (row) => row.reason },
              {
                key: "actions",
                header: "İşlem",
                width: "10%",
                render: (row) => (
                  <Button variant="danger" disabled={busy} onClick={() => remove(row)}>
                    Kapat
                  </Button>
                ),
              },
            ]}
          />
        </Card>
      )}
    </div>
  );
}
