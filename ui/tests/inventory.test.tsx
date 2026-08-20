import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";

import { InventoryTable } from "@/app/kaynaklar/InventoryTable";
import { PipelineHealthSummary } from "@/app/kaynaklar/PipelineHealthSummary";
import type { PipelineHealth, SourceActivityItem, SourceItem } from "@/lib/api/client";
import {
  describeSilence,
  formatSince,
  mergeInventory,
  unassignedSources,
  UNASSIGNED_GROUP,
} from "@/lib/sources/inventory";

/**
 * T17 — envanter ekranı.
 *
 * <p>
 * Sınanan şey ekranın taşıdığı iddialar: "hiç veri gelmedi" ile "N saattir veri
 * gelmiyor" ayrılıyor, envanterde olmayan cihaz gizlenmiyor, ve ekran <b>kendi
 * sessizlik eşiğini uydurmuyor</b> — o eşik T21'in kuralında.
 * </p>
 */

const NOW = new Date("2026-08-20T12:00:00Z");

function source(overrides: Partial<SourceItem> = {}): SourceItem {
  return {
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
    ...overrides,
  };
}

function activity(overrides: Partial<SourceActivityItem> = {}): SourceActivityItem {
  return {
    source_id: "fg-ankara-01",
    owner_group: "network/core",
    last_event_at: "2026-08-20T11:00:00Z",
    last_ingested_at: "2026-08-20T11:00:00Z",
    event_count: 42,
    ...overrides,
  };
}

describe("envanter ile etkinliğin birleştirilmesi", () => {
  it("veri gelmeyen kaynaklar listenin başında", () => {
    // Envanterin en çok bakılma sebebi "hangi cihaz susuyor" sorusu; o satırların
    // dibe düşmesi, ekranı sorulan soruya cevap veremez hâle getiriyor.
    const rows = mergeInventory(
      [source({ source_id: "a" }), source({ source_id: "b" }), source({ source_id: "c" })],
      [activity({ source_id: "a" }), activity({ source_id: "c" })],
    );

    expect(rows.map((row) => row.source.source_id)).toEqual(["b", "a", "c"]);
  });

  it("veri gelenler en eskiden yeniye sıralanıyor", () => {
    const rows = mergeInventory(
      [source({ source_id: "yeni" }), source({ source_id: "eski" })],
      [
        activity({ source_id: "yeni", last_ingested_at: "2026-08-20T11:59:00Z" }),
        activity({ source_id: "eski", last_ingested_at: "2026-08-20T04:00:00Z" }),
      ],
    );

    expect(rows.map((row) => row.source.source_id)).toEqual(["eski", "yeni"]);
  });

  it("hiç veri göndermemiş kaynak listede kalıyor", () => {
    // Olay tablosu var olmayan bir şeyi listeleyemiyor; envanter olmadan bu
    // kaynak hiçbir yerde görünmezdi — oysa asıl bakılması gereken satır bu.
    const rows = mergeInventory([source({ source_id: "hic" })], []);

    expect(rows).toHaveLength(1);
    expect(rows[0]!.activity).toBeUndefined();
    expect(describeSilence(rows[0]!, NOW)).toEqual({
      kind: "quiet",
      label: "bu pencerede veri yok",
    });
  });

  it("son görülme, cihazın saatinden değil bizim aldığımız andan hesaplanıyor", () => {
    // Saati şaşmış bir cihaz `last_event_at` ile "gelecekte" görünebilir;
    // "susuyor mu" sorusunun cevabı bizim aldığımız an.
    const row = {
      source: source(),
      activity: activity({
        last_event_at: "2026-08-25T00:00:00Z",
        last_ingested_at: "2026-08-20T09:00:00Z",
      }),
    };

    expect(describeSilence(row, NOW)).toEqual({ kind: "active", label: "3 saat önce" });
  });
});

describe("envanterde olmayan cihazlar", () => {
  it("yalnızca `_unassigned` grubundakiler, en yeniden eskiye", () => {
    const rows = unassignedSources([
      activity({ source_id: "bilinen" }),
      activity({
        source_id: "10.9.9.9",
        owner_group: UNASSIGNED_GROUP,
        last_ingested_at: "2026-08-20T08:00:00Z",
      }),
      activity({
        source_id: "10.9.9.8",
        owner_group: UNASSIGNED_GROUP,
        last_ingested_at: "2026-08-20T11:30:00Z",
      }),
    ]);

    expect(rows.map((row) => row.source_id)).toEqual(["10.9.9.8", "10.9.9.9"]);
  });

  it("kapsamda `_unassigned` yoksa liste boş — ve bu doğru", () => {
    // Sıradan bir analist bu satırları görmüyor; görmesi başka grupların
    // trafiğini görmesi demek olurdu.
    expect(unassignedSources([activity()])).toEqual([]);
  });
});

describe("geçen süre biçimi", () => {
  it("eşikleri okunur birimlere çeviriyor", () => {
    expect(formatSince("2026-08-20T11:59:30Z", NOW)).toBe("az önce");
    expect(formatSince("2026-08-20T11:30:00Z", NOW)).toBe("30 dakika önce");
    expect(formatSince("2026-08-20T02:00:00Z", NOW)).toBe("10 saat önce");
    expect(formatSince("2026-08-15T12:00:00Z", NOW)).toBe("5 gün önce");
  });

  it("gelecek zaman negatife düşmüyor", () => {
    // Saati ileri kaymış bir cihaz "-3 saat önce" yazdırmamalı.
    expect(formatSince("2026-08-20T13:00:00Z", NOW)).toBe("az önce");
  });

  it("çözümlenemeyen değer olduğu gibi gösteriliyor", () => {
    expect(formatSince("bozuk", NOW)).toBe("bozuk");
  });
});

describe("envanter tablosu", () => {
  const rows = mergeInventory(
    [
      source({ source_id: "fg-1" }),
      source({ source_id: "fg-2", parser_id: null, is_known_to_dispatcher: false }),
      source({ source_id: "fg-3", enabled: false }),
    ],
    [activity({ source_id: "fg-1", last_ingested_at: "2026-08-20T09:00:00Z", event_count: 128 })],
  );

  const html = renderToStaticMarkup(
    <InventoryTable rows={rows} now={NOW} windowHours={24} />,
  );

  it("veri gelmeyen kaynak açıkça işaretleniyor", () => {
    expect(html).toContain("bu pencerede veri yok");
  });

  it("son görülme süresi gösteriliyor", () => {
    expect(html).toContain("3 saat önce");
  });

  it("parser bağı olmayan kaynak uyarı taşıyor", () => {
    // `source_id → parser_id` dispatcher'ın en hızlı kademesi; bağsız kaynak
    // alt kademelere düşüyor ve bu sessiz bir maliyet.
    expect(html).toContain("bağlı değil");
  });

  it("kapalı kaynak metinle de anlatılıyor", () => {
    expect(html).toContain("kapalı");
  });

  it("her satır kendi loglarına bağlanıyor — kaynak filtresi seçili", () => {
    // Kaynak filtresi keyset sayfalamayı sabit süreli kılan şey; köprü onu
    // hazır veriyor.
    expect(html).toContain('href="/olaylar?source_id=fg-1"');
  });
});

describe("boru hattı özeti", () => {
  const health = {
    dispatch: {
      total: 1000,
      bound_ratio: 0.8,
      bound_ratio_target: 0.95,
      bound_ratio_healthy: false,
      bound_misses: 200,
      unmatched_ratio: 0.05,
      unassigned_source_events: 12,
    },
    parse: { ok: 950, unmatched: 50, processed_records: 1000 },
    wal: { total_bytes: 2048, is_full: false, recovery: { segment_count: 0, frame_count: 0, truncated_bytes: 0 } },
    ingest: {
      accepted_records: 1000,
      rejected_full: 0,
      rejected_invalid: 0,
      non_utf8_records: 3,
      declared_encoding_mismatches: 7,
    },
    archive: { by_state: { Uploaded: 5 }, healthy: true },
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

  const html = renderToStaticMarkup(<PipelineHealthSummary health={health} />);

  it("düşen bağlama oranı 'bak' olarak işaretleniyor", () => {
    // Envanter bakımsız kalınca düşen gösterge tam olarak bu — ve sistem bu
    // sırada çalışıyor görünüyor.
    expect(html).toContain("80.0%");
    expect(html).toContain("bak");
  });

  it("kodlama uyuşmazlığı envanterdeki hataya işaret ediyor", () => {
    expect(html).toContain("envanterdeki encoding değeri yanlış");
  });

  it("eşleşmeyen kaynak sayısı görünüyor", () => {
    expect(html).toContain("Eşleşmeyen kaynak");
  });
});
