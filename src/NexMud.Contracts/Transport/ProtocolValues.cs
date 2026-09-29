namespace NexMud.Contracts.Transport;

/// <summary>
/// Lossless representation of an MSDP value. MSDP can carry scalars, arrays, and nested tables.
/// Keeping that shape intact lets game adapters decide how to map server-specific payloads into
/// semantic state without the transport layer flattening or guessing at their meaning.
/// </summary>
public abstract record MsdpValue;

public sealed record MsdpScalar(string Value) : MsdpValue;

public sealed record MsdpArray(IReadOnlyList<MsdpValue> Values) : MsdpValue;

public sealed record MsdpTable(IReadOnlyDictionary<string, MsdpValue> Values) : MsdpValue;
