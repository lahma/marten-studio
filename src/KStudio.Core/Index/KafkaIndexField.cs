namespace KStudio;

/// <summary>The field of a record a <see cref="KafkaIndexQuery" /> asks about.</summary>
public enum KafkaIndexField
{
    /// <summary>The record's key.</summary>
    Key,

    /// <summary>A header, by name (<see cref="KafkaIndexQuery.FieldName" />).</summary>
    Header,

    /// <summary>A CloudEvents attribute, by name (<see cref="KafkaIndexQuery.FieldName" />).</summary>
    CloudEventAttribute,
}
