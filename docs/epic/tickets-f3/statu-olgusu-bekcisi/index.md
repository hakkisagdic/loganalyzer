---
title: "T61 — Statünün gerçeğe bağlanması"
kind: ticket
status: 2
---

# T61 — Merge geçmişi, statünün beşinci gösterimi

[T53](../ticket-statusu-bekcisi/index.md) statüyü **kendi gösterimlerine**
bağladı ve sınırını kendi belgesine yazdı:

> Bu bekçi statünün **gerçeğe** uyduğunu sınamıyor… Hepsi birden yanlış olabilir
> ve bekçi yeşil yanar.

O sınır **ölçüldü ve gerçekleşti**. MCP kolunun dört ticket'ı `status: 0`
görünürken kodları main'deydi; T53'ün dört gösterimi **birbiriyle tamamen
tutarlıydı** ve bekçi yeşil yandı. Yani iç tutarlılık kapısı bu sınıfa yapısal
olarak kör: hepsi birden yanlış olduğunda yalanlayan bir gösterim kalmıyor.

T61 beşinci bir gösterim ekliyor ve o gösterim **depo dışından** geliyor.

## 1 · Ölçüm — bugünün hâli

`docs/epic/**/index.md`: **123 belge**, bunların **56'sı ticket**, **5'i story**,
gerisi `spec`/`review` (statü taşımıyorlar).

| Kutu | Sayı |
| --- | --- |
| Merge mesajı (`git log --merges`) | 179 |
| Bunlardan `Merge branch '<dal>'` biçiminde | 27 |
| Ayrıştırılabilir kimlik ön eki taşıyan | **24** |
| Kimliğin **iki farklı** ticket dizinine düştüğü çakışma | **0** |
| Merge edilmiş, ticket dosyasına bağlanabilen | 17 |
| **Merge edilmiş ama `status: 0`** | **4** (M02, M04, M05, M08) |
| **Merge edilmiş ama hiçbir tabloda anılmayan** | **1** (T56) |
| **`status: 0` story, altında bitmiş ticket** | **2** (`tickets-mcp`, `tickets-f4`) |

## 2 · Gerçeğin kaynağı — üç aday ölçüldü

Karar bu ticket'ın merkezi. Üçünün de bedeli farklı ve hiçbiri tam.

| Aday | Bedeli | Karar |
| --- | --- | --- |
| **Kodda bir sembolün varlığı** | Ticket → sembol eşlemesi **elle** yazılır | **Elendi.** Bayatlayan bir alanı, ikinci bir bayatlayan alanla ölçmek olurdu — T61 tam olarak bu sınıfı kapatmak için var |
| **Kabul kriterinin karşılığı** | Kriter metnini makineye okutmak | **Elendi.** T53 aynı gerekçeyle elemişti: *"ölçtüğü şeyden daha kırılgan bir tahmin"*. Kriterler düzyazı ve her ticket'ta farklı biçimde |
| **Merge geçmişindeki dal adı** | `git`'e bağımlılık | **Seçildi** |

Seçilenin tek başına yeterli olmasının sebebi, **yeni bir liste doğurmaması**:

```
Merge branch 'm02-komut-cekirdegi'
              └┬─┘ └──────┬──────┘
             kimlik    (kullanılmıyor)
                │
                ▼
   yol haritası tablosu: | M02 | [Komut çekirdeği…](komut-cekirdegi/index.md) |
                │
                ▼
   docs/epic/tickets-mcp/komut-cekirdegi/index.md → status:
```

Kimlik → ticket dosyası eşlemesi **zaten** yol haritası tablolarında yazılı ve
T53 onu zaten okuyor. T61 iki mevcut olguyu birleştiriyor; elle tutulan hiçbir
yeni alan doğmuyor.

**Dal adının gövdesi bilerek kullanılmıyor.** `t44-llm-adimlari` dizini
`llm-adimlari-ve-iki-kapi`, `t41-redaksiyon-tabani` dizini
`prompt-redaksiyon-tabani`, `t48-produces-kapisi` dizini
`produces-kapisi-bagi` — üçü de yalnızca ön ek/son ek düzeyinde örtüşüyor.
Bulanık ad eşleştirmesi bu üçünü kurtarırdı ve karşılığında **yanlış ticket'ı
suçlama** riskini alırdı. Yol haritası tablosuna bırakmak, kimliği çözen tek
kaynağı tek yerde tutuyor.

### T53 git'i elemişti — neyi elediği önemli

T53'ün gerekçesi *"commit'lerin çoğu ticket kimliği taşımıyor"* idi ve
**doğruydu**. Ama ölçtüğü şey **commit mesajlarının gövdesiydi**, merge dal
adları değil. Dal adı bu depoda bir konvansiyon değil bir **zorunluluk**:
worktree adı dal adı, dal adı da ticket kimliğiyle başlıyor. 24 kimlikte sıfır
çakışma bunun ölçümü.

Bir eleme gerekçesi, elediği şeyin **hangi biçimini** ölçtüğünü yazmadığında
kapanmış bir kapı gibi okunuyor. Bu, T53'ün kendi listesinde iki kez görülen
maskeleme deseninin üçüncü örneği.

## 3 · Üç kapı, ve neyi mekanik saydıkları

| Kapı | Soru | Bugünkü kırmızı |
| --- | --- | --- |
| `Birlestirilmis_bir_is_baslamamis_gorunmuyor` | Merge edilmiş bir dalın ticket'ı `status: 0` olabilir mi | M02, M04, M05, M08 |
| `Birlestirilmis_her_kimlik_bir_tabloda_aniliyor` | Main'e girmiş bir kimlik hiçbir tabloda anılmıyor olabilir mi | T56 |
| `Isi_baslamis_bir_story_baslamamis_gorunmuyor` | Bitmiş çocuğu olan bir story `status: 0` olabilir mi | `tickets-mcp`, `tickets-f4` |

### `1` ile `2` arasına dokunulmuyor — ve bu kapıların şeklini belirliyor

Üç kapının üçü de yalnızca **`0`**'ı reddediyor. Gerekçe ölçülebilir bir olgu:
merge edilmiş bir dal işin **başladığını** kanıtlıyor, **bittiğini** değil. Bugün
iki ticket tam olarak bu hâlde ve **ikisi de doğru**:

- **M03** (`status: 1`) — `m03-sim-araclari` merge edildi, ticket'ın §6.1'i
  uygulamada çıkan üç soruyu ayrıca yazdı.
- **T32** (`status: 1`) — `t32-sigma-derleme` merge edildi; açık olan **duvar
  saati ölçümü** ve o koşum koordinatörde.

`0 → 1` mekanik, `1 → 2` insan kararı. İkincisini de mekanikleştirmek, bekçinin
ölçemediği bir şeyi ölçüyormuş gibi göstermek olurdu — ve bu depoda
*"ölçemedim"* ile *"sorun yok"*un aynı çıktıya inmesi adı konmuş bir sınıf.

### Story kapısı T53'ün elediği yön değil

T53 *"bütün çocukları `2` olan story `2` olmak zorunda değil"* demişti ve bu
doğru: henüz yazılmamış ticket'ları olabilir, o yönü sınamak yazılmamış işi
"yok" saymak olurdu. T61'in çıkarımı **ters yönde** ve yazılmamış işe hiç
dokunmuyor: bir çocuğu bitmişse story'nin `0` olması, **var olan** bir olgunun
inkârı.

## 4 · Bekçinin yakalayamadıkları — üçü de ölçüldü

Yazılmayan bir sınır, kapı varmış gibi okunuyor.

### 4.1 · Dal adı olmayan merge — **ve bugün bir örneği var**

`Merge M08 and M06, bind the scope seam…` gibi anlatısal merge mesajları kimlik
taşımıyor. Daha sinsi hâli: **bir ticket'ın işi başka bir ticket'ın dalında
merge edilebiliyor.** T54 (`model-muafiyeti-kaydi`) tam olarak böyle girdi —
main'e `Merge branch 't44-llm-adimlari'` altında (`d718ca7`). Yani T54 de
`status: 0` ile bayattı ve **bu bekçi onu görmedi**.

Kapı yalnızca **eksik** yönde yanılıyor: göremediği iş için sessiz kalıyor,
yanlış bir ticket'ı suçlamıyor. Ama sessizliğin bedeli gerçek ve bugün ölçülü.

### 4.2 · Düzyazı okunmuyor

`kalan-is-raporu`'nun MCP bölümü bugün *"sekiz kalem"* diyor ve kalemleri
**tablo değil düzyazı** olarak sayıyor. `ReportedOpen()` tablo satırı arıyor,
dolayısıyla o bölüme kör — F3/F4/F2/FS bölümleri tablo, MCP bölümü değil.

Düzyazıdaki kimlikleri eşleştirmek **denendi ve elendi**: aynı paragraf kapanan
ticket'ları da anıyor (*"T38 ve T48 bu turda kapandı"*), yani "açık" ile
"kapandı" ayırt edilemiyordu. Bir kapının yanlış suçlaması, suçlamamasından
kötü.

### 4.3 · Belgede adı geçen ama dosyası olmayan ticket — **kapsamda değil, karar**

`kapasite-olcumu` beş yeni ticket adlandırıyor (B01–B05) ve **hiçbirinin ticket
dosyası yok**. Bekçi bunu yakalamıyor ve yakalamaması bir karar:

| Hâl | Bekçi | Neden |
| --- | --- | --- |
| Kimlik anılmış, dosya yok, **merge edilmemiş** | Sessiz | Plan yazıldı, iş başlamadı. Ticket dosyası istemek, bir planın **dilimleme önerisini** dosya yazma zorunluluğuna çevirirdi |
| Kimlik anılmış, dosya yok, **merge edilmiş** | **Kırmızı** | İş oldu ve hiçbir yerde kayıtlı değil |

İkinci sebep `kapasite-olcumu`'nun kendi içinde: §6'nın üç açık sorusu
(hedef ortam, `auto`'nun sürdürülebilirlik ölçütü, üretecin nereden koşacağı)
cevaplanmadan B01–B05'in kabul kriterleri yazılamaz. Bekçi onları yazmaya
zorlarsa üretilen şey ticket değil **yer tutucu** olur.

Kapı ilk `b01-*` dalı merge edildiği gün konuşmaya başlıyor. Yani kapsamı,
bağlandığı olguyla birlikte **kendiliğinden** büyüyor — genişletmek için kimsenin
bir listeye satır eklemesi gerekmiyor.

### 4.4 · Ölçülmeyen: statünün *fazla* iddialı olması

Üç kapı da tek yönlü: `0` iken iş var olduğunu yakalıyor. Tersi — **`2` yazılmış
ama iş yok** — hiç sınanmıyor, çünkü *"iş yok"* gözlenebilir bir olgu değil.
Aradım ve elemedim: dalı hiç merge edilmemiş `status: 2` bir ticket bugün
**yok**; ama bu bir bekçi değil bir ölçüm, ve yarın olabilir.

## 5 · `EpicStatusTests` genişletildi, ayrı bekçi yazılmadı

Ölçülen: T61'in üç kapısının ihtiyaç duyduğu dört okuyucudan **üçü** T53'te
zaten var (ticket belgeleri + `status`, yol haritası kimlik→dizin eşlemesi,
story ağacı). Ayrı bir dosya bu üçünü **kopyalardı**, ve ayrışmaları tam olarak
bu bekçinin var olma sebebi (§9: *ikinci kopya yazma*).

Karşı bedel ödendi, gizlenmedi: sınıf artık **git'e** bağımlı ve arıza biçimi
markdown ayrıştırmasından tamamen farklı. Yüzeysel bir klon, ihracat edilmiş bir
ağaç ya da merge mesajı biçiminin değişmesi kümeyi boşaltıyor ve **üç kapı
birden** sessizleşiyor. Bu yüzden `Bekci_bos_kume_uzerinde_donmuyor` iki yeni
iddia kazandı: ayrıştırılabilir kimlik sayısı sıfırdan büyük, **ve** en az bir
kimlik bir yol haritası satırına bağlanabiliyor.

`git` çağrı kabuğu `CiCoverageTests`'te zaten vardı; kopyalanmak yerine
`Git.Lines`'a taşındı ve iki bekçi de oradan okuyor.

## 6 · Kırmızı ölçüldü — enjekte edilmeden

Üç kapı da **gerçek ağaç üzerinde** kırmızı yandı: dört bayat ticket statüsü,
bir kayıtsız kimlik, iki bayat story. §6'nın istediği kanıt burada enjeksiyondan
güçlü — kusur uydurulmadı, **zaten oradaydı** ve iki tur boyunca kimse görmedi.

Statüler düzeltildikten sonra kapıların hâlâ kırmızı yanabildiği ayrıca ölçüldü
(kusur enjeksiyonu + geri alma + tam paket).

## 7 · Düzeltilen bayat alanlar

Düzeltme bekçiden **sonra** yapıldı; ölçülü kırmızı bedavaydı.

| Belge | Önce | Sonra | Gerekçe |
| --- | --- | --- | --- |
| `tickets-mcp/komut-cekirdegi` (M02) | 0 | **2** | `1be1fbd` → `52a6f82`; kabul kriterleri raporda madde madde karşılandı |
| `tickets-mcp/okuma-araclari` (M04) | 0 | **1** | Dalı main'de; kriterlerinin tamamının kapandığı **bu ajan tarafından ölçülmedi** |
| `tickets-mcp/rca-araclari` (M05) | 0 | **1** | Aynı gerekçe |
| `tickets-mcp/kimlik-tasima` (M08) | 0 | **1** | Aynı gerekçe |
| `tickets-f4/model-muafiyeti-kaydi` (T54) | 0 | **1** | `d718ca7`'de main'de; `kalan-is-raporu` hâlâ açık sayıyor (`rca_runs` tarafına bakılmadı) |
| `tickets-mcp` (story) | 0 | **1** | M07 açık |
| `tickets-f4` (story) | 0 | **1** | T47 ve T54 açık |

**`2` yerine `1` seçilmesi bir tereddüt değil bir sınır.** Bu ajan M04/M05/M08'in
kabul kriterlerini okumadı ve koşturmadı; `2` yazmak ölçmediği bir şeyi iddia
etmek olurdu. `1` ise merge geçmişinin **kanıtladığı** şey. `1 → 2` kararı
koordinatörde ve bekçi o kararı zorlamıyor.

## 8 · T56 — kayıtsız iş

`t56-vault-iddia-denetimi` main'e girdi (`76d86d7`, sekiz vault sayfası) ve
kimliği `docs/epic` altındaki **hiçbir tabloda** geçmiyordu. Ticket dosyası da
yok.

Bir **bağsız satır** eklendi (F3'ün *Kapı ticket'ları* bölümüne), ticket dosyası
yazılmadı. Gerekçe: T56'nın işi tamamen `docs/wiki/` içinde ve bu ajan onun kabul
kriterlerini bilmiyor — bir ticket dosyası yazmak, içeriğini uydurmak olurdu.
Satır ise yalnızca *"bu iş oldu"* diyor, ki olgunun kendisi.

Aynı bölüme T61'in satırı da eklendi.

## 9 · Açık bırakılanlar

- **T54'ün bulunamaması yapısal.** Bir ticket'ın işi başka bir ticket'ın dalında
  merge edildiğinde kimlik kayboluyor. Çözümü dal adı değil **commit mesajı
  konvansiyonu** olurdu (`T54: …`) ve bu bir proses kararı, kod değil —
  koordinatörde.
- **`kalan-is-raporu`'nun MCP bölümü düzyazı.** Bu turda metni düzeltildi ama
  **biçimi** düzelmedi: bir sonraki bayatlama yine görünmez olacak. Tabloya
  çevirmek bekçiyi oraya da uzatır; ölçülmedi, karar verilmedi.
- **`spec` belgeleri statü taşımıyor** ve taşımamaları doğru; ama
  `kapasite-olcumu` gibi bir `spec` iş kalemi adlandırdığında o kalemlerin
  akıbetini hiçbir şey izlemiyor. §4.3'ün kararı bunu bilerek açık bırakıyor.
