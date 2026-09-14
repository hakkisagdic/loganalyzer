---
title: Paralel ajan koordinasyonu
category: skills
tags: [surec, test, yordam, bizigo]
aliases: [koordinatör protokolü, worktree düzeni]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: uses
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: related_to
sources: [CLAUDE.md, README.md]
source_digest: "sha256-12/v1 CLAUDE.md=d270c2c035f1 README.md=c8129c49b36e"
summary: Bu depoda iş bir koordinatör ve paralel uygulayıcı ajanlarla yürüyor. Test bölünmesi, worktree yaşam döngüsü ve birleştirme sırası ölçülmüş olaylardan doğdu.
provenance:
  extracted: 0.85
  inferred: 0.15
  ambiguous: 0.0
base_confidence: 0.71
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T16:15:22Z
updated: 2026-08-24T16:15:22Z
---

# Paralel ajan koordinasyonu

`CLAUDE.md`'nin tarif ettiği çalışma düzeni: **bir koordinatör ajan** ve
**paralel uygulayıcı ajanlar**. Her kural gerçekten yaşanmış bir olaydan doğmuş;
gerekçesi olmayan kural yok.

## Roller

- **Koordinatör** planlar, ticket böler, dalları birleştirir, ağır/Docker'lı
  doğrulamaları faz bitimlerinde kendisi koşturur. Kod yazması istisnadır.
- **Uygulayıcı ajan** tek bir ticket'ı kendi worktree'sinde yazar, kendi hafif
  testlerini koşturur, commit eder, raporlar. **Push etmez, birleştirmez.**

Koordinatör **önde boş durur**: uzun süren her şey arka plan prosesi olarak
başlar. Gerekçe ölçülmüş — bir oturumda dokuz dakikalık bir ölçüm turu
koordinatörün turunu kapattı ve o sırada **dört ajan boşta bekledi**.

## Test bölünmesi — pazarlığa açık değil

| Kim | Ne koşturur |
| --- | --- |
| Ajan | `dotnet build`, birim testleri, `npm run typecheck`, `npm test`, `api:generate`/`api:check` |
| Koordinatör | **Konteyner isteyen** entegrasyon testleri, compose, canlı Keycloak/ClickHouse/sidecar, benchmark'lar |

Gerekçe iki katmanlı: makine 16 GB ve beş paralel Testcontainers koşumu onu
swap'e sürüklüyor; ayrıca ölçüm testleri yüklü makinede **yanlış sayı**
üretiyor.

İkinci nokta bu depoda sayıyla kayıtlı: K35 ölçümü ajanda 1,46×, koordinatörde
1,62× çıktı ve ikinci koşumda *yalnız ayrıştırma* kolu *ayrıştırma+etiketleme*
kolundan yavaş göründü — fiziksel olarak imkânsız, yani makine sessiz değildi.

### Bölünmenin ekseni "hangi paket" değil "konteyner gerekiyor mu"

Tablo paket adına yazıldığı sürece, konteyner **istemeyen** bir entegrasyon
testi de ajana yasaktı — oysa maliyeti bir birim testininkiyle aynı ve
yukarıdaki iki gerekçenin ikisi de ona uymuyor.

Ekseni düzeltmek *"ajan karar versin"* demek değil: yargı çağrıları sessizce
genişler. Ölçüt **mekanizmaya** bağlı ve üç maddeli:

1. **Ajan bir entegrasyon testini yalnızca Docker kapalıyken koşturabilir.**
   Konteyner isteyen test daemon'a bağlanamayıp hemen düşer, hiçbir kaynak
   tüketmez. Docker'ı **açmak** hiçbir koşulda ajanın işi değil.
2. **Atlanan test kanıt değildir** — yalnızca **geçen** test konteynersiz
   sayılır. `3 geçti / 4 atlandı` sonucunda o dört test hakkında hiçbir şey
   söylenemez.
3. **Bu çıkarım [[concepts/sessiz-yanlis-davranis]]'a bağlı.** *"Geçti ⇒
   konteynersiz"* ancak beyansız atlama yasakken doğru: konteyner yokluğunu
   görüp `Skip` yerine erken `return` ile çıkan bir test *"geçti"* diye
   raporlanır ve 2. maddeden de temiz geçer.

Kuralın yan etkisi: *"konteyner gerekmiyor"* cümlesi artık bir **izin**
anlamı da taşıyor. Aynı cümle bu depoda iki anlamda daha kullanılıyor ve
karıştırılmaları yanlış çıkarım üretiyor — [[concepts/konteyner-gerekmiyor-uc-iddia]].

Üçüncü madde ilk ikisi kadar önemli. Yazılmasaydı mekanizma **kendi başına
ayakta duruyor** gibi görünürdü, ve dayandığı varsayım değiştiğinde kimse
buraya bakmazdı — bu, bir bekçinin dayanağını yazmamanın kural katmanındaki
karşılığı. ^[inferred]

**Koşturamadığın bir testi "yazdım" diye yeşil gösterme.** `Skip` ile iskelet
bırakmak dürüst; sahte yeşil değil. Bu, [[concepts/sessiz-yanlis-davranis]]
sınıfının rapor katmanındaki karşılığı. ^[inferred]

## Birleştirme sırası

**Önce yapısal değişiklik, sonra satır düzeyindekiler.** Bir ajan bir dosyayı
yeniden yazdıysa onun dalı önce girer; tersi olursa aynı iş iki kez yapılır.

**Üretilen dosyalar elle birleştirilmez.** `ui/openapi/bizigo-api.json` ve
`ui/src/lib/api/schema.d.ts` çakışırsa herhangi bir taraf alınır, sonra
`npm run api:generate` ile kaynaktan yeniden üretilir.

**Git'in göremediği çakışmalar var.** İki sınıf ölçülmüş:

1. Bir ajan arayüze üye ekler, başka bir ajanın test sahtesi onu uygulamaz →
   metinsel merge temiz, **derleme kırık**.
2. İki ajan compose'a ayrı ayrı `redis` servisi ekledi → merge temiz, derleme
   temiz, testler yeşil, ama YAML **hiç ayrıştırılamıyor**. Kırığı yalnızca
   yığını ayağa kaldırmayı deneyen görüyor.

İkinci olay bu depodaki en pahalı disiplin dersini de üretti: bekçi
(`docker compose config --quiet`) T01'den beri duruyordu ve ilk merge'de bağırdı
— **dört merge boyunca kimse bakmadı.**

> Bir kapının kırmızı yanması ile o kırmızının okunması ayrı olaylardır.

Kural: `git push`'un ardından `gh run list` ile koşumun sonucuna bak. Kırmızıysa
sıradaki ticket'ı verme. Aynı ders README'de kanca olarak da çivilenmiş —
`.githooks/pre-push` `main`'e push'tan önce önceki CI koşumuna bakıp kırmızıysa
durduruyor.

**Simetrisi de doğru ve ölçüldü:** `posthog-js` bir merge ile `package.json`'a
girdi, `node_modules`'a girmedi. **CI yeşildi** çünkü orada `npm ci` koşuyor;
yerelde çalışan herkes iki `tsc` hatası görüyordu. Yani CI temiz bir ortamı
ölçüyor — ve **kimsenin çalışmadığı** ortamı.

İkisi farklı soru soruyor: CI *"temiz bir makinede kurulur mu"*, yerel *"bu
makinede bugün çalışır mı"*. Birinin yeşili diğerinin yerine geçmiyor, ve
merge sonrası `npm install` bu yüzden bir alışkanlık değil bir kural (B16).

## Worktree yaşam döngüsü

1. Ticket verilirken worktree açılır.
2. İş `main`'e girip **doğrulandıktan** sonra worktree silinir.
3. Ajan yeni ticket'a geçerken **önce** yeni worktree'ye bağlanır, **sonra**
   eskisi silinir. Sıra tersine dönerse ajan çalışma dizinsiz kalır.
4. `git branch -d` kullanılır, `-D` değil — `-d` girmemiş commit varsa reddeder,
   bu bir bekçidir.

Worktree dizini ile içindeki dalın adı ayrışabilir; **dizin adına güvenme**,
`git worktree list` çıktısına bak.

## Ajanlar arası kesişim

- **Kesişen bir uç varsa sözleşmeyi önceden çivile**, sıraya sokma.
- **Aynı satırı iki ajana sildirme.** Uç sahipliğini ticket başlığından değil
  **kodun çizdiği sınırdan** türet.
- **İkinci kopya yazma.** Ortak yüzey varsa genişlet, kopyalama.
- **Bir ajanın sapması gerekçeliyse kabul et.**

## Açık sorular

- Yapılandırma dosyalarının (compose, CI YAML) birleştirme bekçisi hangi ajanın
  sorumluluğunda? §5 kırığın yalnızca yığını ayağa kaldıranca görüldüğünü
  söylüyor, ama koşturmayı koordinatöre veren §2 ile aradaki boşluk yazılı
  değil. ^[ambiguous]

## Kaynaklar

- `CLAUDE.md` §1–§5, §9 — roller, test bölünmesi, worktree, birleştirme
- `README.md` — "Hızlı başlangıç", `core.hooksPath` ve `pre-push` gerekçesi
- [[references/f2-kapanis]] — faz sonu doğrulama örüntüsü
