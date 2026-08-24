---
title: Wiki Index
---

# Wiki Index

*Dizin taranarak üretildi. Son güncelleme: 2026-08-24T18:06:54Z — 40 içerik sayfası.*

Kapsam kararı, bayatlama bekçisi ve Obsidian kurulumu: [[README]]. Etiket sözlüğü: [[_meta/taxonomy]]. Vault'un bugünkü anlık görüntüsü: [[hot]]. İşlem kaydı: [[log]].

Nereden başlanır: [[projects/bizigo-loganalyzer/bizigo-loganalyzer]] projeyi anlatıyor, [[concepts/sessiz-yanlis-davranis]] ise vault'un geri kalanının etrafında döndüğü fikir.

## Concepts — kavramlar (24)

Fazların arasından geçen, birden çok belgede tekrarlanan fikirler.

### F1 · ham veri ve boru hattı

- [[concepts/f1-duvar-saati-tuzagi]] — F1'de beş yerde çıkan aynı hata; belirtisi hep "test kararsız"dı ama ölçülen şey makinenin o anki hızıydı.  
  <sub>#test #surec #kavram #bizigo</sub>
- [[concepts/f1-gecmis-biriktiren-sey-ertelenmez]] — Faz sıralamasının ölçütü kolaylık değil: geçmiş biriktirmek zorunda olan şey ilk faza girer, biriktirmeyen ertelenir.  
  <sub>#mimari #surec #kavram #bizigo</sub>
- [[concepts/f1-ham-sadakat-zinciri]] — Cihazdan replay'e uzanan bayt zinciri; bir halka koparsa tamamı değersiz oluyor ve kopuş hiçbir belirti üretmiyor.  
  <sub>#log-analiz #veri-deposu #kavram #bizigo</sub>
- [[concepts/f1-kapsam-kaynaktan-gelir]] — Kapsam olayın değil kaynağın özelliği ve tek kapıdan geçiyor — K17'nin iki cümlesi F1'in yedi ticket'ına yayılıyor.  
  <sub>#guvenlik #mimari #kavram #bizigo</sub>
- [[concepts/f1-sema-karari-bir-kez-verilir]] — Sıralama anahtarı, tam metin indeksi ve OCSF türetmesi geri alınması pahalı kararlar; üçü de ölçümle bağlandı ve F2'yi kısıtladı.  
  <sub>#veri-deposu #log-analiz #kavram #bizigo</sub>

### F2 · görünürlük

- [[concepts/f2-bff-deseni]] — Oturum Next.js sunucusunda; erişim token'ı tarayıcıya hiç geçmiyor ve bunun tek kanıtı yanıtın her baytını tarayan bekçi.  
  <sub>#arayuz #guvenlik #mimari #kavram #bizigo</sub>
- [[concepts/f2-degisiklik-beslemesi]] — Üç değişiklik kaynağı daraltılmak yerine sıralandı; değeri bugün değil F3'te görünüyor ve geçmişe dönük üretilemiyor.  
  <sub>#mimari #guvenlik #kavram #bizigo</sub>
- [[concepts/f2-kapsam-tek-kapi]] — Yedi ekran ve üç alt sistem kapsamı kendi uygulamıyor; tekrarlayan kusur şekli her zaman filtrenin ikinci bir kopyası.  
  <sub>#guvenlik #mimari #kavram #bizigo</sub>
- [[concepts/f2-olculen-kisit-ekrani-tasarlar]] — F1'de ölçülen iki sayı arama ekranının tasarımını doğrudan belirledi; ekran ölçümü gizlemiyor, kullanıcıya sayıyla söylüyor.  
  <sub>#arayuz #veri-deposu #test #kavram #bizigo</sub>
- [[concepts/f2-sessizlik-alarmi]] — Üç alarm tipinin ikisi verinin varlığı, biri yokluğu üzerinde çalışıyor — sessizlik hem en zor hem en değerli olanı.  
  <sub>#log-analiz #mimari #kavram #bizigo</sub>

### F3 · detection ve kanıt

- [[concepts/f3-bosluk-tek-cins-degildir]] — "Bir şey yok" diyen farklı olgular aynı boş kutuya düşerse okuyucu iyimser yanılır; faz boyunca en az yedi kez ayrı ayrı kuruldu.  
  <sub>#test #mimari #kavram #bizigo</sub>
- [[concepts/f3-determinizm-bir-kapi-sartidir]] — İki ilgisiz alt sistem aynı kurala vardı: karşılaştırılan çıktıya duvar saati ya da sıra belirsizliği karışırsa kapı ya kalkar ya yumuşatılır.  
  <sub>#test #veri-deposu #kavram #bizigo</sub>
- [[concepts/f3-kanit-once-akil-sonra]] — RCA'nın tek gerçek riski inandırıcı ama yanlış rapor; kanıt LLM'siz üretiliyor, saklanıyor ve raporun her dürüstlük satırı mekanizmaya bağlanıyor.  
  <sub>#mimari #log-analiz #kavram #bizigo</sub>
- [[concepts/f3-oranin-paydasi]] — Aynı Sigma kapsam oranı beş koşumda %0 ile %43 arasında çıktı; hiçbiri yanlış hesaplanmadı, hepsi farklı payda kullandı.  
  <sub>#test #log-analiz #kavram #bizigo</sub>

### Ölçüm kültürü

- [[concepts/olcum-capraz-eksen]] — Aynı soruya ortak varsayım paylaşmayan iki yoldan bakmak; bir eksenin sistematik sapması ancak öbür eksenden görünüyor.  
  <sub>#test #log-analiz #kavram #bizigo</sub>
- [[concepts/olcum-duvar-saati]] — Mutlak süre bütçesi pattern'in davranışını değil makinenin o anki hızını ölçer; bu depoda üç kez oldu, biri ürünün kendisine sızdı.  
  <sub>#test #surec #kavram #bizigo</sub>
- [[concepts/olcum-gerekcesiz-sabit]] — Ticket belgelerindeki "gerekçesi kayıtta yok" tablosunun bedeli T04'te ölçüldü: birbirine bağlı üç sayı birbirinden habersiz seçilmiş.  
  <sub>#surec #mimari #kavram #bizigo</sub>
- [[concepts/olcum-kirmizi-yanamayan-sayi]] — Ölçülen ama hiçbir eşikle karşılaştırılmayan sayı, ölçülmemişten yalnızca biraz iyi; sayaç, eşik ve kapı üç ayrı karar.  
  <sub>#test #surec #kavram #bizigo</sub>
- [[concepts/olcum-sayi-kapsam-degil]] — Test sayısı kaç kararın sınandığını söylemez; paydasını yayımlamayan bir oran üç farklı karara çıkabiliyor.  
  <sub>#test #surec #kavram #bizigo</sub>

### Kapsam, sınır ve durum

- [[concepts/kapsam-kestirme-besleme-yolu]] — Doğrudan ClickHouse'a yazan seed canlı yolu ölçüsüz bıraktı; ilk gerçek syslog koşumu 385 satırın 372'sinin sessizce kaybolduğunu gösterdi.  
  <sub>#log-analiz #veri-deposu #kavram #bizigo</sub>
- [[concepts/kapsam-olcumun-sinirini-yazmak]] — Neyi kanıtlamadığını söylemeyen bir ölçümde "ölçemedim" ile "sorun yok" aynı çıktıya iniyor.  
  <sub>#test #log-analiz #kavram #bizigo</sub>
- [[concepts/kapsam-yazmama-karari-ve-kayan-sinir]] — Pazar araştırması bir kod bütçesi çizdi; iki kalem tuttu, iki kalem tutmadı ve sınırın nerede kaydığı ölçülebilir durumda.  
  <sub>#mimari #log-analiz #kavram #bizigo</sub>

### Faz üstü

- [[concepts/elle-tutulan-liste-bekciyi-korlestirir]] — Denetleyeceği kümeyi elle listeden toplayan bekçi listede olmayanı hiç görmez; F2'de aynı kalıp beş kez yeşil yandı.  
  <sub>#test #surec #kavram #bizigo</sub>
- [[concepts/sessiz-yanlis-davranis]] — Hata, sayaç ve belirti üretmeden yanlış sonuç veren davranış — bu deponun en pahalı hata sınıfı ve vault'un en çok bağlanan düğümü.  
  <sub>#surec #test #kavram #bizigo</sub>

## Skills — yordamlar (10)

Nasıl yapılır bilgisi; her biri birden çok ticket'tan damıtıldı.

### F1 · ham veri ve boru hattı

- [[skills/f1-deklaratif-parser-motoru]] — Kod yazmadan log formatı eklenebilmesi için alınan kararlar ve gerçek vendor logunun bu kararlarda açtığı on yer.  
  <sub>#log-analiz #mimari #yordam #bizigo</sub>
- [[skills/f1-veri-kaybeden-depoyla-tasarim]] — Replay'in tek kaynağı olgunlaşmamış bir nesne deposuysa tasarım veri kaybını varsaymak zorunda; beş koruma, en değerlisi manifest.  
  <sub>#veri-deposu #test #yordam #bizigo</sub>

### F2 · görünürlük

- [[skills/f2-atomik-yayin-akisi]] — Parser'ı üründe yazıp yayınlama yordamı: zorunlu kapılar, atomik referans değişimi ve kataloğun ikiye çıkan kaynağı.  
  <sub>#log-analiz #mimari #yordam #bizigo</sub>
- [[skills/f2-ekran-tutarliligi]] — Görsel tutarlılık kasten ikiye bölündü — jetonlar başta, denetim sonda; denetim toparlamaya dönüyorsa temel eksik yapılmış demektir.  
  <sub>#arayuz #test #yordam #bizigo</sub>

### F3 · detection ve kanıt

- [[skills/f3-eslesmeyen-kural-teshisi]] — Sıfır satır dönen bir kuralın sebebi en az beş farklı şey olabilir ve tabloda hepsi aynı görünür; üç bağımsız eksen sebebi ayırıyor.  
  <sub>#log-analiz #test #yordam #bizigo</sub>
- [[skills/f3-sigma-derleme-kapilari]] — "Derlendi", "koşuyor" ve "doğru şeyi buluyor" üç ayrı iddia; her biri ayrı yerde sınanıyor çünkü tek yer bir sınıfı sessizce geçiriyor.  
  <sub>#test #veri-deposu #yordam #bizigo</sub>

### Ölçüm kültürü

- [[skills/olcum-bekciyi-kirmizi-yakmak]] — Geçen bir test geçtiğini kanıtlar, kırılabildiğini değil; bekçiler koruduğu hata geri konularak sınanıyor.  
  <sub>#test #surec #yordam #bizigo</sub>
- [[skills/olcum-protokolu-sonuctan-once]] — Hangi sayının hangi kararı vereceği ölçüm koşmadan önce bağlanıyor — sonuç geldikten sonra gerekçe uydurulmasın diye.  
  <sub>#test #surec #yordam #bizigo</sub>

### Kapsam, sınır ve durum

- [[skills/kapsam-kapanacak-ile-kapanmayacagi-ayirmak]] — Kapanacak kalemle hiç kapanmayacak kalem aynı listedeyse liste asla boşalmaz ve "bitti mi" sorusu cevapsız kalır.  
  <sub>#surec #test #yordam #bizigo</sub>

### Faz üstü

- [[skills/paralel-ajan-koordinasyonu]] — Koordinatör + paralel ajan düzeni: test bölünmesi, worktree yaşam döngüsü ve birleştirme sırası — her kural yaşanmış bir olaydan.  
  <sub>#surec #test #yordam #bizigo</sub>

## References — kaynak özetleri (5)

Tek bir belge kümesinin sıkıştırılmış hâli.

- [[references/f2-kapanis]] — F2 kapanış belgesinin özeti: ölçülen kısıtlar, yanlış çıkan altı iddia, bekçilerin durumu ve F3'e devredilen beş soru.  
  <sub>#surec #test #kaynak-ozeti #bizigo</sub>
- [[references/f3-detection-ve-rca-kaniti]] — F3'ün iki kolu, planı yarı yarıya değiştiren `template_id` bulgusu ve on ticket'ın bugünkü durumu.  
  <sub>#log-analiz #mimari #kaynak-ozeti #bizigo</sub>
- [[references/graphify-depo-bilgi-grafigi]] — Kod grafını tree-sitter AST'siyle LLM'siz üreten yerel araç; 8.280 düğüm, 18.892 kenar, sıfır token maliyeti.  
  <sub>#arac #mimari #kaynak-ozeti #bizigo</sub>
- [[references/kapsam-feature-parity-matrisi]] — 2026-08-14 parity matrisi bugünkü envanterle karşılaştırıldı: hangi MVP indi, hangi v2 erken geldi, hangi Diff hâlâ kanıtsız.  
  <sub>#log-analiz #mimari #kaynak-ozeti #bizigo</sub>
- [[references/kapsam-nerede-kalindi]] — İki devir notu ile envanterin birlikte çizdiği durum fotoğrafı — faz durumu, devreden borç, gitmeyen iki mesaj.  
  <sub>#surec #log-analiz #kaynak-ozeti #bizigo</sub>

## Projects — proje (1)

- [[projects/bizigo-loganalyzer/bizigo-loganalyzer]] — Plugin tabanlı, çok formatlı log analiz platformu; F1 ve F2 kapandı, F3 ölçüm ağırlıklı faz.  
  <sub>#log-analiz #mimari #proje #bizigo</sub>

## Entities

*Henüz sayfa yok.*

## Synthesis

*Henüz sayfa yok.*

## Journal

*Henüz sayfa yok.*
