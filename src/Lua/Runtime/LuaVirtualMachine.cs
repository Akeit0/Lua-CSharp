using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lua.Internal;

namespace Lua.Runtime;

[SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly")]
public static partial class LuaVirtualMachine
{
    [StructLayout(LayoutKind.Auto)]
    [method: MethodImpl(MethodImplOptions.AggressiveInlining)]
    struct VirtualMachineExecutionContext(
        LuaState state,
        LuaStack stack,
        LuaThread thread,
        in CallStackFrame frame,
        CancellationToken cancellationToken)
    {
        public readonly LuaState State = state;
        public readonly LuaStack Stack = stack;
        public Closure Closure = (Closure)frame.Function;
        public readonly LuaThread Thread = thread;
        public Chunk Chunk => Closure.Proto;
        public int FrameBase = frame.Base;
        public int VariableArgumentCount = frame.VariableArgumentCount;
        public readonly CancellationToken CancellationToken = cancellationToken;
        public int Pc = -1;
        public Instruction Instruction;
        public int CurrentReturnFrameBase = frame.ReturnBase;
        public ValueTask Task;
        public bool IsTopLevel => BaseCallStackCount == Thread.CallStack.Count;

        readonly int BaseCallStackCount = thread.CallStack.Count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Pop(Instruction instruction, int frameBase)
        {
            var count = instruction.B - 1;
            var src = instruction.A + frameBase;
            if (count == -1) count = Stack.Count - src;
            return PopFromBuffer(src, count);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool PopFromBuffer(int src, int srcCount)
        {
            var result = Stack.GetBuffer().Slice(src, srcCount);
            ref var callStack = ref Thread.CallStack;
        Re:
            var frames = callStack.AsSpan();
            if (frames.Length == BaseCallStackCount)
            {
                var returnBase = frames[^1].ReturnBase;
                //Console.WriteLine($"return base: {returnBase} srcCount: {srcCount} src: {src}");
                result.CopyTo(Stack.GetBuffer()[returnBase..]);
                Stack.PopUntil(returnBase + srcCount);
                Console.WriteLine($"return base: {returnBase} srcCount: {srcCount} src: {src} {Stack.Count}");
                
                return false;
            }

            ref readonly var frame = ref frames[^1];
            Pc = frame.CallerInstructionIndex;
            ref readonly var lastFrame = ref frames[^2];
            Closure = Unsafe.As<Closure>(lastFrame.Function);
            if (frame.IsTailCall)
            {
                Thread.PopCallStackFrameUnsafe();
                goto Re;
            }

            var callInstruction = Chunk.Instructions[Pc];
            FrameBase = lastFrame.Base;
            VariableArgumentCount = lastFrame.VariableArgumentCount;

            var opCode = callInstruction.OpCode;
            if (opCode is OpCode.Eq or OpCode.Lt or OpCode.Le)
            {
                var compareResult = srcCount > 0 && result[0].ToBoolean();
                if ((frame.Flags & CallStackFrameFlags.ReversedLe) != 0)
                {
                    compareResult = !compareResult;
                }

                if (compareResult != (callInstruction.A == 1))
                {
                    Pc++;
                }

                Thread.Stack.PopUntil(frame.Base);
                return true;
            }

            var target = callInstruction.A + FrameBase;
            var targetCount = result.Length;
            switch (opCode)
            {
                case OpCode.Call:
                    {
                        var c = callInstruction.C;
                        if (c != 0)
                        {
                            targetCount = c - 1;
                        }

                        break;
                    }
                case OpCode.TForCall:
                    target += 3;
                    targetCount = callInstruction.C;
                    break;
                case OpCode.Self:
                    Stack.Get(target) = result.Length == 0 ? LuaValue.Nil : result[0];
                    Thread.PopCallStackFrameUnsafe(target + 2);
                    return true;
                case OpCode.SetTable or OpCode.SetTabUp:
                    targetCount = 0;
                    break;
                // Other opcodes has one result
                default:
                    targetCount = 1;
                    break;
            }

            var count = Math.Min(result.Length, targetCount);
            Stack.EnsureCapacity(target + targetCount);


            var stackBuffer = Stack.GetBuffer();
            if (count > 0)
            {
                result[..count].CopyTo(stackBuffer.Slice(target, count));
            }

            if (targetCount > count)
            {
                stackBuffer.Slice(target + count, targetCount - count).Clear();
            }

            Stack.PopUntil((target + targetCount));
            Stack.NotifyTop(target + targetCount);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(in CallStackFrame frame)
        {
            Pc = -1;
            Closure = (frame.Function as Closure)!;
            FrameBase = frame.Base;
            CurrentReturnFrameBase = frame.ReturnBase;
            VariableArgumentCount = frame.VariableArgumentCount;
        }

        public void PopOnTopCallStackFrames()
        {
            ref var callStack = ref Thread.CallStack;
            var count = callStack.Count;
            if (count == BaseCallStackCount) return;
            while (callStack.Count > BaseCallStackCount + 1)
            {
                callStack.TryPop();
            }

            Thread.PopCallStackFrame();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearResultsBuffer()
        {
            //if (TaskResult == 0) return;
            // if (TaskResult == 1)
            // {
            //     ResultsBuffer[0] = default;
            //     return;
            // }
            //
            // ResultsBuffer.AsSpan(0, TaskResult).Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClearResultsBuffer(int count)
        {
            if (count == 0) return;
            // if (count == 1)
            // {
            //     ResultsBuffer[0] = default;
            //     return;
            // }
            //
            // ResultsBuffer.AsSpan(0, count).Clear();
        }

        public async ValueTask ExecuteClosureAsyncImpl()
        {
            while (MoveNext(ref this, out var postOperation))
            {
                await Task;
                Task = default;
                //Console.WriteLine($"ExecuteClosureAsyncImpl MoveNext {Thread.CallStack.Count} {postOperation}  {State.GetTraceback()}");
                var stackCount = Stack.Count;
                var resultsSpan = Stack.GetBuffer()[CurrentReturnFrameBase..];
                switch (postOperation)
                {
                    case PostOperationType.Nop: break;
                    case PostOperationType.SetResult:
                        var RA = Instruction.A + FrameBase;
                        Stack.Get(RA) = stackCount > CurrentReturnFrameBase ? Stack.Get(CurrentReturnFrameBase) : LuaValue.Nil;
                        Stack.NotifyTop(RA + 1);
                        Stack.PopUntil(RA + 1);
                        break;
                    case PostOperationType.TForCall:
                        TForCallPostOperation(ref this);
                        break;
                    case PostOperationType.Call:
                        CallPostOperation(ref this);
                        break;
                    case PostOperationType.TailCall:
                        if (!PopFromBuffer(CurrentReturnFrameBase, Stack.Count - CurrentReturnFrameBase))
                        {
                            return;
                        }

                        break;
                    case PostOperationType.Self:
                        SelfPostOperation(ref this, resultsSpan);
                        break;
                    case PostOperationType.Compare:
                        ComparePostOperation(ref this, resultsSpan);
                        break;
                }

                Thread.PopCallStackFrameUnsafe();
            }

            return;
        }
    }

    enum PostOperationType
    {
        None,
        Nop,
        SetResult,
        TForCall,
        Call,
        TailCall,
        Self,
        Compare,
    }

    internal static ValueTask ExecuteClosureAsync(LuaState luaState, CancellationToken cancellationToken)
    {
        var thread = luaState.CurrentThread;
        ref readonly var frame = ref thread.GetCallStackFrames()[^1];

        var context = new VirtualMachineExecutionContext(luaState, thread.Stack, thread, in frame,
            cancellationToken);

        return context.ExecuteClosureAsyncImpl();
    }

    static bool MoveNext(ref VirtualMachineExecutionContext context, out PostOperationType postOperation)
    {
        postOperation = PostOperationType.None;

        try
        {
            // This is a label to restart the execution when new function is called or restarted
        Restart:
            ref var instructionsHead = ref context.Chunk.Instructions[0];
            var frameBase = context.FrameBase;
            var stack = context.Stack;
            stack.EnsureCapacity(frameBase + context.Chunk.MaxStackPosition);
            ref var constHead = ref MemoryMarshalEx.UnsafeElementAt(context.Chunk.Constants, 0);

            while (true)
            {
                var instructionRef = Unsafe.Add(ref instructionsHead, ++context.Pc);
                context.Instruction = instructionRef;
                switch (instructionRef.OpCode)
                {
                    case OpCode.Move:
                        var instruction = instructionRef;
                        ref var stackHead = ref stack.FastGet(frameBase);
                        var iA = instruction.A;
                        Unsafe.Add(ref stackHead, iA) = Unsafe.Add(ref stackHead, instruction.UIntB);
                        stack.NotifyTop(iA + frameBase + 1);
                        continue;
                    case OpCode.LoadK:
                        instruction = instructionRef;
                        stack.GetWithNotifyTop(instruction.A + frameBase) = Unsafe.Add(ref constHead, instruction.Bx);
                        continue;
                    case OpCode.LoadBool:
                        instruction = instructionRef;
                        stack.GetWithNotifyTop(instruction.A + frameBase) = instruction.B != 0;
                        if (instruction.C != 0) context.Pc++;
                        continue;
                    case OpCode.LoadNil:
                        instruction = instructionRef;
                        var ra1 = instruction.A + frameBase + 1;
                        var iB = instruction.B;
                        stack.GetBuffer().Slice(ra1 - 1, iB + 1).Clear();
                        stack.NotifyTop(ra1 + iB);
                        continue;
                    case OpCode.GetUpVal:
                        instruction = instructionRef;
                        stack.GetWithNotifyTop(instruction.A + frameBase) = context.Closure.GetUpValue(instruction.B);
                        continue;
                    case OpCode.GetTabUp:
                    case OpCode.GetTable:
                        instruction = instructionRef;
                        stackHead = ref stack.FastGet(frameBase);
                        ref readonly var vc = ref RKC(ref stackHead, ref constHead, instruction);
                        ref readonly var vb = ref (instruction.OpCode == OpCode.GetTable ? ref Unsafe.Add(ref stackHead, instruction.UIntB) : ref context.Closure.GetUpValueRef(instruction.B));
                        var doRestart = false;
                        if (vb.TryReadTable(out var luaTable) && luaTable.TryGetValue(vc, out var resultValue) || GetTableValueSlowPath(vb, vc, ref context, out resultValue, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            stack.GetWithNotifyTop(instruction.A + frameBase) = resultValue;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;
                    case OpCode.SetTabUp:
                        instruction = instructionRef;
                        stackHead = ref stack.FastGet(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        if (vb.TryReadNumber(out var numB))
                        {
                            if (double.IsNaN(numB))
                            {
                                ThrowLuaRuntimeException(ref context, "table index is NaN");
                                return true;
                            }
                        }

                        var table = context.Closure.GetUpValue(instruction.A);

                        if (table.TryReadTable(out luaTable))
                        {
                            ref var valueRef = ref luaTable.FindValue(vb);
                            if (!Unsafe.IsNullRef(ref valueRef) && valueRef.Type != LuaValueType.Nil)
                            {
                                valueRef = RKC(ref stackHead, ref constHead, instruction);
                                continue;
                            }
                        }

                        vc = ref RKC(ref stackHead, ref constHead, instruction);
                        if (SetTableValueSlowPath(table, vb, vc, ref context, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.Nop;
                        return true;

                    case OpCode.SetUpVal:
                        instruction = instructionRef;
                        context.Closure.SetUpValue(instruction.B, stack.FastGet(instruction.A + frameBase));
                        continue;
                    case OpCode.SetTable:
                        instruction = instructionRef;
                        stackHead = ref stack.FastGet(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        if (vb.TryReadNumber(out numB))
                        {
                            if (double.IsNaN(numB))
                            {
                                ThrowLuaRuntimeException(ref context, " table index is NaN");

                                return true;
                            }
                        }

                        table = Unsafe.Add(ref stackHead, instruction.A);

                        if (table.TryReadTable(out luaTable))
                        {
                            ref var valueRef = ref luaTable.FindValue(vb);
                            if (!Unsafe.IsNullRef(ref valueRef) && valueRef.Type != LuaValueType.Nil)
                            {
                                valueRef = RKC(ref stackHead, ref constHead, instruction);
                                continue;
                            }
                        }

                        vc = ref RKC(ref stackHead, ref constHead, instruction);
                        if (SetTableValueSlowPath(table, vb, vc, ref context, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.Nop;
                        return true;
                    case OpCode.NewTable:
                        instruction = instructionRef;
                        stack.GetWithNotifyTop(instruction.A + frameBase) = new LuaTable(instruction.B, instruction.C);
                        continue;
                    case OpCode.Self:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.FastGet(frameBase);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);
                        table = Unsafe.Add(ref stackHead, instruction.UIntB);

                        doRestart = false;
                        if ((table.TryReadTable(out luaTable) && luaTable.TryGetValue(vc, out resultValue)) || GetTableValueSlowPath(table, vc, ref context, out resultValue, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            Unsafe.Add(ref stackHead, iA) = resultValue;
                            Unsafe.Add(ref stackHead, iA + 1) = table;
                            stack.NotifyTop(iA + frameBase + 2);
                            continue;
                        }

                        postOperation = PostOperationType.Self;
                        return true;
                    case OpCode.Add:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.FastGet(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);
                        if (vb.Type == LuaValueType.Number && vc.Type == LuaValueType.Number)
                        {
                            Unsafe.Add(ref stackHead, iA) = vb.UnsafeReadDouble() + vc.UnsafeReadDouble();
                            stack.NotifyTop(iA + frameBase + 1);
                            continue;
                        }

                        if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out var numC))
                        {
                            Unsafe.Add(ref stackHead, iA) = numB + numC;
                            stack.NotifyTop(iA + frameBase + 1);
                            continue;
                        }

                        if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Add, "add", out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;
                    case OpCode.Sub:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.FastGet(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);

                        if (vb.Type == LuaValueType.Number && vc.Type == LuaValueType.Number)
                        {
                            ra1 = iA + frameBase + 1;
                            Unsafe.Add(ref stackHead, iA) = vb.UnsafeReadDouble() - vc.UnsafeReadDouble();
                            stack.NotifyTop(ra1);
                            continue;
                        }

                        if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out numC))
                        {
                            ra1 = iA + frameBase + 1;
                            Unsafe.Add(ref stackHead, iA) = numB - numC;
                            stack.NotifyTop(ra1);
                            continue;
                        }

                        if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Sub, "sub", out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;

                    case OpCode.Mul:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.FastGet(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);

                        if (vb.Type == LuaValueType.Number && vc.Type == LuaValueType.Number)
                        {
                            ra1 = iA + frameBase + 1;
                            Unsafe.Add(ref stackHead, iA) = vb.UnsafeReadDouble() * vc.UnsafeReadDouble();
                            stack.NotifyTop(ra1);
                            continue;
                        }

                        if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out numC))
                        {
                            ra1 = iA + frameBase + 1;
                            Unsafe.Add(ref stackHead, iA) = numB * numC;
                            stack.NotifyTop(ra1);
                            continue;
                        }

                        if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Mul, "mul", out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;

                    case OpCode.Div:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.FastGet(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);

                        if (vb.Type == LuaValueType.Number && vc.Type == LuaValueType.Number)
                        {
                            ra1 = iA + frameBase + 1;
                            Unsafe.Add(ref stackHead, iA) = vb.UnsafeReadDouble() / vc.UnsafeReadDouble();
                            stack.NotifyTop(ra1);
                            continue;
                        }

                        if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out numC))
                        {
                            ra1 = iA + frameBase + 1;
                            Unsafe.Add(ref stackHead, iA) = numB / numC;
                            stack.NotifyTop(ra1);
                            continue;
                        }

                        if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Div, "div", out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;
                    case OpCode.Mod:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.FastGet(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);
                        if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out numC))
                        {
                            var mod = numB % numC;
                            if ((numC > 0 && mod < 0) || (numC < 0 && mod > 0))
                            {
                                mod += numC;
                            }

                            Unsafe.Add(ref stackHead, iA) = mod;
                            stack.NotifyTop(iA + frameBase + 1);
                            continue;
                        }

                        if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Mod, "mod", out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;
                    case OpCode.Pow:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.FastGet(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);
                        if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out numC))
                        {
                            ra1 = iA + frameBase + 1;
                            Unsafe.Add(ref stackHead, iA) = Math.Pow(numB, numC);
                            stack.NotifyTop(ra1);
                            continue;
                        }

                        if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Pow, "pow", out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;
                    case OpCode.Unm:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.FastGet(frameBase);
                        vb = ref Unsafe.Add(ref stackHead, instruction.UIntB);

                        if (vb.TryReadDouble(out numB))
                        {
                            ra1 = iA + frameBase + 1;
                            Unsafe.Add(ref stackHead, iA) = -numB;
                            stack.NotifyTop(ra1);
                            continue;
                        }

                        if (ExecuteUnaryOperationMetaMethod(vb, ref context, Metamethods.Unm, "unm", false, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;
                    case OpCode.Not:
                        instruction = instructionRef;
                        iA = instruction.A;
                        ra1 = iA + frameBase + 1;
                        stackHead = ref stack.FastGet(frameBase);
                        Unsafe.Add(ref stackHead, iA) = !Unsafe.Add(ref stackHead, instruction.UIntB).ToBoolean();
                        stack.NotifyTop(ra1);
                        continue;

                    case OpCode.Len:
                        instruction = instructionRef;
                        stackHead = ref stack.FastGet(frameBase);

                        vb = ref Unsafe.Add(ref stackHead, instruction.UIntB);

                        if (vb.TryReadString(out var str))
                        {
                            iA = instruction.A;
                            ra1 = iA + frameBase + 1;
                            Unsafe.Add(ref stackHead, iA) = str.Length;
                            stack.NotifyTop(ra1);
                            continue;
                        }

                        if (ExecuteUnaryOperationMetaMethod(vb, ref context, Metamethods.Len, "get length of", true, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;
                    case OpCode.Concat:
                        if (Concat(ref context, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.SetResult;
                        return true;
                    case OpCode.Jmp:
                        instruction = instructionRef;
                        context.Pc += instruction.SBx;
                        iA = instruction.A;
                        if (iA != 0)
                        {
                            context.State.CloseUpValues(context.Thread, frameBase + iA - 1);
                        }

                        continue;
                    case OpCode.Eq:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.Get(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);
                        if (vb == vc)
                        {
                            if (iA != 1)
                            {
                                context.Pc++;
                            }

                            continue;
                        }

                        if (ExecuteCompareOperationMetaMethod(vb, vc, ref context, Metamethods.Eq, null, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.Compare;
                        return true;
                    case OpCode.Lt:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.Get(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);

                        if (vb.TryReadNumber(out numB) && vc.TryReadNumber(out numC))
                        {
                            var compareResult = numB < numC;
                            if (compareResult != (iA == 1))
                            {
                                context.Pc++;
                            }

                            continue;
                        }


                        if (vb.TryReadString(out var strB) && vc.TryReadString(out var strC))
                        {
                            var compareResult = StringComparer.Ordinal.Compare(strB, strC) < 0;
                            if (compareResult != (iA == 1))
                            {
                                context.Pc++;
                            }

                            continue;
                        }

                        if (ExecuteCompareOperationMetaMethod(vb, vc, ref context, Metamethods.Lt, "less than", out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.Compare;
                        return true;
                    case OpCode.Le:
                        instruction = instructionRef;
                        iA = instruction.A;
                        stackHead = ref stack.Get(frameBase);
                        vb = ref RKB(ref stackHead, ref constHead, instruction);
                        vc = ref RKC(ref stackHead, ref constHead, instruction);

                        if (vb.TryReadNumber(out numB) && vc.TryReadNumber(out numC))
                        {
                            var compareResult = numB <= numC;
                            if (compareResult != (iA == 1))
                            {
                                context.Pc++;
                            }

                            continue;
                        }

                        if (vb.TryReadString(out strB) && vc.TryReadString(out strC))
                        {
                            var compareResult = StringComparer.Ordinal.Compare(strB, strC) <= 0;
                            if (compareResult != (iA == 1))
                            {
                                context.Pc++;
                            }

                            continue;
                        }

                        if (ExecuteCompareOperationMetaMethod(vb, vc, ref context, Metamethods.Le, "less than or equals", out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.Compare;
                        return true;
                    case OpCode.Test:
                        instruction = instructionRef;
                        if (stack.Get(instruction.A + frameBase).ToBoolean() != (instruction.C == 1))
                        {
                            context.Pc++;
                        }

                        continue;
                    case OpCode.TestSet:
                        instruction = instructionRef;
                        vb = ref stack.Get(instruction.B + frameBase);
                        if (vb.ToBoolean() != (instruction.C == 1))
                        {
                            context.Pc++;
                        }
                        else
                        {
                            stack.GetWithNotifyTop(instruction.A + frameBase) = vb;
                        }

                        continue;

                    case OpCode.Call:
                        if (Call(ref context, out doRestart))
                        {
                            if (doRestart)
                            {
                                goto Restart;
                            }

                            continue;
                        }

                        postOperation = PostOperationType.Call;
                        return true;
                    case OpCode.TailCall:
                        if (TailCall(ref context, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            if (context.IsTopLevel) goto End;
                            continue;
                        }

                        postOperation = PostOperationType.TailCall;
                        return true;
                    case OpCode.Return:
                        instruction = instructionRef;
                        context.State.CloseUpValues(context.Thread, frameBase);
                        if (context.Pop(instruction, frameBase))
                        {
                            context.Thread.PopCallStackFrameUnsafe();
                            goto Restart;
                        }
                        //
                        // var retCount = instruction.B - 1;
                        // if (retCount != -1)
                        // {
                        //     stack.PopUntil(frameBase + retCount);
                        // }

                        goto End;
                    case OpCode.ForLoop:
                        ref var indexRef = ref stack.Get(instructionRef.A + frameBase);
                        var limit = Unsafe.Add(ref indexRef, 1).UnsafeReadDouble();
                        var step = Unsafe.Add(ref indexRef, 2).UnsafeReadDouble();
                        var index = indexRef.UnsafeReadDouble() + step;

                        if (step >= 0 ? index <= limit : limit <= index)
                        {
                            context.Pc += instructionRef.SBx;
                            indexRef = index;
                            Unsafe.Add(ref indexRef, 3) = index;
                            stack.NotifyTop(instructionRef.A + frameBase + 4);
                            continue;
                        }

                        stack.NotifyTop(instructionRef.A + frameBase + 1);
                        continue;
                    case OpCode.ForPrep:
                        indexRef = ref stack.Get(instructionRef.A + frameBase);

                        if (!indexRef.TryReadDouble(out var init))
                        {
                            ThrowLuaRuntimeException(ref context, "'for' initial value must be a number");
                            return true;
                        }

                        if (!LuaValue.TryReadOrSetDouble(ref Unsafe.Add(ref indexRef, 1), out _))
                        {
                            ThrowLuaRuntimeException(ref context, "'for' limit must be a number");
                            return true;
                        }

                        if (!LuaValue.TryReadOrSetDouble(ref Unsafe.Add(ref indexRef, 2), out step))
                        {
                            ThrowLuaRuntimeException(ref context, "'for' step must be a number");
                            return true;
                        }

                        indexRef = init - step;
                        stack.NotifyTop(instructionRef.A + frameBase + 1);
                        context.Pc += instructionRef.SBx;
                        continue;
                    case OpCode.TForCall:
                        if (TForCall(ref context, out doRestart))
                        {
                            if (doRestart) goto Restart;
                            continue;
                        }

                        postOperation = PostOperationType.TForCall;
                        return true;
                    case OpCode.TForLoop:
                        instruction = instructionRef;
                        iA = instruction.A;
                        ra1 = iA + frameBase + 1;
                        ref var forState = ref stack.Get(ra1);

                        if (forState.Type is not LuaValueType.Nil)
                        {
                            Unsafe.Add(ref forState, -1) = forState;
                            context.Pc += instruction.SBx;
                        }

                        continue;
                    case OpCode.SetList:
                        SetList(ref context);
                        continue;
                    case OpCode.Closure:
                        instruction = instructionRef;
                        iA = instruction.A;
                        ra1 = iA + frameBase + 1;
                        stack.EnsureCapacity(ra1);
                        stack.Get(ra1 - 1) = new Closure(context.State, context.Chunk.Functions[instruction.SBx]);
                        stack.NotifyTop(ra1);
                        continue;
                    case OpCode.VarArg:
                        instruction = instructionRef;
                        iA = instruction.A;
                        ra1 = iA + frameBase + 1;
                        var frameVariableArgumentCount = context.VariableArgumentCount;
                        var count = instruction.B == 0
                            ? frameVariableArgumentCount
                            : instruction.B - 1;
                        var ra = ra1 - 1;
                        stack.EnsureCapacity(ra + count);
                        stackHead = ref stack.Get(0);
                        for (int i = 0; i < count; i++)
                        {
                            Unsafe.Add(ref stackHead, ra + i) = frameVariableArgumentCount > i
                                ? Unsafe.Add(ref stackHead, frameBase - (frameVariableArgumentCount - i))
                                : default;
                        }

                        stack.NotifyTop(ra + count);
                        continue;
                    case OpCode.ExtraArg:
                    default:
                        ThrowLuaNotImplementedException(ref context, context.Instruction.OpCode);
                        return true;
                }
            }

        End:
            postOperation = PostOperationType.None;
            return false;
        }
        catch (Exception e)
        {
            context.State.CloseUpValues(context.Thread, context.FrameBase);
            if (e is not LuaRuntimeException)
            {
                var newException = new LuaRuntimeException(context.State.GetTraceback(), e);
                context.PopOnTopCallStackFrames();
                context = default;
                throw newException;
            }

            context.PopOnTopCallStackFrames();
            throw;
        }
    }


    static void ThrowLuaRuntimeException(ref VirtualMachineExecutionContext context, string message)
    {
        throw new LuaRuntimeException(context.State.GetTraceback(), message);
    }

    static void ThrowLuaNotImplementedException(ref VirtualMachineExecutionContext context, OpCode opcode)
    {
        throw new LuaRuntimeException(context.State.GetTraceback(), $"OpCode {opcode} is not implemented");
    }


    static void SelfPostOperation(ref VirtualMachineExecutionContext context, Span<LuaValue> results)
    {
        var stack = context.Stack;
        var instruction = context.Instruction;
        var RA = instruction.A + context.FrameBase;
        var RB = instruction.B + context.FrameBase;
        ref var stackHead = ref stack.Get(0);
        var table = Unsafe.Add(ref stackHead, RB);
        Unsafe.Add(ref stackHead, RA + 1) = table;
        Unsafe.Add(ref stackHead, RA) = results.Length == 0 ? LuaValue.Nil : results[0];
        stack.NotifyTop(RA + 2);
    }

    static bool Concat(ref VirtualMachineExecutionContext context, out bool doRestart)
    {
        var instruction = context.Instruction;
        var stack = context.Stack;
        var RA = instruction.A + context.FrameBase;
        stack.EnsureCapacity(RA + 1);
        ref var stackHead = ref stack.Get(context.FrameBase);
        ref var constHead = ref MemoryMarshalEx.UnsafeElementAt(context.Chunk.Constants, 0);
        var vb = RKB(ref stackHead, ref constHead, instruction);
        var vc = RKC(ref stackHead, ref constHead, instruction);

        var bIsValid = vb.TryReadString(out var strB);
        var cIsValid = vc.TryReadString(out var strC);

        if (!bIsValid && vb.TryReadDouble(out var numB))
        {
            strB = numB.ToString(CultureInfo.InvariantCulture);
            bIsValid = true;
        }

        if (!cIsValid && vc.TryReadDouble(out var numC))
        {
            strC = numC.ToString(CultureInfo.InvariantCulture);
            cIsValid = true;
        }

        if (bIsValid && cIsValid)
        {
            stack.Get(RA) = strB + strC;
            stack.NotifyTop(RA + 1);
            doRestart = false;
            return true;
        }

        return ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Concat, "concat", out doRestart);
    }

    static bool Call(ref VirtualMachineExecutionContext context, out bool doRestart)
    {
        var instruction = context.Instruction;
        var RA = instruction.A + context.FrameBase;
        var va = context.Stack.Get(RA);
        if (!va.TryReadFunction(out var func))
        {
            if (va.TryGetMetamethod(context.State, Metamethods.Call, out var metamethod) &&
                metamethod.TryReadFunction(out func))
            {
            }
            else
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", va);
            }
        }

        var thread = context.Thread;
        var (newBase, argumentCount, variableArgumentCount) = PrepareForFunctionCall(thread, func, instruction, RA);

        var newFrame = func.CreateNewFrame(ref context, newBase, RA, variableArgumentCount);
        //Console.WriteLine("FuncCall PushCallStackFrame " + context.Thread.CallStack.Count);
        thread.PushCallStackFrame(newFrame);
        if (func is Closure)
        {
            context.Push(newFrame);
            doRestart = true;
            return true;
        }

        doRestart = false;
        return FuncCall(ref context, in newFrame, func, newBase, argumentCount);

        static bool FuncCall(ref VirtualMachineExecutionContext context, in CallStackFrame newFrame, LuaFunction func, int newBase, int argumentCount)
        {
            var task = func.Invoke(ref context, newFrame, argumentCount);

            if (!task.IsCompleted)
            {
                context.Task = task;
                return false;
            }

            var awaiter = task.GetAwaiter();

            awaiter.GetResult();
            var instruction = context.Instruction;
            var ic = instruction.C;

            if (ic != 0)
            {
                var resultCount = ic - 1;
                var stack = context.Stack;
                var top = instruction.A + context.FrameBase + resultCount;
                stack.EnsureCapacity(top);
                stack.PopUntil(top);
                stack.NotifyTop(top);
            }

            context.Thread.PopCallStackFrameUnsafe();
            //Console.WriteLine("FuncCall PopCallStackFrameUnsafe " + context.Thread.CallStack.Count);
            return true;
        }
    }

    static void CallPostOperation(ref VirtualMachineExecutionContext context)
    {
        var instruction = context.Instruction;
        var ic = instruction.C;

        if (ic != 0)
        {
            var resultCount = ic - 1;
            var stack = context.Stack;
            var top = instruction.A + context.FrameBase + resultCount;
            stack.EnsureCapacity(top);
            stack.PopUntil(top);
            stack.NotifyTop(top);
        }

        //context.Thread.PopCallStackFrameUnsafe();
    }

    static bool TailCall(ref VirtualMachineExecutionContext context, out bool doRestart)
    {
        var instruction = context.Instruction;
        var stack = context.Stack;
        var RA = instruction.A + context.FrameBase;
        var state = context.State;
        var thread = context.Thread;

        state.CloseUpValues(thread, context.FrameBase);

        var va = stack.Get(RA);
        if (!va.TryReadFunction(out var func))
        {
            if (!va.TryGetMetamethod(state, Metamethods.Call, out var metamethod) &&
                !metamethod.TryReadFunction(out func))
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", metamethod);
            }
        }

        var (newBase, argumentCount, variableArgumentCount) = PrepareForFunctionTailCall(thread, func, instruction, RA);

        ref readonly var lastFrame = ref thread.CallStack.AsSpan()[^1];
        if (lastFrame.IsTailCall)
        {
            context.Thread.PopCallStackFrameUnsafe();
            //context.Pc = lastFrame.CallerInstructionIndex;
        }

        var newFrame = func.CreateNewTailCallFrame(ref context, newBase, context.CurrentReturnFrameBase, variableArgumentCount);
        thread.PushCallStackFrame(newFrame);

        context.Push(newFrame);
        if (func is Closure)
        {
            doRestart = true;
            return true;
        }

        doRestart = false;
        var task = func.Invoke(ref context, newFrame, argumentCount);

        if (!task.IsCompleted)
        {
            context.Task = task;
            return false;
        }
        

        doRestart = true;
        task.GetAwaiter().GetResult();
        if (!context.PopFromBuffer(context.CurrentReturnFrameBase,context.Stack.Count - context.CurrentReturnFrameBase))
        {
            doRestart = false;
            return true;
        }
        
        context.Thread.PopCallStackFrameUnsafe();
        return true;
    }

    static bool TForCall(ref VirtualMachineExecutionContext context, out bool doRestart)
    {
        doRestart = false;
        var instruction = context.Instruction;
        var stack = context.Stack;
        var RA = instruction.A + context.FrameBase;

        var iteratorRaw = stack.Get(RA);
        if (!iteratorRaw.TryReadFunction(out var iterator))
        {
            LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", iteratorRaw);
        }

        var newBase = RA + 3 + instruction.C;
        stack.Get(newBase) = stack.Get(RA + 1);
        stack.Get(newBase + 1) = stack.Get(RA + 2);
        stack.NotifyTop(newBase + 2);
        var newFrame = iterator.CreateNewFrame(ref context, newBase, RA + 3, 0);
        context.Thread.PushCallStackFrame(newFrame);
        if (iterator is Closure)
        {
            context.Push(newFrame);
            doRestart = true;
            return true;
        }

        var task = iterator.Invoke(ref context, newFrame, 2);

        if (!task.IsCompleted)
        {
            context.Task = task;

            return false;
        }

        var awaiter = task.GetAwaiter();
        awaiter.GetResult();
        context.Thread.PopCallStackFrameUnsafe();
        TForCallPostOperation(ref context);
        return true;
    }

    static void TForCallPostOperation(ref VirtualMachineExecutionContext context)
    {
        // var stack = context.Stack;
        // var instruction = context.Instruction;
        // var RA = instruction.A + context.FrameBase;
        // stack.EnsureCapacity(RA + instruction.C + 3);
        // for (int i = 1; i <= instruction.C; i++)
        // {
        //     var index = i - 1;
        //     stack.Get(RA + 2 + i) = index >= resultCount
        //         ? LuaValue.Nil
        //         : resultBuffer[i - 1];
        // }
        //
        // stack.NotifyTop(RA + instruction.C + 3);
    }

    static void SetList(ref VirtualMachineExecutionContext context)
    {
        var instruction = context.Instruction;
        var stack = context.Stack;
        var RA = instruction.A + context.FrameBase;

        if (!stack.Get(RA).TryReadTable(out var table))
        {
            throw new LuaException("internal error");
        }

        var count = instruction.B == 0
            ? stack.Count - (RA + 1)
            : instruction.B;

        table.EnsureArrayCapacity((instruction.C - 1) * 50 + count);
        stack.GetBuffer().Slice(RA + 1, count)
            .CopyTo(table.GetArraySpan()[((instruction.C - 1) * 50)..]);
        stack.PopUntil(RA + 1);
    }

    static void ComparePostOperation(ref VirtualMachineExecutionContext context, Span<LuaValue> results)
    {
        var compareResult = results.Length != 0 && results[0].ToBoolean();
        if (compareResult != (context.Instruction.A == 1))
        {
            context.Pc++;
        }

        context.ClearResultsBuffer();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref readonly LuaValue RKB(ref LuaValue stack, ref LuaValue constants, Instruction instruction)
    {
        var index = instruction.UIntB;
        return ref (index >= 256 ? ref Unsafe.Add(ref constants, index - 256) : ref Unsafe.Add(ref stack, index));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ref readonly LuaValue RKC(ref LuaValue stack, ref LuaValue constants, Instruction instruction)
    {
        var index = instruction.UIntC;
        return ref (index >= 256 ? ref Unsafe.Add(ref constants, index - 256) : ref Unsafe.Add(ref stack, index));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool GetTableValueSlowPath(LuaValue table, LuaValue key, ref VirtualMachineExecutionContext context, out LuaValue value, out bool doRestart)
    {
        var targetTable = table;
        const int MAX_LOOP = 100;
        doRestart = false;
        var skip = targetTable.Type == LuaValueType.Table;
        for (int i = 0; i < MAX_LOOP; i++)
        {
            if (table.TryReadTable(out var luaTable))
            {
                if (!skip && luaTable.TryGetValue(key, out value))
                {
                    return true;
                }

                skip = false;

                var metatable = luaTable.Metatable;
                if (metatable != null && metatable.TryGetValue(Metamethods.Index, out table))
                {
                    goto Function;
                }

                value = default;
                return true;
            }

            if (!table.TryGetMetamethod(context.State, Metamethods.Index, out var metatableValue))
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "index", table);
            }

            table = metatableValue;
        Function:
            if (table.TryReadFunction(out var function))
            {
                return CallGetTableFunc(targetTable, function, key, ref context, out value, out doRestart);
            }
        }

        throw new LuaRuntimeException(GetTracebacks(ref context), "loop in gettable");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallGetTableFunc(LuaValue table, LuaFunction indexTable, LuaValue key, ref VirtualMachineExecutionContext context, out LuaValue result, out bool doRestart)
    {
        doRestart = false;
        var stack = context.Stack;
        stack.Push(table);
        stack.Push(key);
        var newFrame = indexTable.CreateNewFrame(ref context, stack.Count - 2, stack.Count - 2, 0);

        context.Thread.PushCallStackFrame(newFrame);

        if (indexTable is Closure)
        {
            context.Push(newFrame);
            doRestart = true;
            result = default;
            return true;
        }

        var task = indexTable.Invoke(ref context, newFrame, 2);

        if (!task.IsCompleted)
        {
            context.Task = task;
            result = default;
            return false;
        }

        var awaiter = task.GetAwaiter();
        awaiter.GetResult();
        var results = stack.GetBuffer()[newFrame.Base..];
        result = results.Length == 0 ? default : results[0];
        context.Thread.PopCallStackFrame();
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool SetTableValueSlowPath(LuaValue table, LuaValue key, LuaValue value,
        ref VirtualMachineExecutionContext context, out bool doRestart)
    {
        var targetTable = table;
        const int MAX_LOOP = 100;
        doRestart = false;
        var skip = targetTable.Type == LuaValueType.Table;
        for (int i = 0; i < MAX_LOOP; i++)
        {
            if (table.TryReadTable(out var luaTable))
            {
                ref var valueRef = ref (skip ? ref Unsafe.NullRef<LuaValue>() : ref luaTable.FindValue(key));
                skip = false;
                if (!Unsafe.IsNullRef(ref valueRef) && valueRef.Type != LuaValueType.Nil)
                {
                    valueRef = value;
                    return true;
                }

                var metatable = luaTable.Metatable;
                if (metatable == null || !metatable.TryGetValue(Metamethods.NewIndex, out table))
                {
                    if (Unsafe.IsNullRef(ref valueRef))
                    {
                        luaTable[key] = value;
                        return true;
                    }

                    valueRef = value;
                    return true;
                }

                goto Function;
            }

            if (!table.TryGetMetamethod(context.State, Metamethods.NewIndex, out var metatableValue))
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "index", table);
            }

            table = metatableValue;

        Function:
            if (table.TryReadFunction(out var function))
            {
                return CallSetTableFunc(targetTable, function, key, value, ref context, out doRestart);
            }
        }

        throw new LuaRuntimeException(GetTracebacks(ref context), "loop in settable");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool CallSetTableFunc(LuaValue table, LuaFunction newIndexFunction, LuaValue key, LuaValue value, ref VirtualMachineExecutionContext context, out bool doRestart)
    {
        doRestart = false;
        var thread = context.Thread;
        var stack = thread.Stack;
        stack.Push(table);
        stack.Push(key);
        stack.Push(value);
        var newFrame = newIndexFunction.CreateNewFrame(ref context, stack.Count - 3);

        context.Thread.PushCallStackFrame(newFrame);

        if (newIndexFunction is Closure)
        {
            context.Push(newFrame);
            doRestart = true;
            return true;
        }

        var task = newIndexFunction.Invoke(ref context, newFrame, 3);
        if (!task.IsCompleted)
        {
            context.Task = task;
            return false;
        }

        task.GetAwaiter().GetResult();
        thread.PopCallStackFrame();
        return true;
    }


    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ExecuteBinaryOperationMetaMethod(LuaValue vb, LuaValue vc,
        ref VirtualMachineExecutionContext context, string name, string description, out bool doRestart)
    {
        doRestart = false;
        if (vb.TryGetMetamethod(context.State, name, out var metamethod) ||
            vc.TryGetMetamethod(context.State, name, out metamethod))
        {
            if (!metamethod.TryReadFunction(out var func))
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", metamethod);
            }

            var stack = context.Stack;
            stack.Push(vb);
            stack.Push(vc);

            var newFrame = func.CreateNewFrame(ref context, stack.Count - 2);

            context.Thread.PushCallStackFrame(newFrame);

            if (func is Closure)
            {
                context.Push(newFrame);
                doRestart = true;
                return true;
            }


            var task = func.Invoke(ref context, newFrame, 2);

            if (!task.IsCompleted)
            {
                context.Task = task;
                return false;
            }

            task.GetAwaiter().GetResult();

            var RA = context.Instruction.A + context.FrameBase;

            var results = stack.GetBuffer()[newFrame.Base..];
            stack.Get(RA) = results.Length == 0 ? default : results[0];
            results.Clear();
            context.Thread.PopCallStackFrame();
            return true;
        }

        LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), description, vb, vc);
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ExecuteUnaryOperationMetaMethod(LuaValue vb, ref VirtualMachineExecutionContext context,
        string name, string description, bool isLen, out bool doRestart)
    {
        doRestart = false;
        var stack = context.Stack;
        if (vb.TryGetMetamethod(context.State, name, out var metamethod))
        {
            if (!metamethod.TryReadFunction(out var func))
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", metamethod);
            }

            stack.Push(vb);
            var newFrame = func.CreateNewFrame(ref context, stack.Count - 1);

            context.Thread.PushCallStackFrame(newFrame);

            if (func is Closure)
            {
                context.Push(newFrame);
                doRestart = true;
                return true;
            }


            var task = func.Invoke(ref context, newFrame, 1);

            if (!task.IsCompleted)
            {
                context.Task = task;
                return false;
            }

            var RA = context.Instruction.A + context.FrameBase;
            var results = stack.GetBuffer()[newFrame.Base..];
            stack.Get(RA) = results.Length == 0 ? default : results[0];
            results.Clear();
            context.Thread.PopCallStackFrame();
            return true;
        }

        if (isLen && vb.TryReadTable(out var table))
        {
            var RA = context.Instruction.A + context.FrameBase;
            stack.Get(RA) = table.ArrayLength;
            return true;
        }

        LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), description, vb);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ExecuteCompareOperationMetaMethod(LuaValue vb, LuaValue vc,
        ref VirtualMachineExecutionContext context, string name, string? description, out bool doRestart)
    {
        doRestart = false;
        bool reverseLe = false;
    ReCheck:
        if (vb.TryGetMetamethod(context.State, name, out var metamethod) ||
            vc.TryGetMetamethod(context.State, name, out metamethod))
        {
            if (!metamethod.TryReadFunction(out var func))
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", metamethod);
            }

            var stack = context.Stack;
            stack.Push(vb);
            stack.Push(vc);
            var newFrame = func.CreateNewFrame(ref context, stack.Count - 2);
            if (reverseLe) newFrame.Flags |= CallStackFrameFlags.ReversedLe;
            context.Thread.PushCallStackFrame(newFrame);

            if (func is Closure)
            {
                context.Push(newFrame);
                doRestart = true;
                return true;
            }

            var task = func.Invoke(ref context, newFrame, 2);

            if (!task.IsCompleted)
            {
                context.Task = task;
                return false;
            }

            context.Thread.PopCallStackFrame();
            var results = stack.GetBuffer()[newFrame.Base..];
            var compareResult = results.Length == 0 && results[0].ToBoolean();
            compareResult = reverseLe ? !compareResult : compareResult;
            if (compareResult != (context.Instruction.A == 1))
            {
                context.Pc++;
            }

            results.Clear();
            context.Thread.PopCallStackFrame();

            return true;
        }

        if (name == Metamethods.Le)
        {
            reverseLe = true;
            name = Metamethods.Lt;
            (vb, vc) = (vc, vb);
            goto ReCheck;
        }

        if (description != null)
        {
            if (reverseLe)
            {
                (vb, vc) = (vc, vb);
            }

            LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), description, vb, vc);
        }
        else
        {
            if (context.Instruction.A == 1)
            {
                context.Pc++;
            }
        }

        return true;
    }

    // If there are variable arguments, the base of the stack is moved by that number and the values of the variable arguments are placed in front of it.
    // see: https://wubingzheng.github.io/build-lua-in-rust/en/ch08-02.arguments.html
    [MethodImpl(MethodImplOptions.NoInlining)]
    static (int FrameBase, int ArgumentCount, int VariableArgumentCount) PrepareVariableArgument(LuaStack stack, int newBase, int argumentCount,
        int variableArgumentCount)
    {
        var temp = newBase;
        newBase += variableArgumentCount;

        stack.EnsureCapacity(newBase + argumentCount);
        stack.NotifyTop(newBase + argumentCount);

        var stackBuffer = stack.GetBuffer()[temp..];
        stackBuffer[..argumentCount].CopyTo(stackBuffer[variableArgumentCount..]);
        stackBuffer.Slice(argumentCount, variableArgumentCount).CopyTo(stackBuffer);
        return (newBase, argumentCount, variableArgumentCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (int FrameBase, int ArgumentCount, int VariableArgumentCount) PrepareForFunctionCall(LuaThread thread, LuaFunction function,
        Instruction instruction, int RA)
    {
        var argumentCount = instruction.B - 1;
        if (argumentCount == -1)
        {
            argumentCount = (ushort)(thread.Stack.Count - (RA + 1));
            //Console.WriteLine($"Stack Count: {thread.Stack.Count}, RA: {RA}, Argument Count: {argumentCount}");
        }
        else
        {
            thread.Stack.NotifyTop(RA + 1 + argumentCount);
        }

        var newBase = RA + 1;
        var variableArgumentCount = function.GetVariableArgumentCount(argumentCount);

        if (variableArgumentCount <= 0)
        {
            return (newBase, argumentCount, 0);
        }

        return PrepareVariableArgument(thread.Stack, newBase, argumentCount, variableArgumentCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (int FrameBase, int ArgumentCount, int VariableArgumentCount) PrepareForFunctionTailCall(LuaThread thread, LuaFunction function,
        Instruction instruction, int RA)
    {
        var stack = thread.Stack;

        var argumentCount = instruction.B - 1;
        if (instruction.B == 0)
        {
            argumentCount = (ushort)(stack.Count - (RA + 1));
        }
        else
        {
            thread.Stack.NotifyTop(RA + 1 + argumentCount);
        }

        var newBase = RA + 1;

        // In the case of tailcall, the local variables of the caller are immediately discarded, so there is no need to retain them.
        // Therefore, a call can be made without allocating new registers.
        var currentBase = thread.GetCurrentFrame().Base;
        {
            var stackBuffer = stack.GetBuffer();
            if (argumentCount > 0)
                stackBuffer.Slice(newBase, argumentCount).CopyTo(stackBuffer.Slice(currentBase, argumentCount));
            newBase = currentBase;
        }

        var variableArgumentCount = function.GetVariableArgumentCount(argumentCount);

        if (variableArgumentCount <= 0)
        {
            return (newBase, argumentCount, 0);
        }

        return PrepareVariableArgument(thread.Stack, newBase, argumentCount, variableArgumentCount);
    }

    static Traceback GetTracebacks(ref VirtualMachineExecutionContext context)
    {
        return GetTracebacks(context.State, context.Pc);
    }

    static Traceback GetTracebacks(LuaState state, int pc)
    {
        var frame = state.CurrentThread.GetCurrentFrame();
        state.CurrentThread.PushCallStackFrame(frame with
        {
            CallerInstructionIndex = pc,
            Flags = 0
        });
        var tracebacks = state.GetTraceback();
        state.CurrentThread.PopCallStackFrame();
        return tracebacks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static CallStackFrame CreateNewFrame(this LuaFunction function, ref VirtualMachineExecutionContext context, int newBase)
    {
        return new()
        {
            Base = newBase,
            ReturnBase = newBase,
            Function = function,
            VariableArgumentCount = 0,
            CallerInstructionIndex = context.Pc,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static CallStackFrame CreateNewFrame(this LuaFunction function, ref VirtualMachineExecutionContext context, int newBase, int returnBase, int variableArgumentCount)
    {
        return new()
        {
            Base = newBase,
            ReturnBase = returnBase,
            Function = function,
            VariableArgumentCount = variableArgumentCount,
            CallerInstructionIndex = context.Pc,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static CallStackFrame CreateNewTailCallFrame(this LuaFunction function, ref VirtualMachineExecutionContext context, int newBase, int returnBase, int variableArgumentCount)
    {
        return new()
        {
            Base = newBase,
            ReturnBase = returnBase,
            Function = function,
            VariableArgumentCount = variableArgumentCount,
            CallerInstructionIndex = context.Pc,
            Flags = CallStackFrameFlags.TailCall
        };
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ValueTask Invoke(this LuaFunction function, ref VirtualMachineExecutionContext context, in CallStackFrame frame, int arguments)
    {
        return function.Func(new()
        {
            State = context.State,
            Thread = context.Thread,
            ArgumentCount = arguments,
            FrameBase = frame.Base,
            ReturnFrameBase = frame.ReturnBase,
            CallerInstructionIndex = frame.CallerInstructionIndex,
        }, context.CancellationToken);
    }
}