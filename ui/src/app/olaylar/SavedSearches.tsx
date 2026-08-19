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

const STORAGE_KEY = "bizigo.saved-searches";

interface SavedSearch {
  readonly name: string;
  /** `/olaylar?...` — ölçütlerin tamamı burada. */
  readonly href: string;
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

    persist([...saved.filter((entry) => entry.name !== trimmed), { name: trimmed, href: currentHref }]);
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
