# ============================================================================
# Bizigo.Api — REST uçları, ingest yolu ve arka plan turları
#
# ⚠️ BUILD CONTEXT REPO KÖKÜ. Uygulama üç ayrı ağaçtan dosya okuyor ve üçü de
#    proje dizininin dışında:
#      src/            → kaynak; Api yedi projeye referans veriyor
#      catalog/        → parser, grok pattern, eşleme tablosu ve maske sözlüğü
#      db/clickhouse/  → sıralı SQL göçleri (açılışta uygulanıyor)
#
#    deploy/docker-compose.yml:
#      build:
#        context: ..
#        dockerfile: src/Bizigo.Api/Dockerfile
#
#    Bu, sidecar'ın izlediği kalıbın aynısı ve sebebi de aynı: katalog TEK
#    kaynak. İmaja kopyalanmak yerine imajın içine ayrı bir kopya konulsaydı,
#    .NET tarafı ile sidecar aynı maskeleri farklı sürümlerden okuyabilir ve
#    `template_id` sessizce ayrışırdı.
#
# SÜRÜM SABİTLEME: hiçbir imajda `latest` yok (K25 riski, compose ile aynı kural).
# ============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble AS build

WORKDIR /src

# Önce YALNIZCA proje dosyaları. Kaynak değiştiğinde `restore` katmanı yeniden
# koşmasın diye: paket indirmesi derlemenin en uzun adımı ve kaynağa bağlı
# değil.
#
# Dosyalar tek COPY ile geliyor, sonra kendi dizinlerine dağıtılıyor — her
# projeyi tek tek yazmak, on beşinci proje eklendiği gün sessizce eksik kalan
# bir liste olurdu. Dizin adı ile proje adının aynı olması bu deponun
# konvansiyonu ve `RepositoryLayout` testleri onu zaten sınıyor.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/*/*.csproj ./projeler/
RUN set -eux; \
    for proje in ./projeler/*.csproj; do \
        ad="$(basename "$proje" .csproj)"; \
        mkdir -p "src/$ad"; \
        mv "$proje" "src/$ad/"; \
    done; \
    rmdir ./projeler

RUN dotnet restore src/Bizigo.Api/Bizigo.Api.csproj

COPY src/ src/

# `--no-restore`: yukarıdaki katman zaten yaptı. Yeniden koşarsa katman
# önbelleği anlamsızlaşır.
RUN dotnet publish src/Bizigo.Api/Bizigo.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish


FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-noble

WORKDIR /app

# Çalışma dizini ile içerik kökü BURADA AYNI, ve bu container'ın makinede
# koşan hâle göre tek sadeleşmesi: `appsettings.json` yayınlanan çıktının
# içinde, `catalog/` ve `db/clickhouse/` de aynı dizinde. Makinede koşarken
# ikisi ayrışıyor (CLAUDE.md §12) çünkü appsettings `bin/` altında kalıyor.
COPY --from=build /app/publish ./
COPY catalog/ ./catalog/
COPY db/clickhouse/ ./db/clickhouse/

# WAL dizini AYRI ve kalıcı olmak zorunda. Ham batch'e "alındı" cevabı buraya
# yazılıp fsync edildikten SONRA veriliyor — F1'in dayanıklılık sınırı burada.
# İmaj katmanında bırakılsaydı konteyner yeniden yaratıldığında henüz
# arşivlenmemiş batch'ler kaybolurdu; compose bu yolu bir birime bağlıyor.
RUN mkdir -p /var/lib/bizigo/wal

# Kök olmayan kullanıcı: bu süreç ağdan güvenilmeyen metin alıyor (syslog
# gövdeleri) ve cihazlara SSH açıyor. Sidecar'daki gerekçenin aynısı, uid
# çakışmasın diye bir sonraki numara.
RUN useradd --system --uid 10003 --create-home bizigo \
    && chown -R bizigo:bizigo /app /var/lib/bizigo
USER bizigo

# Konteyner içinde 8080; dışarı hangi portla çıkacağı compose'un kararı.
# Makinedeki konvansiyon 5080 ve compose onu koruyor.
EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

# M11 · K6'nın topoloji kapısının MUAFİYETİ — ve yeri bilinçli: bir satır
# yukarıdaki joker bağlamanın hemen altında, yani bağlamayı değiştiren kişi
# gerekçeyi de görüyor.
#
# Kapı (`McpListenerBoundaryGate`) `0.0.0.0`'ı "dışa açık" saymıyor, "BİLİNMİYOR"
# sayıyor: joker bir bağlamanın kuruma açık olup olmadığını süreç içinden
# görmenin yolu yok — cevabı container ağı, publish kuralı ve ana makinenin
# güvenlik duvarı veriyor. Bu değişkenin yokluğu MCP'yi ayağa kaldırmıyor, ve
# öyle olması gerekiyor: gerekçesiz bir joker bağlama + `internal` beyanı tam
# olarak M11'in kapattığı sessiz hâl.
#
# DİKKAT: bu satır container ağını gerekçe gösteriyor. `network_mode: host` ile
# ya da ana makinede doğrudan koşturulan bir kurulumda gerekçe GEÇERSİZ olur ve
# o kurulum kendi gerekçesini yazmak zorunda.
#
# Değerde `${...}` YOK ve olamaz: Dockerfile `ENV` değerlerinde değişken
# genişletmesi yapıyor, yani compose'un publish ifadesini buraya olduğu gibi
# yazmak metni sessizce başka bir şeye çevirirdi. Metin ASCII: bu değer
# konteyner günlüğüne basılıyor ve orada kod sayfası bizim seçimimiz değil.
ENV Mcp__ListenerBoundaryOverrideReason="Konteyner agi: 0.0.0.0 yalnizca container'in kendi ag alanini dinliyor; disa acilma karari compose'un publish kuralinda (API_PORT:8080) ve surec onu goremiyor."

ENTRYPOINT ["./Bizigo.Api"]
