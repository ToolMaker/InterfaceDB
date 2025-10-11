using System;

namespace Idb.Libraries.Abstractions.Serialization;

public interface IObjectChangeDetector
{
    ObjectFingerprint ComputeFingerprint<T>(T value);
    ObjectFingerprint ComputeFingerprint(object? value, Type valueType);
    bool HasChanged(ObjectFingerprint previous, ObjectFingerprint current);
}
