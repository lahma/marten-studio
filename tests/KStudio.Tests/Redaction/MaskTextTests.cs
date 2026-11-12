using KStudio.Core.Redaction;
using KStudio.Core.Scanning;

namespace KStudio.Tests.Redaction;

/// <summary>There is one mask text: the payload redactor, a masked field's display and the extractor's check all build it from the same prefix.</summary>
public class MaskTextTests
{
    [Theory]
    [InlineData("$.customer.email")]
    [InlineData("pattern phone")]
    [InlineData("the built-in header deny-list")]
    public void The_payload_mask_the_field_display_and_the_prefix_agree(string rule)
    {
        string payload = PayloadRedactor.MaskText(rule);

        payload.Should().Be(KafkaMatchField.Masked(rule).Display);
        payload.Should().Be(KafkaMatchMasks.MaskPrefix + " " + rule + ")");
        payload.Should().StartWith(KafkaMatchMasks.MaskPrefix);
    }
}
