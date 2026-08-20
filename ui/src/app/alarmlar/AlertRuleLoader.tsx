"use client";

import { useEffect, useState } from "react";

import { ErrorState, LoadingState } from "@/components/ui/States";
import { api } from "@/lib/api/client";
import { describeError } from "@/lib/api/errors";
import type {
  AlertRule,
  AlertRuleDetail,
  NotificationChannel,
  NotificationChannelList,
} from "@/lib/alerts/types";

import { AlertRuleEditor } from "./AlertRuleEditor";

export interface AlertRuleLoaderProps {
  /** Düzenlenen kuralın kimliği; yeni kuralda `null`. */
  readonly ruleId: string | null;
  readonly ownerGroups: readonly string[];
  readonly unrestricted: boolean;
}

/**
 * Kural formunun veri yükleyicisi.
 *
 * <p>
 * Ayrı bir bileşen, çünkü <see cref="AlertRuleEditor"/> saf tutuluyor: verisi
 * dışarıdan geliyor, dolayısıyla testte ağ olmadan çizilebiliyor. Yükleme,
 * hata ve dolu durumları burada; editörün içinde olsalardı her test önce bir
 * ağ sahtesi kurmak zorunda kalırdı.
 * </p>
 *
 * <p>
 * Veri <b>istemcide</b> çekiliyor: tarayıcı API'ye doğrudan değil BFF
 * vekilinden konuşuyor ve oturum çerezi orada <c>Authorization</c>'a
 * çevriliyor. Sunucu bileşeninden çekmek, o makineyi ikinci bir yerde
 * kurmak olurdu.
 * </p>
 */
export function AlertRuleLoader({ ruleId, ownerGroups, unrestricted }: AlertRuleLoaderProps) {
  const [rule, setRule] = useState<AlertRule | null>(null);
  const [channelIds, setChannelIds] = useState<readonly string[]>([]);
  const [channels, setChannels] = useState<readonly NotificationChannel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [ready, setReady] = useState(false);

  useEffect(() => {
    const controller = new AbortController();

    async function load() {
      try {
        // Kanal listesi `admin` istiyor; `author` olan bir kullanıcı 403 alır.
        // Bu bir hata DEĞİL: kural yazabilir ama kanal yönetemez. Kanal listesi
        // boş kalıyor ve form bunu "tanımlı kanal yok" diye gösteriyor.
        const channelList = await api
          .get("/v1/alerts/channels", { signal: controller.signal })
          .then((result) => (result as NotificationChannelList).channels)
          .catch(() => [] as NotificationChannel[]);

        if (controller.signal.aborted) {
          return;
        }

        setChannels(channelList);

        if (ruleId) {
          const detail = (await api.get("/v1/alerts/rules/{id}", {
            path: { id: ruleId },
            signal: controller.signal,
          })) as AlertRuleDetail;

          setRule(detail.rule);
          setChannelIds(detail.channel_ids);
        }

        setReady(true);
      } catch (cause) {
        if (!controller.signal.aborted) {
          setError(describeError(cause));
        }
      }
    }

    void load();
    return () => controller.abort();
  }, [ruleId]);

  if (error) {
    return <ErrorState title="Kural yüklenemedi" hint={error} />;
  }

  if (!ready) {
    return <LoadingState label="Kural yükleniyor…" />;
  }

  return (
    <AlertRuleEditor
      rule={rule}
      channelIds={channelIds}
      ownerGroups={ownerGroups}
      unrestricted={unrestricted}
      channels={channels}
    />
  );
}
