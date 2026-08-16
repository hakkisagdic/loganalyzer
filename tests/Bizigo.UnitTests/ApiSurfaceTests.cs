using System.Reflection;
using Bizigo.Contracts;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using NetArchTest.Rules;

namespace Bizigo.UnitTests;

/// <summary>
/// T10 kabul kriterlerinin derleme zamanı bekçileri.
///
/// <para>
/// Bu testlerin hiçbiri "kod çalışıyor mu" diye sormuyor; hepsi <b>bir yolun
/// kapalı kaldığını</b> sınıyor. Kapsam ayrımı bu üründeki en pahalı hata sınıfı
/// ve tek zorlama noktası sorgu API'si — arka kapı açıldığı gün kimse fark etmez.
/// </para>
/// </summary>
public sealed class ApiSurfaceTests
{
    private static readonly Assembly ApiAssembly = typeof(global::Program).Assembly;

    [Fact]
    public void Api_ham_nesne_deposuna_dogrudan_erisemez()
    {
        // `IRawObjectStore` doğrudan kullanılırsa kapsam kontrolü olmadan nesne
        // indirilebilir. Ham okuma `RawEventLocator`/`RawReader` üzerinden gitmeli;
        // ikisi de kapsamı indirmeden ÖNCE doğruluyor.
        var offenders = Types.InAssembly(ApiAssembly)
            .That()
            .HaveDependencyOn("Bizigo.Storage.Raw.IRawObjectStore")
            .GetTypes()
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "API katmanı nesne deposuna doğrudan erişiyor: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Api_olay_yazicisina_dogrudan_erisemez()
    {
        // Yazma da tek kapıdan geçmeli: `IScopedQuery.WriteChangeAsync` çağıranın
        // yalnızca kendi kapsamına yazabildiğini doğruluyor.
        var offenders = Types.InAssembly(ApiAssembly)
            .That()
            .HaveDependencyOn(typeof(EventWriter).FullName!)
            .GetTypes()
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "API katmanı EventWriter'a doğrudan erişiyor: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Serbest_SQL_kabul_eden_bir_filtre_operatoru_yok()
    {
        // Operatör kümesi kapalı. Yeni bir operatör eklemek bilinçli bir karar
        // olmalı; `Enum.TryParse` ile açmak onu istemeden API yüzeyine taşırdı.
        var expected = new[]
        {
            FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.In,
            FilterOperator.GreaterThan, FilterOperator.LessThan,
            FilterOperator.Contains, FilterOperator.StartsWith,
        };

        Assert.Equal(expected.Order(), Enum.GetValues<FilterOperator>().Order());
    }

    [Fact]
    public void IScopedQuery_her_metodunda_kapsam_istiyor()
    {
        // Kapsamsız bir aşırı yükleme eklenirse çağıran onu "kolay yol" diye
        // kullanır ve filtre sessizce düşer.
        var methods = typeof(IScopedQuery).GetMethods();

        Assert.NotEmpty(methods);

        foreach (var method in methods)
        {
            Assert.True(
                method.GetParameters().Any(p => p.ParameterType == typeof(AccessScope)),
                $"IScopedQuery.{method.Name} kapsam parametresi almıyor.");
        }
    }
}
