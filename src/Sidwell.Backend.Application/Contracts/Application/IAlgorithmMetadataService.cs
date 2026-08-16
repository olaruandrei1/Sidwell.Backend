namespace Sidwell.Backend.Application.Contracts.Application;

public sealed record AlgorithmMetadata(string Formula, string Definition, string How);

public interface IAlgorithmMetadataService
{
    IReadOnlyDictionary<string, AlgorithmMetadata> GetAll();
}
