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
        try
        {
await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/coroutine.lua"));
        }
        catch (LuaRuntimeException e)
        {
            Console.WriteLine(e);
             Console.WriteLine(e.LuaTraceback.ToString());
            throw;
        }
        
    }

    [Test]
    public async Task Test_Debug_Mini()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/db_mini.lua"));
    }

    [Test]
    public async Task Test_VeryBig()
    {
        await state.DoFileAsync(FileHelper.GetAbsolutePath("tests-lua/verybig.lua"));
    }
}