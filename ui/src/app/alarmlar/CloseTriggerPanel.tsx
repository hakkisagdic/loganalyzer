"use client";

import { useState } from "react";

import { Badge } from "@/components/ui/Field";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import {
  CLOSE_COST_HINT,
  canClose,
  closeOutcomeText,
  closeRequest,
} from "@/lib/alerts/closure";
import type { AlertTrigger } from "@/lib/alerts/types";
import { CONTRADICTING_CHOICES, REVIEW_STATES } from "@/lib/rca/report";

import styles from "./alerts.module.css";

interface CloseResult {
  readonly trigger_id: string;
  readonly closed_at: string;
  readonly bundle_generated: boolean;
  readonly review_id: string;
  readonly owner_group: string;
}

/**
 * Alarm kapatma — <b>ve incelemenin kaçınılmaz olduğu yer</b> (T38).
 *
 * <p>
 * <b>Neden "kapat" diye tek bir düğme yok:</b> zorunluluk sunucuda yapısal
 * (<c>CloseAsync</c> incelemeyi parametre olarak istiyor), ve ekranın işi o
 * kuralı <i>tekrarlamak</i> değil <b>görünür kılmak</b>. Ayrı bir "kapat"
 * düğmesi olsaydı kullanıcı önce ona basar, 400 alır, ve zorunluluğu bir hata
 * mesajından öğrenirdi. Burada karar düğmeleri <b>kapatma düğmeleridir</b>:
 * "Doğru" demek hem incelemeyi yazmak hem alarmı kapatmak.
 * </p>
 *
 * <p>
 * <b>"Bilmiyorum" eşit bir seçenek</b> ve görsel olarak da öyle duruyor —
 * küçük bir kaçış bağlantısı değil, diğer üçüyle aynı sıradaki düğme. Zorunlu
 * bir soruda kaçış yoksa insanlar rastgele seçip geçer ve altın küme
 * <i>ölçülüyormuş gibi görünen</i> gürültüyle dolar; bu, ölçülememekten kötü.
 * Yanındaki not, seçimin ne yaptığını söylüyor: doğruluk oranına girmiyor,
 * kendi oranı ayrı bir gösterge.
 * </p>
 *
 * <p>
 * <b>Kapatma ucuz değil.</b> Paket yoksa üretimi tetikliyor, yani düğme
 * saniyeler sürebilir. Bu, tıklamadan <b>önce</b> yazılı: söylenmezse yavaş bir
 * düğme "takıldı" diye okunur, kullanıcı ikinci kez tıklar, ve ikinci tık
 * "zaten kapatılmış" hatası alır — sebebi olmayan bir arıza gibi görünen bir
 * hata.
 * </p>
 */
export function CloseTriggerPanel({
  trigger,
  onClosed,
}: {
  trigger: AlertTrigger;
  onClosed?: (result: CloseResult) => void;
}) {
  const [contradicting, setContradicting] = useState<string>("unknown");
  const [rootCause, setRootCause] = useState("");
  const [note, setNote] = useState("");
  const [saving, setSaving] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<CloseResult | null>(null);

  async function close(verdict: string) {
    if (!canClose(verdict)) {
      return;
    }

    setSaving(verdict);
    setError(null);

    try {
      const closed = (await api.post("/v1/alerts/triggers/{triggerId}/close", {
        path: { triggerId: trigger.id },
        body: closeRequest(verdict, contradicting, rootCause, note),
      })) as CloseResult;

      setResult(closed);
      onClosed?.(closed);
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setSaving(null);
    }
  }

  if (result) {
    return (
      <div className={styles.closeDone} data-testid="close-done">
        <Badge tone="success">kapatıldı</Badge>
        {/*
          Paketin bu kapatmada üretilip üretilmediği söyleniyor: kullanıcı
          neden beklediğini sonradan da görebilmeli.
        */}
        <span>{closeOutcomeText(result.bundle_generated)}</span>
      </div>
    );
  }

  return (
    <div className={styles.closePanel} data-testid="close-panel">
      <p className={styles.closeLead}>
        Alarmı kapatmak için <strong>&quot;doğru muydu?&quot;</strong> sorusunu
        cevaplayın. Cevap altın kümeye yazılıyor ve F4&apos;ün kalite ölçümünün
        dayanağı.
      </p>

      <label className={styles.closeField}>
        Çelişen kanıt bölümü
        <select
          value={contradicting}
          onChange={(event) => setContradicting(event.target.value)}
          data-testid="close-contradicting"
        >
          {CONTRADICTING_CHOICES.map((choice) => (
            <option key={choice.value} value={choice.value}>
              {choice.label}
            </option>
          ))}
        </select>
      </label>

      <label className={styles.closeField}>
        Gerçek kök neden
        <input
          type="text"
          value={rootCause}
          onChange={(event) => setRootCause(event.target.value)}
          placeholder="Rapor yanlışsa doğrusu neydi?"
          data-testid="close-root-cause"
        />
      </label>

      <label className={styles.closeField}>
        Not
        <input
          type="text"
          value={note}
          onChange={(event) => setNote(event.target.value)}
          placeholder="Neden böyle düşünüyorsunuz?"
          data-testid="close-note"
        />
      </label>

      {/*
        Dört düğme, dördü de kapatıyor. "Bilmiyorum" diğerleriyle aynı sırada ve
        aynı boyutta: küçültmek onu bir kaçış gibi gösterirdi, oysa ölçümün
        kendisi.
      */}
      <div className={styles.closeButtons}>
        {REVIEW_STATES.map((state) => (
          <button
            key={state.value}
            type="button"
            disabled={saving !== null}
            onClick={() => close(state.value)}
            data-testid={`close-${state.value}`}
          >
            {saving === state.value ? "Kapatılıyor…" : state.label}
          </button>
        ))}
      </div>

      <p className={styles.closeHint} data-testid="close-cost">
        {CLOSE_COST_HINT}
      </p>

      {error ? (
        <p className={styles.closeError} role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}
