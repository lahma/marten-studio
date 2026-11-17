using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Postgres.Tests.Conventions;

/// <summary>
/// The Postgres module's and the engine's half of the one-wording rule (v2 step 6): the engine's <c>StudioLog</c> and <c>PostgresLog</c> declare
/// each event id once across <c>src/</c> - there is no second class in another product's words - and no template names a module's product.
/// </summary>
public class PostgresLogEventConventionsTests
{
    [Fact]
    public void Every_event_id_is_declared_exactly_once_across_src()
    {
        LogEventScan.AssertEveryIdIsDeclaredOnce("src/DataStudio.Postgres.Diagnostics");
    }

    [Fact]
    public void No_log_template_names_a_modules_product()
    {
        LogEventScan.AssertNoTemplateNamesAProduct("src/DataStudio.Postgres", "src/DataStudio.Postgres.Diagnostics");
    }
}
