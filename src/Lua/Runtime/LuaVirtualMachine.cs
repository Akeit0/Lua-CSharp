using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lua.Internal;

namespace Lua.Runtime;

public static partial class LuaVirtualMachine
{
    [StructLayout(LayoutKind.Auto, Pack = 4)]
    [method: MethodImpl(MethodImplOptions.AggressiveInlining)]
    struct VirtualMachineExecutionContext(
        LuaState state,
        LuaStack stack,
        LuaValue[] resultsBuffer,
        Memory<LuaValue> buffer,
        LuaThread thread,
        in CallStackFrame frame,
        CancellationToken cancellationToken)
    {
        public readonly LuaState State = state;
        public readonly LuaStack Stack = stack;
        public Closure Closure = (Closure)frame.Function;
        public readonly LuaValue[] ResultsBuffer = resultsBuffer;
        public readonly Memory<LuaValue> Buffer = buffer;
        public readonly LuaThread Thread = thread;
        public Chunk Chunk => Closure.Proto;
        public int FrameBase = frame.Base;
        public int VariableArgumentCount = frame.VariableArgumentCount;
        public readonly CancellationToken CancellationToken = cancellationToken;
        public int Pc = -1;
        public Instruction Instruction;
        public bool Pushing => localCallStackCount > 0;
        public int ResultCount;
        public int TaskResult;
        public ValueTaskAwaiter<int> Awaiter;
        int localCallStackCount = 0;
        public bool IsTopLevel => localCallStackCount == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Pop(Instruction instruction, int frameBase)
        {
            if (localCallStackCount < 1) return false;
            var count = instruction.B - 1;
            if(count==-1)count = Stack.Count - (instruction.A + frameBase);
            return PopWithCount(count, instruction.A + frameBase);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        bool PopWithCount(int count, int src)
        {
            var frames = Thread.GetCallStackFrames();
            Re:
            if (localCallStackCount < 1) return false;
           
            localCallStackCount--;
            ref readonly var frame = ref frames[^1];
            Pc = frame.CallerInstructionIndex!.Value;
            ref readonly var lastFrame = ref frames[^2];
            Closure = Unsafe.As<Closure>(lastFrame.Function);
            
            var callInstruction = Chunk.Instructions[Pc];
            if (callInstruction.OpCode == OpCode.TailCall)
            {
                count = Stack.Count - src;
                Thread.PopCallStackFrameFast();
                frames=frames[..^1];
                goto Re;
            }
            FrameBase = lastFrame.Base;
            VariableArgumentCount = lastFrame.VariableArgumentCount;
            
            if(callInstruction.OpCode is OpCode.Eq or OpCode.Le or OpCode.Lt)
            {
                var compareResult = count != 0 && Stack.Get(src).ToBoolean();
                if (compareResult != (callInstruction.A == 1))
                {
                    Pc++;
                }
                Thread.PopCallStackFrameFast(frame.Base);
                return true;
            }
            var target = callInstruction.A + FrameBase;

            if (count > 0)
            {
                var stackBuffer = Stack.GetBuffer();
                stackBuffer.Slice(src, count).CopyTo(stackBuffer.Slice(target, count));
            }

            if (callInstruction.OpCode == OpCode.Self)
            {
                Thread.PopCallStackFrameFast(target + 2);
                return true;
            }
            Thread.PopCallStackFrameFast(target + count);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool PopFromBuffer(Span<LuaValue> result)
        {
            var frames = Thread.GetCallStackFrames();
            Re:
            if (localCallStackCount < 1) return false;
            if (--localCallStackCount < 1) return false;
            ref readonly var frame = ref frames[^1];
            Pc = frame.CallerInstructionIndex!.Value;
            ref readonly var lastFrame = ref frames[^2];
            Closure = Unsafe.As<Closure>(lastFrame.Function);
            var callInstruction = Chunk.Instructions[Pc];
            if (callInstruction.OpCode == OpCode.TailCall)
            {
                Thread.PopCallStackFrameFast();
                frames=frames[..^1];
                goto Re;
            }

            FrameBase = lastFrame.Base;
            VariableArgumentCount = lastFrame.VariableArgumentCount;
            
            if(callInstruction.OpCode is OpCode.Eq or OpCode.Le or OpCode.Lt)
            {
                var compareResult = result.Length >0 && result[0].ToBoolean();
                if (compareResult != (callInstruction.A == 1))
                {
                    Pc++;
                }
                localCallStackCount--;
                Thread.PopCallStackFrameFast(frame.Base);
                return true;
            }
            var target = callInstruction.A + FrameBase;
            var count = result.Length; 
            if (count > 0)
            {
                var stackBuffer = Stack.GetBuffer();
                Stack.EnsureCapacity(target + count);
                result.CopyTo(stackBuffer.Slice(target, count));
                Stack.NotifyTop(target + count);
            }

            localCallStackCount--;
            if (callInstruction.OpCode == OpCode.Self)
            {
                Thread.PopCallStackFrameFast(target + 2);
                return true;
            }
            Thread.PopCallStackFrameFast(target + count);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(in CallStackFrame frame)
        {
            localCallStackCount++; Pc = -1;
            Closure = (frame.Function as Closure)!;
            FrameBase = frame.Base;
            VariableArgumentCount = frame.VariableArgumentCount;
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


    static readonly Stack<LuaValue[]> resultsBufferPool = new();

    static LuaValue[] GetResultsBuffer()
    {
        if (resultsBufferPool.Count > 0)
        {
            return resultsBufferPool.Pop();
        }

        return new LuaValue[256];
    }

    static void ReturnResultsBuffer(LuaValue[] buffer)
    {
        buffer.AsSpan().Clear();
        resultsBufferPool.Push(buffer);
    }

    //[AsyncStateMachine(typeof(AsyncStateMachine))]
    internal static ValueTask<int> ExecuteClosureAsync(LuaState luaState, Memory<LuaValue> buffer,
        CancellationToken cancellationToken)
    {
        //Console.WriteLine("[ExecuteClosureAsync]");
        var thread = luaState.CurrentThread;
        ref readonly var frame = ref thread.GetCallStackFrames()[^1];
        var resultBuffer = GetResultsBuffer();

        var stateMachine = new AsyncStateMachine
        {
            Context = new(luaState, thread.Stack, resultBuffer, buffer, thread, in frame,
                cancellationToken),
            Builder = new()
        };
        stateMachine.Builder.Start(ref stateMachine);
        return stateMachine.Builder.Task;
    }


    // //Asynchronous method implementation. 
    // internal async static ValueTask<int> ExecuteClosureAsync(LuaState state, CallStackFrame frame, Memory<LuaValue> buffer, CancellationToken cancellationToken)
    // {
    //     var thread = state.CurrentThread;
    //     var closure = (Closure)frame.Function;
    //     var chunk = closure.Proto;
    //     var resultBuffer = ArrayPool<LuaValue>.Shared.Rent(1024);
    //
    //     var context = new VirtualMachineExecutionContext(state, thread.Stack, resultBuffer, buffer, thread, chunk, frame, cancellationToken);
    //     try
    //     {
    //         var instructions = chunk.Instructions;
    //
    //         while (context.ResultCount == null)
    //         {
    //             var instruction = instructions[++context.Pc];
    //             context.Instruction = instruction;
    //             var operation = operations[(int)instruction.OpCode];
    //             var action = operation(ref context);
    //             if (action != null)
    //             {
    //                 context.TaskResult = await context.Task;
    //                 //if (context.Pushing) //Assuming context.Pushing is always true
    //                 {
    //                     context.Thread.PopCallStackFrame();
    //                     context.Pushing = false;
    //                 }
    //                 action(ref context);
    //             }
    //         }
    //
    //         return context.ResultCount.Value;
    //     }
    //     catch (Exception)
    //     {
    //         if (context.Pushing) context.Thread.PopCallStackFrame();
    //         context.State.CloseUpValues(context.Thread, context.FrameBase);
    //         throw;
    //     }
    //     finally
    //     {
    //         ArrayPool<LuaValue>.Shared.Return(context.ResultsBuffer);
    //     }
    // }

    /// <summary>
    /// Manual implementation of the async state machine
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    struct AsyncStateMachine : IAsyncStateMachine
    {
        enum State
        {
            Running = 0,

            //Await is the state where the task is awaited
            Await,

            //End is the state where the function is done
            End
        }

        public VirtualMachineExecutionContext Context;
        public AsyncValueTaskMethodBuilder<int> Builder;
        State state;
        PostOperationType postOperation;

#if NET6_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        public void MoveNext()
        {
            Restart:
            //If the state is end, the function is done, so set the result and return. I think this state is not reachable in this implementation
            if (state == State.End)
            {
                Builder.SetResult(Context.ResultCount);
                return;
            }

            ref var context = ref Context;


            if (state == State.Await)
            {
                context.TaskResult = context.Awaiter.GetResult();
                context.Awaiter = default;
                context.Thread.PopCallStackFrame();
                //context.Pushing = false;
                switch (postOperation)
                {
                    case PostOperationType.Nop: break;
                    case PostOperationType.SetResult:
                        var RA = context.Instruction.A + context.FrameBase;
                        context.Stack.Get(RA) = context.TaskResult == 0 ? LuaValue.Nil : context.ResultsBuffer[0];
                        context.Stack.NotifyTop(RA + 1);
                        break;
                    case PostOperationType.TForCall:
                        TForCall(ref context);
                        break;
                    case PostOperationType.Call:
                        CallPostOperation(ref context);
                        break;
                    case PostOperationType.TailCall:
                        state = State.End;
                        ReturnResultsBuffer(context.ResultsBuffer);
                        Builder.SetResult(context.TaskResult);
                        return;
                    case PostOperationType.Self:
                        SelfPostOperation(ref context);
                        break;
                    case PostOperationType.Compare:
                        ComparePostOperation(ref context);
                        break;
                }

                postOperation = 0;
                state = State.Running;
            }

            try
            {
                var instructions = context.Chunk.Instructions;
                var frameBase = context.FrameBase;
                var stack = context.Stack;
                ref var constHead = ref MemoryMarshalEx.UnsafeElementAt(context.Chunk.Constants, 0);

                do
                {
                    var instruction = instructions[++context.Pc];
                    context.Instruction = instruction;
                    var iA = instruction.A;
                    var ra1 = iA + frameBase + 1;
                    switch (instruction.OpCode)
                    {
                        case OpCode.Move:
                            stack.EnsureCapacity(ra1);
                            ref var stackHead = ref stack.Get(frameBase);
                            Unsafe.Add(ref stackHead, iA) = Unsafe.Add(ref stackHead, instruction.UIntB);
                            stack.NotifyTop(ra1);
                            continue;
                        case OpCode.LoadK:
                            stack.EnsureCapacity(ra1);
                            stack.Get(ra1 - 1) = Unsafe.Add(ref constHead, instruction.Bx);
                            stack.NotifyTop(ra1);
                            continue;
                        case OpCode.LoadBool:
                            stack.EnsureCapacity(ra1);
                            stack.Get(ra1 - 1) = instruction.B != 0;
                            stack.NotifyTop(ra1);
                            if (instruction.C != 0) context.Pc++;
                            continue;
                        case OpCode.LoadNil:
                            var iB = instruction.B;
                            stack.EnsureCapacity(ra1 + iB);
                            stack.GetBuffer().Slice(ra1 - 1, iB + 1).Clear();
                            stack.NotifyTop(ra1 + iB);
                            continue;
                        case OpCode.GetUpVal:
                            stack.EnsureCapacity(ra1);
                            stack.Get(ra1 - 1) = context.Closure.GetUpValue(instruction.B);
                            stack.NotifyTop(ra1);
                            continue;
                        case OpCode.GetTabUp:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
                            ref readonly var vc = ref RKC(ref stackHead, ref constHead, instruction);
                            var table = context.Closure.GetUpValue(instruction.B);
                            var isTable = table.TryReadTable(out var luaTable);

                            if (isTable && luaTable.TryGetValue(vc, out var resultValue))
                            {
                                Unsafe.Add(ref stackHead, iA) = resultValue;
                                stack.NotifyTop(ra1);
                                continue;
                            }

                            if (TryGetMetaTableValue(table, vc, ref context,out var doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }
                            postOperation = PostOperationType.SetResult;
                            break;
                        case OpCode.GetTable:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
                            vc = ref RKC(ref stackHead, ref constHead, instruction);
                            table = Unsafe.Add(ref stackHead, instruction.UIntB);

                            isTable = table.TryReadTable(out luaTable);

                            if (isTable && luaTable.TryGetValue(vc, out resultValue))
                            {
                                Unsafe.Add(ref stackHead, iA) = resultValue;
                                stack.NotifyTop(ra1);
                                continue;
                            }

                            if (TryGetMetaTableValue(table, vc, ref context,out  doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }
                            postOperation = PostOperationType.SetResult;
                            break;

                        case OpCode.SetTabUp:
                            stackHead = ref stack.Get(frameBase);
                            ref readonly var vb = ref RKB(ref stackHead, ref constHead, instruction);
                            if (vb.TryReadNumber(out var numB))
                            {
                                if (double.IsNaN(numB))
                                {
                                    ThrowLuaRuntimeException(ref context, "table index is NaN");
                                    return;
                                }
                            }

                            table = context.Closure.GetUpValue(instruction.A);

                            if (table.TryReadTable(out luaTable))
                            {
                                luaTable[vb] = RKC(ref stackHead, ref constHead, instruction);
                                continue;
                            }

                            vc = ref RKC(ref stackHead, ref constHead, instruction);
                            if (TrySetMetaTableValue(table, vb, vc, ref context,out doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }
                            postOperation = PostOperationType.Nop;
                            break;
                            
                        case OpCode.SetUpVal:
                            context.Closure.SetUpValue(instruction.B, stack.Get(ra1 - 1));
                            continue;
                        case OpCode.SetTable:
                            stackHead = ref stack.Get(frameBase);
                            vb = ref RKB(ref stackHead, ref constHead, instruction);
                            if (vb.TryReadNumber(out numB))
                            {
                                if (double.IsNaN(numB))
                                {
                                    ThrowLuaRuntimeException(ref context, " table index is NaN");
                                    return;
                                }
                            }

                            table = Unsafe.Add(ref stackHead, iA);

                            if (table.TryReadTable(out luaTable))
                            {
                                
                                if(luaTable.Metatable==null||!luaTable.Metatable!.ContainsKey(Metamethods.NewIndex))
                                {
                                    luaTable[vb] = RKC(ref stackHead, ref constHead, instruction);
                                    continue;
                                }

                                if (luaTable.ContainsKey(vb))
                                {
                                    luaTable[vb] = RKC(ref stackHead, ref constHead, instruction);
                                    continue;
                                }
                                
                            }

                            vc = ref RKC(ref stackHead, ref constHead, instruction);
                            if (TrySetMetaTableValue(table, vb, vc, ref context,out doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }
                            postOperation = PostOperationType.Nop;
                            break;
                            

                        case OpCode.NewTable:
                            stack.EnsureCapacity(ra1);
                            stack.Get(ra1 - 1) = new LuaTable(instruction.B, instruction.C);
                            stack.NotifyTop(ra1);
                            continue;
                        case OpCode.Self:
                            stack.EnsureCapacity(ra1 + 1);
                            stackHead = ref stack.Get(frameBase);
                            vc = ref RKC(ref stackHead, ref constHead, instruction);
                            table = Unsafe.Add(ref stackHead, instruction.UIntB);
                            isTable = table.TryReadTable(out luaTable);
                           
                            if (isTable && luaTable.TryGetValue(vc, out resultValue))
                            {
                                Unsafe.Add(ref stackHead, iA) = resultValue;
                                Unsafe.Add(ref stackHead, iA + 1) = table;
                                stack.NotifyTop(ra1 + 2);
                                continue;
                            }

                            if (TryGetMetaTableValue(table, vc, ref context,out  doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }
                            postOperation = PostOperationType.Self;
                            break;
                        case OpCode.Add:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
                            vb = ref RKB(ref stackHead, ref constHead, instruction);
                            vc = ref RKC(ref stackHead, ref constHead, instruction);

                            if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out var numC))
                            {
                                Unsafe.Add(ref stackHead, iA) = numB + numC;
                                stack.NotifyTop(ra1);
                                //Console.WriteLine($"Add {numB} + {numC} = {Unsafe.Add(ref stackHead, iA)}");
                                continue;
                            }

                            if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Add, "add",out  doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.SetResult;
                            break;
                        case OpCode.Sub:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
                            vb = ref RKB(ref stackHead, ref constHead, instruction);
                            vc = ref RKC(ref stackHead, ref constHead, instruction);
                            if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out numC))
                            {
                                Unsafe.Add(ref stackHead, iA) = numB - numC;
                                stack.NotifyTop(ra1);
                                continue;
                            }

                            if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Sub, "sub",out  doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.SetResult;
                            break;

                        case OpCode.Mul:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
                            vb = ref RKB(ref stackHead, ref constHead, instruction);
                            vc = ref RKC(ref stackHead, ref constHead, instruction);
                            if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out numC))
                            {
                                Unsafe.Add(ref stackHead, iA) = numB * numC;
                                stack.NotifyTop(ra1);
                                continue;
                            }

                            if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Mul, "mul",out  doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.SetResult;
                            break;

                        case OpCode.Div:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
                            vb = ref RKB(ref stackHead, ref constHead, instruction);
                            vc = ref RKC(ref stackHead, ref constHead, instruction);
                            if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out numC))
                            {
                                Unsafe.Add(ref stackHead, iA) = numB / numC;
                                stack.NotifyTop(ra1);
                                continue;
                            }

                            if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Div, "div",out  doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.SetResult;
                            break;
                        case OpCode.Mod:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
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
                                stack.NotifyTop(ra1);
                                continue;
                            }

                            if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Mod, "mod",out  doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.SetResult;
                            break;
                        case OpCode.Pow:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
                            vb = ref RKB(ref stackHead, ref constHead, instruction);
                            vc = ref RKC(ref stackHead, ref constHead, instruction);
                            if (vb.TryReadDouble(out numB) && vc.TryReadDouble(out numC))
                            {
                                Unsafe.Add(ref stackHead, iA) = Math.Pow(numB, numC);
                                stack.NotifyTop(ra1);
                                continue;
                            }

                            if (ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Pow, "pow",out  doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.SetResult;
                            break;

                        case OpCode.Unm:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
                            vb = ref Unsafe.Add(ref stackHead, instruction.UIntB);

                            if (vb.TryReadDouble(out numB))
                            {
                                Unsafe.Add(ref stackHead, iA) = -numB;
                                stack.NotifyTop(ra1);
                                continue;
                            }

                            if (ExecuteUnaryOperationMetaMethod(vb, ref context, Metamethods.Unm, "unm", false,out doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.SetResult;
                            break;

                        case OpCode.Not:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);
                            Unsafe.Add(ref stackHead, iA) = !Unsafe.Add(ref stackHead, instruction.UIntB).ToBoolean();
                            stack.NotifyTop(ra1);
                            continue;

                        case OpCode.Len:
                            stack.EnsureCapacity(ra1);
                            stackHead = ref stack.Get(frameBase);

                            vb = ref Unsafe.Add(ref stackHead, instruction.UIntB);

                            if (vb.TryReadString(out var str))
                            {
                                Unsafe.Add(ref stackHead, iA) = str.Length;
                                stack.NotifyTop(ra1);
                                continue;
                            }

                            if (ExecuteUnaryOperationMetaMethod(vb, ref context, Metamethods.Len, "get length of", true,out doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.SetResult;
                            break;
                        case OpCode.Concat:
                            if (Concat(ref context, out doRestart))
                            {
                                if(doRestart)goto Restart;
                                continue;
                            }
                            postOperation = PostOperationType.SetResult;
                            break;
                        case OpCode.Jmp:
                            context.Pc += instruction.SBx;
                            if (iA != 0)
                            {
                                context.State.CloseUpValues(context.Thread, iA - 1);
                            }

                            continue;
                        case OpCode.Eq:
                            stack.EnsureCapacity(ra1);
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

                            if (ExecuteCompareOperationMetaMethod(vb, vc, ref context, Metamethods.Eq, null,out doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.Compare;
                            break;
                        case OpCode.Lt:
                            stack.EnsureCapacity(ra1);
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

                            if (ExecuteCompareOperationMetaMethod(vb, vc, ref context, Metamethods.Lt, "less than",out doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.Compare;
                            break;

                        case OpCode.Le:
                            stack.EnsureCapacity(ra1);
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

                            if (ExecuteCompareOperationMetaMethod(vb, vc, ref context, Metamethods.Le, "less than or equals",out doRestart))
                            {
                                if(doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.Compare;
                            break;

                        case OpCode.Test:
                            if (stack.Get(ra1 - 1).ToBoolean() != (instruction.C == 1))
                            {
                                context.Pc++;
                            }

                            continue;

                        case OpCode.TestSet:
                            vb = ref stack.Get(instruction.B + frameBase);
                            if (vb.ToBoolean() != (instruction.C == 1))
                            {
                                context.Pc++;
                            }
                            else
                            {
                                stack.Get(ra1 - 1) = vb;
                                stack.NotifyTop(ra1);
                            }

                            continue;

                        case OpCode.Call:
                            if (Call(ref context, out  doRestart))
                            {
                                if (doRestart) goto Restart;
                                continue;
                            }

                            postOperation = PostOperationType.Call;
                            break;
                        case OpCode.TailCall:
                            if (TailCall(ref context, out doRestart))
                            {
                                if (doRestart) goto Restart;
                                if (context.IsTopLevel) goto End;
                                continue;
                            }

                            postOperation = PostOperationType.TailCall;
                            state = State.Await;
                            Builder.AwaitOnCompleted(ref context.Awaiter, ref this);
                            return;
                        case OpCode.Return:
                            context.State.CloseUpValues(context.Thread, frameBase);
                            if (context.Pop(instruction, frameBase)) goto Restart;
                            var retCount = instruction.B - 1;

                            if (retCount == -1)
                            {
                                retCount = stack.Count - (ra1 - 1);
                            }

                            if (0 < retCount)
                            {
                                stack.GetBuffer().Slice(ra1 - 1, retCount).CopyTo(context.Buffer.Span);
                            }

                            context.ResultCount = retCount;
                            goto End;
                        case OpCode.ForLoop:
                            stack.EnsureCapacity(ra1 + 3);
                            ref var indexRef = ref stack.Get(ra1 - 1);

                            var lastIndex = indexRef.UnsafeReadDouble();
                            var step = Unsafe.Add(ref indexRef, 2).UnsafeReadDouble();
                            var index = lastIndex + step;
                            var limit = Unsafe.Add(ref indexRef, 1).UnsafeReadDouble();
                            if (step >= 0 ? index <= limit : limit <= index)
                            {
                                context.Pc += instruction.SBx;
                                indexRef = index;
                                Unsafe.Add(ref indexRef, 3) = index;
                                stack.NotifyTop(ra1 + 3);
                                continue;
                            }

                            stack.NotifyTop(ra1);
                            continue;
                        case OpCode.ForPrep:
                            indexRef = ref stack.Get(ra1 - 1);

                            if (!indexRef.TryReadDouble(out var init))
                            {
                                ThrowLuaRuntimeException(ref context, "'for' initial value must be a number");
                                return;
                            }

                            if (!LuaValue.TryReadOrSetDouble(ref Unsafe.Add(ref indexRef, 1), out _))
                            {
                                ThrowLuaRuntimeException(ref context, "'for' limit must be a number");
                                return;
                            }

                            if (!LuaValue.TryReadOrSetDouble(ref Unsafe.Add(ref indexRef, 2), out step))
                            {
                                ThrowLuaRuntimeException(ref context, "'for' step must be a number");
                                return;
                            }

                            indexRef = init - step;
                            stack.NotifyTop(ra1);
                            context.Pc += instruction.SBx;
                            continue;
                        case OpCode.TForCall:
                            if (TForCall(ref context)) continue;
                            postOperation = PostOperationType.TForCall;
                            break;
                        case OpCode.TForLoop:
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
                            stack.EnsureCapacity(ra1);
                            stack.Get(ra1 - 1) = new Closure(context.State, context.Chunk.Functions[instruction.SBx]);
                            stack.NotifyTop(ra1);
                            continue;
                        case OpCode.VarArg:
                            var frameVariableArgumentCount = context.VariableArgumentCount;
                            var count = instruction.B == 0
                                ? frameVariableArgumentCount
                                : instruction.B - 1;
                            var ra = ra1 - 1;
                            stack.EnsureCapacity(ra + count);
                            stackHead = ref stack.Get(frameBase);
                            for (int i = 0; i < count; i++)
                            {
                                stack.Get(ra + i) = frameVariableArgumentCount > i
                                    ? stack.Get(frameBase - (frameVariableArgumentCount - i))
                                    : default;
                            }

                            stack.NotifyTop(ra + count);
                            continue;
                        case OpCode.ExtraArg:
                        default:
                            ThrowLuaNotImplementedException(ref context, instruction.OpCode);
                            return;
                    }
                } while (postOperation == 0);


                //Set the state to await and return with setting this method as the task's continuation
                Console.WriteLine("Await On"+context.Instruction+GetTracebacks(ref context));
                state = State.Await;
                Builder.AwaitOnCompleted(ref context.Awaiter, ref this);
                return;


                End:
                state = State.End;
                ReturnResultsBuffer(context.ResultsBuffer);
                Builder.SetResult(context.ResultCount);
                return;
            }
            catch (Exception e)
            {
                if (e is not LuaRuntimeException)
                {
                    Console.WriteLine(GetTracebacks(ref context));
                }
                //if (context.Pushing) context.Thread.PopCallStackFrame();
                context.State.CloseUpValues(context.Thread, context.FrameBase);
                ReturnResultsBuffer(context.ResultsBuffer);
                state = State.End;
                context = default;
                Builder.SetException(e);
            }
        }

        [DebuggerHidden]
        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            Builder.SetStateMachine(stateMachine);
        }

        static void ThrowLuaRuntimeException(ref VirtualMachineExecutionContext context, string message)
        {
            throw new LuaRuntimeException(context.State.GetTraceback(), message);
        }

        static void ThrowLuaNotImplementedException(ref VirtualMachineExecutionContext context, OpCode opcode)
        {
            throw new LuaRuntimeException(context.State.GetTraceback(), $"OpCode {opcode} is not implemented");
        }
    }


    static void SelfPostOperation(ref VirtualMachineExecutionContext context)
    {
        var stack = context.Stack;
        var instruction = context.Instruction;
        var RA = instruction.A + context.FrameBase;
        var RB = instruction.B + context.FrameBase;
        ref var stackHead = ref stack.Get(0);
        var table = Unsafe.Add(ref stackHead, RB);
        Unsafe.Add(ref stackHead, RA + 1) = table;
        var value = context.ResultsBuffer[0];
        Unsafe.Add(ref stackHead, RA) = value;
        stack.NotifyTop(RA + 2);
    }

    static bool Concat(ref VirtualMachineExecutionContext context,out bool doRestart)
    {
        var instruction = context.Instruction;
        var stack = context.Stack;
        var RA = instruction.A + context.FrameBase;
        stack.EnsureCapacity(RA + 1);
        ref var stackHead = ref stack.Get(context.FrameBase);
        ref var constHead = ref context.Chunk.Constants[0];
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
            strC = numC.ToString();
            cIsValid = true;
        }

        if (bIsValid && cIsValid)
        {
            stack.Get(RA) = strB + strC;
            stack.NotifyTop(RA + 1);
            doRestart = false;
            return true;
        }

        return ExecuteBinaryOperationMetaMethod(vb, vc, ref context, Metamethods.Concat, "concat",out  doRestart);
    }
#if NET6_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
#endif
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
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", metamethod);
            }
        }


        var thread = context.Thread;
        var (newBase, argumentCount, variableArgumentCount) = PrepareForFunctionCallNoTail(thread, func, instruction, RA);

        var newFrame = new CallStackFrame()
        {
            Base = newBase,
            CallPosition = context.Chunk.SourcePositions[context.Pc],
            ChunkName = context.Chunk.Name,
            RootChunkName = context.Chunk.GetRoot().Name,
            VariableArgumentCount = variableArgumentCount,
            Function = func,
            CallerInstructionIndex = context.Pc,
        };
        thread.PushCallStackFrame(newFrame);
        if (func.IsClosure)
        {
            context.Push(newFrame);
            doRestart = true;
            return true;
        }

        doRestart = false;
        // if (func is FastFunction fastFunction)
        // {
        //     return FastCall(ref context, fastFunction, newBase, argumentCount);
        // }
// #if NET6_0_OR_GREATER
//         [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
// #endif
//         static bool FastCall(ref VirtualMachineExecutionContext context, FastFunction fastFunction, int newBase, int argumentCount)
//         {
//             {
//                 var buffer = context.Stack.GetBuffer();
//                 var ra = context.Instruction.A + context.FrameBase;
//                 var fastContext = new FastFunctionContext()
//                 {
//                     State = context.State,
//                     Arguments = buffer.Slice(newBase, argumentCount),
//                     ResultBuffer = buffer.Slice(ra),
//                 };
//                 var rawResultCount = fastFunction.InvokeFast(fastContext);
//                 var resultCount = rawResultCount;
//                 context.Thread.PopCallStackFrameFast(newBase);
//                 //context.Pushing = false;
//                 var ic = context.Instruction.C;
//                 if (ic != 0)
//                 {
//                     resultCount = ic - 1;
//                 }
//             
//                 if (resultCount == 0)
//                 {
//                     context.Stack.Pop();
//                 }
//             
//                 if (resultCount != rawResultCount)
//                 {
//                     buffer.Slice(ra + resultCount, rawResultCount - resultCount).Clear();
//                 }
//             
//                 return true;
//             }
//         }

#if NET6_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
#endif
        static bool FuncCall(ref VirtualMachineExecutionContext context, in CallStackFrame newFrame, LuaFunction func, int newBase, int argumentCount)
        {
          
            {
                ValueTask<int> task = func.Func(new()
                {
                    State = context.State,
                    Thread = context.Thread,
                    ArgumentCount = argumentCount,
                    FrameBase = newBase,
                    SourcePosition = newFrame.CallPosition,
                    ChunkName = newFrame.ChunkName,
                    RootChunkName = newFrame.RootChunkName,
                    CallerInstructionIndex = context.Pc,
                }, context.ResultsBuffer.AsMemory(), context.CancellationToken);


                var awaiter = task.GetAwaiter();
                if (!awaiter.IsCompleted)
                {
                    context.Awaiter = awaiter;
                    return false;
                }

                context.Thread.PopCallStackFrameFast(newBase);
                //context.Pushing = false;
                context.TaskResult = awaiter.GetResult();
                var instruction = context.Instruction;
                var rawResultCount = context.TaskResult;
                var resultCount = rawResultCount;
                var ic = instruction.C;

                if (ic != 0)
                {
                    resultCount = ic - 1;
                }

                if (resultCount == 0)
                {
                    context.Stack.Pop();
                }
                else
                {
                    //Console.WriteLine($"Result count {resultCount} raw {rawResultCount}");
                    var stack = context.Stack;
                    var RA = instruction.A + context.FrameBase;
                    stack.EnsureCapacity(RA + resultCount);
                    ref var stackHead = ref stack.Get(RA);
                    var results = context.ResultsBuffer;
                    for (int i = 0; i < resultCount; i++)
                    {
                        Unsafe.Add(ref stackHead, i) = i >= rawResultCount
                            ? default
                            : results[i];
                    }

                    stack.NotifyTop(RA + resultCount);
                }

                return true;
            }
        }

        return FuncCall(ref context, in newFrame, func, newBase, argumentCount);
    }

    static void CallPostOperation(ref VirtualMachineExecutionContext context)
    {
        var instruction = context.Instruction;
        var rawResultCount = context.TaskResult;
        var resultCount = rawResultCount;
        var ic = instruction.C;

        if (ic != 0)
        {
            resultCount = ic - 1;
        }

        if (resultCount == 0)
        {
            context.Stack.Pop();
        }
        else
        {
            var stack = context.Stack;
            var RA = instruction.A + context.FrameBase;
            stack.EnsureCapacity(RA + resultCount);
            ref var stackHead = ref stack.Get(RA);
            var results = context.ResultsBuffer;
            for (int i = 0; i < resultCount; i++)
            {
                Unsafe.Add(ref stackHead, i) = i >= rawResultCount
                    ? default
                    : results[i];
            }

            stack.NotifyTop(RA + resultCount);
        }
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
        //var rootChunk = context.Chunk.GetRoot();

        var newFrame = new CallStackFrame()
        {
            Base = newBase,
            CallPosition = context.Chunk.SourcePositions[context.Pc],
            ChunkName = context.Chunk.Name,
            RootChunkName = context.Chunk.GetRoot().Name,
            VariableArgumentCount = variableArgumentCount,
            Function = func,
            CallerInstructionIndex = context.Pc,
            //CallerChunk = context.Chunk,
        };
        thread.PushCallStackFrame(newFrame);
       // context.Pushing = true;

        context.Push(newFrame);
        if (func is Closure)
        {
            doRestart = true;
            return true;
        }

        doRestart = false;
        ValueTask<int> task = func.Func(new()
        {
            State = context.State,
            Thread = thread,
            ArgumentCount = argumentCount,
            FrameBase = newBase,
            SourcePosition = newFrame.CallPosition,
            ChunkName = newFrame.ChunkName,
            RootChunkName = newFrame.RootChunkName,
            CallerInstructionIndex = context.Pc,
        }, context.Buffer, context.CancellationToken);


        var awaiter = task.GetAwaiter();
        if (!awaiter.IsCompleted)
        {
            context.Awaiter = awaiter;
            return false;
        }

        if (context.IsTopLevel)
        {
            context.ResultCount = awaiter.GetResult();
        }

        context.Thread.PopCallStackFrame();
        //context.Pushing = false;
        if (!context.IsTopLevel)
        {
            doRestart = true;
            var resultCount = awaiter.GetResult();
            if (!context.PopFromBuffer(context.Buffer.Span[..resultCount]))
            {
                doRestart = false;
                context.ResultCount = resultCount;
            }
        }

        return true;
    }

    static bool TForCall(ref VirtualMachineExecutionContext context)
    {
        var instruction = context.Instruction;
        var stack = context.Stack;
        var RA = instruction.A + context.FrameBase;

        var iteratorRaw = stack.Get(RA);
        if (!iteratorRaw.TryReadFunction(out var iterator))
        {
            LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", iteratorRaw);
        }

        var nextBase = RA + 3 + instruction.C;
        stack.Get(nextBase) = stack.Get(RA + 1);
        stack.Get(nextBase + 1) = stack.Get(RA + 2);
        stack.NotifyTop(nextBase + 2);
        //context.Pushing = true;
        var task = iterator.InvokeAsyncPushOnly(new()
        {
            State = context.State,
            Thread = context.Thread,
            ArgumentCount = 2,
            FrameBase = nextBase,
            SourcePosition = context.Chunk.SourcePositions[context.Pc],
            ChunkName = context.Chunk.Name,
            RootChunkName = context.Chunk.GetRoot().Name,
            CallerInstructionIndex = context.Pc,
        }, context.ResultsBuffer.AsMemory(), context.CancellationToken);

        var awaiter = task.GetAwaiter();
        if (!awaiter.IsCompleted)
        {
            context.Awaiter = awaiter;

            return false;
        }

        context.TaskResult = awaiter.GetResult();
        context.Thread.PopCallStackFrame();
        //context.Pushing = false;
        TForCallPostOperation(ref context);
        return true;
    }

    static void TForCallPostOperation(ref VirtualMachineExecutionContext context)
    {
        var stack = context.Stack;
        var instruction = context.Instruction;
        var RA = instruction.A + context.FrameBase;
        var resultBuffer = context.ResultsBuffer;
        var resultCount = context.TaskResult;
        stack.EnsureCapacity(RA + instruction.C + 3);
        for (int i = 1; i <= instruction.C; i++)
        {
            var index = i - 1;
            stack.Get(RA + 2 + i) = index >= resultCount
                ? LuaValue.Nil
                : resultBuffer[i - 1];
        }

        stack.NotifyTop(RA + instruction.C + 3);
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
        stack.AsSpan().Slice(RA + 1, count)
            .CopyTo(table.GetArraySpan()[((instruction.C - 1) * 50)..]);
    }

    static void ComparePostOperation(ref VirtualMachineExecutionContext context)
    {
        var compareResult = context.TaskResult != 0 && context.ResultsBuffer[0].ToBoolean();
        if (compareResult != (context.Instruction.A == 1))
        {
            context.Pc++;
        }
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


    static bool TryGetMetaTableValue(LuaValue table, LuaValue key, ref VirtualMachineExecutionContext context,out bool doRestart)
    {
        var  isSelf =context.Instruction.OpCode == OpCode.Self;
        doRestart = false;
        var state = context.State;
        if (table.TryGetMetamethod(state, Metamethods.Index, out var metamethod))
        {
            if (!metamethod.TryReadFunction(out var indexTable))
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", metamethod);
            }

            var stack = context.Stack;
            stack.Push(table);
            stack.Push(key);
            
            
            var newCallFrame = new CallStackFrame()
            {
                Base = stack.Count - 2,
                CallPosition = context.Chunk.SourcePositions[context.Pc],
                ChunkName = context.Chunk.Name,
                RootChunkName = context.Chunk.GetRoot().Name,
                Function = indexTable,
                CallerInstructionIndex = context.Pc,
                VariableArgumentCount = 0
            };
            
            context.Thread.PushCallStackFrame(newCallFrame);
            
            if (indexTable.IsClosure)
            {
                context.Push(newCallFrame);
                doRestart = true;
                return true;
            }

            var task = indexTable.Func(new()
            {
                State = state,
                Thread = context.Thread,
                ArgumentCount = 2,
                SourcePosition = newCallFrame.CallPosition,
                FrameBase = stack.Count - 2,
                ChunkName = newCallFrame.ChunkName,
                RootChunkName = newCallFrame.RootChunkName,
                CallerInstructionIndex = context.Pc,
            }, context.ResultsBuffer, context.CancellationToken);
            var awaiter = task.GetAwaiter();
            if (awaiter.IsCompleted)
            {
                context.Thread.PopCallStackFrame();
                var ra = context.Instruction.A + context.FrameBase;
                context.Stack.Get(ra) = context.ResultsBuffer[0];
                if (isSelf)
                {
                    context.Stack.Get(ra + 1) = table;
                    context.Stack.NotifyTop(ra + 2);
                }
                else
                {
                    context.Stack.NotifyTop(ra + 1);
                }
                return true;
            }
            context.Awaiter = awaiter;
            return false;
        }

        if (table.Type == LuaValueType.Table)
        {
            var ra = context.Instruction.A + context.FrameBase;
            context.Stack.Get(ra) = default;
            if (isSelf)
            {
                context.Stack.Get(ra + 1) = table;
                context.Stack.NotifyTop(ra + 2);
            }
            else
            {
                context.Stack.NotifyTop(ra + 1);
            }

            return true;
        }
        
        
        LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "index", table);
        return false;
    }
    static bool TryGetMetaTableValueSelf(LuaValue table, LuaValue key, ref VirtualMachineExecutionContext context,out bool doRestart)
    {
        doRestart = false;
        var state = context.State;
        if (table.TryGetMetamethod(state, Metamethods.Index, out var metamethod))
        {
            if (!metamethod.TryReadFunction(out var indexTable))
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", metamethod);
            }

            var stack = context.Stack;
            stack.Push(table);
            stack.Push(key);
            
            
            var newCallFrame = new CallStackFrame()
            {
                Base = stack.Count - 2,
                CallPosition = context.Chunk.SourcePositions[context.Pc],
                ChunkName = context.Chunk.Name,
                RootChunkName = context.Chunk.GetRoot().Name,
                Function = indexTable,
                CallerInstructionIndex = context.Pc,
                VariableArgumentCount = 0
            };
            
            context.Thread.PushCallStackFrame(newCallFrame);
            
            if (indexTable.IsClosure)
            {
                context.Push(newCallFrame);
                doRestart = true;
                return true;
            }

            var task = indexTable.Func(new()
            {
                State = state,
                Thread = context.Thread,
                ArgumentCount = 2,
                SourcePosition = newCallFrame.CallPosition,
                FrameBase = stack.Count - 2,
                ChunkName = newCallFrame.ChunkName,
                RootChunkName = newCallFrame.RootChunkName,
                CallerInstructionIndex = context.Pc,
            }, context.ResultsBuffer, context.CancellationToken);
            var awaiter = task.GetAwaiter();
            if (awaiter.IsCompleted)
            {
                context.Thread.PopCallStackFrame();
                var ra = context.Instruction.A + context.FrameBase;
                context.Stack.Get(ra) = context.ResultsBuffer[0];
                context.Stack.NotifyTop(ra + 1);
                return true;
            }
            context.Awaiter = awaiter;
            return false;
        }

        if (table.Type == LuaValueType.Table)
        {
            var ra = context.Instruction.A + context.FrameBase;
            context.Stack.Get(ra) = default;
            context.Stack.NotifyTop(ra + 1);
            return false;
        }
        
        
        LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "index", table);
        return false;
    }

    static bool TrySetMetaTableValue(LuaValue table, LuaValue key, LuaValue value,
        ref VirtualMachineExecutionContext context, out bool doRestart)
    {
        doRestart = false;
        var state = context.State;
        if (table.TryGetMetamethod(state, Metamethods.NewIndex, out var metamethod))
        {
            if (!metamethod.TryReadFunction(out var indexTable))
            {
                LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "call", metamethod);
            }

            var thread = context.Thread;
            var stack = thread.Stack;
            stack.Push(table);
            stack.Push(key);
            stack.Push(value);
            var newCallFrame = new CallStackFrame()
            {
                Base = stack.Count - 3,
                CallPosition = context.Chunk.SourcePositions[context.Pc],
                ChunkName = context.Chunk.Name,
                RootChunkName = context.Chunk.GetRoot().Name,
                Function = indexTable,
                CallerInstructionIndex = context.Pc,
                VariableArgumentCount = 0
            };
            
            context.Thread.PushCallStackFrame(newCallFrame);
            
            if (indexTable.IsClosure)
            {
                context.Push(newCallFrame);
                doRestart = true;
                return true;
            }
            
            //context.Pushing = true;
            var task = indexTable.Func(new()
            {
                State = state,
                Thread = thread,
                ArgumentCount = 3,
                SourcePosition = context.Chunk.SourcePositions[context.Pc],
                FrameBase = stack.Count - 3,
                ChunkName = context.Chunk.Name,
                RootChunkName = context.Chunk.GetRoot().Name,
                CallerInstructionIndex = context.Pc,
            }, context.ResultsBuffer, context.CancellationToken);
            var awaiter = task.GetAwaiter();
            if(awaiter.IsCompleted)
            {
                thread.PopCallStackFrame();
                return true;
            }
            context.Awaiter = awaiter;
            return false;
        }
        LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), "index", table);
        return false;
    }

    static bool ExecuteBinaryOperationMetaMethod(LuaValue vb, LuaValue vc,
        ref VirtualMachineExecutionContext context, string name, string description,out bool doRestart)
    {
        doRestart= false;
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
            
            var newCallFrame = new CallStackFrame()
            {
                Base = stack.Count - 2,
                CallPosition = context.Chunk.SourcePositions[context.Pc],
                ChunkName = context.Chunk.Name,
                RootChunkName = context.Chunk.GetRoot().Name,
                Function = func,
                CallerInstructionIndex = context.Pc,
                VariableArgumentCount = 0
            };
            
            context.Thread.PushCallStackFrame(newCallFrame);

            if (func.IsClosure)
            {
                context.Push(newCallFrame);
                doRestart = true;
                return true;
            }
            
            
            //  context.Pushing = true;
            var task = func.Func(new()
            {
                State = context.State,
                Thread = context.Thread,
                ArgumentCount = 2,
                FrameBase = stack.Count - 2,
                SourcePosition = newCallFrame.CallPosition,
                ChunkName = newCallFrame.ChunkName,
                RootChunkName = newCallFrame.RootChunkName,
                CallerInstructionIndex = context.Pc,
            }, context.ResultsBuffer.AsMemory(), context.CancellationToken);
            context.Awaiter = task.GetAwaiter();
            
            if (context.Awaiter.IsCompleted)
            {
                var taskResult = context.Awaiter.GetResult();
                context.Thread.PopCallStackFrame();
               // context.Pushing = false;
                var RA = context.Instruction.A + context.FrameBase;
                stack.Get(RA) = taskResult == 0 ? LuaValue.Nil : context.ResultsBuffer[0];
                stack.NotifyTop(RA + 1);
                return true;
            }

            return false;
        }

        LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), description, vb, vc);
        return false;
    }

    static bool ExecuteUnaryOperationMetaMethod(LuaValue vb, ref VirtualMachineExecutionContext context,
        string name, string description, bool isLen,out bool doRestart)
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
            var newCallFrame = new CallStackFrame()
            {
                Base = stack.Count - 1,
                CallPosition = context.Chunk.SourcePositions[context.Pc],
                ChunkName = context.Chunk.Name,
                RootChunkName = context.Chunk.GetRoot().Name,
                Function = func,
                CallerInstructionIndex = context.Pc,
                VariableArgumentCount = 0
            };
            
            context.Thread.PushCallStackFrame(newCallFrame);

            if (func.IsClosure)
            {
                context.Push(newCallFrame);
                doRestart = true;
                return true;
            }
            
            
            //  context.Pushing = true;
            var task = func.Func(new()
            {
                State = context.State,
                Thread = context.Thread,
                ArgumentCount = 1,
                FrameBase = stack.Count - 1,
                SourcePosition = newCallFrame.CallPosition,
                ChunkName = newCallFrame.ChunkName,
                RootChunkName = newCallFrame.RootChunkName,
                CallerInstructionIndex = context.Pc,
            }, context.ResultsBuffer.AsMemory(), context.CancellationToken);

            context.Awaiter = task.GetAwaiter();
            if (context.Awaiter.IsCompleted)
            {
                context.Thread.PopCallStackFrame();
                //context.Pushing = false;
                var RA = context.Instruction.A + context.FrameBase;
                var taskResult = context.Awaiter.GetResult();
                stack.Get(RA) = taskResult == 0 ? LuaValue.Nil : context.ResultsBuffer[0];
                stack.NotifyTop(RA + 1);
                return true;
            }

            return false;
        }

        if (isLen && vb.TryReadTable(out var table))
        {
            var RA = context.Instruction.A + context.FrameBase;
            stack.Get(RA) = table.ArrayLength;
            stack.NotifyTop(RA + 1);
            return true;
        }

        LuaRuntimeException.AttemptInvalidOperation(GetTracebacks(ref context), description, vb);
        return true;
    }

    static bool ExecuteCompareOperationMetaMethod(LuaValue vb, LuaValue vc,
        ref VirtualMachineExecutionContext context, string name, string? description,out bool doRestart)
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
            var newCallFrame = new CallStackFrame()
            {
                Base = stack.Count - 2,
                CallPosition = context.Chunk.SourcePositions[context.Pc],
                ChunkName = context.Chunk.Name,
                RootChunkName = context.Chunk.GetRoot().Name,
                Function = func,
                CallerInstructionIndex = context.Pc,
                VariableArgumentCount = 0
            };
            
            context.Thread.PushCallStackFrame(newCallFrame);
            
            if (func.IsClosure)
            {
                context.Push(newCallFrame);
                doRestart = true;
                return true;
            }
            
            var task = func.Func(new()
            {
                State = context.State,
                Thread = context.Thread,
                ArgumentCount = 2,
                FrameBase = stack.Count - 2,
                SourcePosition = context.Chunk.SourcePositions[context.Pc],
                ChunkName = context.Chunk.Name,
                RootChunkName = context.Chunk.GetRoot().Name,
                CallerInstructionIndex = context.Pc,
            }, context.ResultsBuffer.AsMemory(), context.CancellationToken);
            context.Awaiter = task.GetAwaiter();
            
            if (context.Awaiter.IsCompleted)
            {
                context.Thread.PopCallStackFrame();
                var compareResult = context.Awaiter.GetResult() != 0 && context.ResultsBuffer[0].ToBoolean();
                if (compareResult != (context.Instruction.A == 1))
                {
                    context.Pc++;
                }

                return true;
            }

            return false;
        }

        if (description != null)
        {
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


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static (int FrameBase, int ArgumentCount, int VariableArgumentCount) PrepareForFunctionCallNoTail(LuaThread thread, LuaFunction function,
        Instruction instruction, int RA)
    {
        var argumentCount = instruction.B - 1;
        if (argumentCount == -1)
        {
            argumentCount = (ushort)(thread.Stack.Count - (RA + 1));
        }

        var newBase = RA + 1;
        var variableArgumentCount = function.GetVariableArgumentCount(argumentCount);

        if (variableArgumentCount <= 0)
        {
            return (newBase, argumentCount, 0);
        }

        return WithVariableArgument(newBase, variableArgumentCount, thread.Stack, argumentCount);

        // If there are variable arguments, the base of the stack is moved by that number and the values of the variable arguments are placed in front of it.
        // see: https://wubingzheng.github.io/build-lua-in-rust/en/ch08-02.arguments.html
        static (int FrameBase, int ArgumentCount, int VariableArgumentCount) WithVariableArgument(int newBase, int variableArgumentCount,
            LuaStack stack, int argumentCount)
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
    }

    static (int FrameBase, int ArgumentCount, int VariableArgumentCount) PrepareForFunctionTailCall(LuaThread thread, LuaFunction function,
        Instruction instruction, int RA)
    {
        var stack = thread.Stack;

        var argumentCount = instruction.B - 1;
        if (instruction.B == 0)
        {
            argumentCount = (ushort)(stack.Count - (RA + 1));
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

        // If there are variable arguments, the base of the stack is moved by that number and the values of the variable arguments are placed in front of it.
        // see: https://wubingzheng.github.io/build-lua-in-rust/en/ch08-02.arguments.html
        if (variableArgumentCount > 0)
        {
            var temp = newBase;
            newBase += variableArgumentCount;

            stack.EnsureCapacity(newBase + argumentCount);
            stack.NotifyTop(newBase + argumentCount);

            var stackBuffer = stack.GetBuffer()[temp..];
            stackBuffer[..argumentCount].CopyTo(stackBuffer[variableArgumentCount..]);
            stackBuffer.Slice(argumentCount, variableArgumentCount).CopyTo(stackBuffer);
        }

        return (newBase, argumentCount, variableArgumentCount);
    }

    static Traceback GetTracebacks(ref VirtualMachineExecutionContext context)
    {
        return GetTracebacks(context.State, context.Chunk, context.Pc);
    }

    static Traceback GetTracebacks(LuaState state, Chunk chunk, int pc)
    {
        var frame = state.CurrentThread.GetCurrentFrame();
        state.CurrentThread.PushCallStackFrame(frame with
        {
            CallPosition = chunk.SourcePositions[pc],
            ChunkName = chunk.Name,
            RootChunkName = chunk.GetRoot().Name,
        });
        var tracebacks = state.GetTraceback();
        state.CurrentThread.PopCallStackFrame();
        return tracebacks;
    }
}