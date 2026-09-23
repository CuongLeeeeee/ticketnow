using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TicketNow.BuildingBlocks.Messaging;

public sealed class OutboxTypeRegistry(Assembly contractsAssembly)
{
    // Trùng tên giữa hai namespace sẽ làm ToDictionary ném lỗi ngay lúc khởi động — cố ý
    private readonly Dictionary<string, Type> _types = contractsAssembly.GetExportedTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false })
        .ToDictionary(t => t.Name);

    public bool TryResolve(string name, [NotNullWhen(true)] out Type? type) =>
        _types.TryGetValue(name, out type);
}