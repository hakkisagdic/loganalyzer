"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";

import { Button } from "@/components/ui/Button";
import { Field } from "@/components/ui/Field";
import { ErrorState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";

import styles from "./inventory.module.css";

/**
 * Kaynak ekleme/düzenleme ve CSV yükleme (T17).
 *
 * <p>
 * İstemci bileşeni: yazma işlemleri tarayıcıdan BFF vekiline gidiyor ve oradaki
 * oturum çerezi sunucuda <c>Authorization</c>'a çevriliyor — alarm ekranlarıyla
 * aynı desen. Token yine tarayıcıya hiç ulaşmıyor.
 * </p>
 */

interface Props {
  /** Yalnızca yönetici yazabiliyor; buton grubu diğerlerine hiç çizilmiyor. */
  readonly ownerGroups: readonly string[];
}

export function SourceEditor({ ownerGroups }: Props) {
  const router = useRouter();

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<{ message: string; details?: string[] } | undefined>();
  const [done, setDone] = useState<string | undefined>();

  async function submitSource(form: FormData): Promise<void> {
    setBusy(true);
    setError(undefined);
    setDone(undefined);

    try {
      await api.post("/v1/sources", {
        body: {
          sourceId: String(form.get("source_id") ?? "").trim(),
          ownerGroup: String(form.get("owner_group") ?? "").trim(),
          peerAddress: String(form.get("peer_address") ?? "").trim(),
          hostname: String(form.get("hostname") ?? "").trim(),
          vendor: String(form.get("vendor") ?? "").trim(),
          product: String(form.get("product") ?? "").trim(),
          parserId: String(form.get("parser_id") ?? "").trim(),
          encoding: String(form.get("encoding") ?? "auto").trim() || "auto",
          sourceClass: String(form.get("source_class") ?? "default").trim() || "default",
          enabled: form.get("enabled") !== null,
        },
      });

      setDone("Kaynak kaydedildi.");
      router.refresh();
    } catch (cause) {
      setError({ message: describe(cause) });
    } finally {
      setBusy(false);
    }
  }

  async function submitCsv(form: FormData): Promise<void> {
    const file = form.get("csv");

    if (!(file instanceof File) || file.size === 0) {
      setError({ message: "Bir CSV dosyası seçin." });
      return;
    }

    setBusy(true);
    setError(undefined);
    setDone(undefined);

    try {
      // Tiplenmiş istemci JSON gönderiyor; bu uç ham CSV okuyor. Vekil
      // `content-type`'ı ve gövdeyi olduğu gibi taşıdığı için doğrudan
      // çağırmak, gövdeyi JSON'a sarıp sunucuda geri açmaktan dürüst.
      const response = await fetch("/api/bff/v1/sources/csv", {
        method: "POST",
        headers: { "content-type": "text/csv", accept: "application/json" },
        body: await file.text(),
        credentials: "same-origin",
        cache: "no-store",
      });

      const body: unknown = await response.json().catch(() => undefined);

      if (!response.ok) {
        // API satır satır reddediyor; o listeyi yutmak, kullanıcıyı dosyayı
        // tahminle düzeltmeye bırakmak olurdu.
        const problem = body as { error?: string; details?: string[] } | undefined;

        setError({
          message: problem?.error ?? `Yükleme başarısız (HTTP ${response.status}).`,
          details: problem?.details,
        });
        return;
      }

      const summary = body as { created: number; updated: number; total: number };
      setDone(
        `${summary.total} satır işlendi: ${summary.created} eklendi, ${summary.updated} güncellendi.`,
      );
      router.refresh();
    } catch {
      setError({ message: "Sunucuya ulaşılamadı." });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className={styles.editor}>
      <form
        className={styles.editorForm}
        action={submitSource}
        aria-label="Kaynak ekle veya güncelle"
      >
        <p className={styles.blockTitle}>Kaynak ekle / güncelle</p>

        <div className={styles.editorGrid}>
          <Field label="Kaynak kimliği" name="source_id" required placeholder="fg-ankara-01" />
          <Field
            label="Grup"
            name="owner_group"
            required
            placeholder={ownerGroups[0] ?? "network/core"}
            hint="Grubu değiştirmek, o kaynağın verisini başka bir ekibe göstermek demek."
          />
          <Field label="Adres" name="peer_address" placeholder="10.1.1.1" />
          <Field label="Hostname" name="hostname" placeholder="fw-01" />
          <Field label="Vendor" name="vendor" placeholder="fortinet" />
          <Field label="Ürün" name="product" placeholder="fortigate" />
          <Field label="Parser" name="parser_id" placeholder="fortinet.traffic" />
          <Field
            label="Kodlama"
            name="encoding"
            defaultValue="auto"
            hint="Yanlış değer, boru hattında kodlama uyuşmazlığı olarak sayılıyor."
          />
          <Field label="Sınıf" name="source_class" defaultValue="default" />
        </div>

        <label className={styles.check}>
          <input type="checkbox" name="enabled" defaultChecked />
          Etkin
        </label>

        <Button type="submit" variant="primary" disabled={busy}>
          Kaydet
        </Button>
      </form>

      <form className={styles.editorForm} action={submitCsv} aria-label="CSV ile toplu yükleme">
        <p className={styles.blockTitle}>CSV ile toplu yükleme</p>

        <p className={styles.muted}>
          Zorunlu sütunlar: <code>source_id</code>, <code>owner_group</code>. Yükleme{" "}
          <b>ya hep ya hiç</b>: tek satır bile reddedilirse hiçbiri yazılmaz. Yarım bir
          envanter, hangi cihazın hangi gruba düştüğünü belirsiz bırakır ve o belirsizlik
          doğrudan bir kapsam hatasıdır.
        </p>

        <input type="file" name="csv" accept=".csv,text/csv" aria-label="CSV dosyası" />

        <Button type="submit" disabled={busy}>
          Yükle
        </Button>
      </form>

      {done ? (
        <p className={styles.success} role="status">
          {done}
        </p>
      ) : null}

      {error ? (
        <ErrorState
          title={error.message}
          hint={
            error.details?.length
              ? `Reddedilen satırlar: ${error.details.join(" · ")}`
              : undefined
          }
        />
      ) : null}
    </div>
  );
}

function describe(cause: unknown): string {
  if (cause instanceof ApiError) {
    return cause.hint ? `${cause.message} ${cause.hint}` : cause.message;
  }

  return "Kaynak kaydedilemedi.";
}
