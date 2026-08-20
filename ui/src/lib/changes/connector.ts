/**
 * Değişiklik ekranlarının **saf** mantığı.
 *
 * <p>
 * Bileşenin içinde kalsaydı sınanamazdı: kancalı bir istemci bileşenini
 * çizmeden bu kararları yoklamanın yolu yok. T15 aynı ayrımı
 * `lib/events/criteria.ts` ile yapmıştı.
 * </p>
 */

/**
 * Sunucunun kimlik bilgisi yerine döndürdüğü sabit maske.
 *
 * <p>Değeri `ChangeConnectorService.CredentialMask` ile aynı olmak zorunda;
 * ayrışırsa {@link credentialForSave} maskeyi tanıyamaz ve tam da engellemek
 * için var olduğu şeyi yapar.</p>
 */
export const CREDENTIAL_MASK = "••••••••";

export interface ConnectorSummary {
  readonly id: string;
  readonly slug: string;
  readonly name: string;
  readonly connector_type: string;
  readonly owner_group: string;
  readonly config: Record<string, unknown>;
  readonly credential_set: boolean;
  readonly interval_seconds: number | null;
  readonly enabled: boolean;
  readonly last_run_at: string | null;
  readonly last_run_state: string | null;
  readonly last_error: string;
  readonly receive_path: string | null;
}

/**
 * Kaydedilecek kimlik bilgisi — ya da **hiç**.
 *
 * <p>
 * İki durumda <c>undefined</c> dönüyor ve ikisi de "değiştirme" demek:
 * </p>
 *
 * <list type="number">
 * <item>Kullanıcı alanı boş bıraktı.</item>
 * <item>
 * Alanda <b>maskenin kendisi</b> var. Bu, düz metin parola göndermekten daha
 * sinsi bir sızıntı yolu: kullanıcı "değiştirmedim" diye kaydeder, ekran
 * maskeyi geri yollar ve cihazın kayıtlı parolası <c>••••••••</c> olur. Sonraki
 * çekim "kimlik doğrulama reddedildi" der ve kimse sebebini anlamaz.
 * </item>
 * </list>
 */
export function credentialForSave(value: string | null | undefined): string | undefined {
  const trimmed = value?.trim();

  if (!trimmed || trimmed === CREDENTIAL_MASK) {
    return undefined;
  }

  return trimmed;
}

export interface ConnectorRequestBody {
  readonly slug: string;
  readonly name: string;
  readonly connector_type: string;
  readonly owner_group: string;
  readonly config: Record<string, unknown>;
  readonly interval_seconds: number | null;
  readonly enabled: boolean;
  readonly credential?: string;
}

/**
 * Mevcut bir connector'ı etkin/pasif yapmak için gövde.
 *
 * <p>Kimlik bilgisi alanı <b>hiç konmuyor</b>: ekran onu zaten görmüyor ve
 * gönderecek bir değeri yok.</p>
 */
export function toggleRequest(connector: ConnectorSummary): ConnectorRequestBody {
  return {
    slug: connector.slug,
    name: connector.name,
    connector_type: connector.connector_type,
    owner_group: connector.owner_group,
    config: connector.config,
    interval_seconds: connector.interval_seconds,
    enabled: !connector.enabled,
  };
}

export interface ConnectorFormValues {
  readonly slug: string;
  readonly name: string;
  readonly connectorType: string;
  readonly ownerGroup: string;
  readonly provider: string;
  readonly targetKind: string;
  readonly defaultChangeKind: string;
  readonly intervalSeconds: string;
  readonly credential: string;
}

/**
 * Yeni connector gövdesi.
 *
 * <p>
 * <b>Her zaman pasif doğuyor.</b> Etkinleştirmeden önce bağlantı denenebilsin:
 * etkin doğan bir connector, yanlış yapılandırıldığında ilk hatasını kullanıcı
 * ekrandan ayrıldıktan sonra verirdi.
 * </p>
 */
export function createRequest(values: ConnectorFormValues): ConnectorRequestBody {
  const webhook = values.connectorType === "Webhook";
  const device = values.connectorType === "DeviceConfig";

  return {
    slug: values.slug.trim(),
    name: values.name.trim(),
    connector_type: values.connectorType,
    owner_group: values.ownerGroup,
    config: webhook
      ? {
          provider: values.provider,
          // `targetKind` sunucuya METİN gidiyor ("Config"), sayı değil.
          // Bu çeviri bir kez kırıldı: yanıt tarafı enum'u sayı olarak
          // yayınlıyordu ve ekran metin bekliyordu — uç sessizce "yok"
          // görünüyordu.
          targetKind: values.targetKind,
          defaultChangeKind: values.defaultChangeKind.trim(),
        }
      : {},
    interval_seconds: device ? Number(values.intervalSeconds) : null,
    enabled: false,
    credential: credentialForSave(values.credential),
  };
}

/** Elle değişiklik girişi gövdesi. */
export interface ChangeWriteBody {
  readonly owner_group: string;
  readonly target_kind: string;
  readonly target_id: string;
  readonly change_kind: string;
  readonly actor: string;
  readonly summary: string;
  readonly source: string;
  readonly timestamp?: string;
}

export interface ChangeFormValues {
  readonly ownerGroup: string;
  readonly targetKind: string;
  readonly targetId: string;
  readonly changeKind: string;
  readonly actor: string;
  readonly summary: string;
  /** `datetime-local` değeri; boş bırakılabilir. */
  readonly timestamp: string;
}

export function changeWriteRequest(values: ChangeFormValues): ChangeWriteBody {
  const stamp = values.timestamp.trim();

  return {
    owner_group: values.ownerGroup,
    target_kind: values.targetKind,
    target_id: values.targetId.trim(),
    change_kind: values.changeKind.trim(),
    actor: values.actor.trim(),
    summary: values.summary.trim(),
    source: "manual",
    // Boş bırakılırsa API "şimdi"yi kullanıyor. `datetime-local` saat dilimi
    // taşımıyor; tarayıcının yerel saatini UTC'ye çeviriyoruz, UTC varsaymak
    // kullanıcının girdiği saati sessizce kaydırırdı.
    ...(stamp ? { timestamp: new Date(stamp).toISOString() } : {}),
  };
}

export type ScreenState = "loading" | "error" | "empty" | "ready";

/**
 * Ekranın dört durumundan hangisi (T13 kuralı, T28 denetleyecek).
 *
 * <p>
 * Saf bir fonksiyon çünkü asıl kırılgan yer sıra: hata varken "boş" göstermek,
 * kullanıcıya "kayıt yok" diye yanlış bilgi vermek olurdu — oysa kayıt olabilir,
 * yalnızca okunamadı.
 * </p>
 */
export function screenState(rows: readonly unknown[] | null, error: string | null): ScreenState {
  if (rows === null) {
    return "loading";
  }

  if (error) {
    return "error";
  }

  return rows.length === 0 ? "empty" : "ready";
}
