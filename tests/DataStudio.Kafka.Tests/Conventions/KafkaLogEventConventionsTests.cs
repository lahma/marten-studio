using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Kafka.Tests.Conventions;

/// <summary>
/// The Kafka module's half of the one-wording rule (v2 step 6): <c>KafkaStudioLog</c> (9600 to 9649) and <c>KStudioLog</c> (9650 to 9699)
/// declare each event id once across <c>src/</c>, and no template names a module's product - they say Data Studio where they name the
/// product, and Kafka where they mean Kafka.
/// </summary>
public class KafkaLogEventConventionsTests
{
    [Fact]
    public void Every_event_id_is_declared_exactly_once_across_src()
    {
        LogEventScan.AssertEveryIdIsDeclaredOnce("src/DataStudio.Kafka", "src/DataStudio.Kafka.Core");
    }

    [Fact]
    public void No_log_template_names_a_modules_product()
    {
        LogEventScan.AssertNoTemplateNamesAProduct("src/DataStudio.Kafka", "src/DataStudio.Kafka.Core", "src/DataStudio.Kafka.Wolverine");
    }
}
