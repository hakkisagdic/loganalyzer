import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";

import { BucketVisualizer } from "@/app/metrikler/BucketVisualizer";
import { WaterfallVisualizer } from "@/app/izler/WaterfallVisualizer";
import { TopologyGraph } from "@/app/topoloji/TopologyGraph";
import type { components } from "@/lib/api/schema";

type TelemetryPointDto = components["schemas"]["TelemetryPointDto"];
type TelemetrySpanDto = components["schemas"]["TelemetrySpanDto"];
type TopologyNodeDto = components["schemas"]["TopologyNodeDto"];
type TopologyEdgeDto = components["schemas"]["TopologyEdgeDto"];

describe("Sprint 06 UI Surfaces", () => {
  describe("Metrikler / BucketVisualizer", () => {
    it("histogram kovalarini ve erisilebilir tabloyu cizer", () => {
      const point: TelemetryPointDto = {
        start_time_unix_nano: "1724155200000000000",
        time_unix_nano: "1724155260000000000",
        value: { present: false, value: null },
        count: "100",
        sum: { present: true, value: { kind: "double", value: 450.5, special: null } },
        min: { present: false, value: null },
        max: { present: false, value: null },
        attributes: [],
        flags: "0",
        exemplars: [],
        quantile_values: [],
        scale: null,
        zero_count: null,
        zero_threshold: { present: false, value: null },
        positive: null,
        negative: null,
        explicit_bounds: [
          { kind: "double", value: 10, special: null },
          { kind: "double", value: 50, special: null },
          { kind: "double", value: 100, special: null },
        ],
        bucket_counts: ["15", "45", "30", "10"],
      };

      const html = renderToStaticMarkup(<BucketVisualizer point={point} />);

      expect(html).toContain("Histogram Kova Dağılım Tablosu");
      expect(html).toContain("≤ 10");
      expect(html).toContain("(10, 50]");
      expect(html).toContain("(50, 100]");
      expect(html).toContain("&gt; 100");
      expect(html).toContain("15");
      expect(html).toContain("45");
      expect(html).toContain("30");
      expect(html).toContain("10");
    });
  });

  describe("İzler / WaterfallVisualizer", () => {
    it("span hiyerarsisini ve erisilebilir tabloyu cizer", () => {
      const spans: TelemetrySpanDto[] = [
        {
          trace_id: "trace-123",
          span_id: "root-span",
          parent_span_id: "",
          trace_state: "",
          flags: "0",
          name: "HTTP GET /checkout",
          kind: 1,
          start_time_unix_nano: "1724155200000000000",
          end_time_unix_nano: "1724155200150000000",
          attributes: [],
          dropped_attributes_count: "0",
          events: [],
          dropped_events_count: "0",
          links: [],
          dropped_links_count: "0",
          status_code: 1,
          status_message: "OK",
        },
        {
          trace_id: "trace-123",
          span_id: "child-span-1",
          parent_span_id: "root-span",
          trace_state: "",
          flags: "0",
          name: "SELECT FROM cart",
          kind: 3,
          start_time_unix_nano: "1724155200020000000",
          end_time_unix_nano: "1724155200080000000",
          attributes: [],
          dropped_attributes_count: "0",
          events: [],
          dropped_events_count: "0",
          links: [],
          dropped_links_count: "0",
          status_code: 1,
          status_message: "OK",
        },
      ];

      const html = renderToStaticMarkup(<WaterfallVisualizer spans={spans} />);

      expect(html).toContain("Span Hiyerarşisi ve Detay Tablosu");
      expect(html).toContain("HTTP GET /checkout");
      expect(html).toContain("SELECT FROM cart");
      expect(html).toContain("root-span");
      expect(html).toContain("child-span-1");
      expect(html).toContain("— (Kök)");
      expect(html).toContain("150"); // 150 ms
      expect(html).toContain("60");  // 60 ms
    });
  });

  describe("Topoloji / TopologyGraph", () => {
    it("dugumleri, kenarlari, koken ayrimini ve erisilebilir tabloyu cizer", () => {
      const nodes: TopologyNodeDto[] = [
        {
          id: "svc-auth",
          kind: "service",
          display_name: "Kimlik Doğrulama Servisi",
          owner_group: "security/iam",
          enabled: true,
          version: "1.0",
          valid_from_unix_nano: "1724155200000000000",
          valid_to_unix_nano: null,
        },
        {
          id: "db-auth",
          kind: "database",
          display_name: "Auth Postgres",
          owner_group: "security/iam",
          enabled: true,
          version: "1.0",
          valid_from_unix_nano: "1724155200000000000",
          valid_to_unix_nano: null,
        },
      ];

      const edges: TopologyEdgeDto[] = [
        {
          id: "edge-1",
          from_node_id: "svc-auth",
          to_node_id: "db-auth",
          relation: "reads_from",
          provenance: "Observed",
          directed: true,
          confidence: 0.98,
          from_owner_group: "security/iam",
          to_owner_group: "security/iam",
          visibility: "internal",
          first_seen_unix_nano: "1724155200000000000",
          last_seen_unix_nano: "1724155260000000000",
          effective_expiry_unix_nano: "1724158800000000000",
          publication_sequence: "1",
          version: "1.0",
        },
      ];

      const html = renderToStaticMarkup(
        <TopologyGraph nodes={nodes} edges={edges} selectedNodeId="svc-auth" />
      );

      expect(html).toContain("Topoloji Kenar ve Bağlantı Tablosu");
      expect(html).toContain("Kimlik Doğrulama Servisi");
      expect(html).toContain("Auth Postgres");
      expect(html).toContain("svc-auth");
      expect(html).toContain("db-auth");
      expect(html).toContain("reads_from");
      expect(html).toContain("Gözlemlenen (Observed)");
      expect(html).toContain("0.98");
    });
  });
});
