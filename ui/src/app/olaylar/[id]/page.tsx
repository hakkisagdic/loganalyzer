import Link from "next/link";
import { redirect } from "next/navigation";

import { AppShell } from "@/components/AppShell";
import { TimeSourceBadge, describeTimeSource } from "@/components/events/TimeSourceBadge";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { Badge, Card } from "@/components/ui/Field";
import { EmptyState, ErrorState } from "@/components/ui/States";
import type { EventDetail, EventFieldView, EventRaw } from "@/lib/api/client";
import { ApiError, NotFoundError } from "@/lib/api/errors";
import { serverApi } from "@/lib/api/server";
import { currentUser } from "@/lib/auth/currentUser";
import { formatParseStatus, formatSeverity, readParseIssues } from "@/lib/events/format";
import { formatInstant } from "@/lib/ui/time";
import { decodeBase64, decodeText, toHexDump } from "@/lib/events/raw";

import styles from "../events.module.css";

export const dynamic = "force-dynamic";

/**
 * Olay detayı ve ham görünüm (T16).
 *
 * <p>
 * Sekmeler <b>bağlantı</b>: durum adres çubuğunda, istemci tarafı JavaScript
 * gerekmiyor ve bir sekme paylaşılabiliyor. Ham sekmesi ayrı bir istek yapıyor
 * çünkü ham gövde megabaytlarca olabiliyor ve her detay açılışında indirmenin
 * anlamı yok.
 * </p>
 *
 * <p>
 * Bu ekran, F1'de <b>beş ayrı katmanda kırılmış</b> zincirin görünür ucu:
 * ingest → arşiv → manifest → konum bulma → çözme. Bozulduğunda ilk fark
 * edilecek yer burası.
 * </p>
 */

type Tab = "core" | "ocsf" | "otel" | "ham";

const TABS: readonly { readonly id: Tab; readonly label: string }[] = [
  { id: "core", label: "Çözümlenmiş" },
  { id: "ocsf", label: "OCSF" },
  { id: "otel", label: "OpenTelemetry" },
  { id: "ham", label: "Ham baytlar" },
];

function readTab(value: string | string[] | undefined): Tab {
  const first = Array.isArray(value) ? value[0] : value;
  return TABS.some((tab) => tab.id === first) ? (first as Tab) : "core";
}

export default async function EventDetailPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const { id } = await params;
  const query = await searchParams;
  const tab = readTab(query["sekme"]);

  const identity = await currentUser();

  if (identity.status === "anonymous") {
    redirect(`/api/auth/login?returnTo=${encodeURIComponent(`/olaylar/${id}`)}`);
  }

  if (identity.status === "error") {
    return (
      <AppShell>
        <ErrorState title={identity.message} hint={identity.hint} />
      </AppShell>
    );
  }

  const username = identity.user.username || identity.user.subject;

  let detail: EventDetail;

  try {
    detail = (await serverApi.get("/v1/events/{id}", { path: { id } })) as EventDetail;
  } catch (error) {
    if (error instanceof NotFoundError) {
      return (
        <AppShell username={username}>
          <ErrorState
            // 404 "yetkiniz yok" DEMİYOR ve bu bilinçli: kapsam dışı bir olay da
            // 404 dönüyor. 403 demek "böyle bir olay var ama sizin değil"
            // bilgisini sızdırırdı ve bir ekip, kimlik deneyerek başka bir
            // ekibin envanterini çıkarabilirdi.
            title="Olay bulunamadı."
            hint="Kimlik yanlış olabilir ya da kayıt saklama penceresinden düşmüş olabilir."
            action={
              <Link className={styles.reset} href="/olaylar">
                Aramaya dön
              </Link>
            }
          />
        </AppShell>
      );
    }

    return (
      <AppShell username={username}>
        <ErrorState
          title={error instanceof ApiError ? error.message : "Olay okunamadı."}
          hint={error instanceof ApiError ? error.hint : undefined}
        />
      </AppShell>
    );
  }

  const event = detail.event;
  const issues = readParseIssues(event.attrs);
  const timeSource = describeTimeSource(event.time_source);

  return (
    <AppShell username={username}>
      <h1>Olay detayı</h1>

      <Card>
        <div className={styles.detailGrid}>
          <Definition term="Zaman (UTC)">
            <span className={styles.timeCell}>
              <span>{formatInstant(event.ts)}</span>
              <TimeSourceBadge value={event.time_source} />
            </span>
          </Definition>
          <Definition term="Kaynak">{event.source_id || "—"}</Definition>
          <Definition term="Cihaz">{event.host || "—"}</Definition>
          <Definition term="Grup">{event.owner_group}</Definition>
          <Definition term="Önem">{formatSeverity(event.severity_num)}</Definition>
          <Definition term="Çözümleme">
            <Badge
              tone={
                event.parse_status === "ok"
                  ? "success"
                  : event.parse_status === "partial"
                    ? "warning"
                    : "danger"
              }
            >
              {formatParseStatus(event.parse_status)}
            </Badge>
          </Definition>
        </div>

        {/*
          `observed` ya da `received` ise kullanıcı bunu GÖRMEDEN zaman üzerine
          akıl yürütmemeli — T16'nın kabul kriteri.
        */}
        {event.time_source !== "parsed" ? (
          <p className={styles.noticeWarning} role="status">
            <b>Zamanın kaynağı: {timeSource.label}.</b> {timeSource.explanation}
          </p>
        ) : null}

        {issues.length > 0 ? (
          <>
            <p className={styles.definitionTerm}>Çözümleme sorunları</p>
            <ul className={styles.issues}>
              {issues.map((issue) => (
                <li key={`${issue.step}:${issue.message}`}>
                  <span className={styles.issueStep}>adım {issue.step}</span> — {issue.message}
                </li>
              ))}
            </ul>
          </>
        ) : event.parse_status !== "ok" ? (
          <p className={styles.noticeMuted}>
            Bu olay <b>{formatParseStatus(event.parse_status)}</b> ama ayrıntı kaydı yok —
            satır, sorunların olaya yazılmaya başlamasından önce işlenmiş olabilir.
            <code> POST /v1/parsers/try</code> ile satır yeniden çalıştırılabilir.
          </p>
        ) : null}
      </Card>

      <nav className={styles.tabs} aria-label="Görünümler">
        {TABS.map((entry) => (
          <Link
            key={entry.id}
            href={`/olaylar/${id}?sekme=${entry.id}`}
            className={`${styles.tab} ${entry.id === tab ? styles.tabActive : ""}`}
            aria-current={entry.id === tab ? "page" : undefined}
          >
            {entry.label}
          </Link>
        ))}
      </nav>

      {tab === "core" ? <CoreView event={event} /> : null}
      {tab === "ocsf" ? <FieldView title="OCSF görünümü" fields={detail.ocsf} /> : null}
      {tab === "otel" ? <FieldView title="OpenTelemetry görünümü" fields={detail.otel} /> : null}
      {tab === "ham" ? <RawView id={id} declaredFallback={event.encoding_detected} /> : null}
    </AppShell>
  );
}

function Definition({ term, children }: { term: string; children: React.ReactNode }) {
  return (
    <div className={styles.definition}>
      <span className={styles.definitionTerm}>{term}</span>
      <span className={styles.definitionValue}>{children}</span>
    </div>
  );
}

/** `core` alanları + `attrs` haritası (`bizigo.tags`, `bizigo.dispatch_tier` dâhil). */
function CoreView({ event }: { event: EventDetail["event"] }) {
  const attrs = Object.entries(event.attrs).sort(([a], [b]) => a.localeCompare(b, "en"));

  return (
    <>
      <Card>
        <p className={styles.definitionTerm}>Gövde</p>
        {/* Yazı yönü içeriğe bırakılıyor: Arapça gövde sabit `ltr` ile okunamaz. */}
        <p className={styles.bodyText} dir="auto">
          {event.body || "—"}
        </p>
      </Card>

      <Card>
        <div className={styles.detailGrid}>
          <Definition term="event_id">{event.event_id}</Definition>
          <Definition term="Alınma (UTC)">{formatInstant(event.ingested_at)}</Definition>
          <Definition term="Vendor / ürün">
            {[event.vendor, event.product].filter(Boolean).join(" / ") || "—"}
          </Definition>
          <Definition term="Parser">
            {event.parser_id ? `${event.parser_id} @ ${event.parser_version}` : "—"}
          </Definition>
          <Definition term="Çözümleme kuşağı">{event.parse_generation}</Definition>
          <Definition term="Tespit edilen kodlama">{event.encoding_detected || "—"}</Definition>
          <Definition term="Şablon">{event.template_id || "—"}</Definition>
          <Definition term="Kaynak → hedef">
            {`${event.src_ip}:${event.src_port} → ${event.dst_ip}:${event.dst_port}`}
          </Definition>
          <Definition term="Protokol / eylem / sonuç">
            {[event.proto, event.action, event.outcome].filter(Boolean).join(" / ") || "—"}
          </Definition>
          <Definition term="Kullanıcı">{event.user_name || "—"}</Definition>
          <Definition term="OCSF sınıf / etkinlik">
            {`${event.ocsf_class_uid} / ${event.ocsf_activity_id}`}
          </Definition>
          <Definition term="Ham referans">{event.raw_ref || "—"}</Definition>
        </div>
      </Card>

      {attrs.length > 0 ? (
        <PairTable
          caption="attrs — parser alanları, bizigo.tags ve bizigo.dispatch_tier dâhil"
          rows={attrs.map(([name, value]) => ({ name, value }))}
        />
      ) : (
        <Card padded={false}>
          <EmptyState title="Bu olayda ek alan yok." />
        </Card>
      )}
    </>
  );
}

/**
 * OCSF / OTel görünümü.
 *
 * <p>
 * Alan adları API'den geliyor, burada <b>türetilmiyor</b>: eşleme ClickHouse
 * görünümünde tanımlı (<c>db/clickhouse/0003_ocsf_otel_views.sql</c>) ve aynı
 * adları F3'ün Sigma derleyicisi ile doğrudan SQL konuşan araçlar da görüyor.
 * Burada ikinci bir kopya tutmak, görünüme bir alan eklendiği gün sessizce
 * ayrışırdı.
 * </p>
 */
function FieldView({ title, fields }: { title: string; fields: readonly EventFieldView[] }) {
  if (fields.length === 0) {
    return (
      <Card padded={false}>
        <EmptyState
          title={`${title} okunamadı.`}
          description="Görünüm bu olay için satır döndürmedi; ClickHouse göçlerinin uygulandığını doğrulayın."
        />
      </Card>
    );
  }

  return <PairTable caption={title} rows={[...fields]} />;
}

function PairTable({
  caption,
  rows,
}: {
  caption: string;
  rows: readonly { readonly name: string; readonly value: string }[];
}) {
  const columns: Column<{ name: string; value: string }>[] = [
    { key: "name", header: "Alan", width: "20rem", render: (row) => row.name },
    // Değerler serbest metin: çok dilli gövdeler ve uzun haritalar buraya düşüyor.
    { key: "value", header: "Değer", freeText: true, render: (row) => row.value || "—" },
  ];

  return (
    <DataTable caption={caption} columns={columns} rows={rows} rowKey={(row) => row.name} />
  );
}

/**
 * Ham baytlar.
 *
 * <p>
 * Hem hex hem çözümlenmiş metin gösteriliyor; indirme düğmesi baytları
 * <b>olduğu gibi</b> veriyor ve o yol bu bileşenden geçmiyor
 * (<c>./ham/route.ts</c>).
 * </p>
 */
async function RawView({ id, declaredFallback }: { id: string; declaredFallback: string }) {
  let raw: EventRaw;

  try {
    raw = (await serverApi.get("/v1/events/{id}/raw", { path: { id } })) as EventRaw;
  } catch (error) {
    if (error instanceof ApiError) {
      return (
        // API'nin ipucu aynen geçiyor: "nesne henüz yüklenmemiş olabilir"
        // sessiz boş ekrandan çok daha kullanışlı (T16 kabul kriteri).
        <ErrorState title={error.message} hint={error.hint} />
      );
    }

    throw error;
  }

  const bytes = decodeBase64(raw.raw_b64);
  const decoded = decodeText(bytes, raw.encoding_detected || declaredFallback);

  return (
    <>
      <Card>
        <div className={styles.detailGrid}>
          <Definition term="Bayt sayısı">{bytes.length}</Definition>
          <Definition term="Tespit edilen kodlama">{raw.encoding_detected || "—"}</Definition>
          <Definition term="İddia edilen kodlama">{raw.encoding_declared || "—"}</Definition>
          <Definition term="Taşıma">
            {[raw.transport.proto, raw.transport.peer].filter(Boolean).join(" ") || "—"}
          </Definition>
          <Definition term="Arşiv nesnesi">{raw.object_key}</Definition>
          <Definition term="Taranan nesne">{raw.objects_scanned}</Definition>
        </div>

        {decoded.fellBack ? (
          <p className={styles.noticeWarning} role="status">
            <b>{raw.encoding_detected}</b> kodlaması bu ortamda tanınmıyor; metin UTF-8
            varsayılarak çözüldü ve <b>yanlış görünüyor olabilir</b>. Aşağıdaki hex dökümü
            ve indirilen dosya bundan etkilenmiyor.
          </p>
        ) : null}

        <div className={styles.rawActions}>
          <a className={styles.pagerNext} href={`/olaylar/${id}/ham`} download>
            Baytları indir
          </a>
          <span className={styles.muted}>
            İndirilen dosya cihazın gönderdiği baytların birebir aynısı — hiçbir aşamada
            yeniden kodlanmıyor.
          </span>
        </div>
      </Card>

      <Card>
        <p className={styles.definitionTerm}>Çözümlenmiş metin ({decoded.encoding})</p>
        {/* Çözülmüş metin doğal yazı yönünde; hex dökümü ise ASLA (bkz. .hex). */}
        <p className={styles.bodyText} dir="auto">
          {decoded.text}
        </p>
      </Card>

      <Card>
        <p className={styles.definitionTerm}>Hex dökümü</p>
        <pre className={styles.hex}>{toHexDump(bytes)}</pre>
      </Card>
    </>
  );
}
