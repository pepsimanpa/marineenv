using MarineEnvironment.Models;

namespace MarineEnvironment.Sources;

internal interface IEnvironmentDataSource : IDisposable
{
    string Id { get; }
    EnvironmentType Type { get; }
    SourceStatus Status { get; }
    string? StatusMessage { get; }
    EnvironmentValue? Query(EnvironmentQuery query);
}
