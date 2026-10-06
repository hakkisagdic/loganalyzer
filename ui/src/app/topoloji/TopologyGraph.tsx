import type { ReactNode } from "react";
import { Badge } from "@/components/ui/Field";
import { DataTable, type Column } from "@/components/ui/DataTable";
import type { components } from "@/lib/api/schema";
import { formatInstant } from "@/lib/ui/time";
import styles from "./topoloji.module.css";

type TopologyNodeDto = components["schemas"]["TopologyNodeDto"];
type TopologyEdgeDto = components["schemas"]["TopologyEdgeDto"];

interface GraphPoint {
  readonly node: TopologyNodeDto;
  readonly x: number;
  readonly y: number;
}

export function TopologyGraph({
  nodes,
  edges,
  selectedNodeId,
  onSelectNode,
}: {
  readonly nodes: readonly TopologyNodeDto[];
  readonly edges: readonly TopologyEdgeDto[];
  readonly selectedNodeId?: string | null;
  readonly onSelectNode?: (node: TopologyNodeDto) => void;
}) {
  const width = 800;
  const height = 400;
  const centerX = width / 2;
  const centerY = height / 2;
  const radius = Math.min(centerX, centerY) - 60;

  const nodeCount = Math.max(nodes.length, 1);
  const points = new Map<string, GraphPoint>();

  nodes.forEach((n, idx) => {
    const angle = (2 * Math.PI * idx) / nodeCount - Math.PI / 2;
    const x = Math.round(centerX + (nodeCount === 1 ? 0 : radius * Math.cos(angle)));
    const y = Math.round(centerY + (nodeCount === 1 ? 0 : radius * Math.sin(angle)));
    points.set(n.id, { node: n, x, y });
  });

  const edgeColumns: readonly Column<TopologyEdgeDto>[] = [
    {
      key: "from",
      header: "Kaynak Düğüm",
      render: (e) => e.from_node_id,
    },
    {
      key: "to",
      header: "Hedef Düğüm",
      render: (e) => e.to_node_id,
    },
    {
      key: "relation",
      header: "İlişki",
      render: (e) => e.relation,
    },
    {
      key: "provenance",
      header: "Köken (Provenance)",
      render: (e) => {
        const isObserved = e.provenance === "Observed" || e.provenance === "observed";
        return (
          <Badge tone={isObserved ? "accent" : "neutral"}>
            {isObserved ? "Gözlemlenen (Observed)" : "Bildirilen (Declared)"}
          </Badge>
        );
      },
    },
    {
      key: "confidence",
      header: "Güven Skoru",
      numeric: true,
      render: (e) => String(e.confidence),
    },
    {
      key: "expiry",
      header: "Geçerlilik Sonu",
      render: (e) =>
        formatInstant(
          e.effective_expiry_unix_nano && e.effective_expiry_unix_nano !== "0"
            ? new Date(Number(BigInt(e.effective_expiry_unix_nano) / 1000000n)).toISOString()
            : null
        ),
    },
  ];

  return (
    <div>
      <div className={styles.graphContainer}>
        <svg
          className={styles.graphSvg}
          viewBox={`0 0 ${width} ${height}`}
          aria-label="Topoloji Grafiği"
          role="img"
        >
          <g>
            {edges.map((e) => {
              const p1 = points.get(e.from_node_id);
              const p2 = points.get(e.to_node_id);
              if (!p1 || !p2) return null;
              const isObserved = e.provenance === "Observed" || e.provenance === "observed";

              return (
                <line
                  key={e.id}
                  x1={p1.x}
                  y1={p1.y}
                  x2={p2.x}
                  y2={p2.y}
                  className={`${styles.edgeLine} ${isObserved ? styles.edgeLineObserved : ""}`}
                />
              );
            })}
          </g>

          <g>
            {Array.from(points.values()).map(({ node, x, y }) => {
              const isSelected = node.id === selectedNodeId;
              return (
                <g key={node.id} transform={`translate(${x}, ${y})`}>
                  <circle
                    r={isSelected ? 26 : 20}
                    className={styles.nodeCircle}
                    tabIndex={0}
                    role="button"
                    aria-label={`Düğüm: ${node.display_name || node.id}`}
                    onClick={() => onSelectNode?.(node)}
                    onKeyDown={(evt) => {
                      if (evt.key === "Enter" || evt.key === " ") {
                        evt.preventDefault();
                        onSelectNode?.(node);
                      }
                    }}
                  />
                  <text y={32} className={styles.nodeText}>
                    {node.display_name || node.id}
                  </text>
                </g>
              );
            })}
          </g>
        </svg>
      </div>

      <div style={{ marginBlockStart: "var(--space-4)" }}>
        <DataTable
          caption="Topoloji Kenar ve Bağlantı Tablosu"
          columns={edgeColumns}
          rows={edges}
          rowKey={(e) => e.id}
        />
      </div>
    </div>
  );
}
