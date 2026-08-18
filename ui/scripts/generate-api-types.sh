#!/usr/bin/env bash
#
# OpenAPI belgesini `Bizigo.Api`'den üretir ve TypeScript tiplerine çevirir.
#
# İki kip:
#   ./generate-api-types.sh          → depodaki dosyaları günceller
#   ./generate-api-types.sh --check  → GEÇİCİ dizine üretir, depodakiyle
#                                      farklıysa farkı basıp düşer (CI kapısı)
#
# Kapının gerekçesi: şema sürüklenir, UI derlenmeye devam eder ve hata çalışma
# zamanında çıkar. Tipler elle yazılmıyor; sürüklendiği gün CI kırmızı yanıyor.
#
# `--check` depodaki dosyalara DOKUNMUYOR — bilerek. Üzerine yazsaydı, düşen
# bir kapıdan sonra çalışma ağacı değişmiş olurdu ve aynı komutun ikinci
# koşumu sebepsiz yere geçerdi.
set -euo pipefail

ui_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo_root="$(cd "${ui_dir}/.." && pwd)"

document="${ui_dir}/openapi/bizigo-api.json"
types="${ui_dir}/src/lib/api/schema.d.ts"

check_mode=0
if [[ "${1:-}" == "--check" ]]; then
  check_mode=1
fi

# `dotnet` PATH'te SDK 8/9 gösterebiliyor; depo `global.json` ile 10.0.302
# istiyor ve arm64 SDK `~/.dotnet` altında duruyor.
if [[ -x "${HOME}/.dotnet/dotnet" ]]; then
  export PATH="${HOME}/.dotnet:${PATH}"
fi

if [[ ${check_mode} -eq 1 ]]; then
  work="$(mktemp -d)"
  trap 'rm -rf "${work}"' EXIT
  out_document="${work}/bizigo-api.json"
  out_types="${work}/schema.d.ts"
else
  mkdir -p "$(dirname "${document}")" "$(dirname "${types}")"
  out_document="${document}"
  out_types="${types}"
fi

echo "→ OpenAPI belgesi üretiliyor (Bizigo.Api)"
# Üretim adımı artımlı: derleme çıktısı değişmediyse MSBuild onu atlıyor ve
# hiçbir dosya yazılmıyor. Kapı için bu ölümcül — hedef dizin değişse bile
# "zaten güncel" deyip geçerdi. Önbelleği silerek her koşumda üretmeye
# zorluyoruz.
rm -f "${repo_root}/src/Bizigo.Api/obj/Bizigo.Api.OpenApiFiles.cache"
# Belge üretimi uygulamayı gerçekten çalıştırıyor; Program.cs bunu giriş
# derlemesinin adından anlayıp veritabanı göçlerini atlıyor.
dotnet build "${repo_root}/src/Bizigo.Api" \
  --configuration Release \
  -p:OpenApiGenerateDocuments=true \
  -p:OpenApiDocumentsDirectory="$(dirname "${out_document}")" \
  --nologo \
  --verbosity quiet

echo "→ TypeScript tipleri üretiliyor"
npx --yes openapi-typescript "${out_document}" --output "${out_types}"

if [[ ${check_mode} -eq 0 ]]; then
  echo "✓ ui/openapi/bizigo-api.json ve ui/src/lib/api/schema.d.ts güncellendi."
  exit 0
fi

failed=0

if ! diff -u "${document}" "${out_document}" --label "depo/openapi/bizigo-api.json" --label "üretilen"; then
  failed=1
fi

if ! diff -u "${types}" "${out_types}" --label "depo/src/lib/api/schema.d.ts" --label "üretilen"; then
  failed=1
fi

if [[ ${failed} -eq 1 ]]; then
  echo "" >&2
  echo "✗ API şeması değişmiş ama üretilen dosyalar işlenmemiş." >&2
  echo "  Çözüm: ui/ içinde \`npm run api:generate\` çalıştırıp sonucu commit edin." >&2
  exit 1
fi

echo "✓ Üretilen tipler depodakiyle birebir aynı."
