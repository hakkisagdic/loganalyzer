"use client";

import { useEffect, useState } from "react";
import { Badge } from "@/components/ui/Field";
import { Button } from "@/components/ui/Button";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import type { components } from "@/lib/api/schema";
import { formatInstant } from "@/lib/ui/time";
import { TopologyGraph } from "./TopologyGraph";
import styles from "./topoloji.module.css";

type TopologyNodeDto = components["schemas"]["TopologyNodeDto"];
type TopologyEdgeDto = components["schemas"]["TopologyEdgeDto"];
type TopologyNodePageDto = components["schemas"]["TopologyNodePageDto"];
type TopologyEdgePageDto = components["schemas"]["TopologyEdgePageDto"];

export function TopologyView() {
  const [nodes, setNodes] = useState<readonly TopologyNodeDto[]>([]);
  const [edges, setEdges] = useState<readonly TopologyEdgeDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [provenanceFilter, setProvenanceFilter] = useState("all");
  const [selectedNode, setSelectedNode] = useState<TopologyNodeDto | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    const edgeParams = new URLSearchParams();
    if (provenanceFilter !== "all") {
      edgeParams.set("provenance", provenanceFilter);
    }
    const edgeQs = edgeParams.toString();

    Promise.all([
      fetch("/api/bff/v1/topology/nodes").then(async (res) => {
        if (!res.ok) throw new Error(`Düğümler alınamadı: HTTP ${res.status}`);
        return (await res.json()) as TopologyNodePageDto;
      }),
      fetch(`/api/bff/v1/topology/edges${edgeQs ? `?${edgeQs}` : ""}`).then(async (res) => {
        if (!res.ok) throw new Error(`Kenarlar alınamadı: HTTP ${res.status}`);
        return (await res.json()) as TopologyEdgePageDto;
      }),
    ])
      .then(([nodeData, edgeData]) => {
        if (!active) return;
        const n = nodeData.nodes || [];
        const e = edgeData.edges || [];
        setNodes(n);
        setEdges(e);
        if (n.length > 0 && !selectedNode) {
          setSelectedNode(n[0] ?? null);
        }
        setLoading(false);
      })
      .catch((err) => {
        if (!active) return;
        setError(err instanceof Error ? err.message : "Topoloji verisi alınamadı.");
        setLoading(false);
      });

    return () => {
      active = false;
    };
  }, [provenanceFilter]);

  const nodeColumns: readonly Column<TopologyNodeDto>[] = [
    {
      key: "name",
      header: "Düğüm Adı",
      render: (n) => (
        <button
          type="button"
          onClick={() => setSelectedNode(n)}
          style={{
            background: "none",
            border: "none",
            padding: 0,
            color: "var(--accent)",
            fontWeight: "var(--weight-medium)",
            cursor: "pointer",
            textAlign: "start",
          }}
        >
          {n.display_name || n.id}
        </button>
      ),
    },
    {
      key: "id",
      header: "Düğüm ID",
      render: (n) => n.id,
    },
    {
      key: "kind",
      header: "Tür",
      render: (n) => n.kind,
    },
    {
      key: "group",
      header: "Grup",
      render: (n) => n.owner_group,
    },
    {
      key: "status",
      header: "Durum",
      render: (n) => (
        <Badge tone={n.enabled ? "success" : "neutral"}>
          {n.enabled ? "Etkin" : "Devre Dışı"}
        </Badge>
      ),
    },
  ];

  return (
    <div className={styles.container}>
      <div className={styles.headerRow}>
        <h1 className={styles.title}>Topoloji</h1>
        <div className={styles.controls}>
          <label htmlFor="provenance-select" style={{ fontSize: "var(--text-sm)", color: "var(--text-2)" }}>
            Köken Filtresi:
          </label>
          <select
            id="provenance-select"
            className={styles.selectInput}
            value={provenanceFilter}
            onChange={(e) => setProvenanceFilter(e.target.value)}
          >
            <option value="all">Tümü (All)</option>
            <option value="declared">Yalnız Bildirilenler (Declared)</option>
            <option value="observed">Yalnız Gözlemlenenler (Observed)</option>
          </select>
        </div>
      </div>

      {error ? (
        <ErrorState
          title="Topoloji yüklenirken bir hata oluştu"
          hint={error}
          action={
            <Button
              variant="secondary"
              onClick={() => setProvenanceFilter("all")}
            >
              Yeniden Dene
            </Button>
          }
        />
      ) : loading ? (
        <LoadingState label="Topoloji düğümleri ve bağlantıları yükleniyor..." rows={6} />
      ) : nodes.length === 0 ? (
        <EmptyState
          title="Hiç topoloji düğümü bulunamadı"
          description="Sisteme kayıtlı veya gözlemlenmiş herhangi bir topoloji düğümü yok."
        />
      ) : (
        <div className={`${styles.grid} ${selectedNode ? styles.gridTwoCol : ""}`}>
          <div className={styles.panel}>
            <TopologyGraph
              nodes={nodes}
              edges={edges}
              selectedNodeId={selectedNode?.id}
              onSelectNode={(n) => setSelectedNode(n)}
            />

            <div style={{ marginBlockStart: "var(--space-4)" }}>
              <DataTable
                caption="Topoloji Düğümleri Tablosu"
                columns={nodeColumns}
                rows={nodes}
                rowKey={(n) => n.id}
              />
            </div>
          </div>

          {selectedNode ? (
            <div className={styles.panel}>
              <h2 className={styles.panelTitle}>
                {selectedNode.display_name || selectedNode.id}
              </h2>

              <div className={styles.metaGrid}>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Düğüm ID</span>
                  <span className={styles.metaValue}>{selectedNode.id}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Görünen İsim</span>
                  <span className={styles.metaValue}>{selectedNode.display_name}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Tür</span>
                  <span className={styles.metaValue}>{selectedNode.kind}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Kapsam Grubu</span>
                  <span className={styles.metaValue}>{selectedNode.owner_group}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Etkin mi</span>
                  <span className={styles.metaValue}>
                    {selectedNode.enabled ? "Evet" : "Hayır"}
                  </span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Sürüm</span>
                  <span className={styles.metaValue}>{selectedNode.version}</span>
                </div>
                <div className={styles.metaItem}>
                  <span className={styles.metaLabel}>Geçerlilik Başlangıcı</span>
                  <span className={styles.metaValue}>
                    {formatInstant(
                      selectedNode.valid_from_unix_nano && selectedNode.valid_from_unix_nano !== "0"
                        ? new Date(Number(BigInt(selectedNode.valid_from_unix_nano) / 1000000n)).toISOString()
                        : null
                    )}
                  </span>
                </div>
              </div>

              <div style={{ marginBlockStart: "var(--space-4)" }}>
                <h3 style={{ fontSize: "var(--text-md)", margin: 0, marginBlockEnd: "var(--space-2)" }}>
                  Bu Düğüme Bağlı Kenarlar
                </h3>
                {(() => {
                  const relatedEdges = edges.filter(
                    (e) => e.from_node_id === selectedNode.id || e.to_node_id === selectedNode.id
                  );
                  if (relatedEdges.length === 0) {
                    return <p className={styles.metaValue}>Bağlı kenar yok.</p>;
                  }
                  return (
                    <ul style={{ margin: 0, paddingInlineStart: "var(--space-4)", display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
                      {relatedEdges.map((e) => (
                        <li key={e.id} style={{ fontSize: "var(--text-sm)", color: "var(--text-1)" }}>
                          <strong>{e.from_node_id}</strong> &rarr; <strong>{e.to_node_id}</strong> (
                          <em>{e.relation}</em>, güven: {String(e.confidence)})
                        </li>
                      ))}
                    </ul>
                  );
                })()}
              </div>
            </div>
          ) : null}
        </div>
      )}
    </div>
  );
}
