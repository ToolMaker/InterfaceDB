namespace RepoTcpHost.Configuration;

public sealed class TcpConfig
{
    public string IpAddress { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 5051;
    public int Backlog { get; init; } = 4096;
}

public sealed class SerializationConfig
{
    public string Compression { get; init; } = "None";
}
