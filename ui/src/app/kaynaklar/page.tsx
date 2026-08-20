import { redirect } from "next/navigation";

import { AppShell } from "@/components/AppShell";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { Card } from "@/components/ui/Field";
import { EmptyState, ErrorState } from "@/components/ui/States";
import type {
  PipelineHealth,
  SourceActivityItem,
  SourceActivityList,
  SourceItem,
  SourceList,
} from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";
import { serverApi } from "@/lib/api/server";
import { currentUser } from "@/lib/auth/currentUser";
import { formatSince, mergeInventory, unassignedSources } from "@/lib/sources/inventory";

import { InventoryTable } from "./InventoryTable";
import { PipelineHealthSummary } from "./PipelineHealthSummary";
import { SourceEditor } from "./SourceEditor";
import styles from "./inventory.module.css";

export const dynamic = "force-dynamic";

/** Etkinlik penceresi. T21'in kural penceresiyle karıştırılmamalı — bu yalnızca görüntü. */
const WINDOW_HOURS = 24;

/**
 * Envanter ekranı (T17).
 *
 * <p>
 * "Hangi cihaz hangi gruba ait, ne gönderiyor, ne zamandır susuyor." Üç sorunun
 * ikisinin cevabı kontrol düzleminde, biri ClickHouse'da; ekran ikisini
 * <b>birleştiriyor</b> çünkü tek başına ikisi de eksik: envanter veri gelip
 * gelmediğini bilmiyor, olay tablosu hiç veri göndermemiş kaynağı listeleyemiyor.
 * </p>
 *
 * <p>
 * <b>Son görülme sorgusu yeniden yazılmadı.</b> <c>GET /v1/sources/activity</c>
 * arkasında T21'in sessizlik alarmıyla ortak olan
 * <c>IScopedQuery.GetSourceActivityAsync</c> duruyor. İkinci bir kopya, iki
 * farklı zaman kolonu seçimi ve iki farklı kapsam davranışı demek olurdu; ve
 * ayrıştıkları ancak alarm yanlış tetiklendiğinde fark edilirdi.
 * </p>
 */
export default async function InventoryPage() {
  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect("/api/auth/login?returnTo=%2Fkaynaklar");
  }

  if (identity.status === "error") {
    return (
      <AppShell>
        <ErrorState title={identity.message} hint={identity.hint} />
      </AppShell>
    );
  }

  const user = identity.user;
  const username = user.username || user.subject;

  if (user.sees_nothing) {
    return (
      <AppShell username={username}>
        <h1>Kaynak envanteri</h1>
        <ErrorState
          title="Hiçbir gruba eşlenmediğiniz için envanter göremiyorsunuz."
          hint="Kontrol düzlemindeki grup → owner_group eşlemesi eksik olabilir; yöneticinize başvurun."
        />
      </AppShell>
    );
  }

  const [sources, activity, health] = await Promise.all([
    load<SourceList>(() => serverApi.get("/v1/sources") as Promise<SourceList>),
    load<SourceActivityList>(
      () =>
        serverApi.get("/v1/sources/activity", {
          query: { hours: WINDOW_HOURS } as never,
        }) as Promise<SourceActivityList>,
    ),
    load<PipelineHealth>(() => serverApi.get("/v1/health/pipeline") as Promise<PipelineHealth>),
  ]);

  // Zaman tabanı bir kez alınıyor: her satırın kendi `new Date()`'ini çağırması,
  // uzun bir listede satırlar arasında saniyelik tutarsızlık üretirdi.
  const now = new Date();

  const activityRows: SourceActivityItem[] = activity.value?.sources
    ? [...activity.value.sources]
    : [];
  const sourceRows: SourceItem[] = sources.value?.sources ? [...sources.value.sources] : [];

  const rows = mergeInventory(sourceRows, activityRows);
  const unassigned = unassignedSources(activityRows);

  return (
    <AppShell username={username}>
      <h1>Kaynak envanteri</h1>

      {/*
        Envanterde olmayan cihazlar EN ÜSTTE. Verileri reddedilmiyor (veri kaybı
        eksik envanterden kötü) ama hiçbir ekibe görünmüyor — listenin dibinde
        gizlemek, sorunun var olmadığını sanmak demek.
      */}
      {unassigned.length > 0 ? (
        <section aria-label="Envanterde olmayan kaynaklar">
          <p className={styles.alarm} role="status">
            <b>{unassigned.length} cihaz envanterde yok ve veri gönderiyor.</b> Olayları
            reddedilmiyor ama <code>_unassigned</code> grubuna düşüyor, yani hiçbir ekibin
            kapsamında görünmüyorlar. Aşağıdaki formla envantere ekleyin.
          </p>

          <UnassignedTable rows={unassigned} now={now} />
        </section>
      ) : null}

      {health.value ? <PipelineHealthSummary health={health.value} /> : null}

      {health.error ? (
        <p className={styles.noticeMuted}>Boru hattı özeti alınamadı ({health.error}).</p>
      ) : null}

      {sources.error ? (
        <ErrorState title="Envanter listesi alınamadı." hint={sources.error} />
      ) : rows.length === 0 ? (
        <Card padded={false}>
          <EmptyState
            title="Kapsamınızda kayıtlı kaynak yok."
            description="Aşağıdaki formla tek tek ekleyebilir ya da CSV yükleyebilirsiniz."
          />
        </Card>
      ) : (
        <InventoryTable rows={rows} now={now} windowHours={WINDOW_HOURS} />
      )}

      {/*
        Etkinlik gelmezse liste yine çiziliyor, yalnızca "son görülme" sütunu
        boş kalıyor. Sessiz bırakmak, bütün kaynakların sustuğunu sanmak demekti.
      */}
      {activity.error ? (
        <p className={styles.noticeMuted}>
          Son görülme bilgisi alınamadı ({activity.error}); &ldquo;bu pencerede veri yok&rdquo;
          satırları bu yüzden görünüyor olabilir.
        </p>
      ) : null}

      {user.roles.includes("admin") ? (
        <SourceEditor ownerGroups={user.owner_groups} />
      ) : (
        <p className={styles.noticeMuted}>
          Envanteri yalnızca yöneticiler değiştirebiliyor: bir kaynağın grubunu değiştirmek,
          o kaynağın verisini başka bir ekibe göstermek demek.
        </p>
      )}
    </AppShell>
  );
}

/** Envanterde karşılığı olmayan, veri gönderen cihazlar. */
function UnassignedTable({ rows, now }: { rows: readonly SourceActivityItem[]; now: Date }) {
  const columns: Column<SourceActivityItem>[] = [
    {
      key: "source_id",
      header: "Kaynak anahtarı",
      width: "16rem",
      render: (row) => <span className={styles.mono}>{row.source_id || "—"}</span>,
    },
    {
      key: "last",
      header: "Son görülme",
      width: "12rem",
      render: (row) => formatSince(row.last_ingested_at, now),
    },
    {
      key: "count",
      header: "Olay",
      width: "8rem",
      numeric: true,
      render: (row) => row.event_count,
    },
  ];

  return (
    <DataTable
      caption="Envanterde karşılığı olmayan cihazlar"
      columns={columns}
      rows={rows}
      rowKey={(row) => row.source_id}
    />
  );
}

/**
 * Üç isteğin biri düşerse ekranın tamamı kaybolmamalı: her blok kendi hatasını
 * gösteriyor ve diğerleri çiziliyor.
 */
async function load<T>(call: () => Promise<T>): Promise<{ value?: T; error?: string }> {
  try {
    return { value: await call() };
  } catch (error) {
    if (error instanceof ApiError) {
      return { error: error.hint ? `${error.message} ${error.hint}` : error.message };
    }

    return { error: error instanceof Error ? error.message : "bilinmeyen hata" };
  }
}
