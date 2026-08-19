import type { SourceItem } from "@/lib/api/client";
import { Button } from "@/components/ui/Button";
import { Field, SelectField } from "@/components/ui/Field";
import {
  MIN_FULL_TEXT_LENGTH,
  PAGE_SIZES,
  PARAM,
  PARSE_STATUSES,
  type SearchCriteria,
} from "@/lib/events/criteria";

import styles from "./events.module.css";

/**
 * Filtre formu — düz bir <c>GET</c> formu, istemci tarafı durum <b>yok</b>.
 *
 * <p>
 * Sonuç: sorgu adres çubuğunda, geri düğmesi çalışıyor, arama paylaşılabiliyor
 * ve T21'in alarm ekranı buraya derin bağlantı verebiliyor. JavaScript kapalı
 * olsa da ekran çalışıyor.
 * </p>
 *
 * <p>
 * Form imleç alanı <b>taşımıyor</b>: filtre değişince sayfalama baştan
 * başlamalı. Eski imleçle yeni filtre, kullanıcının hiç görmediği bir yerden
 * devam etmek demek olurdu.
 * </p>
 */

export interface SearchFormProps {
  readonly criteria: SearchCriteria;
  readonly sources: readonly SourceItem[];
  readonly ownerGroups: readonly string[];
  readonly unrestricted: boolean;
}

const SEVERITY_LABELS: Record<number, string> = {
  1: "1 — Bilgi",
  2: "2 — Düşük",
  3: "3 — Orta",
  4: "4 — Yüksek",
  5: "5 — Kritik",
  6: "6 — Ölümcül",
};

const PARSE_STATUS_LABELS: Record<string, string> = {
  ok: "Tam çözüldü",
  partial: "Kısmi",
  failed: "Çözülemedi",
};

export function SearchForm({ criteria, sources, ownerGroups, unrestricted }: SearchFormProps) {
  const sourceOptions = sources.map((source) => ({
    value: source.source_id,
    label: source.hostname ? `${source.source_id} — ${source.hostname}` : source.source_id,
  }));

  // Vendor listesi kaynak envanterinden türüyor: kullanıcının kapsamında
  // olmayan bir vendor'ı seçenek olarak göstermek, boş sonuç vaat etmek olurdu.
  const vendorOptions = [...new Set(sources.map((source) => source.vendor).filter(Boolean))]
    .sort((a, b) => a.localeCompare(b, "tr"))
    .map((vendor) => ({ value: vendor, label: vendor }));

  const groupOptions = [
    ...new Set([...ownerGroups, ...sources.map((source) => source.owner_group)]),
  ]
    .filter(Boolean)
    .sort((a, b) => a.localeCompare(b, "tr"))
    .map((group) => ({ value: group, label: group }));

  return (
    <form className={styles.form} method="get" action="/olaylar" role="search">
      <div className={styles.formRow}>
        <Field
          label="Metin ara"
          name={PARAM.fullText}
          defaultValue={criteria.fullText}
          placeholder="örn. bağlantı reddedildi"
          hint={`En az ${MIN_FULL_TEXT_LENGTH} karakter — altındaki sorgular indeksten faydalanmıyor.`}
          className={styles.grow}
        />
      </div>

      <div className={styles.formGrid}>
        {/*
          Kaynak filtresi ilk sırada ve ipucunda ölçülen sayı var: keyset
          sayfalama ancak `owner_group` + `source_id` verildiğinde sabit süreli.
        */}
        <SelectField
          label="Kaynak"
          name={PARAM.sourceId}
          defaultValue={criteria.sourceId}
          emptyLabel="Tümü (derin sayfalama yavaşlar)"
          options={sourceOptions}
          hint="Önerilir: kaynak seçildiğinde derin sayfa da ilk sayfa kadar hızlı."
        />

        <SelectField
          label="Grup"
          name={PARAM.ownerGroup}
          defaultValue={criteria.ownerGroup}
          emptyLabel={unrestricted ? "Tüm gruplar" : "Kapsamımdaki tüm gruplar"}
          options={groupOptions}
        />

        <SelectField
          label="Vendor"
          name={PARAM.vendor}
          defaultValue={criteria.vendor}
          emptyLabel="Tümü"
          options={vendorOptions}
        />

        <SelectField
          label="En düşük önem"
          name={PARAM.severityMin}
          defaultValue={criteria.severityMin === undefined ? "" : String(criteria.severityMin)}
          emptyLabel="Tümü"
          options={Object.entries(SEVERITY_LABELS).map(([value, label]) => ({ value, label }))}
          hint="Önem değeri olmayan (0) olaylar bu filtreyle dışarıda kalıyor."
        />

        <Field
          label="Protokol"
          name={PARAM.proto}
          defaultValue={criteria.proto}
          placeholder="tcp"
        />

        <Field
          label="Eylem"
          name={PARAM.action}
          defaultValue={criteria.action}
          placeholder="accept"
        />

        <Field
          label="Başlangıç"
          name={PARAM.from}
          type="datetime-local"
          defaultValue={criteria.from}
          hint="Boş bırakılırsa son 24 saat."
        />

        <Field label="Bitiş" name={PARAM.to} type="datetime-local" defaultValue={criteria.to} />

        <SelectField
          label="Sayfa boyutu"
          name={PARAM.limit}
          defaultValue={String(criteria.limit)}
          options={PAGE_SIZES.map((size) => ({ value: String(size), label: String(size) }))}
        />
      </div>

      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>Çözümleme durumu</legend>
        <div className={styles.checkRow}>
          {PARSE_STATUSES.map((status) => (
            <label key={status} className={styles.check}>
              <input
                type="checkbox"
                name={PARAM.parseStatus}
                value={status}
                defaultChecked={criteria.parseStatuses.includes(status)}
              />
              {PARSE_STATUS_LABELS[status]}
            </label>
          ))}
        </div>
      </fieldset>

      <div className={styles.formActions}>
        <Button type="submit" variant="primary">
          Ara
        </Button>
        <a className={styles.reset} href="/olaylar">
          Filtreleri sıfırla
        </a>
      </div>
    </form>
  );
}
