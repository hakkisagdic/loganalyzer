using System.Reflection;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest;
using Bizigo.Normalization;
using Bizigo.Parsing;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using NetArchTest.Rules;

namespace Bizigo.UnitTests;

/// <summary>
/// Kapsam ayrımının (K17) derleme zamanı bekçisi.
///
/// Bu kurallar olmadan tek bir aceleci PR yeter: biri kolaylık olsun diye
/// <c>Bizigo.Api</c>'ye <c>ClickHouseConnection</c> açar, kapsam filtresi
/// atlanır ve kimse fark etmez. Testin amacı insanı yakalamak değil, o yolu
/// baştan kapatmak.
/// </summary>
public sealed class ArchitectureTests
{
    private const string ClickHouseDriverNamespace = "ClickHouse.Driver";

    private static readonly Assembly[] AssembliesThatMustNotTouchTheDriver =
    [
        typeof(AccessScope).Assembly,          // Bizigo.Contracts
        typeof(IScopedQuery).Assembly,         // Bizigo.Query
        typeof(ControlPlaneDbContext).Assembly,// Bizigo.ControlPlane
        typeof(RawObjectKey).Assembly,         // Bizigo.Storage.Raw
        typeof(ParserMarker).Assembly,         // Bizigo.Parsing
        typeof(NormalizationMarker).Assembly,  // Bizigo.Normalization
        typeof(IngestMarker).Assembly,         // Bizigo.Ingest
    ];

    [Fact]
    public void Yalnizca_Storage_ClickHouse_surucuye_referans_verebilir()
    {
        foreach (var assembly in AssembliesThatMustNotTouchTheDriver)
        {
            var offenders = Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn(ClickHouseDriverNamespace)
                .GetTypes()
                .Select(t => t.FullName)
                .ToArray();

            Assert.True(
                offenders.Length == 0,
                $"{assembly.GetName().Name} '{ClickHouseDriverNamespace}' bağımlılığı taşıyor: " +
                string.Join(", ", offenders) +
                ". Ham ClickHouse erişimi yalnızca Bizigo.Storage.ClickHouse içinde olabilir " +
                "(K17 / F1 §10.2).");
        }
    }

    [Fact]
    public void Api_dogrudan_ClickHouse_okuyucularina_erisemez()
    {
        // API katmanı EventReader/ChangeEventReader'ı doğrudan kullanamaz;
        // IScopedQuery üzerinden geçmeli. Aksi halde denetim kaydı ve kapsam
        // daraltması atlanabilir.
        var apiAssembly = typeof(global::Program).Assembly;

        var offenders = Types.InAssembly(apiAssembly)
            .That()
            .HaveDependencyOnAny(
                typeof(EventReader).FullName!,
                typeof(ChangeEventReader).FullName!)
            .GetTypes()
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "API katmanı okuyuculara doğrudan erişiyor: " + string.Join(", ", offenders) +
            ". IScopedQuery kullanılmalı.");
    }

    [Fact]
    public void Contracts_hicbir_altyapiya_bagimli_olmamali()
    {
        var offenders = Types.InAssembly(typeof(AccessScope).Assembly)
            .That()
            .HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Amazon", ClickHouseDriverNamespace)
            .GetTypes()
            .Select(t => t.FullName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Bizigo.Contracts altyapı bağımlılığı taşıyor: " + string.Join(", ", offenders));
    }
}
