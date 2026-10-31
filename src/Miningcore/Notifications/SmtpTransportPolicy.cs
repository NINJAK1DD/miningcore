using System.Net;
using System.Net.Security;
using MailKit.Security;
using Miningcore.Configuration;

namespace Miningcore.Notifications;

internal static class SmtpTransportPolicy
{
    internal static bool CertificateIsValid(SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None;

    internal static SecureSocketOptions Resolve(EmailSenderConfig config)
    {
        if(config == null || string.IsNullOrWhiteSpace(config.Host) ||
            config.Port is < 1 or > 65535)
            throw new InvalidOperationException("notifications.email requires a valid host and port");

        if(string.IsNullOrEmpty(config.User) != string.IsNullOrEmpty(config.Password))
            throw new InvalidOperationException("notifications.email requires both user and password, or neither");

        switch(config.TlsMode)
        {
            case "implicitTls":
                if(config.Port is 25 or 587)
                    throw new InvalidOperationException("notifications.email port 25/587 requires startTls");
                return SecureSocketOptions.SslOnConnect;

            case "startTls":
                if(config.Port == 465)
                    throw new InvalidOperationException("notifications.email port 465 requires implicitTls");
                return SecureSocketOptions.StartTls;

            case "localPlaintext":
                if(config.Port == 465 || !IPAddress.TryParse(config.Host, out var address) ||
                    !IPAddress.IsLoopback(address) ||
                    !string.IsNullOrEmpty(config.User) || !string.IsNullOrEmpty(config.Password))
                    throw new InvalidOperationException(
                        "notifications.email localPlaintext requires a loopback IP address and no credentials");
                return SecureSocketOptions.None;

            default:
                throw new InvalidOperationException(
                    "notifications.email.tlsMode must be implicitTls, startTls, or localPlaintext");
        }
    }
}
