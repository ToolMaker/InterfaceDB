namespace Idb.Storage.AtlasCore.Internal;

internal sealed class AtlasRouteRejectedException(string message) : InvalidOperationException(message);
