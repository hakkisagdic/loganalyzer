/**
 * Ekranın dört durumu — **tek yerde** (T13 kuralı, T28 denetimi).
 *
 * <p>
 * Bu fonksiyon `src/lib/changes/connector.ts` içinde doğmuştu; oraya ait
 * değildi. Değişiklik akışına özgü bir modülde durduğu için diğer ekranlar onu
 * bulamadı ve <b>her biri kendi sırasını yazdı</b> — oysa kırılgan olan tam
 * olarak sıra.
 * </p>
 */

export type ScreenState = "loading" | "error" | "empty" | "ready";

/**
 * Hangi durum gösterilecek.
 *
 * <p>
 * <b>Hata her şeyden önce geliyor.</b> Hata varken "kayıt yok" demek kullanıcıya
 * yanlış bilgi vermek olurdu: kayıt olabilir, yalnızca okunamadı.
 * </p>
 *
 * <p>
 * <b>Sıra, önceki hâlinden farklı ve bu bilinçli.</b> Eskiden ilk kontrol
 * <c>rows === null</c> idi, yani <c>screenState(null, "hata")</c> <c>loading</c>
 * dönüyordu: ilk yüklemesinde düşen bir ekran <b>sonsuza kadar iskelet</b>
 * gösteriyor ve hatayı hiç söylemiyordu. Çağıranların bunu <c>catch</c> içinde
 * <c>setRows([])</c> yazarak telafi etmesi gerekiyordu — yani doğruluk, herkesin
 * hatırlaması gereken bir kurala bağlıydı. Artık bağlı değil.
 * </p>
 */
export function screenState(rows: readonly unknown[] | null, error: string | null): ScreenState {
  if (error) {
    return "error";
  }

  if (rows === null) {
    return "loading";
  }

  return rows.length === 0 ? "empty" : "ready";
}
