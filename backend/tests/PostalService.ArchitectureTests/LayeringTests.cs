using System.Reflection;

namespace PostalService.ArchitectureTests;

/// <summary>
/// Enforces the Clean Architecture dependency rule and bounded-context isolation by inspecting
/// compiled assembly references (no extra tooling needed).
/// </summary>
public class LayeringTests
{
    private static readonly string[] Services = { "Identity", "PostOffices", "Shipments" };

    private static Assembly Load(string name) => Assembly.Load(name);

    private static IReadOnlyList<string> References(string assembly) =>
        Load(assembly).GetReferencedAssemblies().Select(a => a.Name!).ToList();

    public static IEnumerable<object[]> ServiceNames() => Services.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public void Domain_depends_on_nothing_but_the_shared_kernel(string service)
    {
        var refs = References($"PostalService.{service}.Domain");
        refs.Where(r => r.StartsWith("PostalService.")).Should().BeEquivalentTo("PostalService.BuildingBlocks.Domain");
        refs.Should().NotContain(r => r.StartsWith("Microsoft.EntityFrameworkCore") || r.StartsWith("Microsoft.AspNetCore")
                                      || r.StartsWith("FluentValidation"));
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public void Application_does_not_depend_on_infrastructure_or_ef(string service)
    {
        var refs = References($"PostalService.{service}.Application");
        refs.Should().NotContain(r => r.StartsWith("Microsoft.EntityFrameworkCore"));
        refs.Should().NotContain(r => r.Contains(".Infrastructure") || r.EndsWith(".Api"));
    }

    [Theory]
    [MemberData(nameof(ServiceNames))]
    public void Services_never_reference_each_other(string service)
    {
        var others = Services.Where(s => s != service).ToArray();
        foreach (var layer in new[] { "Domain", "Application", "Infrastructure" })
        {
            var refs = References($"PostalService.{service}.{layer}");
            refs.Should().NotContain(r => others.Any(o => r.StartsWith($"PostalService.{o}.")),
                $"{service}.{layer} must not reach into another bounded context (database-per-service)");
        }
    }

    [Fact]
    public void Shared_kernel_domain_has_no_dependencies() =>
        References("PostalService.BuildingBlocks.Domain").Where(r => r.StartsWith("PostalService.")).Should().BeEmpty();
}
