import type { SourceActivityItem, SourceItem } from "@/lib/api/client";
import { formatSince } from "@/lib/ui/time";

export { formatSince };

/**
 * Envanter ile olay etkinliğinin birleştirilmesi (T17).
 *
 * <p>
 * İki kaynak iki farklı yerden geliyor ve <b>ikisi de tek başına eksik</b>:
 * envanter listesi (kontrol düzlemi) hangi cihazın kayıtlı olduğunu biliyor ama
 * veri gelip gelmediğini bilmiyor; etkinlik sorgusu (ClickHouse) veri geleni
 * biliyor ama <b>hiç veri göndermemiş bir kaynağı listeleyemiyor</b> — olay
 * tablosu var olmayan bir şeyi döndüremez.
 * </p>
 *
 * <p>
 * Birleştirme bu yüzden burada ve saf: "hiç veri gelmedi" ile "N saattir veri
 * gelmiyor" ayrımı ekranın en önemli bilgisi ve konteyner gerektirmeden
 * sınanabilmeli.
 * </p>
 */

/** Envanterde olmayan cihazların olayları bu gruba düşüyor (F1 §8). */
export const UNASSIGNED_GROUP = "_unassigned";

export interface InventoryRow {
  readonly source: SourceItem;
  /** Pencerede veri geldiyse. Gelmediyse <c>undefined</c> — "hiç" demek değil, "bu pencerede yok". */
  readonly activity: SourceActivityItem | undefined;
}

/**
 * Envanteri etkinlikle eşleştiriyor.
 *
 * <p>Sıra bilinçli: veri gelmeyenler <b>başta</b>. Envanterin en çok bakılma
 * sebebi "hangi cihaz susuyor" sorusu ve o satırların listenin dibinde
 * kalması, ekranı sorulan soruya cevap veremez hâle getiriyor.</p>
 */
export function mergeInventory(
  sources: readonly SourceItem[],
  activity: readonly SourceActivityItem[],
): InventoryRow[] {
  const byId = new Map(activity.map((row) => [row.source_id, row]));

  return [...sources]
    .map((source) => ({ source, activity: byId.get(source.source_id) }))
    .sort((a, b) => {
      // Sessiz olanlar önce; sonra en eski görülme; sonra ada göre.
      if ((a.activity === undefined) !== (b.activity === undefined)) {
        return a.activity === undefined ? -1 : 1;
      }

      if (a.activity && b.activity) {
        const difference =
          Date.parse(a.activity.last_ingested_at) - Date.parse(b.activity.last_ingested_at);

        if (difference !== 0) {
          return difference;
        }
      }

      return a.source.source_id.localeCompare(b.source.source_id, "tr");
    });
}

/**
 * <b>Envanterde olmayan cihazlar.</b>
 *
 * <p>
 * Bunların envanter satırı yok — tanım gereği. Veri gönderiyorlar ve dispatcher
 * onları <c>_unassigned</c> grubuna düşürüyor; olay <b>reddedilmiyor</b>, çünkü
 * veri kaybı eksik envanterden kötü. Ama bu, o cihazın verisinin hiçbir ekibe
 * görünmemesi demek, dolayısıyla ekranın dibinde değil <b>üstünde</b> durmalı.
 * </p>
 *
 * <p>
 * Yalnızca kapsamı <c>_unassigned</c>'ı içeren kullanıcı (pratikte yönetici)
 * bu satırları görüyor; sıradan bir analist için liste boş dönüyor ve bu doğru.
 * </p>
 */
export function unassignedSources(
  activity: readonly SourceActivityItem[],
): SourceActivityItem[] {
  return activity
    .filter((row) => row.owner_group === UNASSIGNED_GROUP)
    .sort((a, b) => Date.parse(b.last_ingested_at) - Date.parse(a.last_ingested_at));
}

export type SilenceKind =
  /** Pencerede hiç veri yok. */
  | "quiet"
  /** Veri geldi. */
  | "active";

export interface Silence {
  readonly kind: SilenceKind;
  readonly label: string;
}

/**
 * "Ne zamandır susuyor" — <b>olgu olarak</b>, eşik uydurmadan.
 *
 * <p>
 * Ekran kendi sessizlik eşiğini <b>tanımlamıyor</b>: o eşik T21'in alarm
 * kuralında, kural başına. Burada ikinci bir eşik tanımlamak, envanterin
 * "sağlıklı" dediği bir kaynağın alarm üretmesi (ya da tersi) demek olurdu ve
 * ayrıştıkları ancak biri şikâyet ettiğinde fark edilirdi.
 * </p>
 *
 * <p>
 * <c>last_ingested_at</c> kullanılıyor, <c>last_event_at</c> değil: ikincisi
 * cihazın kendi saati ve saati şaşmış bir cihaz "gelecekte" görünebilir.
 * "Susuyor mu" sorusunun cevabı <b>bizim aldığımız an</b>.
 * </p>
 */
export function describeSilence(row: InventoryRow, now: Date): Silence {
  if (!row.activity) {
    return { kind: "quiet", label: "bu pencerede veri yok" };
  }

  return { kind: "active", label: formatSince(row.activity.last_ingested_at, now) };
}

