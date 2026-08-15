using Microsoft.AspNetCore.Http;

namespace Miningcore.Api;

internal static class ProtectedRouteClassifier
{
    public const string AdminRoutePrefix = "/api/admin";
    public const string MetricsRoutePrefix = "/metrics";

    public static bool IsAdminRequest(PathString path) =>
        path.StartsWithSegments(AdminRoutePrefix,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsMetricsRequest(PathString path) =>
        path.StartsWithSegments(MetricsRoutePrefix,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsProtectedRequest(PathString path) =>
        IsAdminRequest(path) || IsMetricsRequest(path);
}
