using System.Collections.Concurrent;

namespace Lua.DebugServer;

public class SimpleEngine
{
    readonly ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();


    public void Enqueue(Action action)
    {
        actions.Enqueue(action);
    }

    public void Update()
    {
        Console.WriteLine("SimpleEngine Update  Start");
        while (true)
        {
            while (actions.TryDequeue(out var action))
            {
                Console.WriteLine("SimpleEngine Update Action Start");
                action();
                Console.WriteLine("SimpleEngine Update Action End");
            }


            Thread.Sleep(33);
        }
    }
}