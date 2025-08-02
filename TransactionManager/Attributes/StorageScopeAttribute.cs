namespace TransactionManager.Attributes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface,
                Inherited = false, AllowMultiple = false)]
    public sealed class StorageScopeAttribute : Attribute { }

}
