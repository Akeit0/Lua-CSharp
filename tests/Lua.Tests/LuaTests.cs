using Lua.CodeAnalysis.Compilation;
using Lua.CodeAnalysis.Syntax;
using Lua.Standard;

namespace Lua.Tests;

public class LuaTests
{
    LuaState state = default!;

    [SetUp]
    public void SetUp()
    {
        state = LuaState.Create();
        state.OpenStandardLibraries();
    }

    [Test]
    public async Task Test_Closure()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/closure.lua"));
    }

    [Test]
    public async Task Test_Vararg()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/vararg.lua"));
    }

    [Test]
    public async Task Test_NextVar()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/nextvar.lua"));
    }

    [Test]
    public async Task Test_Math()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/math.lua"));
    }

    [Test]
    public async Task Test_Bitwise()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/bitwise.lua"));
    }

    [Test]
    public async Task Test_Strings()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/strings.lua"));
    }

    [Test]
    public async Task Test_Coroutine()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/coroutine.lua"));
    }

    [Test]
    public async Task Test_Debug_Mini()
    {
        var source = File.ReadAllText(FileHelper.GetAbsolutePath("tests-lua/db_mini.lua"));

        var syntaxTree = LuaSyntaxTree.Parse(source, "@db.lua");

        var chunk = LuaCompiler.Default.Compile(syntaxTree, "@db.lua");
        try
        {
            await state.RunAsync(chunk, new LuaValue[64]);
        }
        catch (LuaRuntimeException e)
        {
            Console.WriteLine(e.LuaTraceback);
            throw;
        }
    }

    [Test]
    public async Task Test_VeryBig()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/verybig.lua"));
    }
}