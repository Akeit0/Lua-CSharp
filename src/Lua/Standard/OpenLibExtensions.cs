using Lua.Runtime;
using Lua.Standard.Basic;
using Lua.Standard.Bitwise;
using Lua.Standard.Coroutines;
using Lua.Standard.IO;
using Lua.Standard.Mathematics;
using Lua.Standard.Modules;
using Lua.Standard.OperatingSystem;
using Lua.Standard.Table;
using Lua.Standard.Text;

namespace Lua.Standard;

public static class OpenLibExtensions
{
    sealed class StringIndexMetamethod(LuaTable table) : LuaFunction
    {
        protected override ValueTask<int> InvokeAsyncCore(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
        {
            context.GetArgument<string>(0);
            var key = context.GetArgument(1);

            buffer.Span[0] = table[key];
            return new(1);
        }
    }

    static readonly LuaFunction[] baseFunctions = [
        AssertFunction.Instance,
        ErrorFunction.Instance,
        PrintFunction.Instance,
        RawGetFunction.Instance,
        RawSetFunction.Instance,
        RawEqualFunction.Instance,
        RawLenFunction.Instance,
        GetMetatableFunction.Instance,
        SetMetatableFunction.Instance,
        ToNumberFunction.Instance,
        ToStringFunction.Instance,
        CollectGarbageFunction.Instance,
        NextFunction.Instance,
        IPairsFunction.Instance,
        PairsFunction.Instance,
        Basic.TypeFunction.Instance,
        PCallFunction.Instance,
        XPCallFunction.Instance,
        DoFileFunction.Instance,
        LoadFileFunction.Instance,
        LoadFunction.Instance,
        SelectFunction.Instance,
    ];

    static readonly LuaFunction[] mathFunctions = [
        AbsFunction.Instance,
        AcosFunction.Instance,
        AsinFunction.Instance,
        Atan2Function.Instance,
        AtanFunction.Instance,
        CeilFunction.Instance,
        CosFunction.Instance,
        CoshFunction.Instance,
        DegFunction.Instance,
        ExpFunction.Instance,
        FloorFunction.Instance,
        FmodFunction.Instance,
        FrexpFunction.Instance,
        LdexpFunction.Instance,
        LogFunction.Instance,
        MaxFunction.Instance,
        MinFunction.Instance,
        ModfFunction.Instance,
        PowFunction.Instance,
        RadFunction.Instance,
        RandomFunction.Instance,
        RandomSeedFunction.Instance,
        SinFunction.Instance,
        SinhFunction.Instance,
        SqrtFunction.Instance,
        TanFunction.Instance,
        TanhFunction.Instance,
    ];

    static readonly LuaFunction[] tableFunctions = [
        PackFunction.Instance,
        UnpackFunction.Instance,
        Table.RemoveFunction.Instance,
        ConcatFunction.Instance,
        InsertFunction.Instance,
        SortFunction.Instance,
    ];

    static readonly LuaFunction[] stringFunctions = [
        ByteFunction.Instance,
        CharFunction.Instance,
        DumpFunction.Instance,
        FindFunction.Instance,
        FormatFunction.Instance,
        LenFunction.Instance,
        LowerFunction.Instance,
        RepFunction.Instance,
        ReverseFunction.Instance,
        SubFunction.Instance,
        UpperFunction.Instance,
    ];

    static readonly LuaFunction[] ioFunctions = [
        OpenFunction.Instance,
        CloseFunction.Instance,
        InputFunction.Instance,
        OutputFunction.Instance,
        WriteFunction.Instance,
        ReadFunction.Instance,
        LinesFunction.Instance,
        IO.TypeFunction.Instance,
    ];

    static readonly LuaFunction[] osFunctions = [
        ClockFunction.Instance,
        DateFunction.Instance,
        DiffTimeFunction.Instance,
        ExecuteFunction.Instance,
        ExitFunction.Instance,
        GetEnvFunction.Instance,
        OperatingSystem.RemoveFunction.Instance,
        RenameFunction.Instance,
        SetLocaleFunction.Instance,
        TimeFunction.Instance,
        TmpNameFunction.Instance,
    ];

    static readonly LuaFunction[] bit32Functions = [
        ArshiftFunction.Instance,
        BandFunction.Instance,
        BnotFunction.Instance,
        BorFunction.Instance,
        BtestFunction.Instance,
        BxorFunction.Instance,
        ExtractFunction.Instance,
        LRotateFunction.Instance,
        LShiftFunction.Instance,
        ReplaceFunction.Instance,
        RRotateFunction.Instance,
        RShiftFunction.Instance,
    ];

    public static void OpenBasicLibrary(this LuaState state)
    {
        // basic
        state.Environment["_G"] = state.Environment;
        state.Environment["_VERSION"] = "Lua 5.2";
        foreach (var func in baseFunctions)
        {
            state.Environment[func.Name] = func;
        }

        // coroutine
        var coroutine = new LuaTable(0, 6);
        coroutine[CoroutineCreateFunction.FunctionName] = new CoroutineCreateFunction();
        coroutine[CoroutineResumeFunction.FunctionName] = new CoroutineResumeFunction();
        coroutine[CoroutineYieldFunction.FunctionName] = new CoroutineYieldFunction();
        coroutine[CoroutineStatusFunction.FunctionName] = new CoroutineStatusFunction();
        coroutine[CoroutineRunningFunction.FunctionName] = new CoroutineRunningFunction();
        coroutine[CoroutineWrapFunction.FunctionName] = new CoroutineWrapFunction();

        state.Environment["coroutine"] = coroutine;
    }

    public static void OpenMathLibrary(this LuaState state)
    {
        state.Environment[RandomFunction.RandomInstanceKey] = new LuaUserData<Random>(new Random());

        var math = new LuaTable(0, mathFunctions.Length);
        foreach (var func in mathFunctions)
        {
            math[func.Name] = func;
        }

        math["pi"] = Math.PI;
        math["huge"] = double.PositiveInfinity;

        state.Environment["math"] = math;
    }

    public static void OpenModuleLibrary(this LuaState state)
    {
        var package = new LuaTable(0, 1);
        package["loaded"] = new LuaTable();
        state.Environment["package"] = package;

        state.Environment[RequireFunction.Instance.Name] = RequireFunction.Instance;
    }

    public static void OpenTableLibrary(this LuaState state)
    {
        var table = new LuaTable(0, tableFunctions.Length);
        foreach (var func in tableFunctions)
        {
            table[func.Name] = func;
        }

        state.Environment["table"] = table;
    }

    public static void OpenStringLibrary(this LuaState state)
    {
        var @string = new LuaTable(0, stringFunctions.Length);
        foreach (var func in stringFunctions)
        {
            @string[func.Name] = func;
        }

        state.Environment["string"] = @string;

        // set __index
        var key = new LuaValue("");
        if (!state.TryGetMetatable(key, out var metatable))
        {
            metatable = new();
            state.SetMetatable(key, metatable);
        }

        metatable[Metamethods.Index] = new StringIndexMetamethod(@string);
    }

    public static void OpenIOLibrary(this LuaState state)
    {
        var io = new LuaTable(0, ioFunctions.Length);
        foreach (var func in ioFunctions)
        {
            io[func.Name] = func;
        }

        io["stdio"] = new FileHandle(Console.OpenStandardInput());
        io["stdout"] = new FileHandle(Console.OpenStandardOutput());
        io["stderr"] = new FileHandle(Console.OpenStandardError());

        state.Environment["io"] = io;
    }

    public static void OpenOperatingSystemLibrary(this LuaState state)
    {
        var os = new LuaTable(0, osFunctions.Length);
        foreach (var func in osFunctions)
        {
            os[func.Name] = func;
        }

        state.Environment["os"] = os;
    }

    public static void OpenBitwiseLibrary(this LuaState state)
    {
        var bit32 = new LuaTable(0, osFunctions.Length);
        foreach (var func in bit32Functions)
        {
            bit32[func.Name] = func;
        }

        state.Environment["bit32"] = bit32;
    }
}