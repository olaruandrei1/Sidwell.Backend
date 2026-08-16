using Sidwell.Backend.Application.Contracts.Persistence;

namespace Sidwell.Backend.Persistence.Implementations;

public sealed class UnitOfWork(IDapperExecutor dapper) : IUnitOfWork
{
    public IDapperExecutor Dapper => dapper;
}
