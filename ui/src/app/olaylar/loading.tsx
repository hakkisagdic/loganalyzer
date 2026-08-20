import { AppShell } from "@/components/AppShell";
import { LoadingState } from "@/components/ui/States";

/**
 * Log arama ekranının yükleniyor sınırı.
 *
 * <p>
 * Bu ekran bir <b>sunucu bileşeni</b>: veri gelene kadar HTML hiç üretilmiyor.
 * Bu dosya olmadan kullanıcı, ClickHouse sorgusu sürerken <b>hiçbir geri
 * bildirim görmüyordu</b> — tarayıcı önceki sayfada bekliyor ve bir şeyin
 * çalışıp çalışmadığı anlaşılmıyordu. T28 denetimi bunu ölçtü: istemcide veri
 * çeken ekranlar iskelet gösteriyordu, sunucuda çekenler hiçbir şey.
 * </p>
 *
 * <p>
 * Kabuk burada da çiziliyor ki gezinme ve tema düğmesi kaybolmasın; yalnızca
 * içerik iskelete dönüşüyor.
 * </p>
 */
export default function Loading() {
  return (
    <AppShell>
      <h1>Log arama</h1>
      <LoadingState label="Olaylar yükleniyor" rows={8} />
    </AppShell>
  );
}
