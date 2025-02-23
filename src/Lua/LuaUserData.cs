namespace Lua;

public sealed class LuaUserData : ILuaUserData
{
    public LuaValue[] Values { get; } = [];
    public LuaTable? Metatable { get; set; }
}

public interface ILuaUserData
{
    LuaTable? Metatable { get; set; }
}