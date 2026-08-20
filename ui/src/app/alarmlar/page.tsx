import { AlertsOverview } from "./AlertsOverview";

/**
 * Alarm ana ekranı: kural listesi ve tetiklenme geçmişi (T23).
 *
 * <p>Veri istemcide çekiliyor, çünkü tarayıcı `Bizigo.Api`'ye doğrudan değil
 * BFF vekilinden konuşuyor ve oturum çerezi orada `Authorization`'a
 * çevriliyor. Kimlik kapısı düzen (layout) katmanında.</p>
 */
export default function AlertsPage() {
  return <AlertsOverview />;
}
