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
        var sqCount = 0;
        while (true)
        {
            while (actions.TryDequeue(out var action))
            {
                var pos=Console.GetCursorPosition();
                Console.SetCursorPosition( 0,pos.Top);
                Console.WriteLine("SimpleEngine Update Action Start");
                action();
                Console.WriteLine("SimpleEngine Update Action End");
            }
            if (sqCount == 10)
            {
                var pos=Console.GetCursorPosition();
                Console.SetCursorPosition( 0,pos.Top);
                sqCount = 0;
            }
            Console.Write("■");
            sqCount++;
            Thread.Sleep(33);
        }
    }
}