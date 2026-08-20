"use client";

import { useEffect, useState } from "react";

import { Button } from "@/components/ui/Button";

import styles from "./events.module.css";

/**
 * Kayıtlı aramalar.
 *
 * <p>
 * Bir arama, ölçütlerinin tamamı adres çubuğunda durduğu için <b>bir URL'den
 * ibaret</b>. Kaydetmek de o URL'i bir adla saklamak demek.
 * </p>
 *
 * <p>
 * <b>Depo tarayıcıda</b> (<c>localStorage</c>): kontrol düzleminde kayıtlı arama
 * tablosu yok ve bu ticket bir EF göçü eklemiyor. Sonucu açıkça söylemek
 * gerekiyor — kayıtlar cihaza bağlı, paylaşılmıyor ve <b>T21'in alarm kuralları
 * buna bağlanamaz</b>: sunucuda duran bir kural, tarayıcıdaki bir girdiye
 * referans veremez. Sunucu tarafı kayıtlı arama ayrı bir uç + tablo demek.
 * </p>
 */

export const STORAGE_KEY = "bizigo.saved-searches";

export interface SavedSearch {
  readonly name: string;
  /** `/olaylar?...` — ölçütlerin tamamı burada. */
  readonly href: string;
}

/**
 * Kaydetmenin <b>saf</b> hâli: yeni liste ne olacak.
 *
 * <p>
 * Bileşenden ayrı duruyor ve sebebi tek: <b>tarayıcı deposuna ne yazıldığı</b>
 * sınanabilir olmalı (T27). Ticket token sızıntısı için üç yer sayıyor — yanıt,
 * çerez ve <c>localStorage</c> — ve ilk ikisi her baytıyla taranırken üçüncüsü
 * hiç sınanmıyordu.
 * </p>
 *
 * <p>
 * Yazılan şeklin dar kalması, §8'in "depolama tipi tel sözleşmesi değildir"
 * kuralının tarayıcı tarafındaki karşılığı: kayda eklenen her alan, kimse karar
 * vermeden cihazda kalıcı hâle gelir.
 * </p>
 */
export function nextEntries(
  existing: readonly SavedSearch[],
  name: string,
  href: string,
): SavedSearch[] {
  const trimmed = name.trim();

  // Aynı ad iki kez kaydedilirse sonuncusu kazanıyor; iki girdi bırakmak
  // kullanıcıya hangisinin güncel olduğunu sormak olurdu.
  return [...existing.filter((entry) => entry.name !== trimmed), { name: trimmed, href }];
}

function read(): SavedSearch[] {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    const parsed: unknown = raw ? JSON.parse(raw) : [];

    return Array.isArray(parsed)
      ? parsed.filter(
          (entry): entry is SavedSearch =>
            typeof entry === "object" &&
            entry !== null &&
            typeof (entry as SavedSearch).name === "string" &&
            typeof (entry as SavedSearch).href === "string",
        )
      : [];
  } catch {
    // Bozuk ya da başka bir sürümden kalma içerik ekranı çökertmemeli.
    return [];
  }
}

export function SavedSearches({ currentHref }: { currentHref: string }) {
  const [saved, setSaved] = useState<SavedSearch[]>([]);
  const [name, setName] = useState("");

  // `localStorage` sunucuda yok; ilk çizim sonrası okunuyor. Bu yüzden sunucu
  // ve istemci çıktısı ilk anda aynı (boş liste) ve hidrasyon uyuşmazlığı
  // oluşmuyor.
  useEffect(() => setSaved(read()), []);

  function persist(next: SavedSearch[]): void {
    setSaved(next);
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
  }

  function save(): void {
    const trimmed = name.trim();

    if (trimmed.length === 0) {
      return;
    }

    persist(nextEntries(saved, trimmed, currentHref));
    setName("");
  }

  return (
    <section className={styles.saved} aria-label="Kayıtlı aramalar">
      <div className={styles.savedForm}>
        <input
          className={styles.savedInput}
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder="Bu aramaya bir ad verin"
          aria-label="Kayıtlı arama adı"
        />
        <Button onClick={save} disabled={name.trim().length === 0}>
          Aramayı kaydet
        </Button>
      </div>

      {saved.length === 0 ? (
        <p className={styles.savedEmpty}>
          Kayıtlı arama yok. Kayıtlar yalnızca <b>bu tarayıcıda</b> tutuluyor.
        </p>
      ) : (
        <ul className={styles.savedList}>
          {saved.map((entry) => (
            <li key={entry.name}>
              <a href={entry.href}>{entry.name}</a>
              <button
                type="button"
                className={styles.savedRemove}
                onClick={() => persist(saved.filter((other) => other.name !== entry.name))}
                aria-label={`${entry.name} aramasını sil`}
              >
                ×
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
