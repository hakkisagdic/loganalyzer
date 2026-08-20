import type { components } from "@/lib/api/schema";
import { PARAM, UNSUPPORTED_PARAM } from "@/lib/events/criteria";

export type RcaDrilldown = components["schemas"]["RcaDrilldownResponse"];
export type RcaDrilldownFilter = components["schemas"]["RcaDrilldownFilterResponse"];

/**
 * Kanıt satırından **olay aramasına** giden bağlantı (T37).
 *
 * <p>
 * Ticket'ın maddesi: <i>"şu imza ilk kez göründü" satırı, o imzayı arayan
 * sorguyu doğru zaman aralığıyla açıyor.</i> Kaynağı sunucudan gelen
 * <c>drilldown</c> — ham SQL değil, yapılandırılmış bir sorgu, ve tıklandığında
 * kapsam kapısından yeniden geçiyor (K17).
 * </p>
 *
 * <h3>Temsil edilemeyen filtre sessizce düşmüyor</h3>
 *
 * <p>
 * Arama ekranının parametre kümesi (<c>PARAM</c>) kanıt sağlayıcılarının
 * ürettiği her filtreyi karşılamıyor: <c>signature_hash</c> üzerinde bir
 * eşitlik, ya da <c>vendor != x</c> gibi bir olumsuzlama, adres çubuğunda
 * karşılığı olmayan şeyler.
 * </p>
 *
 * <p>
 * <b>Böyle bir filtreyi sessizce düşürmek en kötü seçenek</b> ve bu depoda
 * daha önce bir kez bedeli ödendi: kullanıcı, kanıt satırının gösterdiğinden
 * <b>daha geniş</b> bir kümeye bakar ve baktığı kümenin o satırın kümesi
 * olduğunu sanır. Alarm bağlantısında aynı sorun <c>eksik</c> parametresiyle
 * çözülmüştü; burada <b>aynı mekanizma</b> kullanılıyor, ikinci bir kopya
 * yazılmıyor — arama ekranı zaten <c>unsupportedFilters</c> ile okuyup
 * kullanıcıya söylüyor.
 * </p>
 */

/**
 * Drilldown filtresi → arama ekranı parametresi.
 *
 * <p>
 * <b>Operatör de eşleşmek zorunda.</b> Ekran yalnızca eşitlik kurabiliyor;
 * <c>not_equals</c> ya da <c>greater_than</c> taşıyan bir filtreyi eşitliğe
 * çevirmek, kullanıcıya <b>yanlış</b> bir küme göstermek olurdu — sessizce
 * düşürmekten beter, çünkü dolu ve inandırıcı bir sonuç veriyor.
 * </p>
 *
 * <p>
 * Anahtar sunucudaki kolon adı (<c>EventReader.FilterableColumns</c>), değer
 * <c>PARAM</c>'ın anahtarı. Burada olmayan her kolon <c>eksik</c>'e düşüyor.
 * </p>
 */
const EQUALITY_COLUMNS: Readonly<Record<string, keyof typeof PARAM>> = {
  source_id: "sourceId",
  owner_group: "ownerGroup",
  vendor: "vendor",
  proto: "proto",
  action: "action",
};

/** Ekranın kurabildiği tek operatör. */
const SUPPORTED_OPERATOR = "equals";

interface Mapped {
  readonly params: URLSearchParams;
  /** Adres çubuğunda temsil edilemeyen filtre alanları. */
  readonly unsupported: readonly string[];
}

function mapFilters(filters: readonly RcaDrilldownFilter[]): Mapped {
  const params = new URLSearchParams();
  const unsupported: string[] = [];

  for (const filter of filters) {
    const target = EQUALITY_COLUMNS[filter.field];
    const single = filter.values.length === 1 ? filter.values[0] : undefined;

    // Üç şart da gerekiyor: kolonun karşılığı olmalı, operatör eşitlik olmalı
    // ve tek değer olmalı. Çoklu değeri ekran tek kutuda gösteremiyor ve
    // birini seçmek diğerlerini sessizce atmak olurdu.
    if (target !== undefined && filter.operator === SUPPORTED_OPERATOR && single !== undefined) {
      params.set(PARAM[target], single);
      continue;
    }

    unsupported.push(filter.field);
  }

  return { params, unsupported };
}

/**
 * Kanıt satırının olay arama ekranındaki adresi.
 *
 * <p>
 * Zaman aralığı <b>her zaman</b> taşınıyor: doğru pencereyi açmak ticket'ın
 * maddesinin yarısı. Kaynak listesi tek elemanlıysa kutuya yazılıyor, değilse
 * — ekranın tek bir kaynak kutusu var — <c>eksik</c>'e düşüyor.
 * </p>
 */
export function toEventsHref(drilldown: RcaDrilldown): string {
  const { params, unsupported } = mapFilters(drilldown.filters);
  const missing = [...unsupported];

  params.set(PARAM.from, drilldown.from);
  params.set(PARAM.to, drilldown.to);

  if (drilldown.full_text) {
    params.set(PARAM.fullText, drilldown.full_text);
  }

  const onlySource = drilldown.source_ids.length === 1 ? drilldown.source_ids[0] : undefined;
  const onlyGroup = drilldown.owner_groups.length === 1 ? drilldown.owner_groups[0] : undefined;

  if (onlySource !== undefined) {
    params.set(PARAM.sourceId, onlySource);
  } else if (drilldown.source_ids.length > 1) {
    missing.push("source_id");
  }

  if (onlyGroup !== undefined) {
    params.set(PARAM.ownerGroup, onlyGroup);
  } else if (drilldown.owner_groups.length > 1) {
    missing.push("owner_group");
  }

  if (missing.length > 0) {
    // Arama ekranı bunu okuyup kullanıcıya söylüyor: "bu bağlantı şu
    // filtreleri taşıyamadı, gördüğün küme daha geniş".
    params.set(UNSUPPORTED_PARAM, [...new Set(missing)].join(","));
  }

  return `/olaylar?${params.toString()}`;
}

/**
 * Bağlantının kayıp filtre taşıyıp taşımadığı — ekran rozet gösteriyor.
 *
 * <p>
 * Kullanıcının bunu <b>tıklamadan önce</b> bilmesi gerekiyor; tıkladıktan
 * sonra öğrenmek, yanlış kümeye bakmış olmayı geri almıyor.
 * </p>
 */
export function drilldownLosesFilters(drilldown: RcaDrilldown): boolean {
  return toEventsHref(drilldown).includes(`${UNSUPPORTED_PARAM}=`);
}
