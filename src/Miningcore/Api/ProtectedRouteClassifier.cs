using Microsoft.AspNetCore.Http;

namespace Miningcore.Api;

internal static class ProtectedRouteClassifier
{
    public const string AdminRoutePrefix = "/api/admin";
    public const string MetricsRoutePrefix = "/metrics";
    public const string AdminRouteFamily = "admin";
    public const string MetricsRouteFamily = "metrics";
    public const string OtherRouteFamily = "other";

    public static bool IsAdminRequest(PathString path) =>
        path.StartsWithSegments(AdminRoutePrefix,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsMetricsRequest(PathString path) =>
        path.StartsWithSegments(MetricsRoutePrefix,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsProtectedRequest(PathString path) =>
        IsAdminRequest(path) || IsMetricsRequest(path);

    public static string ClassifyWhitelistLocations(string[] locations)
    {
        var hasAdmin = false;
        var hasMetrics = false;
        var hasOther = false;

        foreach(var location in locations ?? Array.Empty<string>())
        {
            if(string.IsNullOrEmpty(location))
            {
                hasOther = true;
                continue;
            }

            var path = new PathString(location);

            if(IsAdminRequest(path))
                hasAdmin = true;
            else if(IsMetricsRequest(path))
                hasMetrics = true;
            else
                hasOther = true;
        }

        // Metric labels must remain fixed-cardinality. Never return a configured
        // location, request path, source address or any other attacker-controlled
        // value from this classifier.
        if(hasAdmin && !hasMetrics && !hasOther)
            return AdminRouteFamily;
        if(hasMetrics && !hasAdmin && !hasOther)
            return MetricsRouteFamily;

        return OtherRouteFamily;
    }
}
