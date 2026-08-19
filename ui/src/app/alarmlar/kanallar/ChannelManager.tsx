"use client";

import { useCallback, useEffect, useState } from "react";

import { Button } from "@/components/ui/Button";
import { Badge, Card, Field, SelectField } from "@/components/ui/Field";
import { DataTable } from "@/components/ui/DataTable";
import { EmptyState, ErrorState, LoadingState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/alerts/errors";
import {
  CHANNEL_TYPE_LABELS,
  CHANNEL_TYPES,
  type ChannelTest,
  type ChannelType,
  type NotificationChannel,
  toNumber,
  type NotificationChannelList,
} from "@/lib/alerts/types";

import styles from "../alerts.module.css";

interface DraftState {
  readonly name: string;
  readonly channelType: ChannelType;
  readonly ownerGroup: string;
  readonly secret: string;
  readonly host: string;
  readonly port: number;
  readonly from: string;
  readonly to: string;
  readonly user: string;
  readonly useStartTls: boolean;
}

const EMPTY: DraftState = {
  name: "",
  channelType: "slack",
  ownerGroup: "",
  secret: "",
  host: "",
  port: 587,
  from: "",
  to: "",
  user: "",
  useStartTls: true,
};

/**
 * Bildirim kanalları ve **test gönderimi** (T23).
 *
 * <p>
 * Kabul kriteri: "test gönderimi gerçek kanala gidiyor ve sonucu ekranda
 * görünüyor". Sonuç iki türlü olabiliyor ve ikisi de gösteriliyor — başarısızlık
 * sessizce yutulursa kullanıcı yanlış yapılandırılmış bir kanalı çalışıyor
 * sanır ve bunu ilk gerçek alarmda öğrenir.
 * </p>
 *
 * <p>
 * <b>Gizli bilgi tek yönlü.</b> Form onu yazıyor, hiçbir yanıt geri
 * döndürmüyor; liste yalnızca "tanımlı mı" diyor. Düzenlemede boş bırakmak
 * mevcut değeri koruyor, yani kanal adını değiştirmek için parolayı yeniden
 * girmek gerekmiyor.
 * </p>
 */
export function ChannelManager({ ownerGroups }: { ownerGroups: readonly string[] }) {
  const [channels, setChannels] = useState<readonly NotificationChannel[] | null>(null);
  const [draft, setDraft] = useState<DraftState>({ ...EMPTY, ownerGroup: ownerGroups[0] ?? "" });
  const [editing, setEditing] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [testResult, setTestResult] = useState<Record<string, ChannelTest>>({});

  const patch = useCallback(
    <K extends keyof DraftState>(key: K, value: DraftState[K]) =>
      setDraft((current) => ({ ...current, [key]: value })),
    [],
  );

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const list = (await api.get("/v1/alerts/channels", { signal })) as NotificationChannelList;
      setChannels(list.channels);
    } catch (cause) {
      if (!signal?.aborted) {
        setError(describeError(cause));
        setChannels([]);
      }
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  async function save() {
    setBusy(true);
    setError(null);

    const body = {
      name: draft.name,
      channelType: draft.channelType,
      ownerGroup: draft.ownerGroup,
      // Boş gizli bilgi GÖNDERİLMİYOR: sunucu tarafında boş değer "mevcut olanı
      // koru" anlamına geliyor ve boş string göndermek onu silmek olurdu.
      secret: draft.secret || undefined,
      enabled: true,
      headers: {},
      host: draft.host,
      port: draft.port,
      from: draft.from,
      to: draft.to
        .split(",")
        .map((item) => item.trim())
        .filter((item) => item.length > 0),
      user: draft.user,
      useStartTls: draft.useStartTls,
    };

    try {
      if (editing) {
        await api.put("/v1/alerts/channels/{id}", { path: { id: editing }, body });
      } else {
        await api.post("/v1/alerts/channels", { body });
      }

      setDraft({ ...EMPTY, ownerGroup: ownerGroups[0] ?? "" });
      setEditing(null);
      await load();
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  async function remove(channel: NotificationChannel) {
    setBusy(true);

    try {
      await api.delete("/v1/alerts/channels/{id}", { path: { id: channel.id } });
      await load();
    } catch (cause) {
      setError(describeError(cause));
    } finally {
      setBusy(false);
    }
  }

  async function test(channel: NotificationChannel) {
    setBusy(true);

    try {
      const result = (await api.post("/v1/alerts/channels/{id}/test", {
        path: { id: channel.id },
      })) as ChannelTest;

      setTestResult((current) => ({ ...current, [channel.id]: result }));
    } catch (cause) {
      // 422 de bir SONUÇ: kanal ulaşılamadı ve sebebi gösterilmeli.
      setTestResult((current) => ({
        ...current,
        [channel.id]: { ok: false, error: describeError(cause) },
      }));
    } finally {
      setBusy(false);
    }
  }

  function edit(channel: NotificationChannel) {
    setEditing(channel.id);
    setDraft({
      name: channel.name,
      channelType: channel.channel_type as ChannelType,
      ownerGroup: channel.owner_group,
      secret: "",
      host: channel.settings.host,
      port: toNumber(channel.settings.port),
      from: channel.settings.from,
      to: channel.settings.to.join(", "),
      user: channel.settings.user,
      useStartTls: channel.settings.use_start_tls,
    });
  }

  const isEmail = draft.channelType === "email";

  return (
    <div className={styles.stack}>
      <div className={styles.toolbar}>
        <h1>Bildirim kanalları</h1>
      </div>

      {error ? <ErrorState title="Kanal işlemi başarısız" hint={error} /> : null}

      <Card>
        <div className={styles.stack}>
          <h2>{editing ? "Kanalı düzenle" : "Yeni kanal"}</h2>

          <div className={styles.formGrid}>
            <Field
              label="Kanal adı"
              value={draft.name}
              onChange={(event) => patch("name", event.target.value)}
              required
            />

            <SelectField
              label="Tip"
              value={draft.channelType}
              onChange={(event) => patch("channelType", event.target.value as ChannelType)}
              options={CHANNEL_TYPES.map((type) => ({ value: type, label: CHANNEL_TYPE_LABELS[type] }))}
            />

            <SelectField
              label="Kapsam"
              value={draft.ownerGroup}
              onChange={(event) => patch("ownerGroup", event.target.value)}
              options={ownerGroups.map((group) => ({ value: group, label: group }))}
              hint="Kanal yalnızca bu gruptaki kurallara bağlanabilir."
            />
          </div>

          <Field
            label={isEmail ? "SMTP parolası" : "Hedef adres (webhook URL'i)"}
            type="password"
            value={draft.secret}
            onChange={(event) => patch("secret", event.target.value)}
            hint={
              editing
                ? "Boş bırakılırsa mevcut değer korunur. Kayıtlı değer hiçbir zaman geri gösterilmez."
                : "Şifreli saklanır ve hiçbir log, hata mesajı veya API yanıtında görünmez."
            }
            autoComplete="new-password"
          />

          {isEmail ? (
            <div className={styles.formGrid}>
              <Field
                label="SMTP sunucusu"
                value={draft.host}
                onChange={(event) => patch("host", event.target.value)}
              />
              <Field
                label="Port"
                type="number"
                value={draft.port}
                onChange={(event) => patch("port", Number(event.target.value))}
              />
              <Field
                label="Gönderen"
                value={draft.from}
                onChange={(event) => patch("from", event.target.value)}
              />
              <Field
                label="Alıcılar (virgülle)"
                value={draft.to}
                onChange={(event) => patch("to", event.target.value)}
              />
              <Field
                label="SMTP kullanıcı adı"
                value={draft.user}
                onChange={(event) => patch("user", event.target.value)}
              />
            </div>
          ) : null}

          <div className={styles.inlineActions}>
            <Button variant="primary" onClick={save} disabled={busy || !draft.name || !draft.ownerGroup}>
              {editing ? "Kaydet" : "Kanal ekle"}
            </Button>
            {editing ? (
              <Button
                onClick={() => {
                  setEditing(null);
                  setDraft({ ...EMPTY, ownerGroup: ownerGroups[0] ?? "" });
                }}
              >
                Vazgeç
              </Button>
            ) : null}
          </div>
        </div>
      </Card>

      {channels === null ? (
        <LoadingState label="Kanallar yükleniyor…" />
      ) : channels.length === 0 ? (
        <Card>
          <EmptyState
            title="Tanımlı kanal yok"
            description="Kanal olmadan tetiklenen bir alarm kimseye ulaşmaz."
          />
        </Card>
      ) : (
        <Card padded={false}>
          <DataTable
            caption="Tanımlı kanallar"
            rowKey={(row) => row.id}
            rows={channels}
            columns={[
              { key: "name", header: "Ad", width: "22%", render: (row) => row.name },
              {
                key: "type",
                header: "Tip",
                width: "14%",
                render: (row) => CHANNEL_TYPE_LABELS[row.channel_type as ChannelType] ?? row.channel_type,
              },
              { key: "scope", header: "Kapsam", width: "14%", render: (row) => row.owner_group },
              {
                key: "secret",
                header: "Gizli bilgi",
                width: "12%",
                render: (row) =>
                  row.secret_set ? <Badge tone="success">tanımlı</Badge> : <Badge tone="danger">yok</Badge>,
              },
              {
                key: "test",
                header: "Test sonucu",
                width: "24%",
                render: (row) => {
                  const result = testResult[row.id];

                  if (!result) {
                    return <span className={styles.muted}>henüz denenmedi</span>;
                  }

                  return result.ok ? (
                    <Badge tone="success">ulaştı</Badge>
                  ) : (
                    <span className={styles.deliveryRow}>
                      <Badge tone="danger">ulaşmadı</Badge>
                      <span className={styles.deliveryError}>{result.error}</span>
                    </span>
                  );
                },
              },
              {
                key: "actions",
                header: "İşlem",
                width: "14%",
                render: (row) => (
                  <span className={styles.inlineActions}>
                    <Button disabled={busy} onClick={() => test(row)}>
                      Test gönder
                    </Button>
                    <Button disabled={busy} onClick={() => edit(row)}>
                      Düzenle
                    </Button>
                    <Button variant="danger" disabled={busy} onClick={() => remove(row)}>
                      Sil
                    </Button>
                  </span>
                ),
              },
            ]}
          />
        </Card>
      )}
    </div>
  );
}
