"use client";

import { useCallback, useEffect, useMemo, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Badge, Card } from "@/components/ui/Field";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/alerts/errors";
import { formatInstant, toNumber } from "@/lib/alerts/types";
import { diffLines } from "@/lib/parsers/diff";
import type {
  ParserDraft,
  ParserDraftDetail,
  ParserDraftList,
  ParserPublishResult,
} from "@/lib/parsers/types";

import styles from "../catalog.module.css";

/**
 * Satır satır YAML farkı (T20 kabul kriteri).
 *
 * <p>
 * Hesap `diffLines`'ta, saf fonksiyonda; burada yalnızca çiziliyor. Fark
 * hesaplanamayacak kadar büyükse bunu <b>söylüyor</b> — sessizce donmuyor.
 * </p>
 */
function DiffView({ previous, next }: { previous: string; next: string }) {
  const diff = useMemo(() => diffLines(previous, next), [previous, next]);

  if (diff.tooLarge) {
    return (
      <ErrorState
        title="Fark gösterilemeyecek kadar büyük"
        hint="İki sürüm arasındaki değişiklik satır satır karşılaştırılamayacak kadar geniş; YAML'ları doğrudan karşılaştırın."
      />
    );
  }

  if (diff.added === 0 && diff.removed === 0) {
    return (
      <EmptyState
        title="İki sürüm aynı"
        description="Taslak yayındaki sürümden farksız; yayınlamak kataloğu değiştirmez."
      />
    );
  }

  return (
    <>
      <p className={styles.muted}>
        {diff.added} satır eklendi, {diff.removed} satır silindi.
      </p>

      {/*
        `dir="ltr"`: YAML'ın kendisi soldan sağa bir yapı. İçindeki Arapça bir
        DEĞER sağdan sola olsa bile satırın tamamını ters çevirmek girintiyi —
        yani YAML'da anlam taşıyan tek şeyi — okunamaz hâle getirirdi.
      */}
      <div className={styles.diff} dir="ltr" role="region" aria-label="YAML farkı" tabIndex={0}>
        {diff.lines.map((line, index) => (
          <div
            key={`${line.kind}-${line.leftNumber ?? "-"}-${line.rightNumber ?? "-"}-${index}`}
            className={[
              styles.diffRow,
              line.kind === "added" ? styles.diffAdded : null,
              line.kind === "removed" ? styles.diffRemoved : null,
            ]
              .filter(Boolean)
              .join(" ")}
          >
            <span className={styles.diffNumber}>{line.leftNumber ?? ""}</span>
            <span className={styles.diffNumber}>{line.rightNumber ?? ""}</span>
            {/*
              İşaret metin olarak da var: rengi tek başına anlam taşısaydı
              renk körü bir kullanıcı eklenen ile sileni ayırt edemezdi
              (WCAG 1.4.1).
            */}
            <span className={styles.diffSign}>
              {line.kind === "added" ? "+" : line.kind === "removed" ? "−" : " "}
            </span>
            <span className={styles.diffText}>{line.text}</span>
          </div>
        ))}
      </div>
    </>
  );
}

/**
 * İnceleme kuyruğu (T20).
 *
 * <p>
 * Yayın bekleyen taslaklar, yayındaki sürüme karşı fark görünümü ve
 * onay/geri gönderme. Kapı kararı da gösteriliyor: inceleyen "bu taslak
 * yayınlanabilir mi" sorusunu yayına basmadan önce görmeli.
 * </p>
 */
export function ReviewQueue() {
  const [drafts, setDrafts] = useState<readonly ParserDraft[] | null>(null);
  const [selected, setSelected] = useState<ParserDraftDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    setError(null);

    try {
      const list = (await api.get("/v1/parsers/drafts", { signal })) as ParserDraftList;
      setDrafts(list.drafts);
    } catch (cause) {
      if (!signal?.aborted) {
        setError(describeError(cause));
        setDrafts([]);
      }
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  async function open(draft: ParserDraft) {
    setBusy(true);

    try {
      setSelected(
        (await api.get("/v1/parsers/drafts/{id}", { path: { id: draft.id } })) as ParserDraftDetail,
      );
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  async function act(draft: ParserDraftDetail, action: "publish" | "return") {
    setBusy(true);
    setError(null);

    try {
      if (action === "publish") {
        const result = (await api.post("/v1/parsers/drafts/{id}/publish", {
          path: { id: draft.id },
        })) as ParserPublishResult;

        if (result.catalog.errors.length > 0) {
          setError(`Yayınlandı ama katalog yüklenirken hata: ${result.catalog.errors.join(", ")}`);
        }
      } else {
        await api.post("/v1/parsers/drafts/{id}/return", { path: { id: draft.id } });
      }

      setSelected(null);
      await load();
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  const queue = useMemo(
    () => (drafts ?? []).filter((draft) => draft.state === "inreview"),
    [drafts],
  );

  if (error && drafts === null) {
    return <ErrorState title="İnceleme kuyruğu yüklenemedi" hint={error} />;
  }

  if (drafts === null) {
    return <LoadingState label="Kuyruk yükleniyor…" />;
  }

  return (
    <div className={styles.stack}>
      <div className={styles.toolbar}>
        <h1>İnceleme kuyruğu</h1>
      </div>

      {error ? <ErrorState title="Son işlem başarısız" hint={error} /> : null}

      {queue.length === 0 ? (
        <Card>
          <EmptyState
            title="İnceleme bekleyen taslak yok"
            description="Editörden incelemeye gönderilen taslaklar burada görünür."
          />
        </Card>
      ) : (
        <Card padded={false}>
          <DataTable
            caption="Yayın bekleyen taslaklar"
            rowKey={(row) => row.id}
            rows={queue}
            columns={[
              { key: "parser", header: "Parser", width: "22%", render: (row) => row.parser_id || "—" },
              { key: "version", header: "Sürüm", width: "12%", render: (row) => row.version || "—" },
              { key: "owner", header: "Sahip", width: "18%", render: (row) => row.owner || "—" },
              {
                key: "tests",
                header: "Geçen test",
                width: "12%",
                numeric: true,
                render: (row) => toNumber(row.passing_tests),
              },
              {
                key: "updated",
                header: "Son değişiklik",
                width: "20%",
                render: (row) => formatInstant(row.updated_at),
              },
              {
                key: "open",
                header: "İşlem",
                width: "16%",
                render: (row) => (
                  <Button disabled={busy} onClick={() => open(row)}>
                    Farkı incele
                  </Button>
                ),
              },
            ]}
          />
        </Card>
      )}

      {selected ? (
        <Card>
          <div className={styles.stack}>
            <div className={styles.toolbar}>
              <h2>
                {selected.parser_id || "(kimliksiz)"} · {selected.version || "(sürümsüz)"}
              </h2>
              {selected.previous_version ? (
                <Badge>yayındaki: {selected.previous_version}</Badge>
              ) : (
                <Badge tone="accent">ilk sürüm</Badge>
              )}
            </div>

            {/*
              Kapı kararı yayına basmadan ÖNCE görünüyor. Yayın anında kapı
              yeniden koşuyor, yani buradaki karar bir vaat değil bir uyarı:
              hatalıysa yayın zaten reddedilecek.
            */}
            <div className={styles.verdictList}>
              {selected.verdict.ok ? (
                <Badge tone="success">
                  Kapıdan geçiyor · {selected.verdict.passing_tests} test
                </Badge>
              ) : (
                <Badge tone="danger">Kapıdan geçmiyor</Badge>
              )}

              {selected.verdict.errors.map((message) => (
                <span className={styles.verdictError} key={message}>
                  ✕ {message}
                </span>
              ))}

              {selected.verdict.warnings.map((message) => (
                <span className={styles.verdictWarning} key={message}>
                  ! {message}
                </span>
              ))}
            </div>

            <DiffView previous={selected.previous_yaml ?? ""} next={selected.yaml} />

            <div className={styles.inlineActions}>
              <Button
                variant="primary"
                disabled={busy || !selected.verdict.ok}
                onClick={() => act(selected, "publish")}
                title={
                  selected.verdict.ok
                    ? "Yayınla — katalog hemen tazeleniyor."
                    : "Kapıdan geçmeyen taslak yayınlanamaz."
                }
              >
                Onayla ve yayınla
              </Button>
              <Button disabled={busy} onClick={() => act(selected, "return")}>
                Taslağa geri gönder
              </Button>
              <Button disabled={busy} onClick={() => setSelected(null)}>
                Kapat
              </Button>
            </div>
          </div>
        </Card>
      ) : null}
    </div>
  );
}
