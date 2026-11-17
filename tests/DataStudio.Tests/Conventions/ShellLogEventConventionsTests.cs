using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Tests.Conventions;

/// <summary>
/// The shell's half of the one-wording rule (v2 step 6): the one event the shell declares, 9220, is declared once across <c>src/</c> - the
/// database module writes the shell's template with its own level, it does not declare a copy - and no template of the shell names a
/// module's product.
/// </summary>
public class ShellLogEventConventionsTests
{
    [Fact]
    public void Every_event_id_is_declared_exactly_once_across_src()
    {
        LogEventScan.AssertEveryIdIsDeclaredOnce("src/DataStudio");
    }

    [Fact]
    public void No_log_template_names_a_modules_product()
    {
        LogEventScan.AssertNoTemplateNamesAProduct("src/DataStudio");
    }
}
