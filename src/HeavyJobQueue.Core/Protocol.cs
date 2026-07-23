using System.Globalization;
using System.Text.Json;

namespace HeavyJobQueue.Core;

public static class Protocol
{
    public const int Version = 2;
    public const string PipeName = "GitHubCopilot.HeavyJobQueue.v2";
    public const int MaximumMessageLength = 65_536;

    public static ClientMessage ParseClientMessage(string? line)
    {
        if (line is null)
        {
            throw new ProtocolException("client_disconnected", "The client disconnected.");
        }

        if (line.Length == 0 || line.Length > MaximumMessageLength)
        {
            throw new ProtocolException("invalid_message", "The message is empty or too large.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            throw new ProtocolException("malformed_json", "The message is not valid JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException("invalid_message", "The message must be a JSON object.");
            }

            var version = GetRequiredInt32(root, "version");
            if (version != Version)
            {
                throw new ProtocolException(
                    "incompatible_version",
                    $"Protocol version {version} is not supported; expected {Version}.");
            }

            var type = GetRequiredString(root, "type");
            return type switch
            {
                "enqueue" => ParseEnqueue(root, version, type),
                "complete" => ParseComplete(root, version, type),
                "cancel" => ParseCancel(root, version, type),
                _ => throw new ProtocolException("unknown_message_type", $"Unknown message type '{type}'.")
            };
        }
    }

    public static string Serialize(object message) =>
        JsonSerializer.Serialize(message, SerializerOptions);

    private static ClientMessage ParseEnqueue(JsonElement root, int version, string type)
    {
        var requestId = GetRequiredGuid(root, "requestId");
        var label = GetRequiredString(root, "label");
        var callerPid = GetRequiredInt32(root, "callerPid");
        var cwd = GetRequiredString(root, "cwd");
        var command = GetOptionalString(root, "command");
        var leaseName = GetRequiredLeaseName(root, requestId);
        var enqueuedAtText = GetRequiredString(root, "enqueuedAt");
        var waitTimeoutSeconds = GetRequiredInt32(root, "waitTimeoutSeconds");

        if (label.Length > 200)
        {
            throw new ProtocolException("invalid_label", "The job label cannot exceed 200 characters.");
        }

        if (callerPid <= 0)
        {
            throw new ProtocolException("invalid_pid", "callerPid must be greater than zero.");
        }

        if (cwd.Length > 32_767)
        {
            throw new ProtocolException("invalid_cwd", "cwd is too long.");
        }

        if (command?.Length > 32_767)
        {
            throw new ProtocolException("invalid_command", "command is too long.");
        }

        if (!DateTimeOffset.TryParse(
                enqueuedAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var enqueuedAt))
        {
            throw new ProtocolException("invalid_enqueue_time", "enqueuedAt must be an ISO 8601 timestamp.");
        }

        if (waitTimeoutSeconds is < 1 or > 86_400)
        {
            throw new ProtocolException(
                "invalid_timeout",
                "waitTimeoutSeconds must be between 1 and 86400.");
        }

        return new ClientMessage(
            version,
            type,
            requestId,
            label,
            callerPid,
            cwd,
            enqueuedAt,
            TimeSpan.FromSeconds(waitTimeoutSeconds),
            command,
            leaseName,
            null,
            null,
            null);
    }

    private static ClientMessage ParseComplete(JsonElement root, int version, string type)
    {
        var requestId = GetRequiredGuid(root, "requestId");
        var succeeded = GetRequiredBoolean(root, "succeeded");
        var exitCode = GetRequiredInt32(root, "exitCode");
        var error = GetOptionalString(root, "error");
        var leaseName = GetRequiredLeaseName(root, requestId);

        return new ClientMessage(
            version,
            type,
            requestId,
            null,
            null,
            null,
            null,
            null,
            null,
            leaseName,
            succeeded,
            exitCode,
            error);
    }

    private static ClientMessage ParseCancel(JsonElement root, int version, string type)
    {
        var requestId = GetRequiredGuid(root, "requestId");
        return new(
            version,
            type,
            requestId,
            null,
            null,
            null,
            null,
            null,
            null,
            GetRequiredLeaseName(root, requestId),
            null,
            null,
            GetOptionalString(root, "reason"));
    }

    private static string GetRequiredLeaseName(JsonElement root, Guid requestId)
    {
        var leaseName = GetRequiredString(root, "leaseName");
        var expected = RequestLease.GetName(requestId);
        if (!string.Equals(leaseName, expected, StringComparison.Ordinal))
        {
            throw new ProtocolException(
                "invalid_lease",
                $"'leaseName' must be '{expected}'.");
        }

        return leaseName;
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolException("missing_field", $"'{propertyName}' must be a string.");
        }

        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new ProtocolException("invalid_field", $"'{propertyName}' cannot be empty.");
        }

        return result;
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ProtocolException("invalid_field", $"'{propertyName}' must be a string.");
        }

        return value.GetString();
    }

    private static int GetRequiredInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            throw new ProtocolException("missing_field", $"'{propertyName}' must be a 32-bit integer.");
        }

        return result;
    }

    private static bool GetRequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ProtocolException("missing_field", $"'{propertyName}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static Guid GetRequiredGuid(JsonElement root, string propertyName)
    {
        var value = GetRequiredString(root, propertyName);
        if (!Guid.TryParse(value, out var result))
        {
            throw new ProtocolException("invalid_request_id", $"'{propertyName}' must be a GUID.");
        }

        return result;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed record ClientMessage(
    int Version,
    string Type,
    Guid RequestId,
    string? Label,
    int? CallerPid,
    string? Cwd,
    DateTimeOffset? EnqueuedAt,
    TimeSpan? WaitTimeout,
    string? Command,
    string? LeaseName,
    bool? Succeeded,
    int? ExitCode,
    string? Error);

public sealed class ProtocolException : Exception
{
    public ProtocolException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
