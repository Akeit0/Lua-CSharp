using Lua.Runtime;

namespace Lua.Debugging;

public interface IDebugger
{
    Instruction HandleDebugBreak(LuaState thread, int pc, LuaClosure closure);
    void RegisterPrototype(Prototype proto);

    /// <summary>
    /// called after a call stack frame is pushed
    /// </summary>
    /// <param name="thread"></param>
    void OnPushCallStackFrame(LuaState thread);

    /// <summary>
    /// called after a call stack frame is popped
    /// </summary>
    /// <param name="thread"></param>
    ///  <param name="poppedFrame"></param>
    void OnPopCallStackFrame(LuaState thread,ref CallStackFrame poppedFrame);
}