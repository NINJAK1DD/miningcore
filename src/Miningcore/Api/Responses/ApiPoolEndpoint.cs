namespace Miningcore.Api.Responses;

public class ApiVarDiffConfig
{
    public double MinDiff { get; set; }
    public double? MaxDiff { get; set; }
    public double? MaxDelta { get; set; }
    public double TargetTime { get; set; }
    public double RetargetTime { get; set; }
    public double VariancePercent { get; set; }
}

public class ApiTcpProxyProtocolConfig
{
    public bool Enable { get; set; }
    public bool Mandatory { get; set; }
}

/// <summary>
/// Public projection of a Stratum endpoint. Runtime-only TLS credentials and
/// trusted PROXY-protocol peer addresses deliberately have no representation.
/// </summary>
public class ApiPoolEndpoint
{
    public string ListenAddress { get; set; }
    public string Name { get; set; }
    public double Difficulty { get; set; }
    public ApiTcpProxyProtocolConfig TcpProxyProtocol { get; set; }
    public ApiVarDiffConfig VarDiff { get; set; }
    public bool Tls { get; set; }
    public bool TlsAuto { get; set; }
}
