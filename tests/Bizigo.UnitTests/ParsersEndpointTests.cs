using Bizigo.Api;
using Bizigo.Parsing.Dispatch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

/// <summary>
/// <c>/v1/parsers</c> yönlendirme ve yetki bağlaması (T10).
///
/// <para>
/// Yalnızca <b>kayıt</b> sınanıyor, davranış değil: <c>MapParsers</c> hiçbir
/// servisi çözmüyor, dolayısıyla ClickHouse/Postgres olmadan koşabiliyor.
/// Kataloğun projeksiyonu <c>VendorCatalogTests</c>, dispatcher davranışı
/// <c>DispatcherTests</c> tarafından zaten kapsanıyor — burada değerli olan tek
/// şey, bir ucun yetki niteliği <b>olmadan</b> eklenmediğinin sabitlenmesi.
/// </para>
/// </summary>
public sealed class ParsersEndpointTests
{
    private static IReadOnlyList<RouteEndpoint> Endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();

        // Kayıtlı olmasalar minimal API bunları GÖVDE parametresi sanıyor ve
        // GET uçları gövde kabul etmediği için kayıt patlıyor. İkisi de boş
        // kurulabiliyor; handler'lar hiç çağrılmadığı için katalog boş kalabilir.
        builder.Services.AddSingleton<ParserCatalog>();
        builder.Services.AddSingleton<DispatchStats>();
        builder.Services.AddSingleton<Dispatcher>();

        var app = builder.Build();
        app.MapParsers();

        // `app.DataSources` okunuyor, DI'daki birleşik `EndpointDataSource` değil:
        // ikincisi yalnızca uygulama başlatıldıktan sonra doluyor ve burada
        // uygulamayı başlatmak istemiyoruz.
        return [.. ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()];
    }

    [Fact]
    public void Uc_uc_da_kayitli()
    {
        var patterns = Endpoints()
            .Select(static e => e.RoutePattern.RawText ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["/v1/parsers/", "/v1/parsers/try", "/v1/parsers/{id}"], patterns);
    }

    /// <summary>
    /// Yetkilendirmesiz bir uç, kataloğu kimlik doğrulamasız açar. Katalog log
    /// verisi taşımıyor ama hangi vendor'ların izlendiğini söylüyor; bu tek
    /// başına dışarıya verilecek bir bilgi değil.
    /// </summary>
    [Fact]
    public void Her_ucun_yetki_niteligi_var()
    {
        foreach (var endpoint in Endpoints())
        {
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>());
        }
    }

    /// <summary>
    /// <c>try</c> yazma değil ama <b>okuma da değil</b>: keyfi bir satırı motora
    /// koşturuyor. Parser yazan rolde olmalı — sıradan bir okuyucunun elinde
    /// bu, bedeli sınırsız bir hesaplama ucudur.
    /// </summary>
    [Fact]
    public void Deneme_ucu_yazar_rolu_istiyor()
    {
        var tryEndpoint = Assert.Single(
            Endpoints(),
            static e => e.RoutePattern.RawText == "/v1/parsers/try");

        var policies = tryEndpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(static a => a.Policy);

        Assert.Contains(BizigoAuthPolicies.Author, policies);
    }
}
