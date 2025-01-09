using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lua.Internal;
using Lua.Runtime;

namespace Lua;

public enum LuaValueType : byte
{
    Nil=0,
    Boolean,
    String,
    Function,
    Thread,
    UserData,
    Table,
    Number,
}

[StructLayout(LayoutKind.Explicit)]
internal struct Union
{
    [FieldOffset(0)]
    public double Number;
    [FieldOffset(4)]
    public InnerType Type;
    [FieldOffset(0)]
    public ulong RawValue;
    public Union(double number)
    {
        Number = number;
    }
    public Union(InnerType type)
    {
        Type = type;
    }
    public Union(bool boolean)
    {
        Type = boolean ? InnerType.TRUE : InnerType.FALSE;
    }
    

    public LuaValueType LuaType
    {
        
        get=>Type switch
        {
            //InnerType.Nil => LuaValueType.Nil,
            InnerType.TRUE => LuaValueType.Boolean,
            InnerType.FALSE => LuaValueType.Boolean,
            InnerType.String => LuaValueType.String,
            InnerType.Function => LuaValueType.Function,
            InnerType.Thread => LuaValueType.Thread,
            InnerType.UserData => LuaValueType.UserData,
            InnerType.Table => LuaValueType.Table,
            _ => LuaValueType.Nil
        };
    }
    
    public bool IsBoolean => Type == InnerType.TRUE || Type == InnerType.FALSE;
    public bool IsTrue => Type == InnerType.TRUE;
    public bool IsFalse => Type == InnerType.FALSE;
    
    public bool IsNilOrNumber => (RawValue<0xffff000000000001ul);
    
}

internal enum InnerType : uint
{
   // Nil = 0xffff0001,
    FALSE = 0xffff0001,
    TRUE = 0xffff0002,
    String = 0xffff0003,
    Function = 0xffff0004,
    Thread = 0xffff0005,
    UserData = 0xffff0006,
    Table = 0xffff0007,
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct LuaValue : IEquatable<LuaValue>
{
    public static  LuaValue Nil => default;

    public  LuaValueType Type
    {
        get
        {
            if(referenceValue==NumberMarker)return LuaValueType.Number;
            return union.LuaType;
        }
    }
    readonly Union union;
    internal readonly object? referenceValue;
    
    

    internal static unsafe object NumberMarker
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            nuint one = 1;
            return Unsafe.As<nuint, object>(ref one);
        }
    }
    
    internal bool IsNil =>referenceValue==null && union.RawValue == 0;
    internal bool IsNotNil => referenceValue!=null || union.RawValue != 0;
    internal bool IsBoolean => union.IsBoolean;
    internal bool IsTrue => union.IsTrue;
    internal bool IsFalse => union.IsFalse;
    internal  bool IsNumber
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.As<LuaValue,nint>(ref Unsafe.AsRef(in this)) == 1;
    }

    internal bool IsString => union.Type == InnerType.String;
    internal bool IsFunction => union.Type == InnerType.Function;
    internal bool IsThread => union.Type == InnerType.Thread;
    internal bool IsUserData => union.Type == InnerType.UserData;
    internal bool IsTable => union.Type == InnerType.Table;

    public bool TryRead<T>(out T result)
    {
        var t = typeof(T);

        switch (Type)
        {
            case LuaValueType.Number:
                if (t == typeof(float))
                {
                    var v = (float)union.Number;
                    result = Unsafe.As<float, T>(ref v);
                    return true;
                }
                else if (t == typeof(double))
                {
                    var v = union.Number;
                    result = Unsafe.As<double, T>(ref v);
                    return true;
                }
                else if (t == typeof(int))
                {
                    if (!MathEx.IsInteger(union.Number)) break;
                    var v = (int)union.Number;
                    result = Unsafe.As<int, T>(ref v);
                    return true;
                }
                else if (t == typeof(long))
                {
                    if (!MathEx.IsInteger(union.Number)) break;
                    var v = (long)union.Number;
                    result = Unsafe.As<long, T>(ref v);
                    return true;
                }
                else if (t == typeof(object))
                {
                    result = (T)(object)union.Number;
                    return true;
                }
                else
                {
                    break;
                }
            case LuaValueType.Boolean:
                if (t == typeof(bool))
                {
                    var v = union.Type == InnerType.TRUE;
                    result = Unsafe.As<bool, T>(ref v);
                    return true;
                }
                else if (t == typeof(object))
                {
                    result = (T)(object)(union.Type == InnerType.TRUE);
                    return true;
                }
                else
                {
                    break;
                }
            case LuaValueType.String:
                if (t == typeof(string))
                {
                    var v = referenceValue!;
                    result = Unsafe.As<object, T>(ref v);
                    return true;
                }
                else if (t == typeof(double))
                {
                    result = default!;
                    return TryParseToDouble(out Unsafe.As<T, double>(ref result));
                }
                else if (t == typeof(object))
                {
                    result = (T)referenceValue!;
                    return true;
                }
                else
                {
                    break;
                }
            case LuaValueType.Function:
                if (t == typeof(LuaFunction) || t.IsSubclassOf(typeof(LuaFunction)))
                {
                    var v = referenceValue!;
                    result = Unsafe.As<object, T>(ref v);
                    return true;
                }
                else if (t == typeof(object))
                {
                    result = (T)referenceValue!;
                    return true;
                }
                else
                {
                    break;
                }
            case LuaValueType.Thread:
                if (t == typeof(LuaThread))
                {
                    var v = referenceValue!;
                    result = Unsafe.As<object, T>(ref v);
                    return true;
                }
                else if (t == typeof(object))
                {
                    result = (T)referenceValue!;
                    return true;
                }
                else
                {
                    break;
                }
            case LuaValueType.UserData:
                if (t == typeof(ILuaUserData) || typeof(ILuaUserData).IsAssignableFrom(t))
                {
                    if (referenceValue is T tValue)
                    {
                        result = tValue;
                        return true;
                    }

                    break;
                }
                else if (t == typeof(object))
                {
                    result = (T)referenceValue!;
                    return true;
                }
                else
                {
                    break;
                }
            case LuaValueType.Table:
                if (t == typeof(LuaTable))
                {
                    var v = referenceValue!;
                    result = Unsafe.As<object, T>(ref v);
                    return true;
                }
                else if (t == typeof(object))
                {
                    result = (T)referenceValue!;
                    return true;
                }
                else
                {
                    break;
                }
        }

        result = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadBool(out bool result)
    {
        switch (union.Type)
        {
            case InnerType.TRUE:
                result = true;
                return true;
            case InnerType.FALSE:
                result = false;
                return true;
            default:
                result = false;
                return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadNumber(out double result)
    {
        if (IsNumber)
        {
            result = union.Number;
            return true;
        }

        result = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadTable(out LuaTable result)
    {
        if (union.Type == InnerType.Table)
        {
            var v = referenceValue!;
            result = Unsafe.As<object, LuaTable>(ref v);
            return true;
        }

        result = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadFunction(out LuaFunction result)
    {
        if (union.Type == InnerType.Function)
        {
            var v = referenceValue!;
            result = Unsafe.As<object, LuaFunction>(ref v);
            return true;
        }

        result = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadString(out string result)
    {
        if (union.Type == InnerType.String)
        {
            var v = referenceValue!;
            result = Unsafe.As<object, string>(ref v);
            return true;
        }

        result = default!;
        return false;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReadDouble(out double result)
    {
        if (IsNumber)
        {
            result = union.Number;
            return true;
        }

        return TryParseToDouble(out result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryReadOrSetDouble(ref LuaValue luaValue, out double result)
    {
        if (luaValue.IsNumber)
        {
            result = luaValue.union.Number;
            return true;
        }

        if (luaValue.TryParseToDouble(out result))
        {
            luaValue = result;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal double UnsafeReadDouble()
    {
        return union.Number;
    }

    bool TryParseToDouble(out double result)
    {
        if (union.Type != InnerType.String)
        {
            result = default!;
            return false;
        }

        var str = Unsafe.As<string>(referenceValue!);
        var span = str.AsSpan().Trim();
        if (span.Length == 0)
        {
            result = default!;
            return false;
        }

        var sign = 1;
        var first = span[0];
        if (first is '+')
        {
            sign = 1;
            span = span[1..];
        }
        else if (first is '-')
        {
            sign = -1;
            span = span[1..];
        }

        if (span.Length > 2 && span[0] is '0' && span[1] is 'x' or 'X')
        {
            // TODO: optimize
            try
            {
                var d = HexConverter.ToDouble(span) * sign;
                result = d;
                return true;
            }
            catch (FormatException)
            {
                result = default!;
                return false;
            }
        }
        else
        {
            return double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }
    }

    public T Read<T>()
    {
        if (!TryRead<T>(out var result)) throw new InvalidOperationException($"Cannot convert LuaValueType.{Type} to {typeof(T).FullName}.");
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T UnsafeRead<T>()
    {
        switch (union.Type)
        {
            case InnerType.TRUE:
                {
                    var v = true;
                    return Unsafe.As<bool, T>(ref v);
                }
            case InnerType.FALSE:
            {
                var v = false;
                return Unsafe.As<bool, T>(ref v);
            }
            
            case InnerType.String:
                case InnerType.Function:
                case InnerType.Thread:
                case InnerType.UserData:
                case InnerType.Table:
                {
                    var v = referenceValue!;
                    return Unsafe.As<object, T>(ref v);
                }
        }
       

        return default!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ToBoolean()
    {
        if (IsNil) return false;
        return union.Type != InnerType.FALSE;
    }
   
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(bool value)
    {
        var increment = value ? 1 : 0;
        union.Type = (InnerType)(increment + 0xffff0001);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public  LuaValue(double value)
    {
        Unsafe.As<LuaValue, nint>(ref Unsafe.AsRef(in this)) = 1;
        union = new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(string value)
    {
        union = new(InnerType.String);
        referenceValue = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(LuaFunction value)
    {
        union = new(InnerType.Function);
        referenceValue = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(LuaTable value)
    {
        union = new(InnerType.Table);
        referenceValue = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(LuaThread value)
    {
        union = new(InnerType.Thread);
        referenceValue = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LuaValue(ILuaUserData value)
    {
        union = new(InnerType.UserData);
        referenceValue = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(bool value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(double value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(string value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(LuaTable value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(LuaFunction value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator LuaValue(LuaThread value)
    {
        return new(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        if(union.IsNilOrNumber)
            return union.Number.GetHashCode();
        return union.Type switch
        {
            InnerType.TRUE => 1,
            InnerType.FALSE => 2,
            InnerType.String => Unsafe.As<string>(referenceValue)!.GetHashCode(),
            _ => referenceValue!=null?referenceValue!.GetHashCode():0
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(LuaValue other)
    {
        
        var unionType = union.Type;
        if (unionType!=other.union.Type) return false;
        
        if(union.IsNilOrNumber)
        {
            if(referenceValue==null) return other.referenceValue==null;
            return referenceValue==other.referenceValue&&union.Number.Equals(other.union.Number);
        }
        
        switch (unionType)
        {
            case InnerType.String:
                return Unsafe.As<string>(other.referenceValue) == Unsafe.As<string>(referenceValue);
            case InnerType.Function:
            case InnerType.Table:
            case InnerType.Thread:
            case InnerType.UserData:
                return  other.referenceValue!.Equals(referenceValue);
            default:
                return true;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool EqualsNotNull(LuaValue other)
    {
        var unionType = union.Type;
        if (unionType!=other.union.Type) return false;
        if(union.IsNilOrNumber)
        {
            return union.Number.Equals(other.union.Number);
        }
        switch (unionType)
        {
            case InnerType.String:
                return Unsafe.As<string>(other.referenceValue) == Unsafe.As<string>(referenceValue);
            case InnerType.Function:
            case InnerType.Table:
            case InnerType.Thread:
            case InnerType.UserData:
                return  other.referenceValue!.Equals(referenceValue);
            default:
                return true;
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is LuaValue value1 && Equals(value1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(LuaValue a, LuaValue b)
    {
        return a.Equals(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(LuaValue a, LuaValue b)
    {
        return !a.Equals(b);
    }

    public override string ToString()
    {
        return Type switch
        {
            LuaValueType.Nil => "nil",
            LuaValueType.Boolean => Read<bool>() ? "true" : "false",
            LuaValueType.String => Read<string>(),
            LuaValueType.Number => Read<double>().ToString(CultureInfo.InvariantCulture),
            LuaValueType.Function => $"function: {referenceValue!.GetHashCode()}",
            LuaValueType.Thread => $"thread: {referenceValue!.GetHashCode()}",
            LuaValueType.Table => $"table: {referenceValue!.GetHashCode()}",
            LuaValueType.UserData => $"userdata: {referenceValue!.GetHashCode()}",
            _ => "",
        };
    }

    public static bool TryGetLuaValueType(Type type, out LuaValueType result)
    {
        if (type == typeof(double) || type == typeof(float) || type == typeof(int) || type == typeof(long))
        {
            result = LuaValueType.Number;
            return true;
        }
        else if (type == typeof(bool))
        {
            result = LuaValueType.Boolean;
            return true;
        }
        else if (type == typeof(string))
        {
            result = LuaValueType.String;
            return true;
        }
        else if (type == typeof(LuaFunction) || type.IsSubclassOf(typeof(LuaFunction)))
        {
            result = LuaValueType.Function;
            return true;
        }
        else if (type == typeof(LuaTable))
        {
            result = LuaValueType.Table;
            return true;
        }
        else if (type == typeof(LuaThread))
        {
            result = LuaValueType.Thread;
            return true;
        }

        result = default;
        return false;
    }

    internal ValueTask<int> CallToStringAsync(LuaFunctionExecutionContext context, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    {
        if (this.TryGetMetamethod(context.State, Metamethods.ToString, out var metamethod))
        {
            if (!metamethod.TryReadFunction(out var func))
            {
                LuaRuntimeException.AttemptInvalidOperation(context.State.GetTraceback(), "call", metamethod);
            }

            context.State.Push(this);

            return func.InvokeAsync(context with
            {
                ArgumentCount = 1,
                FrameBase = context.Thread.Stack.Count - 1,
            }, buffer, cancellationToken);
        }
        else
        {
            buffer.Span[0] = ToString();
            return new(1);
        }
    }
}