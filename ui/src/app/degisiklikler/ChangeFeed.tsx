"use client";

import { useCallback, useEffect, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Badge, Card, Field } from "@/components/ui/Field";
import { DataTable, type Column } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";

import styles from "./changes.module.css";

/** `change_events` satırının UI karşılığı. */
interface ChangeRow {
  readonly changeId?: string;
  readonly timestamp: string;
  readonly ownerGroup: string;
  readonly targetKind: string;
  readonly targetId: string;
  readonly changeKind: string;
  readonly actor: string;
  readonly summary: string;
  readonly source: string;
  readonly externalRef: string;
}

const TARGET_KINDS = ["Device", "Service", "Config", "Inventory", "Maintenance"] as const;

/**
 * Sık kullanılan türler. Serbest metin de kabul ediliyor — <c>change_kind</c>
 * şemada <c>LowCardinality(String)</c>, kapalı bir küme değil; kurumun kendi
 * sözcüğünü kullanmasını engellemek veri girişini caydırırdı.
 */
const CHANGE_KINDS = ["deploy", "config_push", "firmware", "acl_change", "window_open"];

export interface ChangeFeedProps {
  readonly ownerGroups: readonly string[];
  readonly unrestricted: boolean;
}

export function ChangeFeed({ ownerGroups, unrestricted }: ChangeFeedProps) {
  const [rows, setRows] = useState<readonly ChangeRow[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [formOpen, setFormOpen] = useState(false);

  const load = useCallback(async () => {
    setLoadError(null);

    try {
      const body = (await api.get("/v1/changes")) as { changes?: readonly ChangeRow[] };
      setRows(body.changes ?? []);
    } catch (cause) {
      setRows([]);
      setLoadError(describeError(cause));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const columns: readonly Column<ChangeRow>[] = [
    {
      key: "timestamp",
      header: "Zaman",
      width: "17ch",
      render: (row) => <span>{formatInstant(row.timestamp)}</span>,
    },
    {
      key: "target",
      header: "Hedef",
      width: "22ch",
      // Hedef kimliği cihaz adı ya da depo yolu olabiliyor; serbest metin.
      freeText: true,
      render: (row) => (
        <span>
          <Badge>{row.targetKind}</Badge> {row.targetId}
        </span>
      ),
    },
    {
      key: "changeKind",
      header: "Tür",
      width: "14ch",
      render: (row) => <span>{row.changeKind}</span>,
    },
    {
      key: "summary",
      header: "Özet",
      // Özet Türkçe, Arapça ya da Çince gelebiliyor — hizalama içeriğe bırakılıyor.
      freeText: true,
      render: (row) =>
        row.externalRef ? (
          <a href={row.externalRef} rel="noreferrer noopener" target="_blank">
            {row.summary || row.externalRef}
          </a>
        ) : (
          <span>{row.summary || "—"}</span>
        ),
    },
    {
      key: "actor",
      header: "Kim",
      width: "16ch",
      freeText: true,
      render: (row) => <span>{row.actor || "—"}</span>,
    },
    {
      key: "source",
      header: "Kaynak",
      width: "12ch",
      render: (row) => <Badge tone={row.source === "api" ? "neutral" : "accent"}>{row.source}</Badge>,
    },
  ];

  return (
    <>
      <div className={styles.toolbar}>
        <Button onClick={() => setFormOpen((open) => !open)} aria-expanded={formOpen}>
          {formOpen ? "Formu kapat" : "Değişiklik kaydet"}
        </Button>
        <a className={styles.link} href="/degisiklikler/connectorler">
          Connector'lar
        </a>
      </div>

      {formOpen ? (
        <ManualChangeForm
          ownerGroups={ownerGroups}
          unrestricted={unrestricted}
          onSaved={() => {
            setFormOpen(false);
            void load();
          }}
        />
      ) : null}

      {loadError ? <ErrorState title="Değişiklikler yüklenemedi." hint={loadError} /> : null}

      {rows === null ? (
        <LoadingState label="Değişiklikler yükleniyor" />
      ) : rows.length === 0 && !loadError ? (
        <Card padded={false}>
          <EmptyState
            title="Henüz değişiklik kaydı yok"
            description="CI sistemlerini webhook connector'ıyla bağlayın ya da elle bir kayıt girin. Bu tablo geçmişe dönük doldurulamıyor — bugün başlaması gerekiyor."
            action={
              <a className={styles.link} href="/degisiklikler/connectorler">
                Connector tanımla
              </a>
            }
          />
        </Card>
      ) : (
        <DataTable
          caption="Son değişiklikler"
          columns={columns}
          rows={rows}
          rowKey={(row) => row.changeId ?? `${row.timestamp}-${row.targetId}-${row.changeKind}`}
        />
      )}
    </>
  );
}

interface ManualChangeFormProps {
  readonly ownerGroups: readonly string[];
  readonly unrestricted: boolean;
  readonly onSaved: () => void;
}

/**
 * Elle değişiklik girişi.
 *
 * <p>
 * Grup bir <b>seçim</b>, serbest metin değil: kullanıcı yalnızca kendi
 * kapsamındaki gruba yazabiliyor ve API bunu zaten zorluyor
 * (<c>IScopedQuery.WriteChangeAsync</c>). Serbest bırakmak, formu API'nin
 * kesinlikle reddedeceği bir isteğe davet etmek olurdu — ekran kapsam kapısını
 * atlamamalı, tekrarlamalı da değil, <b>yansıtmalı</b>.
 * </p>
 */
function ManualChangeForm({ ownerGroups, unrestricted, onSaved }: ManualChangeFormProps) {
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Kısıtsız kapsamda liste boş geliyor ve admin her gruba yazabiliyor.
  const groupChoices = unrestricted ? [] : ownerGroups;

  async function submit(form: FormData) {
    setSaving(true);
    setError(null);

    const timestamp = String(form.get("timestamp") ?? "").trim();

    try {
      await api.post("/v1/changes", {
        body: {
          ownerGroup: String(form.get("ownerGroup") ?? ""),
          targetKind: String(form.get("targetKind") ?? "Device"),
          targetId: String(form.get("targetId") ?? "").trim(),
          changeKind: String(form.get("changeKind") ?? "").trim(),
          actor: String(form.get("actor") ?? "").trim(),
          summary: String(form.get("summary") ?? "").trim(),
          source: "manual",
          // Boş bırakılırsa API "şimdi"yi kullanıyor. Tarayıcının yerel saatini
          // gönderiyoruz; `datetime-local` saat dilimi taşımıyor ve UTC
          // varsaymak kullanıcının girdiği saati sessizce kaydırırdı.
          ...(timestamp ? { timestamp: new Date(timestamp).toISOString() } : {}),
        },
      } as never);

      onSaved();
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Card>
      <form action={submit} className={styles.form}>
        <div className={styles.field}>
          <label className={styles.label} htmlFor="ownerGroup">
            Grup
          </label>
          {groupChoices.length > 0 ? (
            <select className={styles.select} id="ownerGroup" name="ownerGroup" required>
              {groupChoices.map((group) => (
                <option key={group} value={group}>
                  {group}
                </option>
              ))}
            </select>
          ) : (
            <input
              className={styles.select}
              id="ownerGroup"
              name="ownerGroup"
              required
              placeholder="network/core"
            />
          )}
        </div>

        <div className={styles.field}>
          <label className={styles.label} htmlFor="targetKind">
            Hedef türü
          </label>
          <select className={styles.select} id="targetKind" name="targetKind" defaultValue="Device">
            {TARGET_KINDS.map((kind) => (
              <option key={kind} value={kind}>
                {kind}
              </option>
            ))}
          </select>
        </div>

        <Field
          label="Hedef"
          name="targetId"
          required
          placeholder="fw-core-01"
          hint="Cihaz adı, servis adı ya da depo yolu."
        />

        <div className={styles.field}>
          <label className={styles.label} htmlFor="changeKind">
            Değişiklik türü
          </label>
          <input
            className={styles.select}
            id="changeKind"
            name="changeKind"
            required
            list="change-kinds"
            placeholder="config_push"
          />
          <datalist id="change-kinds">
            {CHANGE_KINDS.map((kind) => (
              <option key={kind} value={kind} />
            ))}
          </datalist>
        </div>

        <Field label="Kim yaptı" name="actor" placeholder="esra.yildiz" />

        <Field
          label="Ne zaman"
          name="timestamp"
          type="datetime-local"
          hint="Boş bırakılırsa şimdi kaydedilir."
        />

        <Field
          label="Açıklama"
          name="summary"
          placeholder="fw-core-01 dış ACL'e 10.20.0.0/16 eklendi"
        />

        {error ? <ErrorState title="Kaydedilemedi." hint={error} /> : null}

        <Button type="submit" disabled={saving}>
          {saving ? "Kaydediliyor…" : "Kaydet"}
        </Button>
      </form>
    </Card>
  );
}

/**
 * Zaman damgası biçimi. `Intl` yerine sabit bir biçim: tabloda hizalanması
 * gereken bir sütun ve yerel biçim uzunluğu satırdan satıra değiştirirdi.
 */
function formatInstant(value: string): string {
  const date = new Date(value);

  return Number.isNaN(date.getTime())
    ? "—"
    : date.toISOString().replace("T", " ").slice(0, 16);
}
