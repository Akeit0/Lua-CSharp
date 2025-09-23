using Lua.Runtime;

namespace Lua.Debugging;

public interface IDebugger
{
    ValueTask<Instruction> HandleDebugBreak(LuaState thread, int pc, LuaClosure closure);
    Instruction GetOriginalInstruction(Prototype proto, int instructionIndex);
    void RegisterPrototype(Prototype proto);

    /// <summary>
    /// called after a call stack frame is pushed
    /// </summary>
    /// <param name="thread"></param>
    ValueTask OnPushCallStackFrame(LuaState thread);

    /// <summary>
    /// called after a call stack frame is popped
    /// </summary>
    /// <param name="thread"></param>
    ///  <param name="pc"></param>
    ValueTask OnPopCallStackFrame(LuaState thread, int pc);
    
    ValueTask OnError(LuaState thread, Exception ex);
}