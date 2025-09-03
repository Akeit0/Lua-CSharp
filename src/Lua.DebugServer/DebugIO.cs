using Lua.IO;

class DebugIO : ILuaStandardIO
{
    public ILuaStream Input => null!;

    public ILuaStream Output { get; } = new BufferedOutputStream(m =>
    {
        RpcServer.WriteToConsole(m.Span[..^1].ToString());
    } );
    public ILuaStream Error => null!;
}
