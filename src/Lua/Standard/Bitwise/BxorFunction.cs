namespace Lua.Standard.Bitwise;

public sealed class BxorFunction : LuaFunction
{
    public override string Name => "bxor";
    public static readonly BxorFunction Instance = new();

    protected override ValueTask<int> InvokeAsyncCore(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        if (context.ArgumentCount == 0)
        {
            buffer.Span[0] = 0;
            return new(1);
        }

        var arg0 = context.GetArgument<double>(0);
        LuaRuntimeException.ThrowBadArgumentIfNumberIsNotInteger(context.State, this, 1, arg0);

        var value = Bit32Helper.ToUInt32(arg0);

        for (int i = 1; i < context.ArgumentCount; i++)
        {
            var arg = context.GetArgument<double>(i);
            LuaRuntimeException.ThrowBadArgumentIfNumberIsNotInteger(context.State, this, 1 + i, arg);

            var v = Bit32Helper.ToUInt32(arg);
            value ^= v;
        }

        buffer.Span[0] = value;
        return new(1);
    }
}