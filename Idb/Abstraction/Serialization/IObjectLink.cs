namespace TransactionManager.Abstraction.Serialization
{
    public interface IObjectLink<TObjectLinkType> 
        where TObjectLinkType : class, new()
    {
        TObjectLinkType ObjectLinkRef { get; set; }

    }

    public interface IObjectLinker<TObjectLinkType>
       where TObjectLinkType : class, new()
    {
        IDictionary<TObjectLinkType, IObjectEnvelope> ObjectLinks { get; }

    }
}
