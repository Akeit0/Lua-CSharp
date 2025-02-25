namespace Lua.Standard;

public sealed class MathematicsLibrary
{
    public static readonly MathematicsLibrary Instance = new();
    public const string RandomInstanceKey = "__lua_mathematics_library_random_instance";

    public MathematicsLibrary()
    {
        Functions =
        [
            new("abs", Abs),
            new("acos", Acos),
            new("asin", Asin),
            new("atan2", Atan2),
            new("atan", Atan),
            new("ceil", Ceil),
            new("cos", Cos),
            new("cosh", Cosh),
            new("deg", Deg),
            new("exp", Exp),
            new("floor", Floor),
            new("fmod", Fmod),
            new("frexp", Frexp),
            new("ldexp", Ldexp),
            new("log", Log),
            new("max", Max),
            new("min", Min),
            new("modf", Modf),
            new("pow", Pow),
            new("rad", Rad),
            new("random", Random),
            new("randomseed", RandomSeed),
            new("sin", Sin),
            new("sinh", Sinh),
            new("sqrt", Sqrt),
            new("tan", Tan),
            new("tanh", Tanh),
        ];
    }

    public readonly LuaFunction[] Functions;

    public sealed class RandomUserData(Random random) : ILuaUserData
    {
        LuaTable? SharedMetatable;

        public LuaTable? Metatable
        {
            get => SharedMetatable;
            set => SharedMetatable = value;
        }

        public Random Random { get; } = random;
    }

    public ValueTask Abs(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Abs(arg0));
        return default;
    }

    public ValueTask Acos(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Acos(arg0));
        return default;
    }

    public ValueTask Asin(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Asin(arg0));
        return default;
    }

    public ValueTask Atan2(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        var arg1 = context.GetArgument<double>(1);

        context.Return(Math.Atan2(arg0, arg1));
        return default;
    }

    public ValueTask Atan(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Atan(arg0));
        return default;
    }

    public ValueTask Ceil(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Ceiling(arg0));
        return default;
    }

    public ValueTask Cos(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Cos(arg0));
        return default;
    }

    public ValueTask Cosh(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Cosh(arg0));
        return default;
    }

    public ValueTask Deg(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(arg0 * (180.0 / Math.PI));
        return default;
    }

    public ValueTask Exp(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Exp(arg0));
        return default;
    }

    public ValueTask Floor(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Floor(arg0));
        return default;
    }

    public ValueTask Fmod(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        var arg1 = context.GetArgument<double>(1);
        context.Return(arg0 % arg1);
        return default;
    }

    public ValueTask Frexp(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);

        var (m, e) = MathEx.Frexp(arg0);
        context.Return(m,e);
        return default;
    }

    public ValueTask Ldexp(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        var arg1 = context.GetArgument<double>(1);

        context.Return(arg0 * Math.Pow(2, arg1));
        return default;
    }

    public ValueTask Log(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);

        if (context.ArgumentCount == 1)
        {
            context.Return(Math.Log(arg0));
        }
        else
        {
            var arg1 = context.GetArgument<double>(1);
            context.Return(Math.Log(arg0, arg1));
        }

        return default;
    }

    public ValueTask Max(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var x = context.GetArgument<double>(0);
        for (int i = 1; i < context.ArgumentCount; i++)
        {
            x = Math.Max(x, context.GetArgument<double>(i));
        }

        context.Return(x);

        return default;
    }

    public ValueTask Min(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var x = context.GetArgument<double>(0);
        for (int i = 1; i < context.ArgumentCount; i++)
        {
            x = Math.Min(x, context.GetArgument<double>(i));
        }

        context.Return(x);
        return default;
    }

    public ValueTask Modf(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        var (i, f) = MathEx.Modf(arg0);
        context.Return(i,f);
        return default;
    }

    public ValueTask Pow(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        var arg1 = context.GetArgument<double>(1);

        context.Return(Math.Pow(arg0, arg1));
        return default;
    }

    public ValueTask Rad(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(arg0 * (Math.PI / 180.0));
        return default;
    }

    public ValueTask Random(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var rand = context.State.Environment[RandomInstanceKey].Read<RandomUserData>().Random;

        if (context.ArgumentCount == 0)
        {
            context.Return(rand.NextDouble());
        }
        else if (context.ArgumentCount == 1)
        {
            var arg0 = context.GetArgument<double>(0);
            context.Return(rand.NextDouble() * (arg0 - 1) + 1);
        }
        else
        {
            var arg0 = context.GetArgument<double>(0);
            var arg1 = context.GetArgument<double>(1);
            context.Return(rand.NextDouble() * (arg1 - arg0) + arg0);
        }

        return default;
    }

    public ValueTask RandomSeed(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.State.Environment[RandomInstanceKey] = new(new RandomUserData(new Random((int)BitConverter.DoubleToInt64Bits(arg0))));
        return default;
    }

    public ValueTask Sin(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Sin(arg0));
        return default;
    }

    public ValueTask Sinh(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Sinh(arg0));
        return default;
    }

    public ValueTask Sqrt(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Sqrt(arg0));
        return default;
    }

    public ValueTask Tan(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Tan(arg0));
        return default;
    }

    public ValueTask Tanh(LuaFunctionExecutionContext context,  CancellationToken cancellationToken)
    {
        var arg0 = context.GetArgument<double>(0);
        context.Return(Math.Tanh(arg0));
        return default;
    }
}