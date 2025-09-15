using Lua.Internal;
using Lua.Runtime;

namespace Lua.Debugging;

public enum StepMode
{
    None,
    Over,
    In,
    Out
}

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

    public static void SetStepMode(LuaState? thread, StepMode mode)
    {
        if (thread is null) return;
        thread.GlobalState.DebuggerStepMode = mode;
    }

    public static string GetInstructionString(Prototype proto, int instructionIndex, Instruction instruction)
    {
        return instruction.ToString();
        // switch (instruction.OpCode)
        // {
        //     case OpCode.Move:
        //         return $"{instruction.OpCode} R{instruction.A} R{instruction.B}";
        //     case OpCode.LoadK:
        //         return $"{instruction.OpCode} R{instruction.A} K{instruction.Bx};{proto.Constants[instruction.Bx]}";
        //     case OpCode.LoadKX:
        //         return $"{instruction.OpCode} R{instruction.A} K{proto.Code[instructionIndex + 1].Ax};{proto.Constants[proto.Code[instructionIndex + 1].Ax]}";
        //     case OpCode.LoadBool:
        //         return $"{instruction.OpCode} R{instruction.A} {instruction.B} {instruction.C}; {(instruction.B != 0 ? "true" : "false")}, skip next: {(instruction.C != 0 ? "yes" : "no")}";
        //     case OpCode.LoadNil:
        //         return $"{instruction.OpCode} R{instruction.A}..R{instruction.B}";
        //     case OpCode.GetUpVal:
        //         return $"{instruction.OpCode} R{instruction.A} U{instruction.B};{proto.UpValues[instruction.B].Name}";
        //     case OpCode.GetTabUp:
        //         {
        //             var c = instruction.C;
        //             return c<=255 
        //                 ? $"{instruction.OpCode} R{instruction.A} U{instruction.B} R{instruction.C};{proto.UpValues[instruction.B].Name}" 
        //                 : $"{instruction.OpCode} R{instruction.A} U{instruction.B} K{c - 256};{proto.UpValues[instruction.B].Name} {proto.Constants[c - 256]}";
        //         }
        //     case OpCode.GetTable:
        //         {
        //             var c = instruction.C;
        //             return c <= 255
        //                 ? $"{instruction.OpCode} R{instruction.A} R{instruction.B} K{instruction.C};{proto.Constants[instruction.C]}"
        //                 : $"{instruction.OpCode} R{instruction.A} R{instruction.B} U{c - 256};{proto.UpValues[c - 256].Name}";
        //         }
        //     case OpCode.SetTable:
        //         {
        //             var b = instruction.B;
        //             var c = instruction.C;
        //             var bStr = b <= 255 ? $"K{b};{proto.Constants[b]}" : $"U{b - 256};{proto.UpValues[b - 256].Name}";
        //             var cStr = c <= 255 ? $"K{c};{proto.Constants[c]}" : $"U{c - 256};{proto.UpValues[c - 256].Name}";
        //             return $"{instruction.OpCode} R{instruction.A} {bStr} {cStr}";
        //         }
        //     case OpCode.NewTable:
        //         return $"{instruction.OpCode} R{instruction.A} {instruction.B} {instruction.C}";
        //     case OpCode.Self:
        //         {
        //             var c = instruction.C;
        //             return c <= 255
        //                 ? $"{instruction.OpCode} R{instruction.A} R{instruction.B} K{instruction.C};{proto.Constants[instruction.C]}"
        //                 : $"{instruction.OpCode} R{instruction.A} R{instruction.B} U{c - 256};{proto.UpValues[c - 256].Name}";
        //         }
        //     case OpCode.Add:
        //     case OpCode.Sub:
        //     case OpCode.Mul:
        //     case OpCode.Div:
        //     case OpCode.Mod:
        //     case OpCode.Pow:
        //         {
        //             var b = instruction.B;
        //             var c = instruction.C;
        //             var bStr = b <= 255 ? $"K{b};{proto.Constants[b]}" : $"R{b - 256}";
        //             var cStr = c <= 255 ? $"K{c};{proto.Constants[c]}" : $"R{c - 256}";
        //             return $"{instruction.OpCode} R{instruction.A} {bStr} {cStr}";
        //         }
        //     case OpCode.Unm:
        //     case OpCode.Not:
        //     case OpCode.Len:
        //         return $"{instruction.OpCode} R{instruction.A} R{instruction.B}";
        //     case OpCode.Concat:
        //         return $"{instruction.OpCode} R{instruction.A} R{instruction.B}..R{instruction.C}";
        //     case OpCode.Jmp:
        //         return $"{instruction.OpCode} {instruction.SBx}; to {instructionIndex + 1 + instruction.SBx}";
        //     case OpCode.Eq:
        //     case OpCode.Lt:
        //     case OpCode.Le:
        //         {
        //             var b = instruction.B;
        //             var c = instruction.C;
        //             var bStr = b <= 255 ? $"K{b};{proto.Constants[b]}" : $"R{b - 256}";
        //             var cStr = c <= 255 ? $"K{c};{proto.Constants[c]}" : $"R{c - 256}";
        //             return $"{instruction.OpCode} R{instruction.A} {bStr} {cStr};{(instruction.A != 0 ? "if" : "if not")} then jump to {instructionIndex + 1 + instruction.SBx}";
        //         }
        //     case OpCode.Test:
        //         return $"{instruction.OpCode} R{instruction.A}; if {(instruction.C != 0 ? "not " : "")}R{instruction.A} then jump to {instructionIndex + 1 + instruction.SBx}";
        //     case OpCode.TestSet:
        //         return $"{instruction.OpCode} R{instruction.A} R{instruction.B}; if {(instruction.C != 0 ? "not " : "")}R{instruction.B} then jump to {instructionIndex + 1 + instruction.SBx} else R{instruction.A}=R{instruction.B}";
        //     case OpCode.Call:
        //         return $"{instruction.OpCode} R{instruction.A} {(instruction.B == 0 ? "vararg" : (instruction.B - 1).ToString())} {(instruction.C == 0 ? "vararg" : (instruction.C - 1).ToString())}";
        //     case OpCode.TailCall:
        //     case OpCode.Return:
        //         return $"{instruction.OpCode} R{instruction.A} {(instruction.B == 0 ? "vararg" : (instruction.B - 1).ToString())}";
        //     case OpCode.ForLoop:
        //         return $"{instruction.OpCode} R{instruction.A}; R{instruction.A}+={proto.Code[instructionIndex + 1].SBx}; if R{instruction.A} <= R{instruction.A + 1} then jump to {instructionIndex + 1 + proto.Code[instructionIndex + 1].SBx}";
        //     case OpCode.ForPrep:
        //         return $"{instruction.OpCode} R{instruction.A}; R{instruction.A}-={proto.Code[instructionIndex + 1].SBx}; jump to {instructionIndex + 1 + proto.Code[instructionIndex + 1].SBx}";
        //     case OpCode.TForCall:
        //         return $"{instruction.OpCode} R{instruction.A} {(instruction.C == 0 ? "vararg" : (instruction.C - 1).ToString())}";
        //     case OpCode.TForLoop:
        //         return $"{instruction.OpCode} R{instruction.A}; if R{instruction.A + 2} ~= nil then jump to {instructionIndex + 1 + instruction.SBx}";
        //     case OpCode.SetList:
        //         return $"{instruction.OpCode} R{instruction.A} {(instruction.B == 0 ? "vararg" : instruction.B.ToString())} {instruction.C}";
        //     case OpCode.Closure:
        //         return $"{instruction.OpCode} R{instruction.A} P{instruction.Bx}; :{proto.ChildPrototypes[instruction.Bx].LineDefined}";
        //     case OpCode.VarArg:
        //         return $"{instruction.OpCode} R{instruction.A} {(instruction.B == 0 ? "vararg" : (instruction.B - 1).ToString())}";
        //     default:
        //         return "fatal error";
        // }
    }
}