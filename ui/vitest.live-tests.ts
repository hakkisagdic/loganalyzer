/**
 * **Canlı bir bileşen isteyen testler** — tek liste, iki yapılandırmanın da
 * okuduğu yer (T27).
 *
 * <p>
 * F2'de aynı soruya iki farklı cevap veriliyordu ve ikisi hiç yan yana
 * konmamıştı:
 * </p>
 *
 * <table>
 *   <tr><td><c>redis-live.test.ts</c></td>
 *       <td><c>describe.skip</c> — <b>hiçbir zaman</b> koşmuyordu; Redis ayakta
 *           olsa bile. Koşturmak için dosyayı düzenlemek gerekiyordu.</td></tr>
 *   <tr><td><c>screenshots/capture.test.tsx</c></td>
 *       <td>Çıplak <c>chromium.launch()</c> — <b>her zaman</b> koşuyor; CI
 *           tarayıcıyı kuruyor, kurmasa kırmızı yanardı.</td></tr>
 * </table>
 *
 * <p>
 * İkisi de savunulabilir ama <b>aynı anda</b> savunulamaz: üçüncü bir canlı
 * test yazan kişi, hangisini gördüyse onu kopyalar. Protokol §7 üç hâlden
 * yalnızca ikisine izin veriyor — "CI'da o bileşenle koş" ya da "koşumdan
 * <b>açıkça</b> dışla". Yasak olan üçüncüsü: koşuma girip ortamı bulamamak.
 * </p>
 *
 * <h3>Bugünkü kural</h3>
 *
 * <ul>
 *   <li><b>CI'ın sağladığı bileşen</b> (chromium) → test koşulsuz koşar. Bileşen
 *       yoksa kırmızı yanar, ve bu doğru yön: kendini sessizce atlayan bir
 *       bekçi, bekçinin kendisinden tehlikeli.</li>
 *   <li><b>CI'ın sağlamadığı bileşen</b> (Docker'lı Redis — ajanlar Docker'a
 *       dokunmuyor, §2) → <b>yapılandırmada</b> dışlanır, dosyanın içinde
 *       değil.</li>
 * </ul>
 *
 * <p>
 * Dışlamanın <b>yapılandırmada</b> olması bu deponun ödediği bir bedelin
 * karşılığı: bir tur önce ekran görüntüsü testi için raporda "varsayılan pakete
 * koymadım" yazılmıştı, oysa <c>include</c> deseni dosyayı topluyordu. Niyet
 * dosyada, gerçek yapılandırmadaydı ve ikisinin ayrıştığını kimse okumadı.
 * Artık gerçek tek yerde ve <c>tests/test-config.test.ts</c> onu sınıyor.
 * </p>
 *
 * <p>
 * <c>describe.skip</c>'e göre kazanç: koordinatör dosyayı <b>düzenlemeden</b>
 * koşturabiliyor — <c>npm run test:live</c>. Düzenlenmesi gereken bir test,
 * koşturulmayan bir testtir.
 * </p>
 */
export const LIVE_TESTS = ["tests/redis-live.test.ts"] as const;
