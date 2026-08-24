import { readdirSync } from "node:fs";
import { join, relative } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * **`ui/src` ağacını gezen tek yürüteç.**
 *
 * <p>
 * Kaynağı tarayan bekçiler bu depoda çoğalıyor — `error_kind` zorlamasını
 * arayan biri vardı, olay üreticisini arayan biri geldi. İkisinin de ilk
 * ihtiyacı aynı: <b>`src` altındaki bütün `.ts`/`.tsx` dosyaları</b>. Bu
 * listeyi her bekçinin kendi içinde kurması CLAUDE.md §9'un yasakladığı
 * ikinci kopya olurdu, ve kopyaların ayrışması burada teorik bir risk değil:
 * ilk yürüteç <c>src</c>'nin üç alt dizinini elle sayıyordu ve
 * <c>src/middleware.ts</c> denetim dışında kalmıştı. Elle sayılan kapsam,
 * sayılmayan dosyayı görünmez yapıyor ve görünmezliğini hiçbir yerde
 * yazmıyor.
 * </p>
 *
 * <p>
 * Bu yüzden burada <b>elle sayılan hiçbir şey yok</b>: kök `src`, süzgeç
 * uzantı. Bir bekçinin daha dar bakması gerekiyorsa dönen listeyi kendisi
 * süzer — ve süzdüğünü kendi dosyasında, gerekçesiyle yazar.
 * </p>
 */

/** `ui/` kökü. Bu dosya `ui/tests/` altında duruyor. */
export const UI_KOK = fileURLToPath(new URL("..", import.meta.url));

/**
 * `ui/src` altındaki bütün `.ts`/`.tsx` dosyaları, **mutlak** yollarla.
 *
 * <p>Sıra dosya sisteminin verdiği sıra; bekçiler buna güvenmemeli.</p>
 */
export function kaynakDosyalari(): readonly string[] {
  const sonuc: string[] = [];

  for (const girdi of readdirSync(join(UI_KOK, "src"), { withFileTypes: true, recursive: true })) {
    if (girdi.isFile() && /\.tsx?$/.test(girdi.name)) {
      sonuc.push(join(girdi.parentPath ?? girdi.path, girdi.name));
    }
  }

  return sonuc;
}

/**
 * Mutlak yolu `ui/` köküne göre kısaltıyor — `src/app/olaylar/page.tsx`.
 *
 * <p>Hata mesajında makinenin ev dizini görünmesin diye: bekçi kırmızı
 * yandığında okunan şey yol DEĞİL, hangi dosya olduğu.</p>
 */
export function kisaYol(mutlak: string): string {
  return relative(UI_KOK, mutlak);
}
