import {
  ApiError,
  ForbiddenError,
  NotFoundError,
  RateLimitedError,
  SessionExpiredError,
  TransportError,
} from "@/lib/api/errors";
import type { RcaTrust } from "@/lib/rca/report";

/**
 * Bir hatayı **sınıfına** indiriyor. Mesajına değil.
 *
 * <p>
 * Bu modülün varlık sebebi `describeError` ile arasındaki fark. O fonksiyon
 * <b>sunucunun cümlesini</b> döndürüyor ve döndürmesi doğru — kullanıcının
 * ekranında "hangi grup kapsam dışında", "hangi sınır aşıldı" yazması
 * gerekiyor. Ama o cümle bir grup adı, bir dosya yolu, bir sınır değeri, hatta
 * bir log satırı taşıyabilir. Telemetriye giden şey <b>o cümle olamaz</b>.
 * </p>
 *
 * <p>
 * Buradaki sözlük <b>kapalı</b>: dönebilecek değerler sayılı. Yeni bir hata
 * tipi eklendiğinde `unknown`'a düşüyor — sessizce ham metin sızdırmıyor.
 * Yanlış yöndeki hata bu olurdu.
 * </p>
 */
export type ErrorKind =
  /**
   * Kimlik katmanı düştü — API çağrısı değil.
   *
   * <p>Ayrı bir üye çünkü ayrı bir arıza: `currentUser` üç durumlu ve
   * "API cevap vermiyor" hâli hiçbir HTTP durumu taşımıyor. `unknown`'a
   * düşürmek onu ekranların ürettiği her sınıflandırılamayan hatayla aynı
   * kovaya koyardı ve "kimlik katmanı mı bozuk, bir uç mu" sorusu veride
   * cevaplanamaz olurdu. Sözlüğe girdi, yani kapalı kalmaya devam ediyor.</p>
   */
  | "identity"
  | "session_expired"
  | "forbidden"
  | "not_found"
  | "rate_limited"
  | "transport"
  | `http_${number}`
  | "unknown";

export function errorKind(cause: unknown): ErrorKind {
  // Sıra önemli: hepsi `ApiError`'dan türüyor, en özel olan önce sorulmalı.
  if (cause instanceof SessionExpiredError) {
    return "session_expired";
  }

  if (cause instanceof ForbiddenError) {
    return "forbidden";
  }

  if (cause instanceof NotFoundError) {
    return "not_found";
  }

  if (cause instanceof RateLimitedError) {
    return "rate_limited";
  }

  if (cause instanceof ApiError) {
    // Durum kodu bir SAYI, sunucunun cümlesi değil. `http_500` "sunucu
    // düştü" diyor ve içinde hiçbir şey taşımıyor.
    return `http_${cause.status}`;
  }

  if (cause instanceof TransportError) {
    return "transport";
  }

  return "unknown";
}

/**
 * Bir hatanın HTTP durumu — biliniyorsa.
 *
 * <p>Ayrı bir fonksiyon çünkü `error_shown` olayı ikisini de taşıyor ve
 * `errorKind`'ın dönüş tipini sayıya bulaştırmak istemedik.</p>
 */
export function errorStatus(cause: unknown): number | undefined {
  return cause instanceof ApiError ? cause.status : undefined;
}


/**
 * Bir RCA penceresinin **zaman güvenilirliği**, kapalı bir sözlüğe indirilmiş —
 * `rca_run` olayının `trust_band` alanı.
 *
 * <p>
 * <b>Neden bu dosyada:</b> `errorKind` ile aynı işi yapıyor, yani zengin bir
 * sunucu nesnesini <b>sayılı</b> bir değere indiriyor. İki kapalı sözlüğün iki
 * ayrı dosyada durması, ikincisini yazacak kişinin birincinin dersini
 * görmemesi demekti. Ayrıca `events.ts` zaten buradan `ErrorKind` alıyor;
 * `measure.ts`'e koymak <c>events.ts → measure.ts → events.ts</c> döngüsü
 * açardı.
 * </p>
 *
 * <p>
 * <b>Alanın adı `quality_band` DEĞİL.</b> Katalogda
 * `quality_band` yazıyordu ve o adın bu üründe <b>zaten bir sahibi var</b>:
 * <c>GoldenSetQuality</c> — incelenmiş raporların doğruluk oranı, aynı RCA
 * ekranında <c>QualityBadge</c> olarak duruyor (T38). Bir pano kuranın
 * "rca_run × quality_band" grafiğini o rozetin yanına koyup <b>aynı şeyi
 * ölçtüklerini sanması</b> kaçınılmazdı; oysa biri incelemenin doğruluğu,
 * diğeri pencerenin zamanı. Üstelik koşum <b>anında</b> o paketin bir
 * incelemesi yok, yani `quality_band` altında gidebilecek bir "kalite" zaten
 * yoktu. Ad değişti çünkü bugün bedeli sıfır: bu alan için PostHog'a
 * <b>hiç olay gitmemişti</b> — depo tarandı, üretici yoktu — yani kırılacak
 * bir pano da yok (CLAUDE.md §8, "kırmak bedava iken kır").
 * </p>
 *
 * <p>
 * <b>Sınırların hiçbiri uydurulmadı.</b> Eşik yok: her ayrım <i>yapısal</i> —
 * ölçüldü mü, payda sıfır mı, pay sıfır mı, pay paydaya eşit mi. Bir "%5 az,
 * %50 çok" tablosu yazmak, aynı ekranın taban penceresi için reddettiği şeyi
 * yapmak olurdu: ölçülmemiş bir sayıyı ölçülmüş gibi göstermek
 * (<c>BASELINE_CHOICES</c>'ın önseçili değeri yok, aynı gerekçe).
 * </p>
 *
 * <p>
 * <b><c>unreliable_ratio</c> bilerek okunmuyor.</b> Sunucu onu
 * <c>Measured &amp;&amp; TotalEvents &gt; 0</c> iken üretiyor, aksi hâlde
 * <c>null</c> (bkz. <c>WindowTrust.UnreliableRatio</c>). Yani <c>null</c> tek
 * bir hâl değil <b>iki</b> hâl: "ölçmedik" ve "pencerede hiç olay yok". Bandı
 * orandan türetseydik bu ikisi tek bir <c>null</c> kovasında birleşirdi —
 * ve bu depoda en pahalı hata sınıfı tam olarak budur (§7). Sayaçlardan
 * türetince ikisi ayrı kalıyor: <c>unmeasured</c> ve <c>no_events</c>. Ters
 * yönde de doğru ve sınanabilir bir değişmez: <c>unreliable_ratio === null</c>
 * ise bant bu ikisinden biridir — sayaçlar okunabildiği sürece; okunamıyorsa
 * <c>unknown</c>, çünkü o hâlde iddia edecek bir şey yok.
 * </p>
 */
export type TrustBand =
  /**
   * Sunucu ölçmedi (<c>measured: false</c>). <b>"Sorun yok" değil,
   * "bilinmiyor".</b> Sayaçlar bu hâlde sıfır geliyor ve sıfırı temiz saymak,
   * ölçülmemiş bir pencereyi ölçülmüş ve tertemiz göstermek olurdu.
   */
  | "unmeasured"
  /**
   * Ölçüldü ama pencerede kapsam altında <b>hiç olay yok</b>
   * (<c>total_events === 0</c>). Ayrı bir üye çünkü <c>clean</c>'e düşerse
   * "boş pencerenin zamanı güvenilirdi" denmiş olur — ve boş pencerede koşan
   * RCA'lar panoda <b>başarılı koşum</b> gibi görünür. "İnsanlar boş pencereye
   * RCA koşturuyor" cevaplanabilir bir ürün sorusu; bu üye olmadan veride yok.
   */
  | "no_events"
  /** Ölçüldü, olay var, zamanı güvenilmez olan <b>yok</b>. */
  | "clean"
  /** Ölçüldü, olayların <b>bir kısmının</b> zamanı cihazdan gelmiyor. */
  | "mixed"
  /**
   * Ölçüldü ve <b>tamamının</b> zamanı güvenilmez. <c>mixed</c>'ten ayrı
   * çünkü rapor bu hâlde bir korelasyon değil bir sıralama tahmini; ayrımın
   * sınırı da uydurulmuş bir yüzde değil, <c>pay === payda</c>.
   */
  | "unreliable"
  /**
   * Sayılar okunamadı. <c>errorKind</c>'ın <c>unknown</c>'ı ile aynı gerekçe:
   * sınıflandırılamayan bir şeyi iyi görünen bir kovaya koymak, sessizce
   * yanlış saymaktır. <c>unmeasured</c>'a düşürmek de yanlış olurdu — o
   * "sunucu ölçmedim dedi" demek, bu "ekran okuyamadı".
   */
  | "unknown";

/**
 * <c>RcaTrustResponse</c> → <c>TrustBand</c>.
 *
 * <p>
 * <c>total_events</c> ve <c>unreliable_time_events</c> şemada
 * <c>number | string</c>: <c>int64</c> alanlar JSON'da dizgi inebiliyor
 * (aynı çevrim <c>rca/quality.ts</c> ve <c>honestyLines</c> içinde de var).
 * </p>
 */
export function trustBand(trust: RcaTrust): TrustBand {
  // Sıra önemli: "ölçmedim" diyen bir yanıtın sayaçları anlamsız, onlara
  // bakmadan çıkıyoruz.
  if (!trust.measured) {
    return "unmeasured";
  }

  const total = Number(trust.total_events);
  const unreliable = Number(trust.unreliable_time_events);

  if (!Number.isFinite(total) || !Number.isFinite(unreliable) || total < 0 || unreliable < 0) {
    return "unknown";
  }

  if (total === 0) {
    return "no_events";
  }

  if (unreliable === 0) {
    return "clean";
  }

  // `>=`, `===` değil: pay paydayı aşan bir yanıt tutarsız, ama onu `mixed`
  // göstermek "bir kısmı" demek olurdu — en iyimser okuma, en yanlış yerde.
  return unreliable >= total ? "unreliable" : "mixed";
}
