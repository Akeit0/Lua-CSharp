using System.Text;
using Lua.Internal;

namespace Lua.Standard.Internal;

internal static class IOHelper
{
    public static FileHandle? Open(LuaFunctionExecutionContext context, string fileName, string mode, bool throwError)
    {
        var fileMode = mode switch
        {
            "r" or "rb" or "r+" or "r+b" => FileMode.Open,
            "w" or "wb" or "w+" or "w+b" => FileMode.Create,
            "a" or "ab" or "a+" or "a+b" => FileMode.Append,
            _ => throw new LuaRuntimeException(context.State.GetTraceback(), "bad argument #2 to 'open' (invalid mode)"),
        };

        var fileAccess = mode switch
        {
            "r" or "rb" => FileAccess.Read,
            "w" or "wb" or "a" or "ab" => FileAccess.Write,
            _ => FileAccess.ReadWrite,
        };

        try
        {
            var stream = File.Open(fileName, fileMode, fileAccess);
            var fileHandle = new FileHandle(stream);
            context.Return(new LuaValue(fileHandle));
            return fileHandle;
        }
        catch (IOException ex)
        {
            if (throwError)
            {
                throw;
            }

            context.Return(LuaValue.Nil, ex.Message, ex.HResult);
            return null;
        }
    }

    // TODO: optimize (use IBuffertWrite<byte>, async)

    public static void Write(FileHandle file, string name, LuaFunctionExecutionContext context)
    {
        try
        {
            for (int i = 1; i < context.ArgumentCount; i++)
            {
                var arg = context.Arguments[i];
                if (arg.TryRead<string>(out var str))
                {
                    file.Write(str);
                }
                else if (arg.TryRead<double>(out var d))
                {
                    using var fileBuffer = new PooledArray<char>(64);
                    var span = fileBuffer.AsSpan();
                    d.TryFormat(span, out var charsWritten);
                    file.Write(span[..charsWritten]);
                }
                else
                {
                    LuaRuntimeException.BadArgument(context.State.GetTraceback(), i + 1, name);
                }
            }
        }
        catch (IOException ex)
        {
            context.Return(LuaValue.Nil, ex.Message, ex.HResult);
            return;
        }

        context.Return(new LuaValue(file));
    }

    static readonly LuaValue[] defaultReadFormat = ["*l"];

    public static bool Read(LuaFunctionExecutionContext context, FileHandle file, string name, int startArgumentIndex, ReadOnlySpan<LuaValue> formats, bool throwError)
    {
        if (formats.Length == 0)
        {
            formats = defaultReadFormat;
        }

        try
        {
            var buffer = context.GetReturnBuffer(formats.Length);
            for (int i = 0; i < formats.Length; i++)
            {
                var format = formats[i];
                if (format.TryRead<string>(out var str))
                {
                    switch (str)
                    {
                        case "*n":
                        case "*number":
                            // TODO: support number format
                            throw new NotImplementedException();
                        case "*a":
                        case "*all":
                            buffer[i] = file.ReadToEnd();
                            break;
                        case "*l":
                        case "*line":
                            buffer[i] = file.ReadLine() ?? LuaValue.Nil;
                            break;
                        case "L":
                        case "*L":
                            var text = file.ReadLine();
                            buffer[i] = text == null ? LuaValue.Nil : text + Environment.NewLine;
                            break;
                    }
                }
                else if (format.TryRead<int>(out var count))
                {
                    using var byteBuffer = new PooledArray<byte>(count);

                    for (int j = 0; j < count; j++)
                    {
                        var b = file.ReadByte();
                        if (b == -1)
                        {
                            context.Return(LuaValue.Nil);
                            return false;
                        }

                        byteBuffer[j] = (byte)b;
                    }

                    buffer[i] = Encoding.UTF8.GetString(byteBuffer.AsSpan());
                }
                else
                {
                    LuaRuntimeException.BadArgument(context.State.GetTraceback(), i + 1, name);
                }
            }

            return true;
        }
        catch (IOException ex)
        {
            if (throwError)
            {
                throw;
            }

            context.Return(LuaValue.Nil, ex.Message, ex.HResult);
            return false;
        }
    }
}