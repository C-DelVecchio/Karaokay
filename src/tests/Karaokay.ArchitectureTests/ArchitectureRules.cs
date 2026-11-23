using System.Reflection;

namespace Karaokay.ArchitectureTests;

/// <summary>
/// Enforces the dependency direction documented in docs/ARCHITECTURE.md §3: Domain and Contracts
/// are pure, Application may only see Domain, Infrastructure is an adapter layer, and the two CLI
/// tools never reach into the apps.
/// </summary>
/// <remarks>
/// These checks intentionally use <see cref="Assembly.GetReferencedAssemblies"/> directly instead
/// of NetArchTest.Rules 1.3.2: on net10.0 that library mis-reports dependencies (a probe showed it
/// claiming Infrastructure does not reference Karaokay.Contracts, which it demonstrably does).
/// The positive-control tests at the bottom prove the reflection probe sees real references.
/// </remarks>
public sealed class ArchitectureRules
{
    private static readonly Assembly DomainAssembly =
        typeof(Karaokay.Domain.DomainAssemblyMarker).Assembly;

    private static readonly Assembly ContractsAssembly =
        typeof(Karaokay.Contracts.ContractsAssemblyMarker).Assembly;

    private static readonly Assembly ApplicationAssembly =
        typeof(Karaokay.Application.ApplicationAssemblyMarker).Assembly;

    private static readonly Assembly InfrastructureAssembly =
        typeof(Karaokay.Infrastructure.InfrastructureAssemblyMarker).Assembly;

    private static readonly Assembly ServiceDefaultsAssembly =
        typeof(Karaokay.ServiceDefaults.SharedServiceDefaults).Assembly;

    private static readonly Assembly SyntheticGenAssembly =
        typeof(Karaokay.SyntheticGen.SyntheticSongGenerator).Assembly;

    private static readonly Assembly ApiAssembly = LoadProjectAssembly("Karaokay.Api");

    private static readonly Assembly AppHostAssembly = LoadProjectAssembly("Karaokay.AppHost");

    private static readonly Assembly ImporterAssembly = LoadProjectAssembly("Karaokay.Importer");

    private static readonly string[] OtherKaraokayAssembliesAndPackages =
    [
        "Karaokay.Application",
        "Karaokay.Infrastructure",
        "Karaokay.Contracts",
        "Karaokay.Api",
        "Karaokay.AppHost",
        "Karaokay.ServiceDefaults",
        "Karaokay.SyntheticGen",
        "Karaokay.Importer",
        "Microsoft.EntityFrameworkCore",
        "Melanchall.DryWetMidi",
        "CliWrap",
    ];

    private static readonly string[] ContractsForbidden =
    [
        "Karaokay.Domain",
        "Karaokay.Application",
        "Karaokay.Infrastructure",
        "Karaokay.Api",
        "Karaokay.AppHost",
        "Karaokay.ServiceDefaults",
        "Karaokay.SyntheticGen",
        "Karaokay.Importer",
        "Microsoft.EntityFrameworkCore",
        "Melanchall.DryWetMidi",
        "CliWrap",
    ];

    private static readonly string[] ApplicationForbidden =
    [
        "Karaokay.Infrastructure",
        "Karaokay.Contracts",
        "Karaokay.Api",
        "Karaokay.AppHost",
        "Karaokay.ServiceDefaults",
        "Karaokay.SyntheticGen",
        "Karaokay.Importer",
        "Microsoft.EntityFrameworkCore",
    ];

    private static readonly string[] InfrastructureForbidden =
    [
        "Karaokay.Api",
        "Karaokay.AppHost",
        "Karaokay.ServiceDefaults",
        "Karaokay.SyntheticGen",
        "Karaokay.Importer",
    ];

    private static readonly string[] ServiceDefaultsForbidden =
    [
        "Karaokay.Domain",
        "Karaokay.Application",
        "Karaokay.Contracts",
        "Karaokay.Infrastructure",
        "Karaokay.SyntheticGen",
        "Karaokay.Importer",
    ];

    private static readonly string[] ToolsForbiddenApps =
    [
        "Karaokay.Api",
        "Karaokay.AppHost",
        "Karaokay.ServiceDefaults",
    ];

    private static readonly string[] ApiForbiddenTools =
    [
        "Karaokay.SyntheticGen",
        "Karaokay.Importer",
    ];

    private static readonly string[] KnownInfrastructureReferences =
    [
        "Karaokay.Contracts",
    ];

    private static readonly string[] KnownSyntheticGenReferences =
    [
        "Karaokay.Infrastructure",
    ];

    [Fact]
    public void DomainHasNoOutwardDependencies()
    {
        AssertNoDependency(DomainAssembly, OtherKaraokayAssembliesAndPackages);
    }

    [Fact]
    public void ContractsArePureBclDataContracts()
    {
        AssertNoDependency(ContractsAssembly, ContractsForbidden);
    }

    [Fact]
    public void ApplicationDependsOnlyOnDomainAmongKaraokayProjects()
    {
        AssertNoDependency(ApplicationAssembly, ApplicationForbidden);
    }

    [Fact]
    public void InfrastructureNeverDependsOnAppsOrTools()
    {
        AssertNoDependency(InfrastructureAssembly, InfrastructureForbidden);
    }

    [Fact]
    public void ServiceDefaultsNeverDependsOnCoreOrTools()
    {
        AssertNoDependency(ServiceDefaultsAssembly, ServiceDefaultsForbidden);
    }

    [Fact]
    public void SyntheticGenNeverDependsOnApps()
    {
        AssertNoDependency(SyntheticGenAssembly, ToolsForbiddenApps);
    }

    [Fact]
    public void ImporterNeverDependsOnApps()
    {
        AssertNoDependency(ImporterAssembly, ToolsForbiddenApps);
    }

    [Fact]
    public void ApiNeverDependsOnTheCliTools()
    {
        AssertNoDependency(ApiAssembly, ApiForbiddenTools);
    }

    // Positive controls: if the reflection probe could not see a reference that provably exists,
    // every rule above would pass vacuously. These fail loudly in that case.

    [Fact]
    public void DependencyProbeSeesTheKnownInfrastructureToContractsReference()
    {
        var detected = FindReferencedDependencies(InfrastructureAssembly, KnownInfrastructureReferences);
        Assert.Contains("Karaokay.Contracts", detected);
    }

    [Fact]
    public void DependencyProbeSeesTheKnownSyntheticGenToInfrastructureReference()
    {
        var detected = FindReferencedDependencies(SyntheticGenAssembly, KnownSyntheticGenReferences);
        Assert.Contains("Karaokay.Infrastructure", detected);
    }

    private static void AssertNoDependency(Assembly subject, string[] forbiddenAssemblyNames)
    {
        var violations = FindReferencedDependencies(subject, forbiddenAssemblyNames);
        Assert.True(
            violations.Count == 0,
            $"{subject.GetName().Name} must not reference [{Join(forbiddenAssemblyNames)}] " +
            $"but does reference [{Join(violations)}]. " +
            $"All referenced assemblies: [{Join(ReferencedAssemblyNames(subject))}].");
    }

    private static IReadOnlyList<string> FindReferencedDependencies(
        Assembly subject,
        IReadOnlyCollection<string> forbiddenAssemblyNames)
    {
        var forbidden = new HashSet<string>(forbiddenAssemblyNames, StringComparer.OrdinalIgnoreCase);
        return [.. ReferencedAssemblyNames(subject).Where(forbidden.Contains)];
    }

    private static IReadOnlyList<string> ReferencedAssemblyNames(Assembly subject) =>
    [
        .. subject.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!),
    ];

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static Assembly LoadProjectAssembly(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
        return File.Exists(path)
            ? Assembly.LoadFrom(path)
            : Assembly.Load(new AssemblyName(name));
    }
}
