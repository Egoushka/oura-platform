using System.Data;
using System.Runtime.CompilerServices;
using Dapper;

namespace OuraPlatform.Storage;

/// <summary>
/// Teaches Dapper about <see cref="DateOnly"/>.
/// </summary>
/// <remarks>
/// Npgsql maps <c>date</c> to <see cref="DateOnly"/> happily; Dapper does not know the type at all
/// and throws <c>"cannot be used as a parameter value"</c> on the way in, then refuses to match a
/// record constructor on the way out. Registering a handler fixes both directions, and because
/// Dapper clones handlers for the nullable form, <c>DateOnly?</c> comes along for free.
/// <para>
/// A module initializer rather than a DI call: every repository in this assembly depends on it, and
/// a registration that has to be remembered is one that eventually is not.
/// </para>
/// </remarks>
internal static class DapperConfiguration
{
    // CA2255 warns against module initializers in libraries because the timing is implicit. That is
    // the point here: registering from AddOuraStorage would leave every direct `new
    // RawDocumentRepository(...)` — including the ones in the tests — running against a differently
    // configured Dapper than production.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Configure() => SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

    private sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value;
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly day => day,
            // Npgsql returns DateTime for `date` when the target type is not known up front.
            DateTime timestamp => DateOnly.FromDateTime(timestamp),
            string text => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidCastException($"Cannot convert {value.GetType()} to DateOnly."),
        };
    }
}
