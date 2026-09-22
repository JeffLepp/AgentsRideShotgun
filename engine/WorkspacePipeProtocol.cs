using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Deskweave.AgentWorkspaces;

/// <summary>Version 1: UTF-8 JSON in a bounded, little-endian length-prefixed local pipe frame.</summary>
internal static class WorkspacePipeProtocol
{
    internal const int MaxRequestBytes = 1024 * 1024;
    internal const int MaxResponseBytes = 64 * 1024 * 1024;

    internal static async Task<string?> Read(Stream stream, int maximum, CancellationToken cancel)
    {
        var head = new byte[4];
        if (await stream.ReadAsync(head.AsMemory(0, 1), cancel).ConfigureAwait(false) == 0) return null;
        await stream.ReadExactlyAsync(head.AsMemory(1), cancel).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length < 0 || length > maximum) throw new InvalidDataException("Workspace message exceeds its limit.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancel).ConfigureAwait(false);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    internal static async Task Write(Stream stream, string? message, int maximum, CancellationToken cancel)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message ?? string.Empty);
        if (bytes.Length > maximum) throw new InvalidDataException("Workspace message exceeds its limit.");
        var head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, bytes.Length);
        await stream.WriteAsync(head, cancel).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }
}
