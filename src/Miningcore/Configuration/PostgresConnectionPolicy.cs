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
    private static readonly PostgresSslMode[] VerifyingModes =
        [PostgresSslMode.VerifyCA, PostgresSslMode.VerifyFull];

    internal static bool UsesAmbientTrust(PostgresConnectionDiagnostic diagnostic) =>
        !diagnostic.RootCertificatePathConfigured && VerifyingModes.Any(mode => ToDriverMode(mode) == diagnostic.SslMode);

    internal static void AddSchemaRules(JObject document)
    {
        // Structural exclusions also help editor/offline validation. Runtime validation
        // remains authoritative for case-insensitive binding and environment sources.
        document["definitions"]["PostgresConfig"]["allOf"] = JArray.Parse($$"""
            [
              { "not": {
                  "type": "object",
                  "required": ["sslMode", "tls"],
                  "properties": { "sslMode": { "type": "string" }, "tls": { "type": "boolean" } }
              } },
              { "not": {
                  "type": "object",
                  "required": ["sslMode", "tlsNoValidate"],
                  "properties": { "sslMode": { "type": "string" }, "tlsNoValidate": { "type": "boolean" } }
              } },
              { "not": {
                  "type": "object",
                  "required": ["tlsRootCert"],
                  "properties": { "tlsRootCert": { "type": "string" } },
                  "not": {
                    "required": ["sslMode"],
                    "properties": { "sslMode": { "enum": {{new JArray(VerifyingModes.Select(mode => mode.ToString())).ToString(Formatting.None)}} } }
                  }
              } }
            ]
            """);
    }

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
                // name is one of the fixed switch labels above, never an arbitrary key/value.
                throw Invalid($"invalid TLS setting '{name}': check its type or sslMode name");
        }

        // Give the same actionable, value-free policy error before schema exclusions fire.
        // All tokens used here have passed the fixed security-field type checks above.
        var mode = postgres.GetValue("sslMode", StringComparison.OrdinalIgnoreCase)?.Value<string>();
        var tls = postgres.GetValue("tls", StringComparison.OrdinalIgnoreCase);
        var noValidate = postgres.GetValue("tlsNoValidate", StringComparison.OrdinalIgnoreCase);
        if(mode != null && (tls?.Type == JTokenType.Boolean || noValidate?.Type == JTokenType.Boolean))
            throw Invalid("sslMode cannot be combined with tls or tlsNoValidate; omit legacy flags");
        if(postgres.GetValue("tlsRootCert", StringComparison.OrdinalIgnoreCase)?.Type == JTokenType.String &&
           !VerifyingModes.Any(candidate => candidate.ToString() == mode))
            throw Invalid("tlsRootCert requires VerifyCA or VerifyFull");
    }

    internal static NpgsqlConnectionStringBuilder Build(PostgresConfig config) =>
        Build(config, Environment.GetEnvironmentVariable("PGSSLROOTCERT"));

    internal static NpgsqlConnectionStringBuilder Build(PostgresConfig config, out PostgresConnectionDiagnostic diagnostic) =>
        Build(config, Environment.GetEnvironmentVariable("PGSSLROOTCERT"), out diagnostic);

    internal static NpgsqlConnectionStringBuilder Build(PostgresConfig config, string environmentRoot) =>
        Build(config, environmentRoot, out _);

    internal static void Validate(PostgresConfig config) =>
        Resolve(config, Environment.GetEnvironmentVariable("PGSSLROOTCERT"));

    private static (SslMode Mode, string Root, string Cert, string Key, string Password) Resolve(
        PostgresConfig config, string environmentRoot)
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
        var verifying = VerifyingModes.Contains(mode);
        if(config.TlsRootCert != null && !verifying)
            throw Invalid("tlsRootCert requires VerifyCA or VerifyFull");
        var root = verifying ? config.TlsRootCert ?? environmentRoot : null;
        if(root != null && string.IsNullOrWhiteSpace(root))
            throw Invalid("root CA setting must be omitted or a nonempty file path");

        // Empty client paths remain omitted for existing example configurations. Whitespace
        // alone is a likely mistake and must not silently disable client authentication.
        var cert = ClientPath(config.TlsCert, "tlsCert");
        var key = ClientPath(config.TlsKey, "tlsKey");
        var password = string.IsNullOrEmpty(config.TlsPassword) ? null : config.TlsPassword;
        if((cert != null || key != null || password != null) &&
           mode is PostgresSslMode.Disable or PostgresSslMode.Allow or PostgresSslMode.Prefer)
            throw Invalid("client TLS settings require Require, VerifyCA or VerifyFull");

        return (ToDriverMode(mode), root, cert, key, password);
    }

    private static SslMode ToDriverMode(PostgresSslMode mode) => mode switch
    {
        PostgresSslMode.Disable => SslMode.Disable,
        PostgresSslMode.Allow => SslMode.Allow,
        PostgresSslMode.Prefer => SslMode.Prefer,
        PostgresSslMode.Require => SslMode.Require,
        PostgresSslMode.VerifyCA => SslMode.VerifyCA,
        PostgresSslMode.VerifyFull => SslMode.VerifyFull,
        _ => throw Invalid("sslMode is unknown"),
    };

    private static string ClientPath(string path, string field)
    {
        if(string.IsNullOrEmpty(path))
            return null;
        if(string.IsNullOrWhiteSpace(path))
            throw Invalid(field + " must be omitted, empty or a nonempty file path");
        return path.Trim();
    }

    internal static NpgsqlConnectionStringBuilder Build(PostgresConfig config, string environmentRoot,
        out PostgresConnectionDiagnostic diagnostic)
    {
        var settings = Resolve(config, environmentRoot);
        diagnostic = new(config.Port, settings.Mode, config.TlsNoValidate == true,
            !string.IsNullOrEmpty(config.Password), settings.Cert != null, settings.Key != null,
            settings.Password != null, settings.Root != null, config.CommandTimeout ?? 300);

        // Explicit and present environment CA paths are copied as data. If neither exists,
        // Npgsql resolves environment/default trust sources at each physical open. Paths
        // do not freeze file contents. See docs/postgres-tls.md for this lifetime contract.
        // Values are data, never connection-string fragments. Do not add validation bypasses.
        return new NpgsqlConnectionStringBuilder
        {
            Host = config.Host,
            Port = config.Port,
            Database = config.Database,
            Username = config.User,
            // Npgsql parses the legacy "Password=;" as omitted as well. Preserve its
            // environment/passfile fallback explicitly instead of emitting an empty key.
            Password = string.IsNullOrEmpty(config.Password) ? null : config.Password,
            SslMode = settings.Mode,
            RootCertificate = settings.Root,
            SslCertificate = settings.Cert,
            SslKey = settings.Key,
            SslPassword = settings.Password,
            CommandTimeout = config.CommandTimeout ?? 300,
        };
    }

    internal static string Diagnostic(PostgresConnectionDiagnostic diagnostic) =>
        // Only reviewed numeric, enum and presence fields; no operator-controlled strings.
        JObject.FromObject(new
        {
            diagnostic.Port,
            SslMode = diagnostic.SslMode.ToString(),
            diagnostic.TlsNoValidate,
            diagnostic.PasswordConfigured,
            diagnostic.TlsCertConfigured,
            diagnostic.TlsKeyConfigured,
            diagnostic.TlsPasswordConfigured,
            diagnostic.RootCertificatePathConfigured,
            diagnostic.CommandTimeout,
        }, JsonSerializer.Create(new JsonSerializerSettings())).ToString(Formatting.None);

    private static PoolStartupException Invalid(string reason) =>
        new("PostgreSQL configuration: " + reason);
}

// The logging boundary accepts no credentials, paths, endpoints or connection builder.
internal readonly record struct PostgresConnectionDiagnostic(int Port, SslMode SslMode,
    bool TlsNoValidate, bool PasswordConfigured, bool TlsCertConfigured, bool TlsKeyConfigured,
    bool TlsPasswordConfigured, bool RootCertificatePathConfigured, int CommandTimeout);
