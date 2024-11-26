using System.Runtime.InteropServices;
using Lua.CodeAnalysis;

namespace Lua.Runtime;

[StructLayout(LayoutKind.Auto)]
public record struct CallStackFrame
{
    public required int Base;
    public required LuaFunction Function;
    public required int VariableArgumentCount;
    public int? CallerInstructionIndex;
    public CallStatus Status;
}

[Flags]
public enum CallStatus
{
    ReversedLe = 1,
}