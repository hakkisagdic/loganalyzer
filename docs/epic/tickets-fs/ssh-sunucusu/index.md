---
title: "S03 — N2: gerçek SSH sunucusu"
kind: ticket
status: 0
---

# S03 — N2: gerçek SSH sunucusu (container)

**Bağımlılık:** S01 · **Sonraki:** S04, S06

## Amaç

`SshDeviceTransport` bugün **hiçbir yerde koşmuyor.** F1'de yazıldı, o günden
beri tek bir test onu ağa çıkarmadı. Bu ticket onu ilk kez gerçek bir SSH
oturumuna sokuyor.

## Karar: **container, süreç içi kütüphane değil**

Üç gerekçe, üçü de mimari — kaynak kısıtı değil.

**1 · Süreç içi bir SSH kütüphanesi istemciyi kütüphaneye karşı sınar,
sunucuya karşı değil.** Aynı süreçte konuşan iki nesne el sıkışmayı, anahtar
değişimini, kanal açılışını ve zaman aşımını gerçekten yaşamaz. Kanıtın kendisi
**dışarıda bir süreç olmasında**.

**2 · S06 aynı zemine ihtiyaç duyuyor.** CLI öykünmesi — prompt, sayfalama,
vendor hata mesajları — bir kabuk gerektiriyor. Container ikisinin ortak
substratı; süreç içi çözüm S06'da atılıp yeniden yazılırdı.

**3 · Testin dışında da kullanılabilir.** Bir geliştirici toplayıcıyı ona
yöneltip elle deneyebilir. Süreç içi bir sahte yalnızca test koşumunda yaşar,
yani ürünü **elle denemenin** yolu açılmaz.

Bedeli kabul: entegrasyon paketine bir konteyner daha giriyor ve koşumu
koordinatörde (§2). Beş simülatör + ClickHouse + Postgres + RustFS aynı anda
kalkacaksa **profil arkasına** alınmalı — varsayılan `docker compose up`
davranışı değişmemeli.

## Kapsam

### İçinde

- SSH sunucusu container'ı: komuta göre profilin çıktısını veren bir kabuk.
- Kimlik doğrulama: doğru parola geçiyor, yanlış parola reddediyor.
- Compose'da **profil arkasında** bir servis.

### Dışında

- Prompt, sayfalama, vendor hata mesajları — S06.
- Ağ gerçekliği (paket kaybı, yarım oturum) — kapsam dışı ve FS §11'de yazılı.

## Kabul kriterleri

- `SshDeviceTransport` **ilk kez** bir testte koşuyor.
- Yanlış parola `DeviceCommandResult.Ok=false` **ve bir kimlik doğrulama
  hatası** üretiyor.

  Ölçütün böyle yazılmasının sebebi: **SSH'ın HTTP durum kodu yok.** Ölçüt
  "401 dönüyor" diye yazılsaydı hiçbir zaman gerçekleşemezdi — bu depoda bir
  kabul kriterinin *tanım gereği* karşılanamaz olması yaşanmış bir hata sınıfı.
- Container **profil arkasında**; `docker compose up -d` onu başlatmıyor.
- Bir bekçi, container'ın okuduğu profilin `SimulatedDeviceTransport`'un
  okuduğuyla **aynı dosya** olduğunu sınıyor (S01'in tek-kaynak kararı).
