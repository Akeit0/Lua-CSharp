using System.Runtime.CompilerServices;

namespace Lua.Runtime;

public static partial class LuaVirtualMachine
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool ExecutePerInstructionHook(ref VirtualMachineExecutionContext context)
    {
        var r = Impl(context);
        if (r.IsCompleted)
        {
            if (r.Result == 0)
            {
                context.Thread.PopCallStackFrame();
            }

            return false;
        }

        context.Task = r;
        context.Pc--;
        return true;

        static async ValueTask<int> Impl(VirtualMachineExecutionContext context)
        {
            bool countHookIsDone = false;
            if (context.Thread.IsCountHookEnabled && --context.Thread.HookCount == 0)
            {
                context.Thread.HookCount = context.Thread.BaseHookCount;

                var hook = context.Thread.Hook!;
                var stack = context.Thread.Stack;
                stack.Push("count");
                stack.Push(LuaValue.Nil);
                var funcContext = new LuaFunctionExecutionContext
                {
                    State = context.State,
                    Thread = context.Thread,
                    ArgumentCount = 2,
                    FrameBase = context.Thread.Stack.Count - 2,
                };
                var frame = new CallStackFrame
                {
                    Base = funcContext.FrameBase,
                    VariableArgumentCount = hook is Closure closure ? Math.Max(funcContext.ArgumentCount - closure.Proto.ParameterCount, 0) : 0,
                    Function = hook,
                    CallerInstructionIndex = context.Pc,
                };
                frame.Flags |= CallStackFrameFlags.InHook;
                context.Thread.IsInHook = true;
                context.Thread.PushCallStackFrame(frame);
                await hook.Func(funcContext, Memory<LuaValue>.Empty, context.CancellationToken);
                context.Thread.IsInHook = false;


                countHookIsDone = true;
            }


            if (context.Thread.IsLineHookEnabled)
            {
                var pc = context.Pc;
                var sourcePositions = context.Chunk.SourcePositions;
                var line = sourcePositions[pc].Line;

                if (countHookIsDone || pc == 0 || context.Thread.LastPc < 0 || pc <= context.Thread.LastPc || sourcePositions[context.Thread.LastPc].Line != line)
                {
                    if (countHookIsDone)
                    {
                        context.Thread.PopCallStackFrame();
                    }


                    var hook = context.Thread.Hook!;
                    var stack = context.Thread.Stack;
                    stack.Push("line");
                    stack.Push(line);
                    var funcContext = new LuaFunctionExecutionContext
                    {
                        State = context.State,
                        Thread = context.Thread,
                        ArgumentCount = 2,
                        FrameBase = context.Thread.Stack.Count - 2,
                    };
                    var frame = new CallStackFrame
                    {
                        Base = funcContext.FrameBase,
                        VariableArgumentCount = hook is Closure closure ? Math.Max(funcContext.ArgumentCount - closure.Proto.ParameterCount, 0) : 0,
                        Function = hook,
                        CallerInstructionIndex = pc,
                    };
                    frame.Flags |= CallStackFrameFlags.InHook;
                    context.Thread.IsInHook = true;
                    context.Thread.PushCallStackFrame(frame);
                    await hook.Func(funcContext, Memory<LuaValue>.Empty, context.CancellationToken);
                    context.Thread.IsInHook = false;
                    context.Pc--;
                    context.Thread.LastPc = pc;
                    return 0;
                }

                context.Thread.LastPc = pc;
            }

            if (countHookIsDone)
            {
                context.Pc--;
                return 0;
            }

            return -1;

            ;
        }
    }

    static void ExecuteCallHook(ref VirtualMachineExecutionContext context, PostOperationType postOperationType)
    {
        context.Task = Impl(context, postOperationType);

        static async ValueTask<int> Impl(VirtualMachineExecutionContext context, PostOperationType postOperationType)
        {
            var top = context.Thread.Stack.Count;
            var hook = context.Thread.Hook!;
            var stack = context.Thread.Stack;
            CallStackFrame frame;
            if (context.Thread.IsCallHookEnabled)
            {
                stack.Push("call");
                stack.Push(LuaValue.Nil);
                var funcContext = new LuaFunctionExecutionContext
                {
                    State = context.State,
                    Thread = context.Thread,
                    ArgumentCount = 2,
                    FrameBase = context.Thread.Stack.Count - 2,
                };
                frame = new CallStackFrame
                {
                    Base = funcContext.FrameBase,
                    VariableArgumentCount = hook is Closure closure ? Math.Max(funcContext.ArgumentCount - closure.Proto.ParameterCount, 0) : 0,
                    Function = hook,
                };
                frame.Flags |= CallStackFrameFlags.InHook;

                context.Thread.PushCallStackFrame(frame);
                try
                {
                    context.Thread.IsInHook = true;
                    await hook.Func(funcContext, Memory<LuaValue>.Empty, context.CancellationToken);
                }
                finally
                {
                    context.Thread.IsInHook = false;
                    context.Thread.PopCallStackFrame();
                }
            }


            frame = context.Thread.GetCurrentFrame();
            var task = frame.Function.Func(new LuaFunctionExecutionContext()
            {
                State = context.State,
                Thread = context.Thread,
                ArgumentCount = top - frame.Base,
                FrameBase = frame.Base,
            }, context.ResultsBuffer, context.CancellationToken);
            if (!context.Thread.IsReturnHookEnabled)
            {
                context.PostOperation = postOperationType;
                return await task;
            }
            else
            {
                context.TaskResult = await task;
                context.Thread.PopCallStackFrame();
                context.ExecutePostOperation(postOperationType);
                stack.Push("return");
                stack.Push(LuaValue.Nil);
                var funcContext = new LuaFunctionExecutionContext
                {
                    State = context.State,
                    Thread = context.Thread,
                    ArgumentCount = 2,
                    FrameBase = context.Thread.Stack.Count - 2,
                };
                frame = new CallStackFrame
                {
                    Base = funcContext.FrameBase,
                    VariableArgumentCount = hook is Closure closure ? Math.Max(funcContext.ArgumentCount - closure.Proto.ParameterCount, 0) : 0,
                    Function = hook,
                };
                frame.Flags |= CallStackFrameFlags.InHook;

                context.Thread.PushCallStackFrame(frame);
                try
                {
                    context.Thread.IsInHook = true;
                    await hook.Func(funcContext, Memory<LuaValue>.Empty, context.CancellationToken);
                }
                finally
                {
                    context.Thread.IsInHook = false;
                }

                context.PostOperation = PostOperationType.Nop;
                return 0;
            }
        }
    }
}