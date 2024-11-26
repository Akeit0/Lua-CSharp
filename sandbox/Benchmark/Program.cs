using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using System.Reflection;
using BenchmarkDotNet.Environments;

BenchmarkSwitcher.FromAssembly(Assembly.GetEntryAssembly()!).Run(args);

class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddDiagnoser(MemoryDiagnoser.Default);
        // AddJob(Job.ShortRun
        //     .WithWarmupCount(50)
        //     .WithIterationCount(50).WithRuntime(CoreRuntime.Core60)
        // );
        // AddJob(Job.ShortRun
        //     .WithWarmupCount(50)
        //     .WithIterationCount(50).WithRuntime(CoreRuntime.Core80)
        // );
        AddJob(Job.ShortRun
            .WithWarmupCount(50)
            .WithIterationCount(50).WithRuntime(CoreRuntime.Core90)
        );
        
        //AddAnalyser(BenchmarkConfig.Create(C))
    }
}