# ============================================================================
# Bizigo dashboard (Next.js + BFF) — T49
#
# Cevapladığı soru API'ninkiyle aynı: *yığının tamamı tek komutla kalkıyor mu*.
# Bugüne kadar cevap "hayır"dı — API container'a girmişti, ekran girmemişti — ve
# bunun sessiz tarafı şuydu: uçtan uca harness ekranı YERELDEN koşturuyor, yani
# BFF'in container ağında çalışıp çalışmadığı hiç sınanmıyordu.
#
# BUILD CONTEXT `ui/` — API'nin aksine repo kökü DEĞİL. API üç ayrı ağaçtan
# dosya okuyor (src/, catalog/, db/clickhouse/); arayüzün okuduğu her şey
# `ui/` altında. Kökü context yapmak imaja depo kadar dosya taşırdı.
#
#   deploy/docker-compose.yml:
#     build:
#       context: ../ui
#       dockerfile: Dockerfile
#
# SÜRÜM SABİTLEME: hiçbir imajda `latest` yok (K25 riski, compose ile aynı kural).
# Node 22: `ui/package.json`'daki `engines` alanı `>=22.6.0` diyor ve CI de
# Node 22 kullanıyor — imajın CI'dan farklı bir ana sürümde koşması, yalnızca
# container'da görülen bir sınıf hata açardı.
# ============================================================================

FROM node:22.20.0-alpine AS deps

WORKDIR /app

# Önce YALNIZCA kilit dosyaları: kaynak değiştiğinde `npm ci` katmanı yeniden
# koşmasın diye. API'nin `dotnet restore` katmanıyla aynı gerekçe.
COPY package.json package-lock.json ./

# `npm ci` — `npm install` DEĞİL. İkincisi kilit dosyasını GÜNCELLEYEBİLİR, yani
# imaj, depoda yazılı olmayan bir bağımlılık kümesiyle kurulabilirdi ve fark
# hiçbir yerde görünmezdi.
RUN npm ci


FROM node:22.20.0-alpine AS build

WORKDIR /app

COPY --from=deps /app/node_modules ./node_modules
COPY . .

# Derleme zamanında hiçbir SIR gerekmiyor ve gerekmemesi bilinçli: `readBffConfig`
# değerleri ilk kullanımda okuyor, modül yüklenirken değil. Bir sır derleme
# argümanı olsaydı imaj katmanlarında kalırdı.
#
# Buradaki iki değişken sırr DEĞİL ve yalnızca derlemenin yapılandırmayı
# okumaya kalkışan bir yolu varsa diye duruyor; çalışma zamanında compose'un
# verdiği değerler geçerli.
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm run build


FROM node:22.20.0-alpine

WORKDIR /app

ENV NODE_ENV=production
ENV NEXT_TELEMETRY_DISABLED=1

# Kök olmayan kullanıcı: bu süreç oturum çerezi ve Keycloak istemci sırrı
# taşıyor. API 10003 kullanıyor, uid çakışmasın diye bir sonraki numara.
RUN addgroup --system --gid 10004 bizigo \
    && adduser --system --uid 10004 --ingroup bizigo bizigo

# `output: standalone` çıktısı: uygulamayı çalıştırmak için gereken
# node_modules'ın izlenebilir kısmı zaten içinde. Geliştirme bağımlılıkları —
# Playwright ve tarayıcı ikilileri dahil — imaja HİÇ girmiyor.
#
# `static` AYRI kopyalanıyor çünkü standalone çıktısı onu taşımıyor: Next
# statik varlıkların bir CDN'den servis edilebileceğini varsayıyor. Burada CDN
# yok, sunucu kendisi servis ediyor, dolayısıyla imajda olmak zorunda. Eksik
# kalsaydı ekran açılır ama STİLSİZ gelirdi — 500 değil, sebebini söylemeyen
# bozuk bir sayfa.
#
# `public/` YOK ve olmadığı ÖLÇÜLDÜ (`ls ui/public` → yok). Çoğu Next
# Dockerfile'ında duran `COPY … /app/public ./public` satırı burada imaj
# derlemesini "not found" ile düşürürdü — ve bu ancak imaj gerçekten
# derlendiğinde görünen bir kırık olurdu. Bir gün `ui/public/` açılırsa bu
# satır geri gelmeli.
COPY --from=build --chown=bizigo:bizigo /app/.next/standalone ./
COPY --from=build --chown=bizigo:bizigo /app/.next/static ./.next/static

USER bizigo

# Container içinde 3000; dışarı hangi portla çıkacağı compose'un kararı.
# 3000 dışarıda da SABİT ve bu pazarlığa açık değil: realm dosyasındaki
# `redirectUris` birebir `http://localhost:3000/signin-oidc` yazıyor.
EXPOSE 3000
ENV PORT=3000

# 0.0.0.0 zorunlu: standalone sunucusu varsayılan olarak `localhost`'a bağlanıyor
# ve container içinde bu, dışarıdan HİÇBİR bağlantı kabul etmemek demek.
# Belirtisi "port açık ama bağlantı reddediliyor" — sağlık kontrolü de düşer.
ENV HOSTNAME=0.0.0.0

CMD ["node", "server.js"]
