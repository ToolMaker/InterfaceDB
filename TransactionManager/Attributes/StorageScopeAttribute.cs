namespace TransactionManager.Attributes
{
    namespace TransactionManager.Attributes
    {
        /// <summary>
        /// Marks a concrete class or struct as an on-disk storage scope.
        /// Interfaces and abstract classes are ignored.
        /// </summary>
        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct,
                        Inherited = false, AllowMultiple = false)]
        public sealed class StorageScopeAttribute : Attribute { }
    }


}
