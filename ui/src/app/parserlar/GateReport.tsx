"use client";

import { Badge, Card } from "@/components/ui/Field";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState } from "@/components/ui/States";
import { toNumber } from "@/lib/api/numbers";
import { GATE_STAGE_COPY, gateStage, type ParserGate, type ParserTestCase } from "@/lib/parsers/types";

import styles from "./parsers.module.css";

export interface GateReportProps {
  readonly gate: ParserGate | null;
  readonly onJumpToLine?: (line: number) => void;
}

/**
 * Yayın kapısının kararı — **kullanıcının diliyle** (T19).
 *
 * <p>
 * T18 kapıları kurdu: lint temiz ve gömülü testler geçmeden bir taslak
 * incelemeye bile gidemiyor, ve kapı yayında <b>tekrar</b> koşuyor çünkü
 * aradan geçen sürede pattern kütüphanesi değişmiş olabilir. Bu bileşenin tek
 * işi o kapıları anlaşılır kılmak: <b>hangi kapıda</b> takıldı, <b>hangi
 * satırda</b>, ve <b>ne yapmalı</b>.
 * </p>
 *
 * <p>
 * Aşama ayrı bir alan olarak geliyor, hata metninden çıkarılmıyor: mesajı
 * biçimlendiren ilk katkı, metinden geri çıkarımı sessizce yanlışlaştırırdı.
 * </p>
 */
export function GateReport({ gate, onJumpToLine }: GateReportProps) {
  if (!gate) {
    return (
      <Card>
        <EmptyState
          title="Kapı henüz koşmadı"
          description="Yazmaya başlayın; taslak her değişiklikte lint, ReDoS taraması ve gömülü testlerden geçiriliyor."
        />
      </Card>
    );
  }

  const stage = gateStage(gate.stage);
  const copy = GATE_STAGE_COPY[stage];
  const blockingRedos = gate.redos.filter((finding) => finding.blocking);
  const advisoryRedos = gate.redos.filter((finding) => !finding.blocking);

  return (
    <Card>
      <div className={styles.stack}>
        <div className={styles.toolbar}>
          <h2>Yayın kapıları</h2>
          <Badge tone={gate.ok ? "success" : "danger"}>{copy.title}</Badge>
        </div>

        <p className={styles.gateDetail}>{copy.detail}</p>

        {gate.parser_id ? (
          <p className={styles.muted}>
            <code>{gate.parser_id}</code> · sürüm <code>{gate.version || "—"}</code> ·{" "}
            {toNumber(gate.passing_tests)} test geçiyor
          </p>
        ) : (
          <p className={styles.muted}>
            Kimlik henüz çözülemedi — <code>metadata.id</code> ve <code>metadata.version</code>{" "}
            YAML'dan okunuyor, ayrıca yazılmıyor.
          </p>
        )}

        {gate.schema_errors.length > 0 ? (
          <section>
            <h3 className={styles.sectionTitle}>Şema hataları</h3>
            <ul className={styles.issueList}>
              {gate.schema_errors.map((error, index) => (
                <li key={`${error.line}-${index}`}>
                  <button
                    type="button"
                    className={styles.lineLink}
                    onClick={() => onJumpToLine?.(toNumber(error.line))}
                  >
                    Satır {toNumber(error.line)}:{toNumber(error.column)}
                  </button>{" "}
                  {error.message}
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        {blockingRedos.length > 0 ? (
          <section>
            <h3 className={styles.sectionTitle}>Yayını durduran pattern bulguları</h3>
            <ul className={styles.issueList}>
              {blockingRedos.map((finding, index) => (
                <li key={`${finding.code}-${index}`}>
                  <Badge tone="danger">{finding.code}</Badge> {finding.message}
                  {finding.fragment ? (
                    <>
                      {" "}
                      <code className={styles.fragment}>{finding.fragment}</code>
                    </>
                  ) : null}
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        {advisoryRedos.length > 0 ? (
          <section>
            <h3 className={styles.sectionTitle}>Yayını durdurmayan bulgular</h3>
            <ul className={styles.issueList}>
              {advisoryRedos.map((finding, index) => (
                <li key={`${finding.code}-${index}`} className={styles.muted}>
                  <Badge>{finding.code}</Badge> {finding.message}
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        <section>
          <h3 className={styles.sectionTitle}>Gömülü testler</h3>
          {gate.tests.length === 0 ? (
            <EmptyState
              title="Gömülü test yok"
              description="Testsiz parser yayınlanamıyor — bir parser'ın doğru çalıştığı ancak testiyle gösterilebilir."
            />
          ) : (
            <TestTable tests={gate.tests} onJumpToLine={onJumpToLine} />
          )}
        </section>
      </div>
    </Card>
  );
}

function TestTable({
  tests,
  onJumpToLine,
}: {
  readonly tests: readonly ParserTestCase[];
  readonly onJumpToLine?: (line: number) => void;
}) {
  return (
    <DataTable
      caption={`Gömülü testler (${tests.filter((t) => t.passed).length}/${tests.length} geçiyor)`}
      rowKey={(row) => `${row.name}-${toNumber(row.line)}`}
      rows={tests}
      columns={[
        {
          key: "durum",
          header: "Durum",
          width: "8rem",
          render: (row) => (
            <Badge tone={row.passed ? "success" : "danger"}>{row.passed ? "geçti" : "düştü"}</Badge>
          ),
        },
        {
          key: "ad",
          header: "Test",
          width: "24%",
          render: (row) => (
            <button
              type="button"
              className={styles.lineLink}
              onClick={() => onJumpToLine?.(toNumber(row.line))}
            >
              {row.name} <span className={styles.muted}>(satır {toNumber(row.line)})</span>
            </button>
          ),
        },
        {
          key: "beklentiler",
          header: "Beklentiler",
          freeText: true,
          render: (row) => (
            <ul className={styles.expectationList}>
              {row.expectations.map((expectation) => (
                <li
                  key={expectation.key}
                  className={expectation.passed ? styles.muted : styles.expectationFailed}
                >
                  <code>{expectation.key}</code>{" "}
                  {expectation.passed ? (
                    <>= {expectation.actual}</>
                  ) : (
                    // Beklenen ve gerçek YAN YANA: hangisinin yanlış olduğu —
                    // parser mı test mi — ancak ikisi birlikte görününce belli
                    // oluyor. `<yok>` alanın hiç atanmadığı anlamına geliyor.
                    <>
                      beklenen {expectation.expected} · gerçek {expectation.actual}
                    </>
                  )}
                </li>
              ))}
            </ul>
          ),
        },
      ]}
    />
  );
}
