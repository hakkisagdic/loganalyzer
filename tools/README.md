# `tools/`

Depo içi araçlar. Ürünün parçası **değil** — ölçüm ve bekçi yardımcıları.

## `machine-resources.sh` — kaynak kapısı

`CLAUDE.md` §3'ün işaret ettiği kapı: ağır bir iş (derleme, test paketi,
konteyner) başlatmadan önce makinenin taşıyıp taşıyamayacağını söylüyor.

```bash
./tools/machine-resources.sh check    # çıkış 1 ise BAŞLAMA
```

### Neden depoda — ve neden geç eklendi

Betik uzun süre yalnızca `~/.claude/scripts/` altında durdu. Depo ona **beş
yerde** atıf yapıyordu ve **içermiyordu**: yani taze bir klonda §3'ün işaret
ettiği dosya yoktu, ve bunu bir ajan sorarak buldu (*"betiğin depoda olmaması
bilinçli mi, bilmiyorum"*). Bir protokol kuralının tek kişinin ev dizinine
bağlanması, o kuralın **tek makinede var olması** demek.

Kopya **birebir** — değiştirilmedi. Değiştirmek yükseltmesini bir merge işine
çevirirdi; `catalog/patterns/` ile aynı gerekçe.

### ⚠ Bugün YALNIZCA macOS'ta doğru ölçüyor — ve Linux'ta sessizce yanlış

Her okuyucusu BSD/macOS'a özgü. Ölçüldü:

| Ne | Nasıl okuyor | Linux'ta ne oluyor |
| --- | --- | --- |
| Bellek | `memory_pressure` | Komut yok → **`100`** basıyor, yani *"%100 boş"* — kapı **her zaman geçiyor** |
| Disk | `df -g "$HOME"` | GNU `df` `-g` tanımıyor → **`0`** GiB, yani kapı **her zaman engelliyor** |
| Swap | `sysctl -n vm.swapusage` | Yok → boş |
| Sayfalama | `vm_stat` `Swapins` | Yok → boş |

İkisinin yönü **zıt** ve ikisi de sessiz: bellek kapısı yanlış yeşil, disk kapısı
yanlış kırmızı. Yani bu betik bir Linux makinesinde koşarsa ürettiği sayı bir
ölçüm değil.

**Bu bir kapanmamış kalem, kapanmış bir karar değil.** Kurulum hedefi Linux ve
Windows; kapı orada da anlamlı olmalı. Düzeltmesi platform başına okuyucu
(`/proc/meminfo`, `df -BG`, `/proc/vmstat` `pswpin`) ve **ölçülmeden yazılmamalı**
— tahminle yazılmış bir kaynak kapısı, ölçtüğünü sandığı şeyi ölçmeyen bir
kapıdır ve bu deponun en pahalı sınıfı orası.

### Durum kaydı makinede, depoda değil

`claim` / `release` durumu `~/.claude/machine-resources.d` altında tutuluyor ve
öyle kalması doğru: o **makinenin** anlık hâli, deponun değil. İki ajan aynı
makinede koşuyorsa aynı kaydı görüyor; farklı makinelerde birbirine karışmıyor.

## `*-kirmizi-olcumu.py`

Bekçilerin **kırmızı yanabildiğini** ölçen betikler (§6). Her biri bir kusuru
dosyaya yazıyor, kusurun gerçekten orada olduğunu **okuyup iddia ediyor**,
testi koşturuyor ve yedekten geri alıyor.

Geri almanın `git checkout` ile yapılmaması bilinçli: commit edilmemiş işi ezer.
`copy2` yerine `copy` + `touch` da bilinçli — `copy2` zaman damgasını da
kopyalıyor ve MSBuild değişikliği görmüyor, yani "geri alındı" denen ağaç
derlemeye hiç ulaşmıyor.

## Ölçülen üçüncü sessizlik: bellek kapısı, VM'i öldüren baskıyı görmüyor

Beş ajan paralel `dotnet build`/`test` koşarken **Docker Desktop'ın VM'i on dakika
içinde iki kez öldü**, ve `check` her iki seferden önce de **yeşildi**. Ölçülen hâl:

| Kapının okuduğu | Makinenin gerçek hâli |
| --- | --- |
| `memory_pressure`: **%40 boş** (taban %15 → geçer) | **0.06 GiB** boş / 16 GiB |
| `swapin_rate`: düşük | takas **17.4 / 18.4 GB** dolu, sıkıştırılmış **7.2 GiB** |

İki okumanın ikisi de yanlış değil, ikisi de **başka bir soruya** cevap veriyor:

- `memory_pressure`'ın *free percentage*'ı **geri kazanılabilir** sayfaları boş
  sayıyor. Çekirdek *"istenirse sayfa bulurum"* diyor; sorulan soru ise *"bir
  VM'e ayıracak birkaç GiB var mı"*.
- `swapin_rate` **sürmekte olan** takası yakalıyor. Her şeyi çoktan diske yazmış
  ve orada duran bir makine **düşük anlık hız** gösteriyor — thrash bitmiş,
  sonucu duruyor.

Docker Desktop'ın kendi logu ölümü onaylıyor:
`unable to accept vfkit connection: invalid magic length: 0/4` — VM süreci
gitmiş, arka uç ona bağlanmaya çalışıyor.

**Eşik BİLEREK değiştirilmedi.** Betiğin kendi yorumu takas yüzdesini niye
eşiklemediğini yazıyor ve gerekçesi duruyor: *"used" bir yüksek-su işareti,
saatler önce zorlanmış bir makine boştayken de %93 okuyor, ve hep kırmızı yanan
bir kapı herkesin görmezden gelmeyi öğrendiği kapıdır.* Buraya bir sayı yazmak,
**doğrulanmamış bir kapı** eklemek olurdu — bu deponun bütün gün kataloglayıp
durduğu hatanın kendisi.

**Açık kalem — kendi ölçümünü istiyor:** hangi sayı bu hâli yakalar ve boş bir
makinede yanlış kırmızı **üretmez**? Aday eksen sıkıştırılmış sayfaların RAM'e
oranı (burada 7.2/16 ≈ %45), ama bir eşik önerilmeden önce boş ve yüklü
makinelerde ölçülmesi gerekiyor.

### Sebep ölçüldü ve ilk atıf YANLIŞTI

Buraya önce *"beş paralel ajan ile container ölçümü bir arada durmuyor"* yazıldı.
**Ölçüm bunu çürüttü:** ajanların bütün `dotnet` ve `node` süreçleri toplam
**0.84 GiB**. Docker öldüğünde beş ajan koşuyordu ve korelasyon nedensellik
sayıldı — bu dosyanın kataloglamak için var olduğu hatanın kendisi.

Gerçek sebep bir **ayırma**:

| Tüketici | |
| --- | --- |
| Masaüstü tabanı (Kiro 1.95 · Chrome 1.86 · Traycer 1.75 · Claude 0.66 · wired 2.94 …) | **~11 GiB** |
| Docker Desktop VM'e **ayrılan** (`MemoryMiB: 8192`, Docker'ın varsayılanı = RAM/2) | **8 GiB** |
| Toplam talep / makine | **19 GiB / 16 GiB** |

Yani VM **ajan sayısından bağımsız olarak** sığmıyordu. Ayar 4096'ya çekildi
(`~/Library/Group Containers/group.com.docker/settings-store.json`, yedeği
`.bak-bizigo`) ve yığın sekiz servisle kalktı.

**Kapının görmediği şey de bu ayrımla netleşiyor:** RAM'e *taahhüt edilmiş ama
henüz dokunulmamış* bir ayırma hiçbir sayaçta görünmüyor. `memory_pressure`
sayfa durumunu okuyor; 8 GiB'lık bir VM ayarı bir sayfa durumu değil, bir
**gelecek talep**. Kapı ona bakamaz, ama bakabileceği bir şey var ve yazılı
olması yeterli: **Docker'ın ayrılmış belleği + masaüstü tabanı, RAM'i aşıyorsa
container işi yapılmaz.**
