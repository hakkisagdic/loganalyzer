"use client";

import { useState } from "react";

import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { Card, Field, SelectField } from "@/components/ui/Field";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { api, type EventSummary, type SourceItem } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import { decodeBase64, decodeText } from "@/lib/events/raw";

import styles from "./parsers.module.css";

export interface ArchiveSamplerProps {
  readonly sources: readonly SourceItem[];
  readonly onPick: (line: string) => void;
}

/**
 * Ham arşivden **gerçek** bir satır çekme (T19).
 *
 * <p>
 * Ticket'ın açık maddesi: <i>uydurma örnekle yazılan parser üretimde
 * çuvallıyor.</i> Cihazın gerçekte yazdığı boşluklar, tırnaklar, kaçışlar ve
 * kodlama ancak arşivdeki baytlarda görünüyor — elle yazılan bir örnek satır,
 * yazanın parser'ın nasıl çalışmasını istediğini gösteriyor, cihazın ne
 * gönderdiğini değil.
 * </p>
 *
 * <p>
 * <b>Neden çözülmüş gövde değil, ham baytlar:</b> olay kaydındaki
 * <c>body</c> boru hattından geçmiş hâl. Yeni bir parser yazan kişinin
 * ihtiyacı boru hattına <b>girmeden önceki</b> satır. Bu yüzden
 * <c>GET /v1/events/{id}/raw</c>'dan base64 alınıp burada çözülüyor —
 * kodlama etiketiyle birlikte, çünkü <c>windows-1254</c> bir satır UTF-8
 * varsayılırsa parser doğru yazılsa bile tutmaz (K4, K27).
 * </p>
 *
 * <p>
 * Kapsam zorlaması sunucuda (K17): kullanıcı yalnızca kendi gruplarının
 * olaylarını görüyor ve kapsam dışı bir kimlik 404 dönüyor. Ekran bunu
 * tekrarlamıyor.
 * </p>
 */
export function ArchiveSampler({ sources, onPick }: ArchiveSamplerProps) {
  const [sourceId, setSourceId] = useState("");
  const [text, setText] = useState("");
  const [events, setEvents] = useState<readonly EventSummary[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fetching, setFetching] = useState<string | null>(null);

  async function search() {
    setLoading(true);
    setError(null);

    try {
      const result = await api.post("/v1/events/search", {
        body: {
          full_text: text.length > 0 ? text : undefined,
          source_ids: sourceId.length > 0 ? [sourceId] : [],
          // Ayrıştırılamayan satırlar önce geliyor DEĞİL — hepsi geliyor.
          // Yeni parser çoğu zaman `failed` satırlar için yazılıyor ama
          // var olan bir parser'ı düzeltirken `ok` satırlar da gerekiyor.
          parse_statuses: [],
          limit: 25,
          ascending: false,
        },
      });

      setEvents(result.events);
    } catch (cause) {
      setEvents(null);
      setError(describeError(cause));
    } finally {
      setLoading(false);
    }
  }

  /**
   * Seçilen olayın **ham baytlarını** getirip editöre koyuyor.
   *
   * <p>Çok satırlı bir gövdenin yalnızca ilk satırı alınıyor: parser tek bir
   * log satırı işliyor ve gövdenin tamamını örnek diye vermek, testin
   * hiçbir zaman geçmeyeceği bir girdi üretirdi.</p>
   */
  async function pick(event: EventSummary) {
    setFetching(event.event_id);
    setError(null);

    try {
      const raw = await api.get("/v1/events/{id}/raw", { path: { id: event.event_id } });
      const decoded = decodeText(decodeBase64(raw.raw_b64), raw.encoding_detected);
      const [first = ""] = decoded.text.split(/\r?\n/);

      onPick(first);

      if (decoded.fellBack) {
        setError(
          `Kodlama "${raw.encoding_detected}" tarayıcı tarafından tanınmadı; satır UTF-8 varsayılarak ` +
            "çözüldü. Parser'ı bu satıra göre yazmadan önce kodlamayı doğrulayın.",
        );
      }
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setFetching(null);
    }
  }

  return (
    <Card>
      <div className={styles.stack}>
        <div className={styles.toolbar}>
          <h2>Ham arşivden satır getir</h2>
        </div>

        <p className={styles.muted}>
          Uydurma örnekle yazılan parser üretimde çuvallıyor. Buradan çekilen satır arşivdeki{" "}
          <strong>orijinal baytlardan</strong> çözülüyor — boru hattından geçmiş gövdeden değil.
        </p>

        <div className={styles.formGrid}>
          <SelectField
            label="Kaynak"
            value={sourceId}
            onChange={(event) => setSourceId(event.target.value)}
            emptyLabel="Tüm kaynaklar"
            options={sources.map((source) => ({
              value: source.source_id,
              label: source.hostname ? `${source.source_id} — ${source.hostname}` : source.source_id,
            }))}
            hint={
              sources.length === 0
                ? "Kapsamınızda kaynak yok; envanter eşlemesi eksik olabilir."
                : "Kaynak filtresi sorguyu belirgin biçimde hızlandırıyor."
            }
          />

          <Field
            label="Metin ara"
            value={text}
            onChange={(event) => setText(event.target.value)}
            placeholder="örn. login failure"
            hint="On bir karakterden kısa aramalar indeksten faydalanmıyor, tüm tabloyu tarıyor."
          />
        </div>

        <div className={styles.inlineActions}>
          <Button variant="primary" onClick={search} disabled={loading}>
            {loading ? "Aranıyor…" : "Son olayları getir"}
          </Button>
        </div>

        {error ? <ErrorState title="Arşivden satır alınamadı" hint={error} /> : null}

        {loading && !events ? <LoadingState label="Olaylar aranıyor…" rows={4} /> : null}

        {events && events.length === 0 ? (
          <EmptyState
            title="Bu filtreyle olay yok"
            description="Kaynağı ya da metni değiştirin. Kapsamınız dışındaki olaylar hiç görünmüyor."
          />
        ) : null}

        {events && events.length > 0 ? (
          <DataTable
            caption={`Son ${events.length} olay`}
            rowKey={(row) => row.event_id}
            rows={events}
            columns={[
              { key: "ts", header: "Zaman", width: "12rem", render: (row) => row.ts },
              { key: "kaynak", header: "Kaynak", width: "20%", render: (row) => row.source_id },
              {
                key: "durum",
                header: "Ayrıştırma",
                width: "8rem",
                render: (row) => row.parse_status,
              },
              { key: "govde", header: "Gövde", freeText: true, render: (row) => row.body },
              {
                key: "sec",
                header: "",
                width: "9rem",
                render: (row) => (
                  <Button onClick={() => pick(row)} disabled={fetching !== null}>
                    {fetching === row.event_id ? "Getiriliyor…" : "Örnek yap"}
                  </Button>
                ),
              },
            ]}
          />
        ) : null}
      </div>
    </Card>
  );
}
