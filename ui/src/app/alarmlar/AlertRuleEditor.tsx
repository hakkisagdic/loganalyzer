"use client";

import { useRouter } from "next/navigation";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Card, Field, SelectField } from "@/components/ui/Field";
import { ErrorState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import {
  COMPARISON_LABELS,
  COMPARISONS,
  RULE_TYPE_LABELS,
  RULE_TYPES,
  describeSeconds,
  toNumber,
  type AlertPreview,
  type AlertRule,
  type Comparison,
  type NotificationChannel,
  type RuleType,
} from "@/lib/alerts/types";

import { PreviewPanel } from "./PreviewPanel";
import styles from "./alerts.module.css";

export interface AlertRuleEditorProps {
  /** Düzenlenen kural; yeni kuralda `null`. */
  readonly rule: AlertRule | null;
  readonly channelIds: readonly string[];
  /**
   * Kullanıcının **kendi kapsamı**. Seçenekler bununla sınırlı: kullanıcı
   * yalnızca kapsamındaki gruplar için kural yazabiliyor (T23 kabul kriteri).
   * Sunucu tarafı da aynı kuralı ayrıca zorluyor — ekran onu tekrarlamıyor,
   * görünür kılıyor.
   */
  readonly ownerGroups: readonly string[];
  readonly unrestricted: boolean;
  readonly channels: readonly NotificationChannel[];
}

interface FormState {
  readonly name: string;
  readonly description: string;
  readonly ruleType: RuleType;
  readonly ownerGroups: readonly string[];
  readonly fullText: string;
  readonly sourceIds: string;
  readonly windowSeconds: number;
  readonly intervalSeconds: number;
  readonly threshold: number;
  readonly comparison: Comparison;
  readonly silenceSeconds: number;
  readonly repeatIntervalSeconds: number;
  readonly enabled: boolean;
  readonly channelIds: readonly string[];
}

function initialState(rule: AlertRule | null, channelIds: readonly string[]): FormState {
  return {
    name: rule?.name ?? "",
    description: rule?.description ?? "",
    ruleType: (rule?.rule_type as RuleType) ?? "threshold",
    ownerGroups: rule?.owner_groups ?? [],
    fullText: rule?.search.full_text ?? "",
    sourceIds: (rule?.search.source_ids ?? []).join(", "),
    windowSeconds: rule ? toNumber(rule.window_seconds) : 300,
    intervalSeconds: rule ? toNumber(rule.interval_seconds) : 60,
    threshold: rule ? toNumber(rule.threshold) : 100,
    comparison: (rule?.comparison as Comparison) ?? "gt",
    silenceSeconds: rule ? toNumber(rule.silence_seconds) : 900,
    repeatIntervalSeconds: rule ? toNumber(rule.repeat_interval_seconds) : 3600,
    enabled: rule?.enabled ?? true,
    channelIds: [...channelIds],
  };
}

function splitSources(value: string): string[] {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

/**
 * Kural formu ve **canlı önizleme** (T23).
 *
 * <p>
 * Ticket'ın taşıyıcı maddesi: kural geçmiş veriye karşı koşturulup "son 24
 * saatte kaç kez tetiklenirdi" gösteriliyor. K16'daki elli kişilik kurumda
 * eşiğini görmeden yazılan kural ya hiç tetiklenmiyor ya herkesi boğuyor —
 * bu ekran gürültüyü kural üretime girmeden kesen tek yer.
 * </p>
 *
 * <p>
 * <b>Kritik davranış: eşik değiştiğinde ağa çıkılmıyor.</b> Önizleme isteği
 * yalnızca <i>yapısal</i> alanlar değiştiğinde atılıyor (tip, pencere, filtre,
 * kapsam, geriye bakış); eşik ve karşılaştırma sunucudan gelen eşikten bağımsız
 * veri üzerinde <b>tarayıcıda</b> yeniden hesaplanıyor. Aksi hâlde kaydırıcıyı
 * sürükleyen tek bir kullanıcı saniyede onlarca ağır sorgu üretirdi.
 * </p>
 */
export function AlertRuleEditor({
  rule,
  channelIds,
  ownerGroups,
  unrestricted,
  channels,
}: AlertRuleEditorProps) {
  const router = useRouter();
  const [form, setForm] = useState<FormState>(() => initialState(rule, channelIds));
  const [preview, setPreview] = useState<AlertPreview | null>(null);
  const [previewError, setPreviewError] = useState<string | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const patch = useCallback(
    <K extends keyof FormState>(key: K, value: FormState[K]) =>
      setForm((current) => ({ ...current, [key]: value })),
    [],
  );

  const body = useCallback(
    () => ({
      name: form.name,
      description: form.description,
      ruleType: form.ruleType,
      ownerGroups: [...form.ownerGroups],
      fullText: form.fullText || undefined,
      filters: [],
      sourceIds: splitSources(form.sourceIds),
      windowSeconds: form.windowSeconds,
      intervalSeconds: form.intervalSeconds,
      threshold: form.threshold,
      comparison: form.comparison,
      silenceSeconds: form.silenceSeconds,
      repeatIntervalSeconds: form.repeatIntervalSeconds,
      enabled: form.enabled,
      channelIds: [...form.channelIds],
    }),
    [form],
  );

  /**
   * Önizleme isteğinin tetikleyicisi.
   *
   * <p>
   * Eşik (`threshold`, `comparison`) bu anahtarda <b>bilerek yok</b>: onlar
   * değiştiğinde yeniden sorgulamıyoruz. Anahtarın içeriği, sunucudan gelen
   * verinin geçersizleştiği alanların tam listesi.
   * </p>
   */
  const structuralKey = useMemo(
    () =>
      JSON.stringify([
        form.ruleType,
        form.ownerGroups,
        form.fullText,
        splitSources(form.sourceIds),
        form.windowSeconds,
        form.silenceSeconds,
      ]),
    [form.ruleType, form.ownerGroups, form.fullText, form.sourceIds, form.windowSeconds, form.silenceSeconds],
  );

  const bodyRef = useRef(body);
  bodyRef.current = body;

  useEffect(() => {
    const controller = new AbortController();

    // Yazarken her tuşta istek atmamak için küçük bir gecikme. Ağ trafiğinden
    // çok ClickHouse için: kaynak listesi yazılırken her ara adım ayrı bir
    // toplu sorgu demek.
    const timer = setTimeout(() => {
      setPreviewing(true);
      setPreviewError(null);

      api
        .post("/v1/alerts/rules/preview", { body: bodyRef.current(), signal: controller.signal })
        .then((result) => setPreview(result as AlertPreview))
        .catch((cause: unknown) => {
          if (controller.signal.aborted) {
            return;
          }

          setPreview(null);
          setPreviewError(describeError(cause));
        })
        .finally(() => {
          if (!controller.signal.aborted) {
            setPreviewing(false);
          }
        });
    }, 400);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [structuralKey]);

  async function save() {
    setSaving(true);
    setSaveError(null);

    try {
      if (rule) {
        await api.put("/v1/alerts/rules/{id}", { path: { id: rule.id }, body: bodyRef.current() });
      } else {
        await api.post("/v1/alerts/rules", { body: bodyRef.current() });
      }

      router.push("/alarmlar");
      router.refresh();
    } catch (cause) {
      setSaveError(describeError(cause));
    } finally {
      setSaving(false);
    }
  }

  const groupOptions = unrestricted && ownerGroups.length === 0 ? form.ownerGroups : ownerGroups;
  const isSilence = form.ruleType === "silence";

  return (
    <div className={styles.stack}>
      <Card>
        <div className={styles.stack}>
          <div className={styles.formGrid}>
            <Field
              label="Kural adı"
              value={form.name}
              onChange={(event) => patch("name", event.target.value)}
              required
            />

            <SelectField
              label="Kural tipi"
              value={form.ruleType}
              onChange={(event) => patch("ruleType", event.target.value as RuleType)}
              options={RULE_TYPES.map((type) => ({ value: type, label: RULE_TYPE_LABELS[type] }))}
              hint={
                isSilence
                  ? "Verinin YOKLUĞU üzerinde çalışır: susan cihaz, gürültü yapandan tehlikelidir."
                  : "Verinin varlığı üzerinde çalışır."
              }
            />
          </div>

          <Field
            label="Açıklama"
            value={form.description}
            onChange={(event) => patch("description", event.target.value)}
            hint="Alarm geldiğinde nöbetteki kişinin okuyacağı cümle."
          />

          <fieldset className={styles.groupList}>
            <legend className={styles.muted}>
              Kapsam — yalnızca kendi gruplarınız
            </legend>

            {groupOptions.length === 0 ? (
              <span className={styles.muted}>
                Hiçbir gruba eşlenmediğiniz için kural yazamazsınız.
              </span>
            ) : (
              groupOptions.map((group) => (
                <label key={group}>
                  <input
                    type="checkbox"
                    checked={form.ownerGroups.includes(group)}
                    onChange={(event) =>
                      patch(
                        "ownerGroups",
                        event.target.checked
                          ? [...form.ownerGroups, group]
                          : form.ownerGroups.filter((item) => item !== group),
                      )
                    }
                  />
                  {group}
                </label>
              ))
            )}
          </fieldset>

          <div className={styles.formGrid}>
            {!isSilence ? (
              <Field
                label="Tam metin araması"
                value={form.fullText}
                onChange={(event) => patch("fullText", event.target.value)}
                // F1'de ölçüldü: indeks ~10-11 karakterden sonra seçici.
                // Kısa sorgu tabloyu tarıyor ve bu kural DAKİKADA bir koşuyor.
                hint="On karakterden kısa aramalar tüm tabloyu tarar; kural periyodik koştuğu için bedeli katlanır."
              />
            ) : null}

            <Field
              label="Kaynaklar (virgülle)"
              value={form.sourceIds}
              onChange={(event) => patch("sourceIds", event.target.value)}
              hint={
                isSilence
                  ? "Boş bırakılırsa kapsamdaki tüm kaynaklar izlenir."
                  : "Kaynak filtresi sorguyu belirgin biçimde hızlandırır."
              }
            />
          </div>

          <div className={styles.formGrid}>
            {isSilence ? (
              <Field
                label="Sessizlik eşiği (saniye)"
                type="number"
                min={60}
                value={form.silenceSeconds}
                onChange={(event) => patch("silenceSeconds", Number(event.target.value))}
                hint={`Bir kaynak ${describeSeconds(form.silenceSeconds)} susarsa alarm.`}
              />
            ) : (
              <>
                <Field
                  label="Değerlendirme penceresi (saniye)"
                  type="number"
                  min={1}
                  value={form.windowSeconds}
                  onChange={(event) => patch("windowSeconds", Number(event.target.value))}
                  hint={describeSeconds(form.windowSeconds)}
                />

                <Field
                  label={form.ruleType === "ratio" ? "Eşik (kat)" : "Eşik (sayı)"}
                  type="number"
                  step="any"
                  value={form.threshold}
                  onChange={(event) => patch("threshold", Number(event.target.value))}
                  hint="Değiştirdiğinizde aşağıdaki sayı anında güncellenir — yeni sorgu atılmaz."
                />

                <SelectField
                  label="Karşılaştırma"
                  value={form.comparison}
                  onChange={(event) => patch("comparison", event.target.value as Comparison)}
                  options={COMPARISONS.map((item) => ({ value: item, label: COMPARISON_LABELS[item] }))}
                />
              </>
            )}

            <Field
              label="Koşum aralığı (saniye)"
              type="number"
              min={30}
              value={form.intervalSeconds}
              onChange={(event) => patch("intervalSeconds", Number(event.target.value))}
              hint={`Her ${describeSeconds(form.intervalSeconds)} bir değerlendirilir.`}
            />

            <Field
              label="Tekrar aralığı (saniye)"
              type="number"
              min={0}
              value={form.repeatIntervalSeconds}
              onChange={(event) => patch("repeatIntervalSeconds", Number(event.target.value))}
              hint="Aynı kural bu süre dolmadan yeniden tetiklenmez — gürültü kontrolünün ilk kademesi."
            />
          </div>

          <fieldset className={styles.channelList}>
            <legend className={styles.muted}>Bildirim kanalları</legend>

            {channels.length === 0 ? (
              <span className={styles.muted}>
                Tanımlı kanal yok. Kanallar sekmesinden ekleyebilirsiniz.
              </span>
            ) : (
              channels.map((channel) => (
                <label key={channel.id}>
                  <input
                    type="checkbox"
                    checked={form.channelIds.includes(channel.id)}
                    onChange={(event) =>
                      patch(
                        "channelIds",
                        event.target.checked
                          ? [...form.channelIds, channel.id]
                          : form.channelIds.filter((item) => item !== channel.id),
                      )
                    }
                  />
                  {channel.name} <span className={styles.muted}>({channel.channel_type})</span>
                </label>
              ))
            )}
          </fieldset>

          <label className={styles.checkboxRow}>
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={(event) => patch("enabled", event.target.checked)}
            />
            Kural etkin
          </label>

          {saveError ? <ErrorState title="Kural kaydedilemedi" hint={saveError} /> : null}

          <div className={styles.inlineActions}>
            <Button variant="primary" onClick={save} disabled={saving || form.ownerGroups.length === 0}>
              {saving ? "Kaydediliyor…" : rule ? "Değişiklikleri kaydet" : "Kuralı oluştur"}
            </Button>
            <Button onClick={() => router.push("/alarmlar")}>Vazgeç</Button>
          </div>
        </div>
      </Card>

      <PreviewPanel
        preview={preview}
        loading={previewing}
        error={previewError}
        threshold={isSilence ? form.silenceSeconds : form.threshold}
        comparison={form.comparison}
        isSilence={isSilence}
      />
    </div>
  );
}
