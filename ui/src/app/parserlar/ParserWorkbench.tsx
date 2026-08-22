"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { Button } from "@/components/ui/Button";
import { CodeEditor, type EditorMarker } from "@/components/ui/CodeEditor";
import { Badge, Card, Field } from "@/components/ui/Field";
import { ErrorState } from "@/components/ui/States";
import { api, type SourceItem } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import { toNumber } from "@/lib/api/numbers";
import { fetchDraft } from "@/lib/parsers/draft";
import { errorKind } from "@/lib/telemetry/classify";
import { track } from "@/lib/telemetry/client";
import { yamlLines } from "@/lib/telemetry/measure";
import { STEP_TYPES, suggest } from "@/lib/parsers/schema";
import { NEW_PARSER_TEMPLATE, appendStep } from "@/lib/parsers/template";
import type { ParserGate, ParserTry } from "@/lib/parsers/types";
import { tokenizeLine } from "@/lib/parsers/yaml";

import { ArchiveSampler } from "./ArchiveSampler";
import { GateReport } from "./GateReport";
import { TryPanel } from "./TryPanel";
import styles from "./parsers.module.css";

export interface ParserWorkbenchProps {
  readonly sources: readonly SourceItem[];
  /** Adres çubuğundan gelen taslak kimliği; yeni parser yazılırken boş. */
  readonly draftId: string;
}

/**
 * Parser editörü (T19).
 *
 * <p>
 * Ticket'ın taşıyıcı fikri: parser <b>ürün içinde</b> yazılabilmeli ve
 * <b>yayınlanmadan önce</b> denenebilmeli. Bu bileşen ikisini tek ekranda
 * birleştiriyor — yazarken kapılar koşuyor, örnek satır anında deneniyor ve
 * "neden yayınlanamıyor" sorusunun cevabı satır numarasıyla duruyor.
 * </p>
 *
 * <p>
 * <b>Tek istek, üç cevap.</b> Her denemede <c>POST /v1/parsers/try</c>
 * çağrılıyor ve dönen gövde hem kapı kararını, hem taslağın örnek satırdaki
 * sonucunu, hem de aynı satırın <b>bugünkü katalogdaki</b> yolunu taşıyor.
 * Üçünü ayrı uçlara bölmek, aralarında zaman farkı olan üç görüntü demekti:
 * kullanıcı bir taslağı düzeltirken kapı eski YAML'ı, önizleme yenisini
 * gösterebilirdi.
 * </p>
 *
 * <p>
 * <b>Gecikme bilinçli.</b> Her tuşta derleme istemek, keyfi YAML'ı derleyen
 * bir ucu tuş hızında çağırmak olurdu. 500 ms sessizlik bekleniyor ve önceki
 * istek iptal ediliyor.
 * </p>
 */
export function ParserWorkbench({ sources, draftId }: ParserWorkbenchProps) {
  const [yaml, setYaml] = useState(NEW_PARSER_TEMPLATE);
  const [line, setLine] = useState("");

  const [tryResult, setTryResult] = useState<ParserTry | null>(null);
  const [tryError, setTryError] = useState<string | null>(null);
  const [trying, setTrying] = useState(false);

  const [savedId, setSavedId] = useState(draftId);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const [loadingDraft, setLoadingDraft] = useState(draftId.length > 0);
  const [loadError, setLoadError] = useState<string | null>(null);

  const editorRef = useRef<HTMLDivElement>(null);

  /**
   * Kaydedilmiş taslağı açıyor.
   *
   * <p>Yükleme <b>başarısızsa iskelet gösterilmiyor</b>: kullanıcı taslağının
   * üstüne yazdığını sanıp kaydeder ve gerçekten kaybederdi. Hata görünür
   * kalıyor ve editör kilitli.</p>
   */
  useEffect(() => {
    if (draftId.length === 0) {
      return;
    }

    const controller = new AbortController();

    fetchDraft(draftId, controller.signal)
      .then((draft) => {
        setYaml(draft.yaml);
        setSavedId(draft.id || draftId);
        setLoadError(null);
      })
      .catch((cause: unknown) => {
        if (controller.signal.aborted) return;
        setLoadError(describeError(cause));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoadingDraft(false);
      });

    return () => controller.abort();
  }, [draftId]);

  useEffect(() => {
    if (loadError) {
      return;
    }

    const controller = new AbortController();

    const timer = setTimeout(() => {
      setTrying(true);
      setTryError(null);

      const started = performance.now();

      api
        .post("/v1/parsers/try", {
          body: { yaml, line, parser_id: "" },
          signal: controller.signal,
        })
        .then((result) => {
          setTryResult(result);

          // Derlemenin ŞEKLİ ölçülüyor, içeriği değil: kaç şema hatası, kaç
          // düşen test, kapı hangi aşamada durdu, YAML kaç satır. Hiçbiri
          // taslağın kendisinden bir parça taşımıyor — `gate_stage` sınırlı
          // bir sözlük, sayılar sayı.
          const verdict = result.draft;

          track("parser_compiled", {
            succeeded: true,
            duration_ms: Math.round(performance.now() - started),
            yaml_lines: yamlLines(yaml),
            gate_ok: verdict?.ok ?? false,
            gate_stage: verdict?.stage,
            schema_error_count: verdict?.schema_errors?.length ?? 0,
            test_failure_count: (verdict?.tests ?? []).filter((test) => !test.passed).length,
            redos_count: verdict?.redos?.length ?? 0,
          });
        })
        .catch((cause: unknown) => {
          if (controller.signal.aborted) return;
          setTryResult(null);
          setTryError(describeError(cause));

          // `describeError` sunucunun CÜMLESİNİ döndürüyor ve ekrana o
          // yazılıyor; telemetriye giden `errorKind` ise sınıflandırılmış
          // bir değer. İkisinin ayrı olması bu modülün varlık sebebi.
          track("parser_compiled", {
            succeeded: false,
            error_kind: errorKind(cause),
            duration_ms: Math.round(performance.now() - started),
            yaml_lines: yamlLines(yaml),
          });
        })
        .finally(() => {
          if (!controller.signal.aborted) setTrying(false);
        });
    }, 500);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [yaml, line, loadError]);

  const gate: ParserGate | null = tryResult?.draft ?? null;

  /**
   * Şema hataları editörün oluğuna taşınıyor.
   *
   * <p>Kabul kriteri "şema hatası satır numarasıyla gösteriliyor" — ama satır
   * numarasını yazmak yetmiyor, kullanıcının o satıra <b>gidebilmesi</b>
   * gerekiyor. Aynı sayım kullanılıyor: sunucu 1'den başlıyor, oluk da.</p>
   */
  const markers: EditorMarker[] = useMemo(
    () =>
      (gate?.schema_errors ?? []).map((error) => ({
        line: toNumber(error.line),
        message: error.message,
      })),
    [gate],
  );

  /** Düşen testler de satırıyla işaretleniyor — kapı yalnızca şemadan ibaret değil. */
  const testMarkers: EditorMarker[] = useMemo(
    () =>
      (gate?.tests ?? [])
        .filter((test) => !test.passed)
        .map((test) => ({
          line: toNumber(test.line),
          message: `Test düştü: ${test.name}`,
        })),
    [gate],
  );

  const jumpToLine = useCallback((target: number) => {
    const textarea = editorRef.current?.querySelector("textarea");
    if (!textarea) return;

    const lines = textarea.value.split("\n");
    const offset = lines.slice(0, Math.max(0, target - 1)).reduce((sum, text) => sum + text.length + 1, 0);

    textarea.focus();
    textarea.setSelectionRange(offset, offset + (lines[target - 1]?.length ?? 0));

    // Odaklanmak seçili satırı görünür kılmıyor; kaydırma elle yapılıyor.
    const lineHeight = textarea.scrollHeight / Math.max(1, lines.length);
    textarea.scrollTop = Math.max(0, (target - 3) * lineHeight);
  }, []);

  async function save() {
    setSaving(true);
    setSaveError(null);
    setNotice(null);

    try {
      const result = savedId
        ? await api.put("/v1/parsers/drafts/{id}", { path: { id: savedId }, body: { yaml } })
        : await api.post("/v1/parsers/drafts", { body: { yaml } });

      if (result.id) {
        setSavedId(result.id);
        // Adres çubuğu taslağı taşıyor: sayfa yenilenince aynı taslak açılıyor
        // ve bağlantı paylaşılabiliyor. `replaceState` — kaydetmek geri
        // düğmesine bir adım eklememeli.
        window.history.replaceState(null, "", `/parserlar?taslak=${encodeURIComponent(result.id)}`);
      }

      setNotice(
        result.verdict?.ok
          ? "Taslak kaydedildi ve kapılardan geçiyor — incelemeye gönderebilirsiniz."
          : "Taslak kaydedildi. Kapılardan geçmiyor; incelemeye göndermeden önce aşağıdaki raporu giderin.",
      );
    } catch (cause) {
      setSaveError(describeError(cause));
    } finally {
      setSaving(false);
    }
  }

  /**
   * İncelemeye gönderme.
   *
   * <p>Kapı sunucuda <b>yeniden</b> koşuyor; ekranın "geçiyor" demesi bir
   * garanti değil, bir tahmin — aradan geçen sürede pattern kütüphanesi
   * değişmiş olabilir. Bu yüzden 422 yanıtı hata değil <b>sonuç</b> gibi ele
   * alınıyor ve kapı raporu tazeleniyor.</p>
   */
  async function submit() {
    if (!savedId) return;

    setSaving(true);
    setSaveError(null);
    setNotice(null);

    // Ekranın gönderim ANINDA ne sandığı. Kapı sunucuda YENİDEN koşuyor, yani
    // bu bir tahmin; tahminle sonucun ayrıştığı oran ölçülebilir ve ürün için
    // anlamlı bir sayı.
    const sanilan = gate?.ok ?? false;

    try {
      const result = await api.post("/v1/parsers/drafts/{id}/submit", { path: { id: savedId } });
      setNotice(`Taslak incelemeye gönderildi (durum: ${result.state}).`);
      track("parser_submitted", { succeeded: true, gate_ok_before_submit: sanilan });
    } catch (cause) {
      setSaveError(describeError(cause));
      track("parser_submitted", {
        succeeded: false,
        error_kind: errorKind(cause),
        gate_ok_before_submit: sanilan,
      });
    } finally {
      setSaving(false);
    }
  }

  if (loadError) {
    return (
      <ErrorState
        title="Taslak açılamadı"
        hint={`${loadError} Taslağınız yerinde — okunamayan biziz. Sayfayı yenilemeyi deneyin.`}
      />
    );
  }

  return (
    <div className={styles.layout}>
      <div className={styles.stack}>
        <Card>
          <div className={styles.stack}>
            <div className={styles.toolbar}>
              <h1>Parser editörü</h1>
              {savedId ? <Badge tone="accent">taslak kaydedildi</Badge> : <Badge>kaydedilmedi</Badge>}
              {trying ? <Badge>deneniyor…</Badge> : null}
            </div>

            <div className={styles.inlineActions}>
              <span className={styles.muted}>Adım ekle:</span>
              {STEP_TYPES.map((step) => (
                <Button key={step.name} onClick={() => setYaml((current) => appendStep(current, step.snippet))}>
                  {step.name}
                </Button>
              ))}
            </div>

            <div ref={editorRef}>
              <CodeEditor
                label="Parser YAML"
                value={yaml}
                onChange={setYaml}
                disabled={loadingDraft}
                tokenize={tokenizeLine}
                complete={suggest}
                markers={[...markers, ...testMarkers]}
                hint="Anahtar yazarken öneri listesi açılıyor — ↑/↓ ile gezin, Enter ile seçin, Esc ile kapatın."
              />
            </div>

            <Field
              label="Örnek satır"
              value={line}
              onChange={(event) => setLine(event.target.value)}
              placeholder="Cihazın gerçekte yazdığı ham satır"
              hint="Aşağıdaki panelden ham arşivdeki gerçek bir satırı çekebilirsiniz."
            />

            {saveError ? <ErrorState title="İşlem başarısız" hint={saveError} /> : null}

            {notice ? (
              <p className={styles.notice} role="status">
                {notice}
              </p>
            ) : null}

            <div className={styles.inlineActions}>
              <Button variant="primary" onClick={save} disabled={saving || loadingDraft}>
                {saving ? "Kaydediliyor…" : savedId ? "Taslağı güncelle" : "Taslağı kaydet"}
              </Button>

              <Button
                onClick={submit}
                disabled={saving || !savedId || !gate?.ok}
                title={
                  gate?.ok
                    ? undefined
                    : "Kapılardan geçmeyen taslak incelemeye gönderilemiyor — inceleyenin zamanını harcamak, yayını sonda reddetmekten pahalı."
                }
              >
                İncelemeye gönder
              </Button>
            </div>
          </div>
        </Card>

        <ArchiveSampler sources={sources} onPick={setLine} />
      </div>

      <div className={styles.stack}>
        <GateReport gate={gate} onJumpToLine={jumpToLine} />
        <TryPanel result={tryResult} loading={trying} error={tryError} hasLine={line.length > 0} />
      </div>
    </div>
  );
}
