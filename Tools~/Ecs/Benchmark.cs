using System;
using System.Diagnostics;
using System.Globalization;
using EjoyFramework.Core.Ecs;
using EjoyGame.Samples.EcsDemo;

internal static class Benchmark
{
    private static void Main()
    {
        Console.WriteLine("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Console.WriteLine("Mode: serial scalar movement; excludes setup, warmup, rendering, physics and structural changes.");
        foreach (int count in new[] { 1000, 10000, 100000 }) Measure(count);
    }

    private static void Measure(int count)
    {
        using var world = new World(count);
        using var systems = new SystemGroup(world);
        Entity last = default;
        for (int i = 0; i < count; i++)
        {
            last = world.CreateEntity();
            world.Set(last, new Position());
            world.Set(last, new Velocity { X = 1 });
        }
        systems.Add(new MovementSystem(world));
        const int warmup = 100;
        const int iterations = 200;
        var time = EcsTime.ForFrame(0.5);
        for (int i = 0; i < warmup; i++) systems.Update(in time);
        var elapsed = new double[iterations];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            systems.Update(in time);
            elapsed[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        float expected = (warmup + iterations) * 0.5f;
        if (world.Get<Position>(last).X != expected) throw new Exception("Movement verification failed.");
        Array.Sort(elapsed);
        double median = (elapsed[iterations / 2 - 1] + elapsed[iterations / 2]) / 2;
        double p95 = elapsed[(int)Math.Ceiling(iterations * 0.95) - 1];
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "entities={0}, ticks={1}, median_ms={2:F4}, p95_ms={3:F4}, allocated_bytes={4}", count, iterations, median, p95, allocated));
    }
}
