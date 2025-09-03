using Lua.Runtime;

namespace Lua.Debugging;

public interface IDebugger
{
    Instruction HandleDebugBreak(LuaState thread, int pc, LuaClosure closure);
    void RegisterPrototype(Prototype proto);
}