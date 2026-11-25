using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Karaokay.Contracts.Hubs;

namespace Karaokay.UnitTests;

/// <summary>
/// Compile-time bridge between the C# SignalR contracts and the TypeScript mirror
/// (docs/ARCHITECTURE.md §4.3 / constraint C4). The TypeScript file is scanned as text — the same
/// literal constants the browser runs — so the two sides can never drift silently.
/// </summary>
public sealed class SignalRContractTests
{
    private static readonly string ProtocolPath = Path.Combine(
        TestPaths.RepoRoot,
        "src",
        "clients",
        "web",
        "src",
        "lib",
        "signalr",
        "protocol.ts");

    [Fact]
    public void StageHubMethodNamesMatchTheTypeScriptMirror()
    {
        var csharpMethods = InterfaceMethodNames(typeof(IStageHub));
        var typescriptMethods = ParseConstObject(ReadProtocol(), "HUB_METHODS");

        Assert.Equal(15, csharpMethods.Count);
        Assert.Equal(
            csharpMethods.OrderBy(name => name, StringComparer.Ordinal),
            typescriptMethods.Values.OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(typescriptMethods, pair => Assert.Equal(pair.Key, pair.Value));
    }

    [Fact]
    public void StageHubClientMethodNamesMatchTheTypeScriptMirror()
    {
        var csharpMethods = InterfaceMethodNames(typeof(IStageHubClient));
        var typescriptMethods = ParseConstObject(ReadProtocol(), "CLIENT_METHODS");

        Assert.Equal(14, csharpMethods.Count);
        Assert.Equal(
            csharpMethods.OrderBy(name => name, StringComparer.Ordinal),
            typescriptMethods.Values.OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(typescriptMethods, pair => Assert.Equal(pair.Key, pair.Value));
    }

    [Fact]
    public void RoomCommandSerializationMatchesTheTypeScriptInterface()
    {
        var command = new RoomCommand
        {
            Type = RoomCommandTypes.MoveQueueItem,
            RoomCode = "ABCD",
            ConnectionId = "connection-1",
            QueueItemId = Guid.NewGuid(),
            SongId = Guid.NewGuid(),
            NewPosition = 2,
            Volume = 0.5,
            AudioLatencyEstimateMs = 75,
        };

        var camelCaseOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        var withPolicy = SerializedPropertyNames(command, camelCaseOptions);
        var withoutPolicy = SerializedPropertyNames(command, new JsonSerializerOptions());
        var typescriptFields = ParseInterfaceFields(ReadProtocol(), "RoomCommand");

        Assert.Equal(8, typescriptFields.Count);
        Assert.Equal(typescriptFields.OrderBy(name => name, StringComparer.Ordinal), withPolicy);
        Assert.Equal(withPolicy, withoutPolicy);
    }

    [Fact]
    public void RoomCommandTypeConstantsMatchTheTypeScriptMirror()
    {
        var csharpConstants = typeof(RoomCommandTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false }
                && field.FieldType == typeof(string))
            .ToDictionary(
                field => field.Name,
                field => (string)field.GetRawConstantValue()!,
                StringComparer.Ordinal);

        var typescriptConstants = ParseConstObject(ReadProtocol(), "RoomCommandTypes");

        Assert.Equal(12, csharpConstants.Count);
        Assert.Equal(
            csharpConstants.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            typescriptConstants.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(typescriptConstants, pair => Assert.Equal(pair.Key, pair.Value));
    }

    [Fact]
    public void HubErrorCodesMatchTheTypeScriptMirror()
    {
        var csharpConstants = typeof(HubErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false }
                && field.FieldType == typeof(string))
            .ToDictionary(
                field => field.Name,
                field => (string)field.GetRawConstantValue()!,
                StringComparer.Ordinal);

        var typescriptConstants = ParseConstObject(ReadProtocol(), "HUB_ERROR_CODES");

        Assert.Equal(
            csharpConstants.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            typescriptConstants.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(typescriptConstants, pair => Assert.Equal(pair.Key, pair.Value));
    }

    private static string ReadProtocol() => File.ReadAllText(ProtocolPath);

    private static IReadOnlyCollection<string> InterfaceMethodNames(Type interfaceType) =>
    [
        .. interfaceType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name),
    ];

    private static IReadOnlyList<string> SerializedPropertyNames(
        RoomCommand command,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(command, options));
        return
        [
            .. document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Parses a one-entry-per-line <c>export const NAME = { Key: 'Value', ... } as const;</c>
    /// block. Comment-only lines are ignored; malformed entries fail the caller's exact-set check.
    /// </summary>
    private static Dictionary<string, string> ParseConstObject(string source, string name)
    {
        var block = ExtractBlock(source, $@"export const {Regex.Escape(name)}\s*=\s*\{{", "} as const;");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(
                trimmed,
                @"^(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*'(?<value>[^']*)'\s*,?$");
            if (!match.Success)
            {
                continue;
            }

            Assert.True(
                values.TryAdd(match.Groups["key"].Value, match.Groups["value"].Value),
                $"Duplicate TypeScript entry '{match.Groups["key"].Value}' in {name}.");
        }

        return values;
    }

    /// <summary>Parses the property names of a flat <c>export interface NAME { ... }</c> block.</summary>
    private static List<string> ParseInterfaceFields(string source, string name)
    {
        var block = ExtractBlock(source, $@"export interface {Regex.Escape(name)}\s*\{{", "}");
        var fields = new List<string>();

        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(trimmed, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\??\s*:");
            if (match.Success && !fields.Contains(match.Groups["name"].Value, StringComparer.Ordinal))
            {
                fields.Add(match.Groups["name"].Value);
            }
        }

        return fields;
    }

    private static string ExtractBlock(string source, string startPattern, string endMarker)
    {
        var start = Regex.Match(source, startPattern, RegexOptions.Singleline);
        Assert.True(start.Success, $"Could not find TypeScript declaration matching '{startPattern}'.");

        var end = source.IndexOf(endMarker, start.Index + start.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find the end marker '{endMarker}' for '{startPattern}'.");

        return source[(start.Index + start.Length)..end];
    }
}
