"use client";

import { useState } from "react";

import { Button } from "@/components/ui/Button";
import { DataTable } from "@/components/ui/DataTable";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import { drilldownLosesFilters, toEventsHref } from "@/lib/rca/drilldown";
import {
  CONTRADICTING_CHOICES,
  honestyLines,
  presentStatus,
  REVIEW_STATES,
  reviewRequest,
  type RcaFinding,
  type RcaReasoning,
  type RcaReport,
  type RcaReview,
  type RcaSlice,
} from "@/lib/rca/report";

import styles from "../rca.module.css";

/**
 * RCA raporu — kanıt paketinin insan okuduğu hâli (T37).
 *
 * <p>
 * <b>Ekranın çivili değişmezi:</b> <c>empty</c> · <c>never_fed</c> ·
 * <c>unavailable</c>/<c>failed</c> · <c>not_registered</c> ayırt edilebilir
 * kalıyor. Tek bir "veri yok" kutusu çizmek T34 ve T36'nın kurduğu her şeyi tek
 * satırda geri alır ve hiçbir şey haber vermez — hata yok, sayaç yok, belirti
 * yok; yalnızca raporu okuyanın yanlış sonuca varması.
 * </p>
 *
 * <p>
 * Bu yüzden iki ayrı bölüm var — <b>Bakıldı, kanıt çıkmadı</b> ve
 * <b>Bakılmayanlar</b> — ve ikincisinde her satır kendi rozetini ve
 * gerekçesini taşıyor. Sunucunun Markdown'ı da aynı ayrımı aynı iki başlıkla
 * yapıyor; ikisi aynı kaynaktan beslendiği için ayrışamıyorlar.
 * </p>
 *
 * <p>
 * <b>F4 için yer ayrıldı:</b> yorum bölümü özetin <b>altına</b>, bulguların
 * <b>üstüne</b> gelecek ve kanıtın yerine geçmeyecek. Kullanıcı her zaman ham
 * kanıta bakabilmeli.
 * </p>
 */
export interface ReportViewProps {
  readonly report: RcaReport;
}

export function ReportView({ report }: ReportViewProps) {
  const [review, setReview] = useState(report.review ?? null);
  const [reviewError, setReviewError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [rootCause, setRootCause] = useState("");
  // Varsayılan `unknown`: ekran bu boyutu bilemiyor ve kullanıcı adına
  // çıkarım yapmıyor (bkz. CONTRADICTING_CHOICES).
  const [contradicting, setContradicting] = useState<string>("unknown");

  const warnings = honestyLines(report);

  async function submitReview(verdict: string) {
    setSaving(true);
    setReviewError(null);

    try {
      // Çelişen-kanıt kararı karara **bağlı değil**: hangi düğmeye basılırsa
      // basılsın seçilen değer onunla birlikte gidiyor. Tek tık korunuyor,
      // ikinci boyut yine de her incelemede soruluyor.
      const saved = (await api.post("/v1/rca/{id}/review", {
        path: { id: report.bundle_id },
        body: reviewRequest(verdict, contradicting, rootCause),
      })) as RcaReview;

      setReview(saved);
      setRootCause("");
    } catch (cause) {
      setReviewError(describeError(cause));
    } finally {
      setSaving(false);
    }
  }

  return (
    <article className={styles.report}>
      <header className={styles.reportHead}>
        <div>
          <h1>RCA kanıt paketi</h1>
          <p className={styles.meta}>
            <span>
              Olay penceresi: <time dateTime={report.window.from}>{report.window.from}</time> →{" "}
              <time dateTime={report.window.to}>{report.window.to}</time>
            </span>
            <span>
              Taban: <time dateTime={report.window.baseline_from}>{report.window.baseline_from}</time> →{" "}
              <time dateTime={report.window.baseline_to}>{report.window.baseline_to}</time>
            </span>
            <span className={styles.hash}>içerik hash: {report.content_hash.slice(0, 12)}</span>
          </p>
        </div>

        {/* Export sunucudan ve ekranla aynı metinden. Tarayıcıda ikinci bir
            biçimlendirici, ekranla export'un sessizce ayrışması demekti. */}
        <a className={styles.exportLink} href={`/api/bff/v1/rca/${report.bundle_id}/export`} download>
          Markdown indir
        </a>
      </header>

      {warnings.length > 0 ? (
        <section className={styles.honesty} aria-labelledby="rapor-uyarilari">
          <h2 id="rapor-uyarilari">Bu raporu okurken</h2>
          <ul>
            {warnings.map((line) => (
              <li key={line.id} data-warning={line.id}>
                {line.text}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {/* T37'nin ayırdığı yer, T51'de dolduruldu: özetin altında, bulguların
          üstünde ve kanıtın YANINDA — yerine değil. */}
      <ReasoningSection reasoning={report.reasoning} />

      <section aria-labelledby="bulgular">
        <h2 id="bulgular">Bulgular</h2>
        {report.findings.length === 0 ? (
          <p className={styles.quiet}>Hiçbir sinyal bu pencerede kanıt üretmedi.</p>
        ) : (
          <ol className={styles.findings}>
            {report.findings.map((finding) => (
              <FindingRow key={finding.id} finding={finding} />
            ))}
          </ol>
        )}
      </section>

      {/* İki bölüm AYRI. Birleştirmek, "baktık ve yok" ile "bakamadık"ı aynı
          şey gibi göstermek olurdu. */}
      <SliceSection
        id="bakildi-bos"
        title="Bakıldı, kanıt çıkmadı"
        hint="Bu sağlayıcılar koştu ve bu pencerede eşleşme bulamadı. Sessizlikleri bir bilgi."
        slices={report.silent}
        emptyText="Koşan her sağlayıcı kanıt üretti."
      />

      <SliceSection
        id="bakilmayanlar"
        title="Bakılmayanlar"
        hint="Bu kanıt türlerine bakılamadı. Yokluklarını bir bulgu gibi okumayın."
        slices={report.not_consulted}
        // Boş liste "her şeye bakıldı" demek ve bu bir bilgi; bölümün sessizce
        // kaybolması yanlış olurdu.
        emptyText="Her kanıt türüne bakıldı."
      />

      <section aria-labelledby="zaman-cizelgesi">
        <h2 id="zaman-cizelgesi">Zaman çizelgesi</h2>
        {report.timeline.length === 0 ? (
          <p className={styles.quiet}>Çizelgeye girecek kanıt yok.</p>
        ) : (
          <DataTable
            caption="Kanıt satırlarının zaman sıralı görünümü"
            rowKey={(entry) => entry.id}
            rows={report.timeline}
            columns={[
              {
                key: "ts",
                header: "Zaman",
                width: "13rem",
                render: (entry) => <time dateTime={entry.timestamp}>{entry.timestamp}</time>,
              },
              {
                key: "provider",
                header: "Sinyal",
                width: "12rem",
                render: (entry) => <code>{entry.provider_id}</code>,
              },
              // Kanıt özetleri ham log satırı taşıyor: `freeText` çok dilli
              // gövdenin hizalamasını ve kırpılmasını bileşene bırakıyor.
              { key: "summary", header: "Olay", freeText: true, render: (entry) => entry.summary },
            ]}
          />
        )}
      </section>

      <section className={styles.reviewBox} aria-labelledby="inceleme">
        <h2 id="inceleme">Bu rapor doğru muydu?</h2>
        <p className={styles.quiet}>
          Cevabınız kaydediliyor ve RCA doğruluğunun ölçüldüğü kümeye giriyor.
        </p>

        <label className={styles.rootCause}>
          Gerçek kök neden (biliniyorsa)
          <textarea
            value={rootCause}
            onChange={(event) => setRootCause(event.target.value)}
            rows={2}
            placeholder="&quot;Yanlış&quot; demek modeli düzeltmiyor; doğrusunun ne olduğu düzeltiyor."
          />
        </label>

        {/*
          Çelişen kanıt AYRI bir soru ve "yanlış/eksik" seçilince açılan bir alt
          soru DEĞİL: tiyatronun en tehlikeli hâli raporun bütün olarak doğru
          olduğu hâl, ve karara bağlansaydı ölçüm tam da o durumu hiç
          örneklemezdi. Ama ikinci bir düğme grubu da değil — kararı iki tıka
          çıkarmak inceleme yorgunluğunu büyütürdü. Seçim tek tıkı bozmadan
          yanında duruyor.
        */}
        <label className={styles.contradicting}>
          Çelişen kanıt bölümü
          <select
            value={contradicting}
            onChange={(event) => setContradicting(event.target.value)}
            aria-describedby="celisen-notu"
            data-testid="contradicting-evidence"
          >
            {CONTRADICTING_CHOICES.map((choice) => (
              <option key={choice.value} value={choice.value}>
                {choice.label}
              </option>
            ))}
          </select>
        </label>

        <p className={styles.quiet} id="celisen-notu">
          Model bu bölümü doldurmak için önemsiz bir şey uydurmuş olabilir ve
          rapor bütün olarak yine de doğru görünebilir. Bu yüzden ayrı soruluyor.
        </p>

        <div className={styles.reviewButtons}>
          {REVIEW_STATES.map((state) => (
            <Button
              key={state.value}
              type="button"
              disabled={saving}
              onClick={() => void submitReview(state.value)}
            >
              {state.label}
            </Button>
          ))}
        </div>

        {reviewError ? <p className={styles.reviewError}>{reviewError}</p> : null}

        {review ? (
          <p className={styles.reviewSaved} data-review-state={review.verdict}>
            Son inceleme: <strong>{REVIEW_STATES.find((s) => s.value === review.verdict)?.label ?? review.verdict}</strong>
            {" — "}
            <time dateTime={review.reviewed_at}>{review.reviewed_at}</time>
            {review.actual_root_cause ? ` · kök neden: ${review.actual_root_cause}` : ""}
          </p>
        ) : (
          <p className={styles.quiet}>Bu rapor henüz incelenmedi.</p>
        )}
      </section>
    </article>
  );
}

function FindingRow({ finding }: { readonly finding: RcaFinding }) {
  const drilldown = finding.drilldown ?? null;
  const losesFilters = drilldown ? drilldownLosesFilters(drilldown) : false;

  return (
    <li className={styles.finding}>
      <div className={styles.findingHead}>
        <code className={styles.provider}>{finding.provider_id}</code>
        <time className={styles.quiet} dateTime={finding.timestamp}>
          {finding.timestamp}
        </time>
      </div>

      <p className={styles.summary}>{finding.summary}</p>

      {/* Drilldown null olabiliyor (change.feed satırları olay tablosunda
          değil). Boş bir arama açmak, kullanıcıyı ilgisiz bir sonuca
          göndermek olurdu. */}
      {drilldown ? (
        <p className={styles.drilldown}>
          <a href={toEventsHref(drilldown)}>Bu kanıtın olaylarını aç</a>
          {losesFilters ? (
            <span className={styles.lossy} title="Bağlantı bazı filtreleri taşıyamıyor; açılan küme daha geniş.">
              filtre kaybı var
            </span>
          ) : null}
        </p>
      ) : (
        <p className={styles.quiet}>Bu kanıt türü olay tablosunda değil — inilecek ham log yok.</p>
      )}

      {Object.keys(finding.payload).length > 0 ? (
        <dl className={styles.payload}>
          {Object.entries(finding.payload).map(([key, value]) => (
            <div key={key}>
              <dt>{key}</dt>
              <dd>{value}</dd>
            </div>
          ))}
        </dl>
      ) : null}
    </li>
  );
}

interface SliceSectionProps {
  readonly id: string;
  readonly title: string;
  readonly hint: string;
  readonly slices: readonly RcaSlice[];
  readonly emptyText: string;
}

function SliceSection({ id, title, hint, slices, emptyText }: SliceSectionProps) {
  return (
    <section aria-labelledby={id} data-section={id}>
      <h2 id={id}>{title}</h2>
      <p className={styles.quiet}>{hint}</p>

      {slices.length === 0 ? (
        <p className={styles.quiet}>{emptyText}</p>
      ) : (
        <ul className={styles.slices}>
          {slices.map((slice) => {
            const presented = presentStatus(slice.status);

            return (
              <li key={slice.provider_id} data-status={slice.status}>
                <code className={styles.provider}>{slice.provider_id}</code>
                <span className={styles.badge} data-tone={presented.tone}>
                  {presented.label}
                </span>
                {/* Gerekçe olmadan rozet tek başına "neden bakılmadı"yı
                    cevaplamıyor. */}
                <span className={styles.detail}>{slice.detail || presented.meaning}</span>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

/**
 * F4'ün model yorumu — ve <b>Karar 1'in sayacının göründüğü yer</b> (T51).
 *
 * <p>
 * <i>"Referanssız cümle rapora hiç girmiyor — ama atıldığı sayılıyor ve
 * <b>gösteriliyor</b>."</i> İçerik burada yok; sayı burada var. Yalnızca atmak
 * kaliteyi ölçülemez yapardı — <i>"ölçemedim"</i> ile <i>"sorun yok"</i>un aynı
 * çıktıya inmesi, bu deponun dört kez adını koyduğu sınıf.
 * </p>
 *
 * <h3>Üç hâl ve neden üçü de ayrı çiziliyor</h3>
 *
 * <p>
 * Bu, F3'ün dört durumunun (<code>empty</code> / <code>never_fed</code> /
 * <code>unavailable</code> / <code>not_registered</code>) F4 tarafındaki
 * karşılığı ve <b>aynı sınıf</b>:
 * </p>
 *
 * <ul>
 *   <li><b>A</b> — <code>reasoning === null</code>: model <b>hiç koşmadı</b>.
 *       Bölüm sessizce kaybolmuyor; yokluğu bir cümleyle söyleniyor.</li>
 *   <li><b>B</b> — koştu, <b>hiç cümle üretmedi</b>. Oran ölçülemedi.</li>
 *   <li><b>C</b> — koştu, <b>her cümlesi atıldı</b>. <b>En pahalısı:</b> boş bir
 *       bulgu listesi "bulgu yok" diye okunursa, modelin uydurup elendiği
 *       gerçeği kaybolur — ve F4'ün ölçmek istediği tam olarak o.</li>
 * </ul>
 *
 * <p>
 * B ile C'nin bulgu listesi <b>aynı</b> (ikisi de boş); ayrımı taşıyan şey liste
 * değil <b>sayılar</b>. Bu yüzden boş liste metni sayıya bakarak seçiliyor.
 * </p>
 */
function ReasoningSection({ reasoning }: { readonly reasoning: RcaReasoning | null }) {
  // A · Model hiç koşmadı. Bölümü tümden gizlemek, "yorum yok" ile "yorum
  // boş çıktı"yı ayırt edilemez yapardı — RCA §6'nın "kapalı sağlayıcılar
  // görünüyor" kararının aynısı.
  if (!reasoning) {
    return (
      <section aria-labelledby="model-yorumu" data-section="model-yorumu" data-reasoning="absent">
        <h2 id="model-yorumu">Model yorumu</h2>
        <p className={styles.quiet}>
          Bu rapor için model hiç çalışmadı — aşağıdakiler yalnızca deterministik kanıt.
          Bu <strong>&quot;model bir şey bulamadı&quot; değil</strong>.
        </p>
      </section>
    );
  }

  const { findings, actions, sentence_gate_skipped: skipped, model } = reasoning;

  // Üretilen tipler sayıları `number | string` veriyor (int64 taşması için);
  // `honestyLines` da aynı sebeple `Number(...)` kullanıyor.
  const produced = Number(reasoning.produced_sentence_count);
  const dropped = Number(reasoning.dropped_sentence_count);
  const fabricated = Number(reasoning.fabricated_citation_sentence_count);

  // ORAN AYRI ELE ALINIYOR ve sırası önemli: `Number(null)` **0** döndürüyor,
  // yani körlemesine dönüştürmek "ölçülemedi"yi sessizce "hiç cümle
  // atılmadı" — yani MÜKEMMEL KALİTE — diye çizerdi. Sunucu tarafında `null`
  // seçilmesinin bütün sebebi buydu; ekranda geri kazanmak, kararı boşa
  // çıkarmak olurdu.
  const rawRatio = reasoning.dropped_sentence_ratio;
  const ratio = rawRatio === null || rawRatio === undefined ? null : Number(rawRatio);

  return (
    <section
      aria-labelledby="model-yorumu"
      data-section="model-yorumu"
      data-reasoning={dropped > 0 && findings.length === 0 ? "all-dropped" : "present"}
    >
      <h2 id="model-yorumu">Model yorumu</h2>

      {/* Sayaç EN ÜSTTE. Raporun sonunda duran bir kısıt okunmuyor, ve
          okunmayan bir kısıt hiç yazılmamış gibi (RCA §6). */}
      <p className={styles.honesty} data-counter="dropped-sentences">
        {ratio === null ? (
          <>
            Model hiç cümle üretmedi — atılan cümle oranı{" "}
            <strong>ölçülemedi</strong> (sıfır değil).
          </>
        ) : (
          <>
            Model <strong>{produced}</strong> cümle üretti,{" "}
            <strong>{dropped}</strong> tanesi kanıta bağlanamadı ve çıkarıldı (
            {(ratio * 100).toFixed(1)}%).
          </>
        )}
        {fabricated > 0 ? (
          <>
            {" "}
            Bunların <strong>{fabricated}</strong> tanesi var olmayan bir kanıta atıf yapıyordu.
          </>
        ) : null}
      </p>

      {skipped.length > 0 ? (
        <ul className={styles.quiet} data-gate="skipped">
          {skipped.map((gate) => (
            <li key={gate.step_id}>
              <code>{gate.step_id}</code> adımında cümle bağlama koşmadı ({gate.reason}): {gate.detail}
            </li>
          ))}
        </ul>
      ) : null}

      <h3>Hipotezler</h3>
      {findings.length === 0 ? (
        // B ile C AYNI listeye sahip; ayrım burada, sayıda.
        <p className={styles.quiet}>
          {dropped > 0
            ? `Model ${dropped} cümle üretti ve hiçbiri kanıta bağlanamadı — hepsi çıkarıldı. Bu "bulgu yok" DEĞİL.`
            : "Model hiçbir hipotez üretmedi."}
        </p>
      ) : (
        // SIRA ANLAMLI: liste modelin kendi sıralaması ve depolama boyunca
        // korunuyor. T47'nin "doğru olan kaçıncı bulgu" ölçümü buna dayanıyor,
        // o yüzden burada yeniden sıralama YOK.
        <ol className={styles.findings} data-ordered="model">
          {findings.map((finding, index) => (
            <li key={`${index}-${finding.hypothesis}`} className={styles.finding}>
              <p className={styles.summary}>{finding.hypothesis}</p>
              <p className={styles.meta}>
                Destekleyen: {finding.evidence_ids.join(", ") || "yok"}
                {finding.contradicting_evidence_ids.length > 0 ? (
                  <>
                    {" · "}
                    <span data-contradicting="true">
                      Çelişen: {finding.contradicting_evidence_ids.join(", ")}
                    </span>
                  </>
                ) : null}
              </p>
            </li>
          ))}
        </ol>
      )}

      <h3>Önerilen aksiyonlar</h3>
      {actions.length === 0 ? (
        <p className={styles.quiet}>Kanıta bağlanan aksiyon kalmadı.</p>
      ) : (
        <ul className={styles.findings}>
          {actions.map((action, index) => (
            <li key={`${index}-${action.text}`} className={styles.finding}>
              <p className={styles.summary}>{action.text}</p>
              <p className={styles.meta}>Kanıt: {action.evidence_ids.join(", ") || "yok"}</p>
            </li>
          ))}
        </ul>
      )}

      <p className={styles.meta} data-model="info">
        {model.provider} / {model.model}
        {" · "}
        {model.tokens_complete ? (
          <>belirteç: {model.prompt_tokens ?? 0} giriş / {model.completion_tokens ?? 0} çıkış</>
        ) : (
          // Kısmi bir toplam bir ALT SINIR ve bunu söylemeyen bir sayı tam
          // sanılır — sıfır yazmanın daha sinsi hâli.
          <span data-tokens="incomplete">
            belirteç eksik bildirildi ({model.unreported_attempts} deneme sayı vermedi); toplam bir alt sınır
          </span>
        )}
      </p>

      {/* HER İKİ HÂL DE yazılıyor. Yalnız muafiyet varken görünen bir rozet,
          muafiyetsiz koşumu "bu soru sorulmamış" hâline sokardı — ve raporu
          okuyan model bölümünü TAM sanıyor. Bugün rapor model hakkında hiçbir
          şey söylemiyordu ve okuyan bunu biliyordu; artık söylüyor. */}
      <p className={styles.meta} data-boundary={model.boundary_overridden ? "overridden" : "verified"}>
        {model.boundary_overridden ? (
          <>
            <strong>K6 sınır doğrulaması atlandı.</strong> Gerekçe:{" "}
            {model.boundary_override_reason ?? <strong>yazılmamış</strong>}
          </>
        ) : (
          <>K6 sınır doğrulaması uygulandı; muafiyet kullanılmadı.</>
        )}
      </p>
    </section>
  );
}
