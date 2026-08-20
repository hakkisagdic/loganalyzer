import type { ReactElement } from "react";

import { InventoryTable } from "@/app/kaynaklar/InventoryTable";
import { PipelineHealthSummary } from "@/app/kaynaklar/PipelineHealthSummary";
import { ResultsTable } from "@/app/olaylar/ResultsTable";
import { SearchForm } from "@/app/olaylar/SearchForm";
import { Badge, Card } from "@/components/ui/Field";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { Button } from "@/components/ui/Button";
import type { EventSummary, PipelineHealth, SourceActivityItem, SourceItem } from "@/lib/api/client";
import type { SearchCriteria } from "@/lib/events/criteria";
import { mergeInventory } from "@/lib/sources/inventory";

/**
 * Ekran görüntüsü sahneleri (T28 kabul kriteri).
 *
 * <p>
 * Sahneler <b>bileşen düzeyinde</b>, rota düzeyinde değil. Bunun bedeli ve
 * kazancı açık:
 * </p>
 *
 * <ul>
 *   <li><b>Kanıtlıyor:</b> dört durum, iki temada, gerçek jetonlarla ve gerçek
 *       bileşen CSS'iyle nasıl görünüyor. Kriterin sorduğu şey bu.</li>
 *   <li><b>Kanıtlamıyor:</b> Next yönlendirmesi, kimlik akışı ve düzen
 *       birleşimi. Onlar için sunucu + sahte Keycloak + sahte API gerekiyordu;
 *       üç uzun ömürlü proses, ve protokolün §3'ü tam olarak o riski anlatıyor.</li>
 * </ul>
 *
 * <p>
 * Veri <b>elle</b> kuruluyor: dört durumun her biri deterministik olsun diye.
 * Ağ yok, zaman yok, rastgelelik yok — aynı girdi her koşumda aynı görüntü.
 * </p>
 */

/** Süre hesaplarının tabanı. Sabit, yoksa her koşum başka görüntü üretirdi. */
export const NOW = new Date("2026-08-20T12:00:00Z");

const CRITERIA: SearchCriteria = {
  fullText: "",
  sourceId: "",
  ownerGroup: "",
  vendor: "",
  parseStatuses: [],
  severityMin: undefined,
  proto: "",
  action: "",
  from: "",
  to: "",
  limit: 100,
  cursor: undefined,
  force: false,
};

const SOURCES: SourceItem[] = [
  {
    source_id: "fg-ankara-01",
    owner_group: "network/core",
    peer_address: "10.1.1.1",
    hostname: "fw-01",
    vendor: "fortinet",
    product: "fortigate",
    parser_id: "fortinet.traffic",
    encoding: "windows-1254",
    source_class: "firewall",
    enabled: true,
    is_known_to_dispatcher: true,
  },
  {
    source_id: "asa-izmir-02",
    owner_group: "network/edge",
    peer_address: "10.2.2.2",
    hostname: null,
    vendor: "cisco",
    product: "asa",
    parser_id: null,
    encoding: "utf-8",
    source_class: "firewall",
    enabled: false,
    is_known_to_dispatcher: false,
  },
];

const ACTIVITY: SourceActivityItem[] = [
  {
    source_id: "fg-ankara-01",
    owner_group: "network/core",
    last_event_at: "2026-08-20T09:00:00Z",
    last_ingested_at: "2026-08-20T09:00:00Z",
    event_count: 128_400,
  },
];

/**
 * Üç alfabe, gerçek uzunluklarda.
 *
 * <p>Bu ürünün <b>özel riski</b> (F2 planı): İngilizceyle düzgün görünen her
 * düzen Türkçe, Arapça ve Çince gövdelerde sınanmalı. Görüntülerin varlık
 * sebeplerinden biri bu — kırpma ve hizalama sayıyla ölçülemiyor.</p>
 */
function event(overrides: Partial<EventSummary> = {}): EventSummary {
  return {
    event_id: "0199a1b2-c3d4-7000-8000-000000000001",
    ts: "2026-08-20T09:19:47Z",
    time_source: "parsed",
    ingested_at: "2026-08-20T09:19:50Z",
    owner_group: "network/core",
    source_id: "fg-ankara-01",
    host: "fw-01",
    vendor: "fortinet",
    product: "fortigate",
    parser_id: "fortinet.traffic",
    parser_version: "1.2.0",
    parse_status: "ok",
    parse_generation: 1,
    encoding_detected: "windows-1254",
    template_id: "",
    severity_num: 5,
    ocsf_class_uid: 4001,
    ocsf_activity_id: 6,
    src_ip: "10.1.2.3",
    dst_ip: "8.8.8.8",
    src_port: 41022,
    dst_port: 443,
    proto: "tcp",
    action: "accept",
    outcome: "success",
    user_name: "ahmet",
    attrs: {},
    body: "İstanbul şubesi: bağlantı reddedildi",
    raw_ref: "raw/network/core/2026/08/20/09/firewall/",
    ...overrides,
  };
}

const EVENTS: EventSummary[] = [
  event(),
  event({
    event_id: "0199a1b2-c3d4-7000-8000-000000000002",
    time_source: "observed",
    parse_status: "partial",
    severity_num: 3,
    body: "فشل تسجيل دخول المستخدم، يرجى التحقق من بيانات الاعتماد وإعادة المحاولة لاحقًا",
  }),
  event({
    event_id: "0199a1b2-c3d4-7000-8000-000000000003",
    time_source: "",
    parse_status: "failed",
    severity_num: 0,
    body: "用户登录失败，请检查凭据。这是一段没有空格的长文本用于测试换行行为，如果换行规则错误整个表格布局都会被撑坏。",
  }),
  // Çok uzun tek satır: dört satırda kesilmeli, tabloyu yatay kaydırmaya
  // sokmamalı.
  event({
    event_id: "0199a1b2-c3d4-7000-8000-000000000004",
    body: `date=2026-08-20 time=09:19:47 devname="FG100E" devid="FG100E1234567890" logid="0000000013" type="traffic" subtype="forward" level="notice" vd="root" srcip=10.1.2.3 srcport=41022 srcintf="port1" dstip=8.8.8.8 dstport=443 dstintf="port2" poluuid="abc" sessionid=123456 proto=6 action="accept" policyid=7 policytype="policy" service="HTTPS" dstcountry="United States" srccountry="Reserved" trandisp="snat" transip=203.0.113.5 transport=41022 duration=180 sentbyte=4096 rcvdbyte=8192`,
  }),
];

const HEALTH = {
  dispatch: {
    total: 1_284_000,
    bound_ratio: 0.82,
    bound_ratio_target: 0.95,
    bound_ratio_healthy: false,
    bound_misses: 231_120,
    unmatched_ratio: 0.04,
    unassigned_source_events: 4_120,
  },
  parse: { ok: 1_232_640, unmatched: 51_360, processed_records: 1_284_000 },
  wal: {
    total_bytes: 8_388_608,
    is_full: false,
    recovery: { segment_count: 0, frame_count: 0, truncated_bytes: 0 },
  },
  ingest: {
    accepted_records: 1_284_000,
    rejected_full: 0,
    rejected_invalid: 12,
    non_utf8_records: 3_400,
    declared_encoding_mismatches: 7,
  },
  archive: { by_state: { Uploaded: 512, Verified: 480 }, healthy: true },
  sidecar: {
    enabled: true,
    circuit: "Closed",
    opened_count: 0,
    dropped_queue_full: 0,
    dropped_circuit_open: 0,
    signature_drift: 0,
  },
  inventory: { unassigned_sources: 2 },
} as unknown as PipelineHealth;

/** "Çok veri" — sentetik 500 satır. Ölçülen şey ekranın davranışı. */
const MANY = Array.from({ length: 500 }, (_, index) =>
  event({
    event_id: `0199a1b2-c3d4-7000-8000-${String(index).padStart(12, "0")}`,
    severity_num: (index % 6) + 1,
    parse_status: index % 7 === 0 ? "partial" : "ok",
    time_source: index % 5 === 0 ? "observed" : "parsed",
  }),
);

export interface Scene {
  readonly id: string;
  readonly title: string;
  /** Hangi CSS modülleri bağlanacak — sınıf adı çakışmasını dar tutuyor. */
  readonly styles: readonly string[];
  /**
   * Tam sayfa mı, ekran kadarı mı.
   *
   * <p>500 satırlık sahnede tam sayfa görüntü 7,5 MB tutuyor ve depoya 15 MB
   * bindiriyordu. Sorulan soru "düzen bozuluyor mu" ve cevabı ilk ekranda
   * görünüyor: sütun genişlikleri, başlık ve ilk satırlar. Satır sayısının
   * kendisi <c>events-screen.test.tsx</c>'te zaten sabit.</p>
   */
  readonly fullPage?: boolean;
  readonly node: ReactElement;
}

export const SCENES: readonly Scene[] = [
  {
    id: "olaylar-dolu",
    title: "Log arama — dolu (çok dilli gövdeler)",
    styles: ["app/olaylar/events.module.css"],
    node: <ResultsTable events={EVENTS} />,
  },
  {
    id: "olaylar-cok-veri",
    title: "Log arama — çok veri (500 satır, ilk ekran)",
    styles: ["app/olaylar/events.module.css"],
    fullPage: false,
    node: <ResultsTable events={MANY} />,
  },
  {
    id: "olaylar-filtre",
    title: "Log arama — filtre çubuğu",
    styles: ["app/olaylar/events.module.css"],
    node: (
      <Card>
        <SearchForm
          criteria={CRITERIA}
          sources={SOURCES}
          ownerGroups={["network/core"]}
          unrestricted={false}
        />
      </Card>
    ),
  },
  {
    id: "durum-bos",
    title: "Boş durum",
    styles: [],
    node: (
      <Card padded={false}>
        <EmptyState
          title="Bu ölçütlerle olay bulunamadı."
          description="Zaman aralığını genişletin ya da filtreleri gevşetin. Yalnızca kapsamınızdaki gruplar aranıyor."
          action={<Button>Filtreleri sıfırla</Button>}
        />
      </Card>
    ),
  },
  {
    id: "durum-hata",
    title: "Hata durumu (error + hint)",
    styles: [],
    node: (
      <ErrorState
        title="Arama metni çok kısa (9 karakter)."
        hint="Tam metin indeksi ~11 karakterden sonra seçici oluyor. Daha kısa bir sorgu 1M satırlık tabloda bütün satırları okur; bu yüzden sorgu çalıştırılmadı."
        action={<Button variant="danger">Yine de ara (tam tarama)</Button>}
      />
    ),
  },
  {
    id: "durum-yukleniyor",
    title: "Yükleniyor durumu",
    styles: [],
    node: (
      <Card>
        <LoadingState label="Olaylar yükleniyor" rows={6} />
      </Card>
    ),
  },
  {
    id: "kaynaklar-envanter",
    title: "Envanter — sessiz kaynak başta",
    styles: ["app/kaynaklar/inventory.module.css"],
    node: (
      <InventoryTable rows={mergeInventory(SOURCES, ACTIVITY)} now={NOW} windowHours={24} />
    ),
  },
  {
    id: "kaynaklar-saglik",
    title: "Boru hattı özeti",
    styles: ["app/kaynaklar/inventory.module.css"],
    node: <PipelineHealthSummary health={HEALTH} />,
  },
  {
    id: "rozetler",
    title: "Rozetler — kontrast düzeltmesinin göründüğü yer",
    styles: [],
    node: (
      <Card>
        <div style={{ display: "flex", gap: "0.75rem", flexWrap: "wrap", alignItems: "center" }}>
          <Badge>nötr</Badge>
          <Badge tone="accent">vurgu</Badge>
          <Badge tone="success">tam çözüldü</Badge>
          <Badge tone="warning">kısmi</Badge>
          <Badge tone="danger">çözülemedi</Badge>
          <Button variant="primary">Ara</Button>
          <Button variant="secondary">Sıfırla</Button>
          <Button variant="danger">Sil</Button>
        </div>
      </Card>
    ),
  },
];
