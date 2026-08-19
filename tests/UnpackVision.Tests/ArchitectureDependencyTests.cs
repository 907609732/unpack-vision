using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace UnpackVision.Tests;

public sealed class ArchitectureDependencyTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(Core.ScanRecord).Assembly,
            typeof(Application.Recording.RecordingCoordinator).Assembly,
            typeof(Infrastructure.SqliteScanRecordRepository).Assembly)
        .Build();

    private static readonly IObjectProvider<IType> CoreLayer =
        Types().That().ResideInAssembly(typeof(Core.ScanRecord).Assembly);

    private static readonly IObjectProvider<IType> ApplicationLayer =
        Types().That().ResideInAssembly(typeof(Application.Recording.RecordingCoordinator).Assembly);

    private static readonly IObjectProvider<IType> InfrastructureLayer =
        Types().That().ResideInAssembly(typeof(Infrastructure.SqliteScanRecordRepository).Assembly);

    [Fact]
    public void CoreDoesNotDependOnApplicationOrInfrastructure()
    {
        Types().That().Are(CoreLayer).Should()
            .NotDependOnAny(ApplicationLayer)
            .Check(Architecture);

        Types().That().Are(CoreLayer).Should()
            .NotDependOnAny(InfrastructureLayer)
            .Check(Architecture);
    }

    [Fact]
    public void ApplicationDoesNotDependOnInfrastructure()
    {
        Types().That().Are(ApplicationLayer).Should()
            .NotDependOnAny(InfrastructureLayer)
            .Check(Architecture);
    }
}
