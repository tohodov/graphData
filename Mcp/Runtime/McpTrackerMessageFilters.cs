using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using JsonRpcErrorMessage = ModelContextProtocol.Protocol.JsonRpcError;
using JsonRpcMessage = ModelContextProtocol.Protocol.JsonRpcMessage;
using JsonRpcMessageWithId = ModelContextProtocol.Protocol.JsonRpcMessageWithId;
using JsonRpcNotificationMessage = ModelContextProtocol.Protocol.JsonRpcNotification;
using JsonRpcRequestMessage = ModelContextProtocol.Protocol.JsonRpcRequest;
using JsonRpcResponseMessage = ModelContextProtocol.Protocol.JsonRpcResponse;
using RequestId = ModelContextProtocol.Protocol.RequestId;
using TrackerLog = Protocol.Log;
using TrackerMessageKind = Protocol.MessageKind;

namespace GraphData.Mcp.Runtime;

public static class McpTrackerMessageFilterExtensions {
    public static IMcpServerBuilder WithMcpTrackerMessageFilters(this IMcpServerBuilder builder) {
        builder.Services.TryAddSingleton<McpTrackerCallRegistry>();
        return builder.WithMessageFilters(filters => {
            filters.AddIncomingFilter(next => async (context, cancellationToken) => {
                McpTrackerMessageLogger.LogIncoming(context);
                await next(context, cancellationToken).ConfigureAwait(false);
            });
            filters.AddOutgoingFilter(next => async (context, cancellationToken) => {
                McpTrackerMessageLogger.LogOutgoing(context);
                await next(context, cancellationToken).ConfigureAwait(false);
            });
        });
    }
}

sealed class McpTrackerCallRegistry {
    readonly ConcurrentDictionary<string, string> _endpoints = new();

    public void Track(string callId, string endpoint) {
        if (callId.Length == 0)
            return;
        _endpoints[callId] = endpoint;
    }

    public string TakeEndpoint(string callId) {
        if (callId.Length == 0)
            return "";
        return _endpoints.TryRemove(callId, out var endpoint) ? endpoint : "";
    }
}

static class McpTrackerMessageLogger {
    const string ToolsCallMethod = "tools/call";

    public static void LogIncoming(MessageContext context) {
        switch (context.JsonRpcMessage) {
            case JsonRpcNotificationMessage notification:
                AddLog(context, TrackerMessageKind.Notification, SerializeMessage(notification), "", notification.Method ?? "");
                break;
            case JsonRpcRequestMessage request:
                var callId = FormatRequestId(request.Id);
                var (endpoint, text) = GetRequestDetails(request);
                GetServices(context)?.GetService<McpTrackerCallRegistry>()?.Track(callId, endpoint);
                AddLog(context, TrackerMessageKind.RequestReceived, text, callId, endpoint);
                break;
        }
    }

    public static void LogOutgoing(MessageContext context) {
        switch (context.JsonRpcMessage) {
            case JsonRpcResponseMessage response:
                LogResponse(context, response);
                break;
            case JsonRpcErrorMessage error:
                LogResponse(context, error);
                break;
        }
    }

    static void LogResponse(MessageContext context, JsonRpcMessageWithId message) {
        var callId = FormatRequestId(message.Id);
        var endpoint = GetServices(context)?.GetService<McpTrackerCallRegistry>()?.TakeEndpoint(callId) ?? "";
        AddLog(context, TrackerMessageKind.ResponseSent, SerializeMessage(message), callId, endpoint);
    }

    static (string Endpoint, string Text) GetRequestDetails(JsonRpcRequestMessage request) {
        var endpoint = request.Method ?? "";
        var text = SerializeValue(request.Params, "{}");
        if (!string.Equals(request.Method, ToolsCallMethod, StringComparison.Ordinal))
            return (endpoint, text);
        if (TryGetObjectProperty(request.Params, "name", out var name)
            && name.ValueKind == JsonValueKind.String)
            endpoint = name.GetString() ?? endpoint;
        if (TryGetObjectProperty(request.Params, "arguments", out var arguments))
            text = FormatJsonElement(arguments, "{}");
        return (endpoint, text);
    }

    static bool TryGetObjectProperty(object? value, string name, out JsonElement property) {
        if (value is JsonElement element)
            return TryGetObjectProperty(element, name, out property);
        if (value is JsonDocument document)
            return TryGetObjectProperty(document.RootElement, name, out property);
        if (value is null) {
            property = default;
            return false;
        }
        try {
            using var serializedDocument = JsonSerializer.SerializeToDocument(value, value.GetType(), McpJsonUtilities.DefaultOptions);
            if (TryGetObjectProperty(serializedDocument.RootElement, name, out property)) {
                property = property.Clone();
                return true;
            }
            return false;
        } catch (NotSupportedException) {
            property = default;
            return false;
        } catch (JsonException) {
            property = default;
            return false;
        }
    }

    static bool TryGetObjectProperty(JsonElement element, string name, out JsonElement property) {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out property))
            return true;
        property = default;
        return false;
    }

    static string FormatRequestId(RequestId id) {
        return id.Id switch {
            null => "",
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var value => value.ToString() ?? ""
        };
    }

    static string SerializeMessage(JsonRpcMessage message) {
        return JsonSerializer.Serialize(message, message.GetType(), McpJsonUtilities.DefaultOptions);
    }

    static string SerializeValue(object? value, string emptyValue) {
        if (value is null)
            return emptyValue;
        if (value is JsonElement element)
            return FormatJsonElement(element, emptyValue);
        if (value is JsonDocument document)
            return FormatJsonElement(document.RootElement, emptyValue);
        return JsonSerializer.Serialize(value, value.GetType(), McpJsonUtilities.DefaultOptions);
    }

    static string FormatJsonElement(JsonElement element, string emptyValue) {
        return element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? emptyValue
            : element.GetRawText();
    }

    static void AddLog(MessageContext context, TrackerMessageKind kind, string text, string callId, string endpoint) {
        GetServices(context)?.GetService<MessageQueue>()?.Add(new TrackerLog {
            Kind = kind,
            Text = text,
            CallId = callId,
            Endpoint = endpoint
        });
    }

    static IServiceProvider? GetServices(MessageContext context) {
        return context.Services ?? context.Server.Services;
    }
}
