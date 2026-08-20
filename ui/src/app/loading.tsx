import { AppShell } from "@/components/AppShell";
import { LoadingState } from "@/components/ui/States";

/**
 * Genel bakış ekranının yükleniyor sınırı.
 *
 * <p>
 * Bu sayfa da bir sunucu bileşeni ve <c>/auth/me</c>'yi bekliyor. Kısa bir
 * çağrı ama API ulaşılamazken saniyelerce sürebiliyor; sınır olmadan kullanıcı
 * o sürede hiçbir geri bildirim görmüyordu.
 * </p>
 *
 * <p>
 * <b>Kök sınırı iç içe rotaları kurtarmıyor:</b> Next açısından kapsıyor, ama
 * <c>ui-consistency.test.ts</c> onu yalnızca bu rotanın sınırı sayıyor. Her
 * ekran kendi geri bildirimini tanımlamak zorunda — kökten miras almak, bir
 * ekranın sınırının unutulduğunu görünmez kılardı.
 * </p>
 */
export default function Loading() {
  return (
    <AppShell>
      <h1>Yükleniyor</h1>
      <LoadingState label="Kimlik doğrulanıyor" rows={3} />
    </AppShell>
  );
}
