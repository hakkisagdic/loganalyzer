# T28 ekran görüntüleri

Dört durum (dolu, boş, yükleniyor, hata) ve çok veri, **açık ve koyu temada**.
`ui/tests/screenshots/capture.test.tsx` üretiyor; `npx vitest run
tests/screenshots` ile yeniden alınıyor.

## Ne kanıtlıyor, ne kanıtlamıyor

**Kanıtlıyor:** bileşenlerin gerçek jetonlar ve gerçek CSS ile nasıl göründüğü —
çok dilli gövdelerin kırpılması ve hizalanması, rozet kontrastı, 500 satırlık
tablonun düzeni bozup bozmadığı.

**Kanıtlamıyor:** Next yönlendirmesi, kimlik akışı ve düzen birleşimi. Onlar
için sunucu + sahte Keycloak + sahte API gerekiyordu — üç uzun ömürlü proses ve
protokolün §3'ünün anlattığı risk. Uçtan uca akışlar T27'de.

## Bu görüntülerin yakaladığı, testlerin yakalayamadığı

| Bulgu | Neden test göremezdi |
| --- | --- |
| `DataTable` gövde hücresi tablo hücresi olmaktan çıkıyordu (`display: -webkit-box` bir `<td>`'ye uygulanınca satır ona göre boyutlanmıyor); uzun gövdenin son satırı tablonun alt kenarından **yarım** taşıyordu | HTML çıktısı doğru, kural doğru; bozulan şey **yerleşim** |
| Önem sütunu 7rem'di, "belirtilmemiş" kelime ortasından kırılıyordu | Metin HTML'de tam; kırılma tarayıcıda oluyor |

Ayrıca ilk koşumda sahneler ortak bileşen CSS'i olmadan alınmıştı: hücreler
ortalanmış, rozetler düz metin çıkmıştı. Test **yeşildi** — sayfanın boyanıp
boyanmadığına bakıyordu, doğru göründüğüne değil. Görüntülere bakılmasaydı o
hata da fark edilmezdi.
