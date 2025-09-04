using Lua.Internal;
using Lua.Runtime;

namespace Lua.Debugging;

public static class DebugUtility
{
    public static readonly Instruction DebugBreakInstruction = new() { OpCode = (OpCode)40 };
    public static Instruction PatchInstruction(Prototype proto, int instructionIndex, Instruction instruction)
    {
        if (instructionIndex < 0 || instructionIndex >= proto.Code.Length)
            throw new ArgumentOutOfRangeException(nameof(instructionIndex));

        var oldInstruction = proto.Code[instructionIndex];
        proto.UnderlyingCode[instructionIndex] = instruction;
        return oldInstruction;
    }

    public static Instruction PatchDebugInstruction(Prototype proto, int instructionIndex)
    {
        return PatchInstruction(proto, instructionIndex, DebugBreakInstruction);
    }

    public static string? GetLocalVariableName(Prototype proto, int registerIndex, int instructionIndex)
    {
        return LuaDebug.GetLocalName(proto, registerIndex, instructionIndex);
    }
}