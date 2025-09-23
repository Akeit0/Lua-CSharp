using Lua.CodeAnalysis;
using Lua.CodeAnalysis.Compilation;
using Lua.Debugging;
using System.Buffers;

namespace Lua.Runtime;

public sealed class Prototype(
    LuaState state,
    string chunkName,
    int lineDefined,
    int lastLineDefined,
    int parameterCount,
    int maxStackSize,
    bool hasVariableArguments,
    LuaValue[] constants,
    Instruction[] code,
    Prototype[] childPrototypes,
    int[] lineInfo,
    LocalVariable[] localVariables,
    UpValueDesc[] upValues
)
{
    internal readonly LuaGlobalState GlobalState = state.GlobalState;
    public LuaState State => GlobalState.MainThread;
    public ReadOnlySpan<LuaValue> Constants => constants;

    public ReadOnlySpan<Instruction> Code => code;

    public Instruction GetInstruction(int index)
    {
        if (index < 0 || index >= code.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        var instruction = code[index];
        if (instruction.OpCode == DebugUtility.DebugBreakInstruction.OpCode && GlobalState.Debugger is { } debugger) // DebugBreak
        {
            instruction = debugger.GetOriginalInstruction(this, index);
        }

        return instruction;
    }

    public ReadOnlySpan<Prototype> ChildPrototypes => childPrototypes;

    public ReadOnlySpan<int> LineInfo => lineInfo;

    public ReadOnlySpan<LocalVariable> LocalVariables => localVariables;

    public ReadOnlySpan<UpValueDesc> UpValues => upValues;

    //public LuaClosure Cache;
    public readonly string ChunkName = chunkName;
    public readonly int LineDefined = lineDefined, LastLineDefined = lastLineDefined;
    public readonly int ParameterCount = parameterCount, MaxStackSize = maxStackSize;
    public readonly bool HasVariableArguments = hasVariableArguments;

    internal Instruction[] UnderlyingCode => code;

    /// <summary>
    ///  Lua bytecode signature. If the bytes start with this signature, they are considered as Lua bytecode.
    /// </summary>
    public static ReadOnlySpan<byte> LuaByteCodeSignature => Header.LuaSignature;

    /// <summary>
    ///  Converts a Lua bytecode to a Prototype object.
    /// </summary>
    /// <param name="state">the Lua state</param>
    /// <param name="span">binary bytecode</param>
    /// <param name="name">chunk name</param>
    /// <returns></returns>
    public static Prototype FromByteCode(LuaState state, ReadOnlySpan<byte> span, ReadOnlySpan<char> name)
    {
        return Parser.UnDump(state, span, name);
    }

    /// <summary>
    ///  Converts a Prototype object to a Lua bytecode.
    ///  </summary>
    ///  <param name="useLittleEndian">true if the bytecode should be in little endian format, false if it should be in big endian format</param>
    /// <returns>binary bytecode</returns>
    public byte[] ToByteCode(bool useLittleEndian = true)
    {
        return Parser.Dump(this, useLittleEndian);
    }

    /// <summary>
    ///  Writes the Lua bytecode to a buffer writer.
    /// </summary>
    /// <param name="bufferWriter">the buffer writer to write the bytecode to</param>
    /// <param name="useLittleEndian">true if the bytecode should be in little endian format, false if it should be in big endian format</param>
    public void WriteByteCode(IBufferWriter<byte> bufferWriter, bool useLittleEndian = true)
    {
        Parser.Dump(this, bufferWriter, useLittleEndian);
    }
}