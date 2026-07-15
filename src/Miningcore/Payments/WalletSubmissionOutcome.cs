using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Miningcore.JsonRpc;
using Newtonsoft.Json;

namespace Miningcore.Payments;

/// <summary>
/// Shared fail-closed classification for wallet submission outcomes. Callers should use
/// these helpers only after beginning an operation that may broadcast or commit funds;
/// pre-submission validation failures remain ordinary, conclusive failures.
/// </summary>
public static class WalletSubmissionOutcome
{
    public const int TransportErrorCode = -500;

    public static bool IsUnknown(JsonRpcError error) =>
        error?.Code == TransportErrorCode;

    public static void ThrowIfUnknown(JsonRpcError error, string operation)
    {
        if(IsUnknown(error))
            throw new PayoutOutcomeUncertainException(
                $"{operation} outcome is unknown: {error.Message}", error.InnerException);
    }

    public static string RequireTransactionId(string transactionId, string operation)
    {
        if(string.IsNullOrWhiteSpace(transactionId))
            throw new PayoutOutcomeUncertainException(
                $"{operation} returned success without a transaction id");

        return transactionId;
    }

    public static void RethrowIfUnknown(Exception ex, string operation)
    {
        var uncertain = FindUncertain(ex);
        if(uncertain != null)
            throw uncertain;

        if(IsTransportFailure(ex))
            throw new PayoutOutcomeUncertainException(
                $"{operation} outcome is unknown because its response was not received", ex);
    }

    private static PayoutOutcomeUncertainException FindUncertain(Exception ex)
    {
        if(ex is PayoutOutcomeUncertainException uncertain)
            return uncertain;

        if(ex is AggregateException aggregate)
        {
            foreach(var inner in aggregate.Flatten().InnerExceptions)
            {
                var result = FindUncertain(inner);
                if(result != null)
                    return result;
            }
        }

        return ex?.InnerException != null ? FindUncertain(ex.InnerException) : null;
    }

    public static bool IsTransportFailure(Exception ex)
    {
        for(var current = ex; current != null; current = current.InnerException)
        {
            if(current is OperationCanceledException or TimeoutException or
               HttpRequestException or IOException or WebException or SocketException or
               JsonException or System.Text.Json.JsonException)
                return true;

            if(current is RpcException rpcException && rpcException.StatusCode is
               StatusCode.Cancelled or StatusCode.DeadlineExceeded or
               StatusCode.Unavailable or StatusCode.Unknown)
                return true;
        }

        return false;
    }
}
