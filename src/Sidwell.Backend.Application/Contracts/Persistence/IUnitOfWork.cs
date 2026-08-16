namespace Sidwell.Backend.Application.Contracts.Persistence;

public interface IUnitOfWork
{
    IDapperExecutor Dapper { get; }
}
