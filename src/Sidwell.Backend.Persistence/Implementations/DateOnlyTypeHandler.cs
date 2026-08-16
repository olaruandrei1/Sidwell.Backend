using System.Data;
using Dapper;

namespace Sidwell.Backend.Persistence.Implementations;

// Dapper does not recognize DateOnly as a parameter value out of the box (Npgsql accepts it on the
// resulting parameter). This handler bridges DateOnly <-> a Postgres `date` for both reads and writes.
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => DateOnly.Parse(value.ToString()!),
    };

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value;
    }
}
