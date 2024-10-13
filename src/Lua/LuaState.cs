using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Lua.Internal;
using Lua.Loaders;
using Lua.Runtime;

namespace Lua;

public sealed class LuaState
{
    public const string DefaultChunkName = "chunk";

    // states
    readonly LuaMainThread mainThread = new();
    FastListCore<UpValue> openUpValues;
    FastStackCore<LuaThread> threadStack;
    readonly LuaTable packages = new();
    readonly LuaTable environment;
    readonly UpValue envUpValue;
    bool isRunning;

    internal UpValue EnvUpValue => envUpValue;
    internal ref FastStackCore<LuaThread> ThreadStack => ref threadStack;
    internal ref FastListCore<UpValue> OpenUpValues => ref openUpValues;

    public LuaTable Environment => environment;
    public LuaTable LoadedModules => packages;
    public LuaMainThread MainThread => mainThread;
    public LuaThread CurrentThread
    {
        get
        {
            if (threadStack.TryPeek(out var thread)) return thread;
            return mainThread;
        }
    }

    public ILuaModuleLoader ModuleLoader { get; set; } = FileModuleLoader.Instance;

    // metatables
    LuaTable? nilMetatable;
    LuaTable? numberMetatable;
    LuaTable? stringMetatable;
    LuaTable? booleanMetatable;
    LuaTable? functionMetatable;
    LuaTable? threadMetatable;

    public static LuaState Create()
    {
        return new();
    }

    LuaState()
    {
        environment = new();
        envUpValue = UpValue.Closed(environment);
    }

    public async ValueTask<int> RunAsync(Chunk chunk, Memory<LuaValue> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfRunning();

        Volatile.Write(ref isRunning, true);
        var funcContext = LuaFunctionExecutionContextPool.Rent();
        try
        {
            var closure = new Closure(this, chunk);

            funcContext.State = this;
            funcContext.Thread = CurrentThread;
            funcContext.ArgumentCount = 0;
            funcContext.FrameBase = 0;
            funcContext.SourcePosition = null;
            funcContext.RootChunkName = chunk.Name;
            funcContext.ChunkName = chunk.Name;

            return await closure.InvokeAsync(funcContext, buffer, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref isRunning, false);
            LuaFunctionExecutionContextPool.Return(funcContext);
        }
    }

    public void Push(LuaValue value)
    {
        CurrentThread.Stack.Push(value);
    }

    public Traceback GetTraceback()
    {
        // TODO: optimize
        return new()
        {
            StackFrames = threadStack.AsSpan().ToArray()
                .Append(MainThread)
                .SelectMany(x => x.GetCallStackFrames()[1..].ToArray())
                .ToArray()
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetMetatable(LuaValue value, [NotNullWhen(true)] out LuaTable? result)
    {
        result = value.Type switch
        {
            LuaValueType.Nil => nilMetatable,
            LuaValueType.Boolean => booleanMetatable,
            LuaValueType.String => stringMetatable,
            LuaValueType.Number => numberMetatable,
            LuaValueType.Function => functionMetatable,
            LuaValueType.Thread => threadMetatable,
            LuaValueType.UserData => value.UnsafeRead<LuaUserData>().Metatable,
            LuaValueType.Table => value.UnsafeRead<LuaTable>().Metatable,
            _ => null
        };

        return result != null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetMetatable(LuaValue value, LuaTable metatable)
    {
        switch (value.Type)
        {
            case LuaValueType.Nil:
                nilMetatable = metatable;
                break;
            case LuaValueType.Boolean:
                booleanMetatable = metatable;
                break;
            case LuaValueType.String:
                stringMetatable = metatable;
                break;
            case LuaValueType.Number:
                numberMetatable = metatable;
                break;
            case LuaValueType.Function:
                functionMetatable = metatable;
                break;
            case LuaValueType.Thread:
                threadMetatable = metatable;
                break;
            case LuaValueType.UserData:
                value.UnsafeRead<LuaUserData>().Metatable = metatable;
                break;
            case LuaValueType.Table:
                value.UnsafeRead<LuaTable>().Metatable = metatable;
                break;
        }
    }

    internal UpValue GetOrAddUpValue(LuaThread thread, int registerIndex)
    {
        foreach (var upValue in openUpValues.AsSpan())
        {
            if (upValue.RegisterIndex == registerIndex && upValue.Thread == thread)
            {
                return upValue;
            }
        }

        var newUpValue = UpValue.Open(thread, registerIndex);
        openUpValues.Add(newUpValue);
        return newUpValue;
    }

    internal void CloseUpValues(LuaThread thread, int frameBase)
    {
        for (int i = 0; i < openUpValues.Length; i++)
        {
            var upValue = openUpValues[i];
            if (upValue.Thread != thread) continue;

            if (upValue.RegisterIndex >= frameBase)
            {
                upValue.Close();
                openUpValues.RemoveAtSwapback(i);
                i--;
            }
        }
    }

    void ThrowIfRunning()
    {
        if (Volatile.Read(ref isRunning))
        {
            throw new InvalidOperationException("the lua state is currently running");
        }
    }
}
