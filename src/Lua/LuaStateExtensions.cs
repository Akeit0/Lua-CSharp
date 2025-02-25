using System.Buffers;
using Lua.CodeAnalysis.Compilation;
using Lua.CodeAnalysis.Syntax;

namespace Lua;

public static class LuaStateExtensions
{
    public static async ValueTask<int> DoStringAsync(this LuaState state, string source, Memory<LuaValue> buffer, string? chunkName = null, CancellationToken cancellationToken = default)
    {
        var syntaxTree = LuaSyntaxTree.Parse(source, chunkName);
        var chunk = LuaCompiler.Default.Compile(syntaxTree, chunkName);
        using var result = await state.RunAsync(chunk, cancellationToken);
        result.AsSpan().CopyTo(buffer.Span);
        return result.Count;
    }

    public static async ValueTask<LuaValue[]> DoStringAsync(this LuaState state, string source, string? chunkName = null, CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<LuaValue>.Shared.Rent(1024);
        try
        {
            var resultCount = await DoStringAsync(state, source, buffer, chunkName, cancellationToken);
            return buffer.AsSpan(0, resultCount).ToArray();
        }
        finally
        {
            ArrayPool<LuaValue>.Shared.Return(buffer);
        }
    }

    public static async ValueTask<int> DoFileAsync(this LuaState state, string path, Memory<LuaValue> buffer, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var fileName = Path.GetFileName(path);
        var syntaxTree = LuaSyntaxTree.Parse(text, fileName);
        var chunk = LuaCompiler.Default.Compile(syntaxTree, fileName);
       await state.RunAsync(chunk, cancellationToken);
        var stack = state.CurrentThread.Stack;
        stack.AsSpan().CopyTo(buffer.Span);
        var count = stack.Count;
        stack.Clear();
        return count;
    }

    public static async ValueTask<LuaValue[]> DoFileAsync(this LuaState state, string path, CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<LuaValue>.Shared.Rent(1024);
        try
        {
            var resultCount = await DoFileAsync(state, path, buffer, cancellationToken);
            return buffer.AsSpan(0, resultCount).ToArray();
        }
        finally
        {
            ArrayPool<LuaValue>.Shared.Return(buffer);
        }
    }
}