using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TicketNow.BuildingBlocks.Messaging;

public sealed class OutboxTypeRegistry(Assembly contractsAssembly)
{
    // Duplicate type names across namespaces make ToDictionary throw at startup, on purpose
    private readonly Dictionary<string, Type> _types = contractsAssembly.GetExportedTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false })
        .ToDictionary(t => t.Name);

    public bool TryResolve(string name, [NotNullWhen(true)] out Type? type) =>
        _types.TryGetValue(name, out type);
}