using Miningcore.Mining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;

namespace Miningcore.Configuration;

public enum PostgresSslMode
{
    Disable,
    Allow,
    Prefer,
    Require,
    VerifyCA,
    VerifyFull,
}

// Do not accept integer enum values, comma-separated names or whitespace aliases.
public sealed class PostgresSslModeConverter : JsonConverter
{
    public override bool CanConvert(Type type) =>
        type == typeof(PostgresSslMode) || type == typeof(PostgresSslMode?);

    public override object ReadJson(JsonReader reader, Type type, object existingValue,
        JsonSerializer serializer)
    {
        if(reader.TokenType == JsonToken.Null && type == typeof(PostgresSslMode?))
            return null;
        if(reader.TokenType == JsonToken.String &&
           Enum.GetNames<PostgresSslMode>().Contains((string) reader.Value, StringComparer.Ordinal))
            return Enum.Parse<PostgresSslMode>((string) reader.Value);
        throw new JsonSerializationException("PostgreSQL sslMode must be Disable, Allow, Prefer, Require, VerifyCA or VerifyFull");
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) =>
        writer.WriteValue(value?.ToString());
}

internal static class PostgresConnectionPolicy
{
    internal static void ValidateSyntax(JObject document)
    {
        if(document.GetValue("persistence", StringComparison.OrdinalIgnoreCase) is not JObject persistence ||
           persistence.GetValue("postgres", StringComparison.OrdinalIgnoreCase) is not JObject postgres)
            return;
        // Reject malformed security fields before schema errors can echo their values.
        foreach(var property in postgres.Properties())
        {
            var name = property.Name.ToLowerInvariant();
            var token = property.Value;
            if(token.Type == JTokenType.Null)
                continue;
            var valid = name switch
            {
                "sslmode" => token.Type == JTokenType.String &&
                    Enum.GetNames<PostgresSslMode>().Contains(token.Value<string>(), StringComparer.Ordinal),
                "tls" or "tlsnovalidate" => token.Type == JTokenType.Boolean,
                "tlscert" or "tlskey" or "tlspassword" or "tlsrootcert" => token.Type == JTokenType.String,
                _ => true,
            };
            if(!valid)
                throw Invalid("invalid TLS setting type or sslMode name");
        }
    }

    internal static NpgsqlConnectionStringBuilder Build(PostgresConfig config) =>
        Build(config, Environment.GetEnvironmentVariable("PGSSLROOTCERT"));

    // Resolve the environment once, then pin the effective CA in the connection string.
    internal static NpgsqlConnectionStringBuilder Build(PostgresConfig config, string environmentRoot)
    {
        if(config == null)
            throw Invalid("configuration is missing");
        if(string.IsNullOrWhiteSpace(config.Host))
            throw Invalid("host is missing");
        if(config.Port is < 1 or > ushort.MaxValue)
            throw Invalid("port must be between 1 and 65535");
        if(string.IsNullOrWhiteSpace(config.Database))
            throw Invalid("database is missing");
        if(string.IsNullOrWhiteSpace(config.User))
            throw Invalid("user is missing");
        if(config.CommandTimeout < 0)
            throw Invalid("commandTimeout must be nonnegative");
        if(config.SslMode.HasValue && !Enum.IsDefined(config.SslMode.Value))
            throw Invalid("sslMode is unknown");
        if(config.SslMode.HasValue && (config.Tls.HasValue || config.TlsNoValidate.HasValue))
            throw Invalid("sslMode cannot be combined with tls or tlsNoValidate; omit legacy flags");
        if(config.TlsNoValidate == true && config.Tls != true)
            throw Invalid("tlsNoValidate requires legacy tls=true");

        var mode = config.SslMode ?? (config.Tls == true ? PostgresSslMode.Require : PostgresSslMode.Prefer);
        var verifying = mode is PostgresSslMode.VerifyCA or PostgresSslMode.VerifyFull;
        if(config.TlsRootCert != null && !verifying)
            throw Invalid("tlsRootCert requires VerifyCA or VerifyFull");
        var root = verifying ? config.TlsRootCert ?? environmentRoot : null;
        if(root != null && string.IsNullOrWhiteSpace(root))
            throw Invalid("root CA setting must be omitted or a nonempty file path");

        // Preserve legacy trimming of client paths, but never trim passwords or the new CA path.
        var cert = string.IsNullOrWhiteSpace(config.TlsCert) ? null : config.TlsCert.Trim();
        var key = string.IsNullOrWhiteSpace(config.TlsKey) ? null : config.TlsKey.Trim();
        var password = string.IsNullOrEmpty(config.TlsPassword) ? null : config.TlsPassword;
        if((cert != null || key != null || password != null) &&
           mode is PostgresSslMode.Disable or PostgresSslMode.Allow or PostgresSslMode.Prefer)
            throw Invalid("client TLS settings require Require, VerifyCA or VerifyFull");

        // Values are data, never connection-string fragments. Do not add validation bypasses.
        return new NpgsqlConnectionStringBuilder
        {
            Host = config.Host,
            Port = config.Port,
            Database = config.Database,
            Username = config.User,
            Password = config.Password ?? string.Empty,
            SslMode = Enum.Parse<SslMode>(mode.ToString()),
            RootCertificate = root,
            SslCertificate = cert,
            SslKey = key,
            SslPassword = password,
            CommandTimeout = config.CommandTimeout ?? 300,
        };
    }

    internal static string Diagnostic(PostgresConfig config, NpgsqlConnectionStringBuilder connection) =>
        // Only reviewed numeric, enum and presence fields; no operator-controlled strings.
        JObject.FromObject(new
        {
            connection.Port,
            SslMode = connection.SslMode.ToString(),
            TlsNoValidate = config.TlsNoValidate == true,
            PasswordConfigured = !string.IsNullOrEmpty(config.Password),
            TlsCertConfigured = !string.IsNullOrWhiteSpace(config.TlsCert),
            TlsKeyConfigured = !string.IsNullOrWhiteSpace(config.TlsKey),
            TlsPasswordConfigured = !string.IsNullOrEmpty(config.TlsPassword),
            RootCertificateConfigured = !string.IsNullOrEmpty(connection.RootCertificate),
            connection.CommandTimeout,
        }, JsonSerializer.Create(new JsonSerializerSettings())).ToString(Formatting.None);

    private static PoolStartupException Invalid(string reason) =>
        new("PostgreSQL configuration: " + reason);
}
