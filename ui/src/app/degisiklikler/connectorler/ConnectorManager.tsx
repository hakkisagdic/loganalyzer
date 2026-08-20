"use client";

import { useCallback, useEffect, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Card, Field } from "@/components/ui/Field";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import {
  createRequest,
  screenState,
  toggleRequest,
  type ConnectorSummary,
} from "@/lib/changes/connector";

import styles from "../changes.module.css";

import { ConnectorTable } from "./ConnectorTable";

const PROVIDERS = ["github", "jenkins", "gitlab", "generic"] as const;
const TARGET_KINDS = ["Device", "Service", "Config", "Inventory", "Maintenance"] as const;

export interface ConnectorManagerProps {
  readonly ownerGroups: readonly string[];
  readonly unrestricted: boolean;
  readonly canManage: boolean;
}

export function ConnectorManager({ ownerGroups, unrestricted, canManage }: ConnectorManagerProps) {
  const [rows, setRows] = useState<readonly ConnectorSummary[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const [testing, setTesting] = useState<string | null>(null);
  const [testResult, setTestResult] = useState<{ id: string; ok: boolean; message: string } | null>(null);

  const load = useCallback(async () => {
    setLoadError(null);

    try {
      const body = (await api.get("/v1/changes/connectors")) as { connectors?: readonly ConnectorSummary[] };
      setRows(body.connectors ?? []);
    } catch (cause) {
      setRows([]);
      setLoadError(describeError(cause));
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const state = screenState(rows, loadError);

  async function test(connector: ConnectorSummary) {
    setTesting(connector.id);
    setTestResult(null);

    try {
      const body = (await api.post("/v1/changes/connectors/{id}/test", {
        path: { id: connector.id },
      } as never)) as { ok: boolean; message: string };

      setTestResult({ id: connector.id, ok: body.ok, message: body.message });
    } catch (cause) {
      setTestResult({ id: connector.id, ok: false, message: describeError(cause) });
    } finally {
      setTesting(null);
    }
  }

  async function toggle(connector: ConnectorSummary) {
    try {
      // Gövde saf fonksiyondan: `credential` alanı HİÇ konmuyor. Maskeyi
      // geri göndermek, kayıtlı gizli anahtarı `••••••••` yapardı — bunu
      // sınayan bir test var.
      await api.put("/v1/changes/connectors/{id}", {
        path: { id: connector.id },
        body: toggleRequest(connector),
      } as never);

      await load();
    } catch (cause) {
      setLoadError(describeError(cause));
    }
  }

  return (
    <>
      <div className={styles.toolbar}>
        {canManage ? (
          <Button variant="primary" onClick={() => setFormOpen((open) => !open)} aria-expanded={formOpen}>
            {formOpen ? "Formu kapat" : "Connector ekle"}
          </Button>
        ) : null}
        <a className={styles.link} href="/degisiklikler">
          Değişiklik akışı
        </a>
      </div>

      {formOpen ? (
        <ConnectorForm
          ownerGroups={ownerGroups}
          unrestricted={unrestricted}
          onSaved={() => {
            setFormOpen(false);
            void load();
          }}
        />
      ) : null}

      {state === "error" ? (
        <ErrorState title="Connector'lar yüklenemedi." hint={loadError ?? undefined} />
      ) : null}

      {testResult ? (
        <div className={styles.testResult}>
          {testResult.ok ? (
            <Card>
              <p>{testResult.message}</p>
            </Card>
          ) : (
            // Hata metni sunucuda redaksiyondan geçmiş hâliyle geliyor; ekran
            // onu olduğu gibi gösterebiliyor.
            <ErrorState title="Bağlantı denemesi başarısız." hint={testResult.message} />
          )}
        </div>
      ) : null}

      {state === "loading" ? (
        <LoadingState label="Connector'lar yükleniyor" />
      ) : state === "empty" ? (
        <Card padded={false}>
          <EmptyState
            title="Henüz connector yok"
            description="CI sisteminizi imzalı bir webhook connector'ıyla bağlayın. change_events tablosu geçmişe dönük doldurulamıyor — birikmeye bugün başlaması gerekiyor."
          />
        </Card>
      ) : state === "ready" ? (
        <ConnectorTable
          rows={rows ?? []}
          canManage={canManage}
          testingId={testing}
          onTest={(row) => void test(row)}
          onToggle={(row) => void toggle(row)}
        />
      ) : null}

      {rows?.some((row) => row.receive_path) ? (
        <Card>
          <h2>Webhook adresleri</h2>
          <div className={styles.detail}>
            {rows
              .filter((row) => row.receive_path)
              .map((row) => (
                <div key={row.id}>
                  <span className={styles.definitionTerm}>{row.name}</span>
                  <code className={styles.definitionValue}>POST {row.receive_path}</code>
                </div>
              ))}
          </div>
        </Card>
      ) : null}
    </>
  );
}

interface ConnectorFormProps {
  readonly ownerGroups: readonly string[];
  readonly unrestricted: boolean;
  readonly onSaved: () => void;
}

/**
 * Connector ekleme formu.
 *
 * <p>
 * <b>Kimlik bilgisi alanı yalnızca yazılıyor.</b> Değeri hiçbir zaman geri
 * okunmuyor; düzenlemede boş bırakmak "değiştirme" demek. Alanın
 * <c>autoComplete="new-password"</c> taşıması bilinçli: tarayıcının parola
 * yöneticisi buraya başka bir sitenin parolasını doldurmamalı.
 * </p>
 */
function ConnectorForm({ ownerGroups, unrestricted, onSaved }: ConnectorFormProps) {
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [type, setType] = useState("Webhook");

  const groupChoices = unrestricted ? [] : ownerGroups;

  async function submit(form: FormData) {
    setSaving(true);
    setError(null);

    const connectorType = String(form.get("connectorType") ?? "Webhook");

    try {
      await api.post("/v1/changes/connectors", {
        body: createRequest({
          slug: String(form.get("slug") ?? ""),
          name: String(form.get("name") ?? ""),
          connectorType,
          ownerGroup: String(form.get("ownerGroup") ?? ""),
          provider: String(form.get("provider") ?? "github"),
          targetKind: String(form.get("targetKind") ?? "Service"),
          defaultChangeKind: String(form.get("defaultChangeKind") ?? "deploy"),
          intervalSeconds: String(form.get("intervalSeconds") ?? "900"),
          credential: String(form.get("credential") ?? ""),
        }),
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
        <Field
          label="Kimlik (slug)"
          name="slug"
          required
          placeholder="gh-network"
          hint="Webhook adresinde görünüyor; küçük harf, rakam ve tire."
        />

        <Field label="Ad" name="name" required placeholder="GitHub — ağ yapılandırması" />

        <div className={styles.field}>
          <label className={styles.label} htmlFor="connectorType">
            Tip
          </label>
          <select
            className={styles.select}
            id="connectorType"
            name="connectorType"
            value={type}
            onChange={(event) => setType(event.target.value)}
          >
            <option value="Webhook">Webhook (CI sistemi bildiriyor)</option>
            <option value="DeviceConfig">Cihaz config farkı</option>
            <option value="Manual">Yalnızca elle giriş</option>
          </select>
          {type === "DeviceConfig" ? (
            <p className={styles.definitionTerm}>
              Cihaz toplayıcısı henüz yok (T26); kaydedilebilir ama etkinleştirilemez.
            </p>
          ) : null}
        </div>

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
            <input className={styles.select} id="ownerGroup" name="ownerGroup" required placeholder="network/core" />
          )}
        </div>

        {type === "Webhook" ? (
          <>
            <div className={styles.field}>
              <label className={styles.label} htmlFor="provider">
                Sağlayıcı
              </label>
              <select className={styles.select} id="provider" name="provider" defaultValue="github">
                {PROVIDERS.map((provider) => (
                  <option key={provider} value={provider}>
                    {provider}
                  </option>
                ))}
              </select>
            </div>

            <div className={styles.field}>
              <label className={styles.label} htmlFor="targetKind">
                Hedef türü
              </label>
              <select className={styles.select} id="targetKind" name="targetKind" defaultValue="Service">
                {TARGET_KINDS.map((kind) => (
                  <option key={kind} value={kind}>
                    {kind}
                  </option>
                ))}
              </select>
            </div>

            <Field
              label="Varsayılan değişiklik türü"
              name="defaultChangeKind"
              defaultValue="deploy"
              hint="Sağlayıcı gövdesinden tür çıkmadığında kullanılıyor."
            />
          </>
        ) : null}

        {type === "DeviceConfig" ? (
          <Field
            label="Çekim aralığı (saniye)"
            name="intervalSeconds"
            type="number"
            min={60}
            defaultValue={900}
            hint="En az 60 — sık çekim izlenen cihazı yorar."
          />
        ) : null}

        {type !== "Manual" ? (
          <Field
            label={type === "Webhook" ? "İmza anahtarı" : "Parola / jeton"}
            name="credential"
            type="password"
            autoComplete="new-password"
            required
            hint="Şifreli saklanıyor; bir daha görüntülenemiyor."
          />
        ) : null}

        {error ? <ErrorState title="Kaydedilemedi." hint={error} /> : null}

        <Button type="submit" variant="primary" disabled={saving}>
          {saving ? "Kaydediliyor…" : "Kaydet (pasif)"}
        </Button>
      </form>
    </Card>
  );
}
