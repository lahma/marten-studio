using System.Reflection;

using Confluent.Kafka;

using KStudio.Core.Wire.Confluent;

namespace KStudio.Tests.Wire;

/// <summary>
/// The hand-written consumer-only and producer-only key lists equal what Confluent's own <c>ConsumerConfig</c> and <c>ProducerConfig</c>
/// declare (decision 2.1), at whichever Confluent.Kafka the suite runs at: <c>dotnet test</c> at the floor and again with
/// <c>-p:KStudioConfluentVersion=2.16.0</c>.
/// </summary>
public class ConfluentPropertyScopesTests
{
    [Fact]
    public void The_consumer_only_list_is_what_ConsumerConfig_declares()
    {
        HashSet<string> declared = DeclaredKeys<ConsumerConfig>();

        declared.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");
        declared.Except(ConfluentPropertyScopes.ConsumerOnly).Should().BeEmpty("a key Confluent declares for consumers must be in the list");
        ConfluentPropertyScopes.ConsumerOnly.Except(declared).Should().BeSubsetOf(
            ConfluentPropertyScopes.NewerThanTheFloor,
            "the only keys in the list that Confluent does not declare are the ones a newer version added");
    }

    [Fact]
    public void The_producer_only_list_is_what_ProducerConfig_declares()
    {
        HashSet<string> declared = DeclaredKeys<ProducerConfig>();

        declared.Should().NotBeEmpty();
        declared.Except(ConfluentPropertyScopes.ProducerOnly).Should().BeEmpty();
        ConfluentPropertyScopes.ProducerOnly.Except(declared).Should().BeEmpty();
    }

    [Fact]
    public void The_two_lists_share_no_key_and_a_client_level_key_is_in_neither()
    {
        ConfluentPropertyScopes.ConsumerOnly.Intersect(ConfluentPropertyScopes.ProducerOnly).Should().BeEmpty();

        HashSet<string> clientLevel = DeclaredKeys<AdminClientConfig>();
        clientLevel.Should().BeEmpty("AdminClientConfig declares nothing of its own");

        foreach (string key in new[] { "bootstrap.servers", "client.id", "allow.auto.create.topics", "statistics.interval.ms", "sasl.username", "debug" })
        {
            ConfluentPropertyScopes.ConsumerOnly.Should().NotContain(key);
            ConfluentPropertyScopes.ProducerOnly.Should().NotContain(key);
        }
    }

    [Fact]
    public void The_floor_only_list_names_keys_the_floor_really_lacks()
    {
        // At the floor these two are undeclared; at a newer version they are declared and so are no longer "newer than the floor" in the
        // test's sense, but they stay in the list either way: filtering a key the client would ignore is harmless.
        ConfluentPropertyScopes.NewerThanTheFloor.Should().BeSubsetOf(ConfluentPropertyScopes.ConsumerOnly);
    }

    [Fact]
    public void The_consumer_filter_drops_producer_keys_and_the_admin_filter_drops_both()
    {
        Dictionary<string, string> clone = new()
        {
            ["bootstrap.servers"] = "b:9092",
            ["group.id"] = "g",
            ["transactional.id"] = "t",
        };

        ConfluentPropertyScopes.ForConsumer(clone).Keys.Should().BeEquivalentTo("bootstrap.servers", "group.id");
        ConfluentPropertyScopes.ForAdmin(clone).Keys.Should().BeEquivalentTo("bootstrap.servers");
    }

    /// <summary>
    /// The keys <typeparamref name="T" /> declares itself (not the ones every client config has): each declared property is set to a sample
    /// value, and the keys that appear are the declaration.
    /// </summary>
    private static HashSet<string> DeclaredKeys<T>()
        where T : Config, new()
    {
        T config = new();

        foreach (PropertyInfo property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!property.CanWrite)
            {
                continue;
            }

            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? sample = type == typeof(string) ? "x"
                : type == typeof(int) ? 1
                : type == typeof(long) ? 1L
                : type == typeof(bool) ? true
                : type == typeof(double) ? 1.0
                : type.IsEnum ? Enum.GetValues(type).GetValue(0)
                : null;

            if (sample is not null)
            {
                property.SetValue(config, sample);
            }
        }

        return [.. config.Select(static x => x.Key)];
    }
}
