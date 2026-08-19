import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";

import { ResultsTable } from "@/app/olaylar/ResultsTable";
import { SearchForm } from "@/app/olaylar/SearchForm";
import type { EventSummary, SourceItem } from "@/lib/api/client";
import { MIN_FULL_TEXT_LENGTH, type SearchCriteria } from "@/lib/events/criteria";

/**
 * Arama ekranının **çizilen** kısmı.
 *
 * <p>
 * Sayfanın kendisi sunucu bileşeni ve <c>cookies()</c> ile Next istek bağlamı
 * istiyor; burada çizilebilen parçalar formu ve sonuç tablosu. Sınanan şey de
 * tam olarak ekranın taşıdığı iddialar: <c>time_source</c> her satırda görünüyor,
 * kaynak filtresi yönlendiriyor, ve <c>DataTable</c>'ın çok dilli davranışı
 * gerçek verilerle de tutuyor.
 * </p>
 */

const criteria: SearchCriteria = {
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

const sources: SourceItem[] = [
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
    peer_address: null,
    hostname: null,
    vendor: "cisco",
    product: "asa",
    parser_id: null,
    encoding: "utf-8",
    source_class: "firewall",
    enabled: true,
    is_known_to_dispatcher: false,
  },
];

function event(overrides: Partial<EventSummary> = {}): EventSummary {
  return {
    event_id: "0199a1b2-c3d4-7000-8000-000000000001",
    ts: "2026-08-16T12:30:00Z",
    time_source: "parsed",
    ingested_at: "2026-08-16T12:30:05Z",
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
    body: "bağlantı kabul edildi",
    raw_ref: "raw/network/core/2026/08/16/12/firewall/",
    ...overrides,
  };
}

describe("filtre formu", () => {
  const html = renderToStaticMarkup(
    <SearchForm criteria={criteria} sources={sources} ownerGroups={["network/core"]} unrestricted={false} />,
  );

  it("kısa sorgu sınırını kutunun yanında söylüyor", () => {
    // Kullanıcı sınırı ancak sorgu düştükten sonra öğrenmemeli.
    expect(html).toContain(`En az ${MIN_FULL_TEXT_LENGTH} karakter`);
  });

  it("kaynak filtresini öneriyor ama zorunlu kılmıyor", () => {
    // "Tümü" seçeneği duruyor — dayatma yok — ama bedeli yazılı.
    expect(html).toContain("Tümü (derin sayfalama yavaşlar)");
    expect(html).toContain("derin sayfa da ilk sayfa kadar hızlı");
  });

  it("kaynak ve vendor seçenekleri kapsamdaki envanterden geliyor", () => {
    // Kapsam dışı bir kaynağı seçenek olarak göstermek boş sonuç vaat etmek olurdu.
    expect(html).toContain("fg-ankara-01 — fw-01");
    expect(html).toContain("asa-izmir-02");
    expect(html).toContain(">fortinet<");
    expect(html).toContain(">cisco<");
  });

  it("form imleç alanı taşımıyor", () => {
    // Filtre değişince sayfalama baştan başlamalı; eski imleçle yeni filtre
    // kullanıcının hiç görmediği bir yerden devam etmek olurdu.
    expect(html).not.toContain("after_ts");
    expect(html).not.toContain("after_id");
  });

  it("çözümleme durumu üç seçeneğin hepsini veriyor", () => {
    expect(html).toContain('value="ok"');
    expect(html).toContain('value="partial"');
    expect(html).toContain('value="failed"');
  });
});

describe("sonuç tablosu", () => {
  const rows = [
    event(),
    event({
      event_id: "0199a1b2-c3d4-7000-8000-000000000002",
      time_source: "observed",
      body: "فشل تسجيل دخول المستخدم، يرجى التحقق من بيانات الاعتماد",
      parse_status: "partial",
    }),
    event({
      event_id: "0199a1b2-c3d4-7000-8000-000000000003",
      time_source: "",
      body: "用户登录失败，请检查凭据。这是一段没有空格的长文本。",
      parse_status: "failed",
    }),
  ];

  const html = renderToStaticMarkup(<ResultsTable events={rows} />);

  it("her satırda zamanın kaynağı görünüyor", () => {
    // Bunu görmeden "olay saat 14:03'te oldu" cümlesi kurulamıyor: değer
    // cihazın damgası da olabilir, bizim gözlemimiz de (F1).
    expect(html).toContain("cihaz saati");
    expect(html).toContain("gözlem saati");
    // Kolon eklenmeden önce yazılmış satır 'parsed' sayılmıyor.
    expect(html).toContain("bilinmiyor");
  });

  it("zaman UTC ve saniye hassasiyetinde", () => {
    // Yerel dilime çevirmek, farklı dilimlerdeki cihazları konuşurken ortak
    // ölçeği kaybettirirdi.
    expect(html).toContain("2026-08-16 12:30:00Z");
  });

  it("her satır detayına bağlanıyor", () => {
    expect(html).toContain('href="/olaylar/0199a1b2-c3d4-7000-8000-000000000001"');
  });

  it("gövde sütunu yazı yönünü içeriğe bırakıyor", () => {
    // `DataTable`'ın ilk gerçek tüketicisi: çok dilli gövde davranışı burada da
    // tutuyor mu. Yalnızca gövde sütununda — kimlik ve sayı sütunları LTR.
    expect(html.match(/dir="auto"/g)).toHaveLength(rows.length);
    expect(html).toContain("فشل تسجيل دخول");
    expect(html).toContain("用户登录失败");
  });

  it("çözümleme durumu renkle değil metinle de anlatılıyor", () => {
    expect(html).toContain("kısmi");
    expect(html).toContain("çözülemedi");
  });
});
