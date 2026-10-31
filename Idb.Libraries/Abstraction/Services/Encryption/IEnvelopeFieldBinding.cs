using Idb.Libraries.Abstractions.Dtos;
using Idb.Libraries.Abstractions.Services.Serialization;

namespace Idb.Libraries.Abstractions.Services.Encryption;

/// <summary>Finalizes encrypted fields after the provider chooses the physical record identity.</summary>
public interface IEnvelopeFieldBinding<TContract> where TContract : class, IStoredContract<TContract>
{
    void BindFields(IObjectEnvelope<TContract> envelope);
}

public enum EncryptedFieldWriteMode
{
    /// <summary>Authenticate fields against repository, physical record identity and field name.</summary>
    ContextBound,
    /// <summary>Temporary reader-first rollout only. Does not protect fields from ciphertext substitution.</summary>
    LegacyContextless
}
