using System.Net;
using System.Net.Security;
using System.Runtime.Serialization;
using MailKit.Security;
using Newtonsoft.Json;

namespace Miningcore.Configuration;

public enum SmtpTlsMode
{
    [EnumMember(Value = "startTls")]
    StartTls,
    [EnumMember(Value = "implicitTls")]
    ImplicitTls,
    [EnumMember(Value = "localPlaintext")]
    LocalPlaintext,
}

// Accept only the documented wire values, never integers, aliases, or whitespace.
public sealed class SmtpTlsModeConverter : JsonConverter
{
    public override bool CanConvert(Type type) =>
        type == typeof(SmtpTlsMode) || type == typeof(SmtpTlsMode?);

    public override object ReadJson(JsonReader reader, Type type, object existingValue,
        JsonSerializer serializer)
    {
        if(reader.TokenType == JsonToken.Null && type == typeof(SmtpTlsMode?))
            return null;

        return reader.TokenType == JsonToken.String ? reader.Value switch
        {
            "startTls" => SmtpTlsMode.StartTls,
            "implicitTls" => SmtpTlsMode.ImplicitTls,
            "localPlaintext" => SmtpTlsMode.LocalPlaintext,
            _ => throw InvalidMode(),
        } : throw InvalidMode();
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) =>
        writer.WriteValue(value == null ? null : ToConfigValue((SmtpTlsMode) value));

    internal static string ToConfigValue(SmtpTlsMode mode) => mode switch
    {
        SmtpTlsMode.StartTls => "startTls",
        SmtpTlsMode.ImplicitTls => "implicitTls",
        SmtpTlsMode.LocalPlaintext => "localPlaintext",
        _ => throw InvalidMode(),
    };

    private static JsonSerializationException InvalidMode() => new(
        "notifications.email.tlsMode must be startTls, implicitTls, or localPlaintext");
}

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
            case SmtpTlsMode.ImplicitTls:
                if(config.Port is 25 or 587)
                    throw new InvalidOperationException("notifications.email port 25/587 requires startTls");
                return SecureSocketOptions.SslOnConnect;

            case SmtpTlsMode.StartTls:
                if(config.Port == 465)
                    throw new InvalidOperationException("notifications.email port 465 requires implicitTls");
                return SecureSocketOptions.StartTls;

            case SmtpTlsMode.LocalPlaintext:
                if(config.Port == 465 || !IPAddress.TryParse(config.Host, out var address) ||
                    !IPAddress.IsLoopback(address) ||
                    !string.IsNullOrEmpty(config.User) || !string.IsNullOrEmpty(config.Password))
                    throw new InvalidOperationException(
                        "notifications.email localPlaintext requires a loopback IP address and no credentials");
                return SecureSocketOptions.None;

            default:
                throw new InvalidOperationException(
                    "notifications.email.tlsMode must be startTls, implicitTls, or localPlaintext");
        }
    }
}
